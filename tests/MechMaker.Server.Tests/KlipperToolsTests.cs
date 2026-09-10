namespace MechMaker.Server.Tests;

/// <summary>
/// The klipper-protocol seam through the MCP workspace: an agent connects, runs the
/// klipper configuration flow with decoded integer args, drives the axis with
/// queue_step, and homes it via endstop_home — all over the real wire format.
/// </summary>
public class KlipperToolsTests : IDisposable
{
    private readonly McpWorkspace _w = TestRepo.ExampleWorkspace();

    public KlipperToolsTests() => _w.AddEndstop("j_carriage", -0.020);

    public void Dispose() => _w.Dispose();

    private string Configure()
    {
        // enumerate pins: the connect message names them in enum order (steppers first,
        // then endstop pins registered before connect). We mirror the dictionary order:
        // motor_left=0, motor_right=1, endstop_j_carriage=2.
        Assert.Contains("motor_left", _w.KlipperConnect());

        _w.KlipperSend("allocate_oids", [8]);
        _w.KlipperSend("config_stepper", [0, 0, 0, 0, 0]);        // oid 0 = motor_left
        _w.KlipperSend("config_endstop", [1, 2, 1]);              // oid 1 = endstop pin
        _w.KlipperSend("endstop_set_stepper", [1, 0]);
        _w.KlipperSend("finalize_config", [0x1234]);
        var config = _w.KlipperSend("get_config", []);
        Assert.Contains("response 1 1 4660", config); // config, is_config=1, crc 0x1234
        return config;
    }

    [Fact]
    public void The_klipper_flow_configures_and_reports_the_clock()
    {
        Configure();

        _w.RunFor(0.01); // advance the live sim so the clock ticks
        var clock = _w.KlipperSend("get_clock", []);
        Assert.Contains("response 2 80", clock); // clock response, 80 ticks
    }

    [Fact]
    public void Queue_step_moves_the_axis_over_the_wire()
    {
        Configure();

        // 200 steps at interval 40 ticks = 1 rev = 40 mm of carriage.
        _w.KlipperSend("reset_step_clock", [0, 1000]);
        _w.KlipperSend("set_next_step_dir", [0, 1]);
        _w.KlipperSend("queue_step", [0, 40, 200, 0]);

        _w.RunFor(1.15);

        var position = _w.KlipperSend("stepper_get_position", [0]);
        Assert.Contains("response 3 0 200", position); // stepper_position, oid 0, 200 steps
        Assert.Contains("position 200 steps", _w.KlipperStatus());
    }

    [Fact]
    public void Endstop_home_homes_the_axis_over_the_wire()
    {
        Configure();

        // Arm the endstop; the arm ack comes back as endstop_home homing=1 (id 4).
        var arm = _w.KlipperSend("endstop_home", [1, 100, 10, 3, 100, 1]);
        Assert.Contains("response 4 1 1", arm);

        // Command 2 rev toward the endstop (triggers at 20 mm ≈ 100 steps).
        _w.KlipperSend("reset_step_clock", [0, 0]);
        _w.KlipperSend("set_next_step_dir", [0, 1]);
        _w.KlipperSend("queue_step", [0, 40, 400, 0]);

        string? trigger = null;
        for (var i = 0; i < 40 && trigger is null; i++)
        {
            _w.RunFor(0.05);
            var drained = _w.KlipperSend("endstop_query_state", [1]);
            foreach (var line in drained.Split('\n'))
                if (line.Contains("response 4 1 0")) // endstop_home oid=1 homing=0
                    trigger = line;
        }

        Assert.NotNull(trigger);
        var status = _w.KlipperStatus();
        // Halted between 95 and 130 steps (≈ 20 mm), queue cleared.
        var position = int.Parse(status.Split('\n')
            .First(l => l.StartsWith("motor_left"))
            .Split("position ")[1].Split(" steps")[0]);
        Assert.InRange(position, 95, 130);
        Assert.Contains("0 queued", status);
    }

    [Fact]
    public void Klipper_send_before_connect_throws()
        => Assert.Throws<InvalidOperationException>(() => _w.KlipperSend("get_clock", []));

    [Fact]
    public void Unknown_commands_list_the_valid_ones()
    {
        _w.KlipperConnect();
        var exception = Assert.Throws<KeyNotFoundException>(
            () => _w.KlipperSend("not_a_command", []));
        Assert.Contains("queue_step", exception.Message);
    }

    [Fact]
    public void Stopping_the_run_ends_the_klipper_session()
    {
        Assert.Contains("Klipper MCU connected", _w.KlipperConnect());
        Assert.Contains("Run stopped", _w.StopRun());
        Assert.Contains("No run active", _w.StopRun());
        Assert.Throws<InvalidOperationException>(() => _w.KlipperStatus());
    }
}