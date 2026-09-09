using System.Xml.Linq;
using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Model;
using MechMaker.Engine;
using Xunit;

namespace MechMaker.Engine.Tests;

/// <summary>Full pipeline: machine.json -> MJCF -> native MuJoCo -> dynamics.</summary>
public class SimulationTests
{
    private static string CompileExample()
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json")));
        var compiler = new MjcfCompiler(TestRepo.Catalog());
        var doc = compiler.Compile(machine);
        return doc.ToString();
    }

    [Fact]
    public void Compiled_machine_loads_into_mujoco()
    {
        using var sim = Simulator.FromMjcf(CompileExample());

        var report = sim.BuildReport();
        Assert.Equal(0.000125, sim.Timestep, 6);
        Assert.Equal(3, report.Joints.Count);
        Assert.Contains(report.Joints, j => j.Name == "j_carriage" && j.Kind == "slide");
        Assert.Contains(report.Joints, j => j.Name == "j_motor_left_rotor" && j.Kind == "hinge");
        Assert.Equal(["a_motor_left", "a_motor_right"], report.Actuators);

        // 12 part bodies + ground + belt strap.
        Assert.Equal(14, report.BodyCount);
        Assert.InRange(report.TotalMassKg, 1.5, 2.5);
    }

    [Fact]
    public void Torque_spins_rotors_through_the_belt_coupler()
    {
        using var sim = Simulator.FromMjcf(CompileExample());
        sim.SetCtrl("a_motor_left", 0.02);
        sim.SetCtrl("a_motor_right", -0.02);
        sim.RunFor(1.0);

        var left = sim.GetJointPos("j_motor_left_rotor");
        var right = sim.GetJointPos("j_motor_right_rotor");

        // Steady state: total drag = 2 rotors x bearing damping 0.01; raw torque test,
        // no motor model involved -> w = 0.04 / 0.02 = 2.0.
        Assert.True(Math.Abs(left) > 0.5, $"left rotor barely moved: {left}");
        Assert.Equal(2.0, sim.GetJointVel("j_motor_left_rotor"), 1);
        // The 1:1 belt coupler keeps the rotors mirrored.
        Assert.True(Math.Abs(left + right) < 0.01 * Math.Abs(left), $"coupler drift too large: {left} vs {right}");
    }

    [Fact]
    public void Carriage_is_belt_coupled_to_the_rotors()
    {
        using var sim = Simulator.FromMjcf(CompileExample());
        sim.SetCtrl("a_motor_left", 0.02);
        sim.SetCtrl("a_motor_right", -0.02);
        sim.RunFor(0.5);

        // The clamp coupler: q_carriage = -pitch_radius * q_rotor (meters per radian),
        // soft by ~5% — that slack is the (deliberately) elastic belt constraint.
        var left = sim.GetJointPos("j_motor_left_rotor");
        var carriage = sim.GetJointPos("j_carriage");
        Assert.True(Math.Abs(left) > 0.3, $"rotor barely moved: {left}");
        Assert.True(Math.Abs(carriage + 0.006366 * left) < 0.02 * Math.Abs(carriage),
            $"carriage not belt-coupled: {carriage} vs {-0.006366 * left}");
    }

    [Fact]
    public void Identical_runs_are_bitwise_deterministic()
    {
        string mjcf = CompileExample();

        double[] run()
        {
            using var sim = Simulator.FromMjcf(mjcf);
            sim.SetCtrl("a_motor_left", 0.05);
            sim.RunFor(0.5);
            return sim.QposSnapshot();
        }

        Assert.Equal(run(), run());
    }
}

/// <summary>M2: machine-level closed loop — virtual MCU + stepper model + belt-driven carriage.</summary>
public class MachineSimulationTests
{
    private static MachineSimulation NewMachine()
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json")));
        return MachineSimulation.FromMachine(machine, TestRepo.Catalog());
    }

    [Fact]
    public void Carriage_rides_the_belt()
    {
        using var machine = NewMachine();
        var left = machine.Stepper("motor_left");
        left.Enable();
        left.SetVelocityRevPerSec(1.0);
        machine.RunFor(1.0);

        // 20T GT2 = 2 mm pitch -> 40 mm of belt per pulley revolution; the 40 rev/s²
        // ramp costs ~0.5 mm of travel.
        var carriage = machine.Simulator.GetJointPos("j_carriage");
        Assert.InRange(Math.Abs(carriage), 0.036, 0.043);

        // Rotor followed the command: ~1 revolution (ramp shaving ~1.25%), zero missed steps.
        Assert.InRange(Math.Abs(left.RotorAngleRev), 0.96, 1.0);
        Assert.Equal(0, left.MissedSteps);
        Assert.False(left.IsStalled);
    }

    [Fact]
    public void Commanding_beyond_the_torque_speed_limit_stalls_the_motor()
    {
        using var machine = NewMachine();
        var left = machine.Stepper("motor_left");
        left.AccelerationRevPerSec2 = 1e9; // no ramp: commanded phase runs away
        left.Enable();
        // 20 rev/s needs ~7.5 N·m of drag compensation; holding torque is 0.4.
        left.SetVelocityRevPerSec(20.0);
        machine.RunFor(0.2);

        Assert.True(left.MissedSteps > 50, $"expected slip, missed={left.MissedSteps}");
        Assert.True(left.IsStalled);
    }

    [Fact]
    public void Endstop_fires_when_the_carriage_reaches_the_limit()
    {
        using var machine = NewMachine();
        var endstop = machine.Mcu.AddEndstop("j_carriage", triggerPosition: -0.020);
        var left = machine.Stepper("motor_left");
        left.Enable();
        left.SetVelocityRevPerSec(1.0); // positive rotor -> carriage moves toward -X (clamp sign)

        machine.RunFor(0.35); // ~13.5 mm traveled (ramp included)
        Assert.False(endstop.Pressed);

        machine.RunFor(0.2); // past the 20 mm trigger (~t=0.51 s)
        Assert.True(endstop.Pressed);
    }

    [Fact]
    public void Mcu_commanded_angle_matches_the_acceleration_profile()
    {
        using var machine = NewMachine();
        var left = machine.Stepper("motor_left");
        left.Enable();
        left.SetVelocityRevPerSec(1.0);
        machine.RunFor(0.5);

        // 40 rev/s^2 ramp to 1 rev/s takes 25 ms; travel = 0.5 - (ramp deficit 0.0125) = 0.4875 rev.
        Assert.InRange(left.CommandedAngleRev, 0.4855, 0.4895);
        Assert.Equal(Math.Round(left.CommandedAngleRev * 3200), (double)left.CommandedMicrosteps);
    }

    [Fact]
    public void Machine_simulations_are_deterministic()
    {
        double[] Run()
        {
            using var machine = NewMachine();
            var left = machine.Stepper("motor_left");
            left.Enable();
            left.SetVelocityRevPerSec(1.0);
            machine.RunFor(0.5);
            return machine.Simulator.QposSnapshot();
        }

        Assert.Equal(Run(), Run());
    }
}

public static class TestRepo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalog")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    public static PartCatalog Catalog() => PartCatalog.LoadFromDirectory(Path.Combine(Root(), "catalog"));
}
