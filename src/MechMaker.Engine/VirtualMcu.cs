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
    private readonly List<ServoChannel> _servos = [];
    private readonly List<FanChannel> _fans = [];
    private readonly List<ThermalChannel> _heaters = [];
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

    /// <summary>The MCU's clock in ticks — one tick per physics step (8 kHz for the
    /// compiled models). This is the clock Klipper's host schedules steps against.</summary>
    public long Clock { get; private set; }

    /// <summary>Raised after every control tick (endstop sampling, protocol hooks).</summary>
    public event Action? PostTick;

    public IReadOnlyList<StepperChannel> Steppers => _steppers;
    public IReadOnlyList<EndstopChannel> Endstops => _endstops;

    public StepperChannel AddStepper(string actuatorName, StepperSpec? spec = null)
    {
        var channel = new StepperChannel(this, actuatorName, spec ?? new StepperSpec());
        _steppers.Add(channel);
        return channel;
    }

/// <summary>A hobby/position servo: the position actuator's target. Hinge servos take
    /// degree ranges; slide servos take metre positions.</summary>
    public ServoChannel AddServo(string actuatorName, double minAngleDeg, double maxAngleDeg,
        bool isSlide = false)
    {
        var channel = isSlide
            ? new ServoChannel(this, actuatorName, minAngleDeg, maxAngleDeg, isSlide: true)
            : new ServoChannel(this, actuatorName, minAngleDeg * Math.PI / 180.0, maxAngleDeg * Math.PI / 180.0, isSlide: false);
        _servos.Add(channel);
        return channel;
    }

    /// <summary>A DC fan: velocity actuator, duty scales the rated speed.</summary>
    public FanChannel AddFan(string actuatorName, double ratedRevPerSec)
    {
        var channel = new FanChannel(this, actuatorName, ratedRevPerSec);
        _fans.Add(channel);
        return channel;
    }

    /// <summary>A heater (hotend, bed): lumped thermal model integrated per MCU tick.</summary>
    public ThermalChannel AddHeater(string instanceId, double powerWatts, double thermalMassJPerK,
        double coolingWPerK, double ambientC)
    {
        var channel = new ThermalChannel(instanceId, powerWatts, thermalMassJPerK, coolingWPerK,
            ambientC, ControlPeriod);
        _heaters.Add(channel);
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
        Clock++;
        foreach (var stepper in _steppers)
            stepper.Tick();
        foreach (var servo in _servos)
            servo.Tick();
        foreach (var fan in _fans)
            fan.Tick();
        foreach (var heater in _heaters)
            heater.Tick();
        foreach (var endstop in _endstops)
            endstop.Tick();
        PostTick?.Invoke();
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

    // ---------- Klipper step queue ----------
    // The protocol-level host interface: queue_step schedules full steps (step pin
    // pulses) at precise clock times; the magnetic model turns them into torque
    // exactly as a driver+motor pair would. While a queue exists it replaces the
    // velocity-ramp path for this channel.

    private sealed class StepRun
    {
        public long NextStepClock;
        public long Interval;
        public long Add;
        public int Remaining;
        public bool Dir;
    }

    private readonly List<StepRun> _stepQueue = [];
    private long _nextStepClock;
    private bool _queueMode;
    private bool _pendingDir = true;

    /// <summary>Signed full-step position (dir=1 steps minus dir=0 steps) — the
    /// counter behind klipper's stepper_get_position.</summary>
    public long SignedStepPosition { get; private set; }

    /// <summary>Full steps currently scheduled in the klipper move queue.</summary>
    public int QueuedSteps => _stepQueue.Sum(r => r.Remaining);

    /// <summary>Step runs completed since power-on (the MCU's move-queue accounting).</summary>
    public int CompletedStepRuns { get; private set; }

    public void SetNextStepDir(bool dir) => _pendingDir = dir;

    /// <summary>The next queue_step's first step becomes relative to the given clock
    /// (the host sends this once at the start of a session).</summary>
    public void ResetStepClock(long clock)
    {
        _nextStepClock = clock;
        _queueMode = true;
    }

    /// <summary>
    /// Appends <paramref name="count"/> steps spaced <paramref name="interval"/> ticks
    /// apart (adjusted by <paramref name="add"/> after each step), the first one
    /// <paramref name="interval"/> ticks after the last scheduled step — klipper's
    /// queue_step semantics.
    /// </summary>
    public void QueueStep(long interval, int count, long add)
    {
        if (!_queueMode)
            throw new InvalidOperationException("queue_step before reset_step_clock.");
        _stepQueue.Add(new StepRun
        {
            NextStepClock = _nextStepClock + interval,
            Interval = interval,
            Add = add,
            Remaining = count,
                Dir = _pendingDir
            });
    }

    /// <summary>Clears the queued moves immediately (endstop trigger halting).</summary>
    public void ClearStepQueue()
    {
        _stepQueue.Clear();
        // The timing reference stays at the last executed step, as klipper does.
    }

    private void StepOnce(bool dir)
    {
        var fullStep = _spec.FullStepRev;
        CommandedAngleRev += dir ? fullStep : -fullStep;
        SignedStepPosition += dir ? 1 : -1;
    }

    internal void Tick()
    {
        var sim = _mcu.Simulator;
        var clock = _mcu.Clock;

        // 0. Klipper step queue: execute every step scheduled at or before this tick.
        if (_stepQueue.Count > 0)
        {
            for (var i = _stepQueue.Count - 1; i >= 0; i--)
            {
                var run = _stepQueue[i];
                while (run.Remaining > 0 && clock >= run.NextStepClock)
                {
                    StepOnce(run.Dir);
                    run.Interval += run.Add;
                    run.NextStepClock += run.Interval;
                    run.Remaining--;
                    _nextStepClock = run.NextStepClock;
                }
                if (run.Remaining <= 0)
                {
                    _stepQueue.RemoveAt(i);
                    CompletedStepRuns++;
                }
            }

            // Magnetic torque as usual (commanded phase advances in full-step
            // jumps — classic full-step drive; the rotor interpolates under load).
            ApplyTorque();
            BookkeepMissedSteps();
            return;
        }

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

        ApplyTorque();
        BookkeepMissedSteps();
    }

    private void ApplyTorque()
    {
        // Magnetic torque: a sinusoid of the electrical angle error between the
        // commanded phase and the rotor, minus winding (back-EMF) losses.
        double torque = 0;
        if (_enabled)
        {
            var errorRev = CommandedAngleRev - RotorAngleRev;
            var electricalError = 2.0 * Math.PI * _spec.PolePairsPerRevolution * errorRev;
            torque = _spec.HoldingTorqueNm * Math.Sin(electricalError)
                     - _spec.BackEmfDamping * 2.0 * Math.PI * RotorVelocityRevPerSec;
        }
        _mcu.Simulator.SetCtrlByIndex(_actuatorId, torque);
    }

    private void BookkeepMissedSteps()
    {
        // Missed-step bookkeeping: lag beyond 1.5 full steps means lost steps.
        // Only meaningful while energized — a disabled rotor is back-driven by
        // its load (belts, gravity) and can't "miss" anything.
        if (!_enabled)
            return;
        var lagSteps = Math.Abs(CommandedAngleRev - RotorAngleRev) / _spec.FullStepRev;
        var missed = Math.Max(0, (int)Math.Round(lagSteps) - 1);
        if (missed > MissedSteps)
            MissedSteps = missed;
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

/// <summary>A PWM-driven channel (klipper's set_pwm_out): duty 0..1 maps to the
/// channel's own actuation (servo angle sweep, fan speed).</summary>
public interface IPwmChannel
{
    void ApplyDuty(double duty);
}

/// <summary>A position servo channel: the position actuator's target, written to ctrl
    /// every tick. ctrl is stored in SI joint units internally (radians hinge, metres
    /// slide); the accessors convert for hinges.</summary>
    public sealed class ServoChannel : IPwmChannel
{
    private readonly VirtualMcu _mcu;
    private readonly int _actuatorId;
    private readonly string _jointName;
    private readonly double _min;   // SI: radians (hinge) or metres (slide)
    private readonly double _max;
    private readonly bool _isSlide;

    internal ServoChannel(VirtualMcu mcu, string actuatorName, double min, double max, bool isSlide)
    {
        _mcu = mcu;
        _actuatorId = mcu.Simulator.ActuatorId(actuatorName);
        _jointName = mcu.Simulator.JointForActuator(_actuatorId);
        _min = min;
        _max = max;
        _isSlide = isSlide;
    }

    /// <summary>The joint this servo positions (e.g. j_servo_arm).</summary>
    public string JointName => _jointName;

    public double MinAngleDeg => _isSlide
        ? throw new InvalidOperationException("Slide servo: use MinPositionM")
        : _min * 180.0 / Math.PI;
    public double MaxAngleDeg => _isSlide
        ? throw new InvalidOperationException("Slide servo: use MaxPositionM")
        : _max * 180.0 / Math.PI;

    /// <summary>Slide-servo travel range in metres (androidtester's press plunger).</summary>
    public double MinPositionM => _isSlide ? _min : throw new InvalidOperationException("MinPositionM is for slide servos");
    public double MaxPositionM => _isSlide ? _max : throw new InvalidOperationException("MaxPositionM is for slide servos");

    public bool IsSlide => _isSlide;

    /// <summary>Commanded target in joint units (degrees hinge, metres slide).</summary>
    public double TargetAngleDeg { get; private set; }

    /// <summary>Actual joint position in joint units (degrees hinge, metres slide).</summary>
    public double AngleDeg => _isSlide
        ? _mcu.Simulator.GetJointPos(_jointName)
        : _mcu.Simulator.GetJointPos(_jointName) * 180.0 / Math.PI;

    /// <summary>Hinge servos: command the target angle in degrees.</summary>
    public void SetTargetAngleDeg(double degrees)
    {
        if (_isSlide)
            throw new InvalidOperationException("Slide servos take SetTargetPositionM.");
        TargetAngleDeg = Math.Clamp(degrees, _min * 180.0 / Math.PI, _max * 180.0 / Math.PI);
    }

    /// <summary>Slide servos: command the joint position in metres.</summary>
    public void SetTargetPositionM(double metres)
    {
        if (!_isSlide)
            throw new InvalidOperationException("SetTargetPositionM is for slide servos.");
        TargetAngleDeg = Math.Clamp(metres, _min, _max);
    }

    /// <summary>Duty 0..1 sweeps the position range (0 → min, 1 → max).</summary>
    public void ApplyDuty(double duty)
    {
        var d = Math.Clamp(duty, 0.0, 1.0);
        var target = _min + d * (_max - _min);
        TargetAngleDeg = _isSlide ? target : target * 180.0 / Math.PI;
    }

    internal void Tick() =>
        _mcu.Simulator.SetCtrlByIndex(_actuatorId,
            _isSlide ? TargetAngleDeg : TargetAngleDeg * Math.PI / 180.0);
}

/// <summary>A DC fan (or any speed-controlled DC motor): duty scales the rated speed.</summary>
public sealed class FanChannel : IPwmChannel
{
    private readonly VirtualMcu _mcu;
    private readonly int _actuatorId;
    private readonly string _jointName;
    private readonly double _ratedRevPerSec;

    internal FanChannel(VirtualMcu mcu, string actuatorName, double ratedRevPerSec)
    {
        _mcu = mcu;
        _actuatorId = mcu.Simulator.ActuatorId(actuatorName);
        _jointName = mcu.Simulator.JointForActuator(_actuatorId);
        _ratedRevPerSec = ratedRevPerSec;
    }

    public string JointName => _jointName;

    /// <summary>Drive duty 0..1; the actuator targets duty × rated speed.</summary>
    public double Duty { get; private set; }

    public double RevPerSec => _mcu.Simulator.GetJointVel(_jointName) / (2.0 * Math.PI);

    public void SetDuty(double duty)
    {
        Duty = Math.Clamp(duty, 0.0, 1.0);
    }

    public void ApplyDuty(double duty) => SetDuty(duty);

    internal void Tick() =>
        _mcu.Simulator.SetCtrlByIndex(_actuatorId, Duty * _ratedRevPerSec * 2.0 * Math.PI);
}

/// <summary>
/// A lumped thermal channel (hotend, heated bed): heater power against Newton
/// cooling to ambient. Integrated per MCU tick at the physics timestep — the
/// engine's first non-mechanical state, deterministic like everything else.
/// Params from the catalog part: heater_power_w, thermal_mass_j_per_k, cooling_w_per_k.
/// </summary>
public sealed class ThermalChannel
{
    private readonly double _powerWatts;
    private readonly double _thermalMassJPerK;
    private readonly double _coolingWPerK;
    private readonly double _ambientC;
    private readonly double _dt;

    internal ThermalChannel(string instanceId, double powerWatts, double thermalMassJPerK,
        double coolingWPerK, double ambientC, double controlPeriod)
    {
        InstanceId = instanceId;
        _powerWatts = powerWatts;
        _thermalMassJPerK = thermalMassJPerK;
        _coolingWPerK = coolingWPerK;
        _ambientC = ambientC;
        _dt = controlPeriod;
        TemperatureC = ambientC;
    }

    public string InstanceId { get; }

    /// <summary>Lump temperature in °C.</summary>
    public double TemperatureC { get; private set; }

    /// <summary>Heater duty 0..1.</summary>
    public double Duty { get; private set; }

    public void SetDuty(double duty) => Duty = Math.Clamp(duty, 0.0, 1.0);

    /// <summary>Steady-state temperature at the current duty (for validation tooling).</summary>
    public double SteadyStateC => _ambientC + Duty * _powerWatts / _coolingWPerK;

    internal void Tick()
    {
        var netWatts = Duty * _powerWatts - _coolingWPerK * (TemperatureC - _ambientC);
        TemperatureC += netWatts / _thermalMassJPerK * _dt;
    }
}
