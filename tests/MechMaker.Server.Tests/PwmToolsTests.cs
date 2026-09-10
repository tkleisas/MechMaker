namespace MechMaker.Server.Tests;

/// <summary>Servo/fan/heater PWM tools through the workspace.</summary>
public class PwmToolsTests : IDisposable
{
    private readonly McpWorkspace _w = TestRepo.ExampleWorkspace();

    public void Dispose() => _w.Dispose();

    /// <summary>Beam + mount plate so every component is attached to the tree.</summary>
    private void BuildMachine(params (string id, string catalogId)[] components)
    {
        _w.NewMachine("pwm");
        _w.AddPart("beam_2020_400", "base", 0, 0, 0);
        foreach (var (id, catalogId) in components)
        {
            _w.AddPart("motor_mount_plate_2020", $"{id}_plate");
            _w.AddPart(catalogId, id);
        }
        _w.AddBoard("main");
        var baseFaces = new[] { "end_b", "top_a", "top_b" };
        for (var i = 0; i < components.Length; i++)
        {
            _w.AddConnection("base", baseFaces[i], $"{components[i].id}_plate", "tslot");
            _w.AddConnection($"{components[i].id}_plate", "motor_face", components[i].id, "mount");
        }
    }

    [Fact]
    public void Servo_fan_and_heater_drive_their_channels()
    {
        BuildMachine(("wrist", "sg90_servo"), ("blower", "fan_40mm"), ("hotend", "hotend_e3d_style"));
        _w.Wire("wrist", "pwm", "main", "P1_1");
        _w.Wire("blower", "pwm", "main", "P2_1");
        _w.Wire("hotend", "heater", "main", "P3_1");
        _w.StartRun();

        Assert.Contains("target 45", _w.SetServoAngle("wrist", 45));
        Assert.Contains("duty 0.5", _w.SetFanDuty("blower", 0.5));
        Assert.Contains("duty 1", _w.SetHeaterDuty("hotend", 1.0));

        _w.RunFor(1.0);
        var status = _w.GetRunStatus();
        Assert.Contains("servo wrist", status);
        Assert.Contains("fan blower", status);
        Assert.Contains("heater hotend", status);
        Assert.Contains("°C", status);
    }

    [Fact]
    public void Klipper_pwm_pins_are_enumerated_and_set_pwm_out_drives_them()
    {
        BuildMachine(("blower", "fan_40mm"));
        _w.Wire("blower", "pwm", "main", "P1_1");
        var connect = _w.KlipperConnect();
        Assert.Contains("blower", connect);

        // config_pwm_out oid=0 pin=0(blower) cycle_ticks=100 value=128 initial
        _w.KlipperSend("allocate_oids", [2]);
        _w.KlipperSend("config_pwm_out", [0, 0, 100, 128, 0, 0]);
        _w.RunFor(0.2);
        Assert.Contains("duty 0.5", _w.GetRunStatus());

        // set_pwm_out is immediate: full blast.
        _w.KlipperSend("set_pwm_out", [0, 255]);
        _w.RunFor(0.2);
        var status = _w.GetRunStatus();
        Assert.Contains("duty 1", status);
        Assert.Contains("rpm", status);
    }
}