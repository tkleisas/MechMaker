using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Model;

namespace MechMaker.Engine;

/// <summary>
/// A runnable machine: compiles the machine definition, owns the physics simulator
/// and the virtual MCU, and advances them in lockstep: MCU tick, then physics step.
/// The MCU runs at the physics rate (8 kHz, like a real board's step generator) —
/// necessary because the stepper's magnetic spring is stiff relative to coarser
/// zero-order-hold updates.
/// This is the headless engine API the UI, CLI, MCP server, and CI all drive.
/// </summary>
public sealed class MachineSimulation : IDisposable
{
    public const double DefaultMicrosteps = 16;

    public Simulator Simulator { get; }
    public VirtualMcu Mcu { get; }

    public static MachineSimulation FromMachine(MachineDefinition machine, PartCatalog catalog,
        int microsteps = (int)DefaultMicrosteps, double holdingTorqueScale = 1.0, double ambientC = 25)
    {
        var compiler = new MjcfCompiler(catalog);
        var mjcf = compiler.Compile(machine).ToString();
        var simulator = Simulator.FromMjcf(mjcf);
        var mcu = new VirtualMcu(simulator, simulator.Timestep);
        var steppersByInstance = new Dictionary<string, StepperChannel>(StringComparer.Ordinal);
        var servosByInstance = new Dictionary<string, ServoChannel>(StringComparer.Ordinal);
        var fansByInstance = new Dictionary<string, FanChannel>(StringComparer.Ordinal);
        var heatersByInstance = new Dictionary<string, ThermalChannel>(StringComparer.Ordinal);

        var partsById = machine.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var channelsOf = machine.Wiring
            .GroupBy(w => w.Component, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Signal).ToHashSet(), StringComparer.Ordinal);

        // One stepper channel per wired 'step' signal, specs from the catalog part.
        foreach (var instanceId in SignalsOf("step"))
        {
            if (!partsById.TryGetValue(instanceId, out var instance) ||
                !catalog.TryGet(instance.Part, out var definition) ||
                definition?.Motor is not { Kind: MotorKind.Stepper } motor)
                continue;

            steppersByInstance[instanceId] = mcu.AddStepper($"a_{instanceId}", new StepperSpec
            {
                StepsPerRevolution = 360.0 / motor.StepAngleDeg,
                Microsteps = microsteps,
                HoldingTorqueNm = motor.HoldingTorqueNm * holdingTorqueScale
            });
        }

        // Servos and fans: one PWM channel per wired 'pwm' signal, by motor kind.
        foreach (var instanceId in SignalsOf("pwm"))
        {
            if (!partsById.TryGetValue(instanceId, out var instance) ||
                !catalog.TryGet(instance.Part, out var definition) ||
                definition?.Motor is not { } motor)
                continue;

            switch (motor.Kind)
            {
                case MotorKind.Servo:
                    servosByInstance[instanceId] = mcu.AddServo($"a_{instanceId}",
                        definition.Params.GetValueOrDefault("min_angle_deg", -90),
                        definition.Params.GetValueOrDefault("max_angle_deg", 90));
                    break;
                case MotorKind.Dc:
                    var ratedRpm = definition.Params.GetValueOrDefault("rated_rpm", 6000);
                    fansByInstance[instanceId] = mcu.AddFan($"a_{instanceId}", ratedRpm / 60.0);
                    break;
            }
        }

        // Heaters: one lumped thermal channel per wired 'heater' signal.
        foreach (var instanceId in SignalsOf("heater"))
        {
            if (!partsById.TryGetValue(instanceId, out var instance) ||
                !catalog.TryGet(instance.Part, out var definition))
                continue;

            heatersByInstance[instanceId] = mcu.AddHeater(instanceId,
                definition.Params.GetValueOrDefault("heater_power_w", 25),
                definition.Params.GetValueOrDefault("thermal_mass_j_per_k", 15),
                definition.Params.GetValueOrDefault("cooling_w_per_k", 0.4),
                ambientC);
        }

        IEnumerable<string> SignalsOf(string signal) =>
            machine.Wiring.Where(w => w.Signal == signal)
                .Select(w => w.Component)
                .Distinct();

        return new MachineSimulation(simulator, mcu, steppersByInstance, servosByInstance,
            fansByInstance, heatersByInstance);
    }

    /// <summary>The MCU stepper channel driving a motor instance.</summary>
    public StepperChannel Stepper(string instanceId) =>
        _steppersByInstance.TryGetValue(instanceId, out var channel)
            ? channel
            : throw new KeyNotFoundException($"No stepper channel for motor '{instanceId}' (is it wired?).");

    private readonly Dictionary<string, StepperChannel> _steppersByInstance;
    private readonly Dictionary<string, ServoChannel> _servosByInstance;
    private readonly Dictionary<string, FanChannel> _fansByInstance;
    private readonly Dictionary<string, ThermalChannel> _heatersByInstance;

    /// <summary>The servo channel for a wired 'pwm' servo instance.</summary>
    public ServoChannel Servo(string instanceId) =>
        _servosByInstance.TryGetValue(instanceId, out var channel)
            ? channel
            : throw new KeyNotFoundException($"No servo channel for '{instanceId}' (is it wired with signal 'pwm'?");

    /// <summary>The fan/DC channel for a wired 'pwm' DC instance.</summary>
    public FanChannel Fan(string instanceId) =>
        _fansByInstance.TryGetValue(instanceId, out var channel)
            ? channel
            : throw new KeyNotFoundException($"No fan channel for '{instanceId}' (is it wired with signal 'pwm'?");

    /// <summary>The thermal channel for a wired 'heater' instance.</summary>
    public ThermalChannel Heater(string instanceId) =>
        _heatersByInstance.TryGetValue(instanceId, out var channel)
            ? channel
            : throw new KeyNotFoundException($"No heater channel for '{instanceId}' (is it wired with signal 'heater'?");

    public IReadOnlyDictionary<string, StepperChannel> SteppersByInstance => _steppersByInstance;
    public IReadOnlyDictionary<string, ServoChannel> ServosByInstance => _servosByInstance;
    public IReadOnlyDictionary<string, FanChannel> FansByInstance => _fansByInstance;
    public IReadOnlyDictionary<string, ThermalChannel> HeatersByInstance => _heatersByInstance;

    private MachineSimulation(Simulator simulator, VirtualMcu mcu,
        Dictionary<string, StepperChannel> steppersByInstance,
        Dictionary<string, ServoChannel> servosByInstance,
        Dictionary<string, FanChannel> fansByInstance,
        Dictionary<string, ThermalChannel> heatersByInstance)
    {
        Simulator = simulator;
        Mcu = mcu;
        _steppersByInstance = steppersByInstance;
        _servosByInstance = servosByInstance;
        _fansByInstance = fansByInstance;
        _heatersByInstance = heatersByInstance;
    }

    /// <summary>One control tick: MCU computes torques/endstops, then physics integrates.</summary>
    public void Step()
    {
        Mcu.Tick();
        Simulator.Step();
    }

    public void RunFor(double seconds)
    {
        var steps = (long)Math.Round(seconds / Simulator.Timestep);
        for (long i = 0; i < steps; i++)
            Step();
    }

    public void Dispose() => Simulator.Dispose();
}
