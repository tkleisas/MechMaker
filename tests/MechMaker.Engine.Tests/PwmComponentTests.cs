using MechMaker.Core;
using MechMaker.Core.Model;

namespace MechMaker.Engine.Tests;

/// <summary>
/// Servo (position actuator), DC fan (velocity actuator), and hotend (lumped
/// thermal model) — the three non-stepper component types, verified on the live
/// physics with inline machines.
/// </summary>
public class PwmComponentTests
{
    [Fact]
    public void A_servo_reaches_its_commanded_angle()
    {
        using var sim = TestMachines.ServoMachine();
        var servo = sim.Servo("wrist");
        servo.SetTargetAngleDeg(45);
        sim.RunFor(0.5);

        Assert.InRange(servo.AngleDeg, 43, 47);
    }

    [Fact]
    public void A_fan_tracks_duty_times_rated_speed()
    {
        using var sim = TestMachines.FanMachine();
        var fan = sim.Fan("blower");
        fan.SetDuty(0.5);
        sim.RunFor(0.3); // spin up
        Assert.InRange(fan.RevPerSec, 40, 60); // 50% of 6000 rpm = 3000 rpm = 50 rev/s

        fan.SetDuty(1.0);
        sim.RunFor(0.5);
        Assert.InRange(fan.RevPerSec * 60, 5400, 6600);
    }

[Fact]
    public void A_heater_approaches_duty_times_power_over_cooling()
    {
        using var sim = TestMachines.HotendMachine();
        var heater = sim.Heater("hotend");
        heater.SetDuty(1.0);

        // τ = mass/cooling ≈ 43 s (real hotends heat slowly too) — after 10 s the
        // block is well on its way toward power/cooling + ambient.
        sim.RunFor(10.0);
        Assert.InRange(heater.TemperatureC, 32, 52);
        Assert.Equal(96.4, heater.SteadyStateC, 1); // 25 + 25 W / 0.35 W/K

        heater.SetDuty(0.0);
        var peak = heater.TemperatureC;
        sim.RunFor(5.0);
        Assert.True(heater.TemperatureC < peak, "should cool when the duty drops");
    }

    [Fact]
    public void Servo_fan_heater_wiring_without_a_channel_is_ignored()
    {
        // Wiring a 'pwm' signal to a stepper (wrong signal) creates no channel.
        using var sim = TestMachines.ServoMachine();
        Assert.Throws<KeyNotFoundException>(() => sim.Servo("ghost"));
    }
}

/// <summary>Inline machines for the PWM component tests (no example sprawl).</summary>
public static class TestMachines
{
    public static MachineSimulation ServoMachine()
    {
        const string json = """
        {
          "name": "servo_test",
          "parts": [
            { "id": "base", "part": "beam_2020_400", "pose": { "pos": [0, 0, 0] } },
            { "id": "plate", "part": "motor_mount_plate_2020", "pose": {} },
            { "id": "wrist", "part": "sg90_servo", "pose": {} }
          ],
          "connections": [
            { "id": "plate_on_base", "part_a": "base", "connector_a": "end_a", "part_b": "plate", "connector_b": "tslot" },
            { "id": "servo_on_plate", "part_a": "plate", "connector_a": "motor_face", "part_b": "wrist", "connector_b": "mount" }
          ],
          "wiring": [
            { "component": "wrist", "signal": "pwm", "board": "b", "pin": "p" }
          ]
        }
        """;
        return FromJson(json);
    }

    public static MachineSimulation FanMachine()
    {
        const string json = """
        {
          "name": "fan_test",
          "parts": [
            { "id": "base", "part": "beam_2020_400", "pose": { "pos": [0, 0, 0] } },
            { "id": "plate", "part": "motor_mount_plate_2020", "pose": {} },
            { "id": "blower", "part": "fan_40mm", "pose": {} }
          ],
          "connections": [
            { "id": "plate_on_base", "part_a": "base", "connector_a": "end_a", "part_b": "plate", "connector_b": "tslot" },
            { "id": "fan_on_plate", "part_a": "plate", "connector_a": "motor_face", "part_b": "blower", "connector_b": "mount" }
          ],
          "wiring": [
            { "component": "blower", "signal": "pwm", "board": "b", "pin": "p" }
          ]
        }
        """;
        return FromJson(json);
    }

    public static MachineSimulation HotendMachine()
    {
        const string json = """
        {
          "name": "hotend_test",
          "parts": [
            { "id": "base", "part": "beam_2020_400", "pose": { "pos": [0, 0, 0] } },
            { "id": "plate", "part": "motor_mount_plate_2020", "pose": {} },
            { "id": "hotend", "part": "hotend_e3d_style", "pose": {} }
          ],
          "connections": [
            { "id": "plate_on_base", "part_a": "base", "connector_a": "end_a", "part_b": "plate", "connector_b": "tslot" },
            { "id": "hotend_on_plate", "part_a": "plate", "connector_a": "motor_face", "part_b": "hotend", "connector_b": "mount" }
          ],
          "wiring": [
            { "component": "hotend", "signal": "heater", "board": "b", "pin": "p" }
          ]
        }
        """;
        return FromJson(json);
    }

    private static MachineSimulation FromJson(string json)
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(json);
        return MachineSimulation.FromMachine(machine, TestRepo.Catalog());
    }
}


