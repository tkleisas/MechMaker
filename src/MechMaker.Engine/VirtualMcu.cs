namespace MechMaker.Engine;

/// <summary>
/// A simulated printer-class control board. Runs once per physics tick, in lockstep
/// with the simulator: steppers convert their commanded motion into magnetic torque
/// on the rotor (so load, friction, and belts can stall them or make them miss steps),
/// endstops sample joint positions with debouncing.
///
/// This is the MCU side of the Klipper split: it executes motion; it does not plan it.
/// The host (tests today, the scenario runner or real Klipper later) commands velocity.
/// </summary>
public sealed class VirtualMcu
{
    private readonly List<StepperChannel> _steppers = [];
    private readonly List<EndstopChannel> _endstops = [];

    internal VirtualMcu(Simulator simulator, double controlPeriod = 0.001)
    {
        Simulator = simulator;
        ControlPeriod = controlPeriod;
    }

    public Simulator Simulator { get; }

    /// <summary>MCU tick period (1 kHz, like a printer board's step loop), independent
    /// of the physics substep the simulator integrates at.</summary>
    public double ControlPeriod { get; }

    public IReadOnlyList<StepperChannel> Steppers => _steppers;
    public IReadOnlyList<EndstopChannel> Endstops => _endstops;

    public StepperChannel AddStepper(string actuatorName, StepperSpec? spec = null)
    {
        var channel = new StepperChannel(this, actuatorName, spec ?? new StepperSpec());
        _steppers.Add(channel);
        return channel;
    }

    /// <summary>Senses a joint coordinate against a trigger — how a printer endstop works:
    /// pressed when the carriage reaches the limit position.</summary>
    public EndstopChannel AddEndstop(string jointName, double triggerPosition, int debounceTicks = 3)
    {
        var channel = new EndstopChannel(this, jointName, triggerPosition, debounceTicks);
        _endstops.Add(channel);
        return channel;
    }

    internal void Tick()
    {
        foreach (var stepper in _steppers)
            stepper.Tick();
        foreach (var endstop in _endstops)
            endstop.Tick();
    }
}

/// <summary>One stepper axis: open-loop commutation of a hybrid stepper motor.</summary>
public sealed class StepperChannel
{
    private readonly VirtualMcu _mcu;
    private readonly StepperSpec _spec;
    private readonly int _actuatorId;
    private readonly string _jointName;

    private double _microstepAccumulator; // fractional microsteps awaiting application
    private bool _enabled;

    internal StepperChannel(VirtualMcu mcu, string actuatorName, StepperSpec spec)
    {
        _mcu = mcu;
        _spec = spec;
        _actuatorId = mcu.Simulator.ActuatorId(actuatorName);
        _jointName = mcu.Simulator.JointForActuator(_actuatorId);
    }

    /// <summary>The rotor joint this channel drives (e.g. j_motor_left_rotor).</summary>
    public string JointName => _jointName;

    /// <summary>Commanded rotor angle in revolutions (advances in microstep increments).</summary>
    public double CommandedAngleRev { get; private set; }

    /// <summary>Actual rotor angle in revolutions, read from the physics state.</summary>
    public double RotorAngleRev => _mcu.Simulator.GetJointPos(_jointName) / (2.0 * Math.PI);

    public double RotorVelocityRevPerSec => _mcu.Simulator.GetJointVel(_jointName) / (2.0 * Math.PI);

    /// <summary>Total microsteps commanded since power-on (the host's step counter).</summary>
    public long CommandedMicrosteps { get; private set; }

    /// <summary>Steps the rotor failed to follow (lagging by whole full steps).</summary>
    public int MissedSteps { get; private set; }

    /// <summary>True when steps were missed and the rotor is no longer turning
    /// (a stalled rotor only jiggles — allow some residual oscillation).</summary>
    public bool IsStalled => MissedSteps > 0 && Math.Abs(RotorVelocityRevPerSec) < 0.5;

    /// <summary>Whether the channel is energized (a disabled rotor is dragged by its
    /// load — that is back-driving, not missed steps).</summary>
    public bool IsEnabled => _enabled;

    public double CommandedVelocityRevPerSec { get; private set; }

    /// <summary>Current velocity target; the channel ramps toward it at AccelerationRevPerSec2.</summary>
    public double TargetVelocityRevPerSec { get; private set; }

    /// <summary>Acceleration limit, revolutions/s² (the host planner's ramp).</summary>
    public double AccelerationRevPerSec2 { get; set; } = 40;

    public void Enable(bool on = true) => _enabled = on;

    /// <summary>Command the shaft velocity in revolutions per second (host-side planner interface).
    /// The channel ramps at its acceleration limit — like real firmware; a genuine
    /// instantaneous step to full speed makes a stepper slip, exactly as in hardware.</summary>
    public void SetVelocityRevPerSec(double revPerSec) => TargetVelocityRevPerSec = revPerSec;

    internal void Tick()
    {
        var sim = _mcu.Simulator;

        // 1. Ramp the commanded velocity toward the target, then advance the commanded
        //    angle in whole microsteps (quantized, as a real driver does).
        if (_enabled)
        {
            var delta = TargetVelocityRevPerSec - CommandedVelocityRevPerSec;
            var maxDelta = AccelerationRevPerSec2 * _mcu.ControlPeriod;
            CommandedVelocityRevPerSec += Math.Clamp(delta, -maxDelta, maxDelta);
        }

        if (_enabled && CommandedVelocityRevPerSec != 0)
        {
            _microstepAccumulator += CommandedVelocityRevPerSec * _mcu.ControlPeriod
                                     * _spec.Microsteps * _spec.StepsPerRevolution;
            var whole = (long)Math.Floor(_microstepAccumulator);
            if (whole != 0)
            {
                _microstepAccumulator -= whole;
                CommandedMicrosteps += whole;
                CommandedAngleRev += whole / (_spec.Microsteps * _spec.StepsPerRevolution);
            }
        }

        // 2. Magnetic torque: a sinusoid of the electrical angle error between the
        //    commanded phase and the rotor, minus winding (back-EMF) losses.
        double torque = 0;
        if (_enabled)
        {
            var errorRev = CommandedAngleRev - RotorAngleRev;
            var electricalError = 2.0 * Math.PI * _spec.PolePairsPerRevolution * errorRev;
            torque = _spec.HoldingTorqueNm * Math.Sin(electricalError)
                     - _spec.BackEmfDamping * 2.0 * Math.PI * RotorVelocityRevPerSec;
        }
        sim.SetCtrlByIndex(_actuatorId, torque);

        // 3. Missed-step bookkeeping: lag beyond 1.5 full steps means lost steps.
        //    Only meaningful while energized — a disabled rotor is back-driven by
        //    its load (belts, gravity) and can't "miss" anything.
        if (_enabled)
        {
            var lagSteps = Math.Abs(CommandedAngleRev - RotorAngleRev) / _spec.FullStepRev;
            var missed = Math.Max(0, (int)Math.Round(lagSteps) - 1);
            if (missed > MissedSteps)
                MissedSteps = missed;
        }
    }
}

/// <summary>A debounced digital input sampled from a joint coordinate (a limit switch).</summary>
public sealed class EndstopChannel
{
    private readonly VirtualMcu _mcu;
    private readonly string _jointName;
    private readonly double _triggerPosition;
    private readonly int _debounceTicks;
    private int _consecutive;

    internal EndstopChannel(VirtualMcu mcu, string jointName, double triggerPosition, int debounceTicks)
    {
        _mcu = mcu;
        _jointName = jointName;
        _triggerPosition = triggerPosition;
        _debounceTicks = debounceTicks;
    }

    public string JointName => _jointName;
    public double TriggerPosition => _triggerPosition;

    /// <summary>Debounced switch state.</summary>
    public bool Pressed { get; private set; }

    internal void Tick()
    {
        var position = _mcu.Simulator.GetJointPos(_jointName);
        var raw = position <= _triggerPosition;
        _consecutive = raw ? _consecutive + 1 : 0;
        Pressed = _consecutive >= _debounceTicks;
    }
}
