using MechMaker.Core.Compilation;
using MechMaker.Core.Model;

namespace MechMaker.Server.Tests;

/// <summary>Session-state tests on a fresh McpWorkspace per test (no singleton, no cross-test state).</summary>
public class McpWorkspaceTests : IDisposable
{
    private readonly McpWorkspace _w = TestRepo.NewWorkspace();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        _w.Dispose();
        foreach (var file in _tempFiles.Where(File.Exists))
            File.Delete(file);
    }

    private string TempPath(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mechmaker_test_{Guid.NewGuid():N}_{name}");
        _tempFiles.Add(path);
        return path;
    }

    // ---------- machine file ----------

    [Fact]
    public void Catalog_discovers_the_repo_seed_parts()
    {
        Assert.Equal(19, _w.Catalog.All.Count);
        Assert.NotNull(_w.Catalog.Find("nema17_stepper"));
        Assert.NotNull(_w.Catalog.Find("gt2_belt_400"));
    }

    [Fact]
    public void Open_machine_loads_the_example_axis()
    {
        var result = _w.OpenMachine(TestRepo.ExampleMachine());
        Assert.Contains("linear_axis_v0", result);
        Assert.Equal(11, _w.Machine.Parts.Count);
        Assert.Equal(12, _w.Machine.Connections.Count);
        Assert.Single(_w.Machine.Boards);
        Assert.Equal(7, _w.Machine.Wiring.Count);
    }

    [Fact]
    public void Open_machine_with_a_missing_file_throws()
        => Assert.Throws<FileNotFoundException>(() => _w.OpenMachine("no/such/machine.json"));

    [Fact]
    public void New_machine_resets_the_session()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.NewMachine("fresh");
        Assert.Empty(_w.Machine.Parts);
        Assert.Empty(_w.Machine.Connections);
        Assert.Equal("fresh", _w.Machine.Name);
    }

    [Fact]
    public void New_machine_on_a_fresh_session_can_save_without_a_path()
    {
        // Regression: NewMachine used to throw on a workspace with no open file.
        _w.NewMachine("fresh");
        var path = TempPath("fresh.json");
        var result = _w.SaveMachine(path);
        Assert.Contains("saved", result);
    }

    [Fact]
    public void Save_without_a_path_reuses_the_last_opened_file()
    {
        var path = TempPath("machine.json");
        _w.NewMachine("roundtrip");
        _w.AddPart("beam_2020_400", "b1", x: 0, y: 0, z: 0);
        _w.SaveMachine(path);

        _w.AddPart("corner_bracket_2020", "c1", x: 0.1, y: 0, z: 0);
        _w.SaveMachine(); // overwrite the same file, no path given

        var fresh = TestRepo.NewWorkspace();
        fresh.OpenMachine(path);
        Assert.Equal(2, fresh.Machine.Parts.Count);
    }

    [Fact]
    public void Save_without_a_path_or_open_file_throws()
        => Assert.Throws<InvalidOperationException>(() => _w.SaveMachine());

    // ---------- parts ----------

    [Fact]
    public void Add_part_auto_generates_unique_ids()
    {
        _w.NewMachine("m");
        var first = _w.AddPart("beam_2020_400");
        var second = _w.AddPart("beam_2020_400");
        Assert.NotEqual(first, second);
        Assert.Equal(2, _w.Machine.Parts.Count);
    }

    [Fact]
    public void Add_part_with_a_taken_id_throws()
    {
        _w.NewMachine("m");
        _w.AddPart("beam_2020_400", "b1");
        Assert.Throws<InvalidOperationException>(() => _w.AddPart("beam_2020_400", "b1"));
    }

    [Fact]
    public void Add_part_unknown_catalog_id_throws()
        => Assert.Throws<KeyNotFoundException>(() => _w.AddPart("no_such_part"));

    [Fact]
    public void Add_part_stacks_new_parts_above_existing_ones()
    {
        _w.NewMachine("m");
        _w.AddPart("beam_2020_400", "b1", x: 0, y: 0, z: 0);
        _w.AddPart("corner_bracket_2020", "c1"); // auto-z
        var z1 = _w.Machine.Parts.Single(p => p.Id == "b1").Pose.Position.Z;
        var z2 = _w.Machine.Parts.Single(p => p.Id == "c1").Pose.Position.Z;
        Assert.True(z2 > z1, $"expected auto-stacked z {z2} above explicit z {z1}");
    }

    [Fact]
    public void Update_part_pose_moves_the_part()
    {
        _w.NewMachine("m");
        _w.AddPart("beam_2020_400", "b1", x: 0, y: 0, z: 0);
        _w.UpdatePartPose("b1", 0.1, 0.2, 0.3, rx: 90);
        var pose = _w.Machine.Parts.Single(p => p.Id == "b1").Pose;
        Assert.Equal(0.1, pose.Position.X, 9);
        Assert.Equal(0.3, pose.Position.Z, 9);
        Assert.Equal(90, pose.RotationEulerDeg.X, 9);
    }

    [Fact]
    public void Update_pose_of_unknown_part_throws()
        => Assert.Throws<KeyNotFoundException>(() => _w.UpdatePartPose("ghost", 0, 0, 0));

    [Fact]
    public void Delete_part_removes_its_connections_and_wiring()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.DeletePart("motor_left");
        Assert.DoesNotContain("motor_left", _w.Machine.Parts.Select(p => p.Id));
        Assert.DoesNotContain(_w.Machine.Connections,
            c => c.PartA == "motor_left" || c.PartB == "motor_left");
        Assert.DoesNotContain(_w.Machine.Wiring, w => w.Component == "motor_left");
    }

    [Fact]
    public void Delete_unknown_part_throws()
        => Assert.Throws<KeyNotFoundException>(() => _w.DeletePart("ghost"));

    // ---------- connections ----------

    [Fact]
    public void Connection_ids_continue_after_opening_a_machine()
    {
        _w.OpenMachine(TestRepo.ExampleMachine()); // 12 connections
        _w.AddPart("corner_bracket_2020", "extra");
        var message = _w.AddConnection("extra", "side_a", "beam", "end_a");
        Assert.Contains("c13", message); // ids continue after the loaded 12
        Assert.Equal(13, _w.Machine.Connections.Count);
    }

    [Fact]
    public void Connection_to_a_missing_connector_names_the_available_ones()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var exception = Assert.Throws<KeyNotFoundException>(
            () => _w.AddConnection("beam", "no_such_face", "rail", "mount"));
        Assert.Contains("Available:", exception.Message);
        Assert.Contains("end_a", exception.Message);
    }

    [Fact]
    public void Connection_between_unknown_parts_throws()
        => Assert.Throws<KeyNotFoundException>(() => _w.AddConnection("ghost", "x", "rail", "mount"));

    [Fact]
    public void An_incompatible_connection_is_added_but_flagged_by_validation()
    {
        // shaft_5mm cannot mate tslot_2020 — creation checks connector existence only;
        // compatibility surfaces in validate_machine / compile as mm004.
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.AddConnection("motor_left", "shaft", "beam", "end_a");

        var report = _w.Validate();
        Assert.Contains(report.Diagnostics, d => d.Code == "mm004");
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Delete_connection_removes_it()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.DeleteConnection("rail_on_beam");
        Assert.DoesNotContain(_w.Machine.Connections, c => c.Id == "rail_on_beam");
    }

    [Fact]
    public void Delete_unknown_connection_throws()
        => Assert.Throws<KeyNotFoundException>(() => _w.DeleteConnection("no_such_connection"));

    [Fact]
    public void Connectors_of_a_part_expose_name_and_type()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var connectors = _w.ConnectorsOf("motor_left").ToList();
        Assert.Contains(connectors, c => c.Name == "shaft" && c.Type == ConnectorType.Shaft5mm);
        Assert.Contains(connectors, c => c.Name == "mount" && c.Type == ConnectorType.BoltM3);
    }

    // ---------- boards & wiring ----------

    [Fact]
    public void Wire_without_a_board_tells_you_to_add_one()
    {
        _w.NewMachine("m");
        _w.AddPart("nema17_stepper", "m1");
        var exception = Assert.Throws<InvalidOperationException>(() => _w.Wire("m1", "step", "main", "P2_2"));
        Assert.Contains("add_board", exception.Message);
    }

    [Fact]
    public void Wire_to_an_unknown_board_throws()
    {
        _w.NewMachine("m");
        _w.AddPart("nema17_stepper", "m1");
        _w.AddBoard("main");
        Assert.Throws<KeyNotFoundException>(() => _w.Wire("m1", "step", "other_board", "P2_2"));
    }

    [Fact]
    public void Rewiring_the_same_signal_replaces_the_wire()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.Wire("motor_left", "step", "main", "P9_9");
        var wires = _w.Machine.Wiring.Where(w => w.Component == "motor_left" && w.Signal == "step").ToList();
        Assert.Single(wires);
        Assert.Equal("P9_9", wires[0].Pin);
    }

    [Fact]
    public void Duplicate_board_id_throws()
    {
        _w.NewMachine("m");
        _w.AddBoard("main");
        Assert.Throws<InvalidOperationException>(() => _w.AddBoard("main"));
    }

    // ---------- validation & compilation ----------

    [Fact]
    public void The_example_machine_validates_clean()
        => Assert.Equal("OK", _w.Validate().ToString());

    [Fact]
    public void Two_unconnected_parts_flag_a_disconnected_island()
    {
        _w.NewMachine("m");
        _w.AddPart("beam_2020_400", "b1");
        _w.AddPart("corner_bracket_2020", "c1");
        Assert.Contains(_w.Validate().Diagnostics, d => d.Code == "mm020");
    }

    [Fact]
    public void Compile_produces_mjcf()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var (mjcf, report) = _w.Compile();
        Assert.Contains("<mujoco", mjcf);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Compile_of_a_broken_machine_throws_with_diagnostics()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.AddConnection("motor_left", "shaft", "beam", "end_a"); // incompatible
        Assert.Throws<MjcfCompileException>(() => _w.Compile());
    }

    // ---------- run mode ----------

    [Fact]
    public void Run_commands_before_start_run_throw()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        Assert.Throws<InvalidOperationException>(() => _w.RunFor(0.1));
        Assert.Throws<InvalidOperationException>(() => _w.GetRunStatus());
        Assert.Throws<InvalidOperationException>(() => _w.SetMotorVelocity("motor_left", 1));
        Assert.Throws<InvalidOperationException>(() => _w.EnableMotor("motor_left", true));
        Assert.Throws<InvalidOperationException>(() => _w.ReadEndstop("j_carriage"));
    }

    [Fact]
    public void Start_run_creates_a_channel_per_wired_stepper()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var result = _w.StartRun();
        Assert.Contains("2 stepper channel(s)", result);
        Assert.Contains("motor_left", result);
        Assert.Contains("motor_right", result);
    }

    [Fact]
    public void Start_run_of_a_broken_machine_throws()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.AddConnection("motor_left", "shaft", "beam", "end_a");
        Assert.Throws<MjcfCompileException>(() => _w.StartRun());
    }

    [Fact]
    public void Run_duration_bounds_are_enforced()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        Assert.Throws<ArgumentOutOfRangeException>(() => _w.RunFor(0.0005));
        Assert.Throws<ArgumentOutOfRangeException>(() => _w.RunFor(10.1));
    }

    [Fact]
    public void Commands_for_unwired_motors_throw()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        Assert.Throws<KeyNotFoundException>(() => _w.SetMotorVelocity("carriage", 1));
    }

    [Fact]
    public void A_driven_motor_tracks_its_command_without_missing_steps()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        _w.EnableMotor("motor_left", true);
        _w.SetMotorVelocity("motor_left", 1.0);
        _w.RunFor(1.0);

        var status = _w.GetRunStatus();
        var leftLine = status.Split('\n').Single(l => l.StartsWith("motor_left"));
        Assert.Contains("missed steps 0", leftLine);
        Assert.DoesNotContain("STALLED", status);
    }

    [Fact]
    public void A_disabled_motor_back_driven_by_the_belt_reports_no_missed_steps()
    {
        // Regression: back-driving a de-energized channel is not a fault — the idle
        // motor must report zero missed steps while the belt spins its rotor.
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        _w.EnableMotor("motor_left", true);
        _w.SetMotorVelocity("motor_left", 1.0);
        _w.RunFor(0.55);

        var status = _w.GetRunStatus();
        var rightLine = status.Split('\n').Single(l => l.StartsWith("motor_right"));
        Assert.Contains("(disabled)", rightLine);
        Assert.Contains("missed steps 0", rightLine);
        // It really is being dragged by the belt, not sitting still:
        Assert.DoesNotContain("actual 0 rev", rightLine);
    }

    [Fact]
    public void Commanding_beyond_the_torque_speed_limit_stalls_the_motor()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        _w.EnableMotor("motor_left", true);
        _w.SetMotorVelocity("motor_left", 20.0);
        _w.RunFor(1.0);

        var leftLine = _w.GetRunStatus().Split('\n').Single(l => l.StartsWith("motor_left"));
        Assert.DoesNotContain("missed steps 0", leftLine);
        Assert.Contains("STALLED", leftLine);
    }

    [Fact]
    public void Endstop_added_before_a_run_activates_on_start_run()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var message = _w.AddEndstop("j_carriage", -0.020);
        Assert.Contains("next run", message);

        Assert.Contains("1 endstop(s)", _w.StartRun());
    }

    [Fact]
    public void Endstop_opens_then_fires_as_the_carriage_passes_the_trigger()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        _w.AddEndstop("j_carriage", -0.020);
        _w.EnableMotor("motor_left", true);
        _w.SetMotorVelocity("motor_left", 1.0); // positive rotor -> carriage toward -X

        _w.RunFor(0.35);
        Assert.Contains("open", _w.ReadEndstop("j_carriage"));

        _w.RunFor(0.2);
        Assert.Contains("PRESSED", _w.ReadEndstop("j_carriage"));
    }

    [Fact]
    public void Read_endstop_for_a_joint_without_one_throws()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        Assert.Throws<KeyNotFoundException>(() => _w.ReadEndstop("j_carriage"));
    }

    [Fact]
    public void Stop_run_twice_is_benign()
    {
        Assert.Contains("No run active", _w.StopRun());
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        Assert.StartsWith("Run stopped", _w.StopRun());
        Assert.Contains("No run active", _w.StopRun());
    }

    [Fact]
    public void Edits_during_a_run_do_not_disturb_the_live_snapshot()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var startMessage = _w.StartRun();
        _w.AddPart("corner_bracket_2020"); // mid-run edits are allowed
        Assert.Equal(12, _w.Machine.Parts.Count);

        // The live run still reflects the compiled snapshot: same channels, untouched state.
        var status = _w.GetRunStatus();
        Assert.Contains("motor_left", status);
        Assert.Contains("t = 0 s", status);
        Assert.Equal(2, status.Split('\n').Count(l => l.StartsWith("motor_")));
        Assert.Contains("2 stepper channel(s)", startMessage);
    }
}




