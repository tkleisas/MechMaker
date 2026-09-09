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
        int microsteps = (int)DefaultMicrosteps, double holdingTorqueScale = 1.0)
    {
        var compiler = new MjcfCompiler(catalog);
        var mjcf = compiler.Compile(machine).ToString();
        var simulator = Simulator.FromMjcf(mjcf);
        var mcu = new VirtualMcu(simulator, simulator.Timestep);
        var steppersByInstance = new Dictionary<string, StepperChannel>(StringComparer.Ordinal);

        // One stepper channel per wired stepper motor, specs from the catalog part.
        var partsById = machine.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var wiredMotors = machine.Wiring
            .Where(w => w.Signal == "step")
            .Select(w => w.Component)
            .Distinct();

        foreach (var instanceId in wiredMotors)
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

        return new MachineSimulation(simulator, mcu, steppersByInstance);
    }

    /// <summary>The MCU stepper channel driving a motor instance.</summary>
    public StepperChannel Stepper(string instanceId) =>
        _steppersByInstance.TryGetValue(instanceId, out var channel)
            ? channel
            : throw new KeyNotFoundException($"No stepper channel for motor '{instanceId}' (is it wired?).");

    private readonly Dictionary<string, StepperChannel> _steppersByInstance;

    private MachineSimulation(Simulator simulator, VirtualMcu mcu,
        Dictionary<string, StepperChannel> steppersByInstance)
    {
        Simulator = simulator;
        Mcu = mcu;
        _steppersByInstance = steppersByInstance;
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
