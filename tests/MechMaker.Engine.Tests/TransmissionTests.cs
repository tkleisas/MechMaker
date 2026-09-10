using MechMaker.Core;
using MechMaker.Core.Model;

namespace MechMaker.Engine.Tests;

/// <summary>
/// M5 catalog growth: leadscrews (rotation→translation at lead/(2π)) and meshed
/// spur gears (hinge→hinge at the pitch-radius ratio) — kinematics verified on the
/// live physics through the same closed loop as the belt example.
/// </summary>
public class TransmissionTests
{
    private static MachineSimulation FromExample(string name)
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", name)));
        return MachineSimulation.FromMachine(machine, TestRepo.Catalog());
    }

    [Fact]
    public void A_leadscrew_nut_travels_the_lead_per_revolution()
    {
        using var machine = FromExample("leadscrew_z_axis.json");
        var motor = machine.Stepper("z_motor");
        motor.Enable();
        motor.SetVelocityRevPerSec(1.0);
        machine.RunFor(1.05); // 40 rev/s² ramp to 1 rev/s, ~1 rev total

        // T8-8: 8 mm of nut travel per screw revolution.
        var nut = machine.Simulator.GetJointPos("j_z_nut");
        Assert.InRange(Math.Abs(nut), 0.0074, 0.0086);
        Assert.Equal(0, motor.MissedSteps);
    }

    [Fact]
    public void Meshed_gears_follow_the_pitch_radius_ratio()
    {
        using var machine = FromExample("gear_train.json");
        // Drive the 20T side; the 40T side turns at half speed.
        var motorA = machine.Stepper("motor_a");
        motorA.Enable();
        motorA.SetVelocityRevPerSec(1.0);
        machine.RunFor(1.05);

        var rotorA = machine.Simulator.GetJointPos("j_motor_a_rotor");
        var rotorB = machine.Simulator.GetJointPos("j_motor_b_rotor");
        Assert.True(Math.Abs(rotorA) > 0.9, $"motor_a should turn ~1 rev, got {rotorA}");
        var ratio = Math.Abs(rotorB / rotorA);
        Assert.InRange(ratio, 0.45, 0.55); // 20T : 40T = 1 : 2
    }

    [Fact]
    public void Leadscrew_and_gear_examples_validate_clean()
    {
        using var leadscrew = new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"),
            Path.Combine(TestRepo.Root(), "examples", "leadscrew_z_axis.json"));
        Assert.Equal("OK", leadscrew.Validate().ToString());
        Assert.NotEmpty(leadscrew.EditScene);

        using var gears = new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"),
            Path.Combine(TestRepo.Root(), "examples", "gear_train.json"));
        Assert.Equal("OK", gears.Validate().ToString());
        Assert.NotEmpty(gears.EditScene);
    }
}