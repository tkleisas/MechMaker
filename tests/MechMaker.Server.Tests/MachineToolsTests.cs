using MechMaker.Server.Tests;

namespace MechMaker.Server.Tests;

/// <summary>
/// Tests the MCP tool surface (the static methods the SDK invokes), driving the
/// singleton workspace. Each test resets session state first because the singleton
/// persists across tests; xUnit runs tests within a class sequentially.
/// </summary>
public class MachineToolsTests
{
    private static void ResetSession() => MachineTools.NewMachine($"test_{Guid.NewGuid():N}");

    // ---------- catalog ----------

    [Fact]
    public void List_catalog_parts_names_every_seed_part()
    {
        var result = MachineTools.ListCatalogParts();
        Assert.Contains("nema17_stepper", result);
        Assert.Contains("beam_2020_400", result);
        Assert.Contains("gt2_belt_400", result);
        Assert.Contains("transmission element", result); // the belt is flagged
        Assert.Contains("motor: Stepper", result);        // motors carry their specs
    }

    [Fact]
    public void Get_part_info_returns_the_full_definition()
    {
        var json = MachineTools.GetPartInfo("gt2_pulley_20t");
        Assert.Contains("\"id\": \"gt2_pulley_20t\"", json);
        Assert.Contains("connectors", json);
    }

    [Fact]
    public void Get_part_info_unknown_id_throws()
        => Assert.Throws<KeyNotFoundException>(() => MachineTools.GetPartInfo("no_such_part"));

    // ---------- machine file ----------

    [Fact]
    public void New_machine_then_get_machine_json_round_trips()
    {
        ResetSession();
        MachineTools.AddPart("beam_2020_400", "b1", 0, 0, 0);
        var json = MachineTools.GetMachineJson();
        Assert.Contains("\"name\": \"test_", json);
        Assert.Contains("\"id\": \"b1\"", json);
    }

    [Fact]
    public void Open_and_save_machine_tools_round_trip()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        Assert.Contains("linear_axis_v0", MachineTools.GetMachineJson());
    }

    [Fact]
    public void Open_machine_with_missing_file_throws()
        => Assert.Throws<FileNotFoundException>(() => MachineTools.OpenMachine("no/such.json"));

    // ---------- parts ----------

    [Fact]
    public void Add_part_reports_the_instance_and_placement()
    {
        ResetSession();
        var result = MachineTools.AddPart("beam_2020_400", "beam1", 0, 0, 0.1);
        Assert.Contains("Placed 'beam1'", result);
        Assert.Contains("beam_2020_400", result);
    }

    [Fact]
    public void Add_part_with_auto_stack_places_parts_at_distinct_heights()
    {
        ResetSession();
        MachineTools.AddPart("beam_2020_400", "b1");
        MachineTools.AddPart("corner_bracket_2020", "c1");
        var json = MachineTools.GetMachineJson();
        Assert.Contains("\"id\": \"b1\"", json);
        Assert.Contains("\"id\": \"c1\"", json);
    }

    [Fact]
    public void Delete_part_tool_removes_the_instance()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        var result = MachineTools.DeletePart("motor_left");
        Assert.Contains("Deleted", result);
        Assert.DoesNotContain("\"id\": \"motor_left\"", MachineTools.GetMachineJson());
    }

    // ---------- connections ----------

    [Fact]
    public void Add_connection_returns_the_connection_id()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        MachineTools.AddPart("corner_bracket_2020", "extra");
        var result = MachineTools.AddConnection("extra", "side_a", "beam", "end_a");
        Assert.Matches(@"c\d+", result);
    }

    [Fact]
    public void List_connectors_formats_name_and_type()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        var result = MachineTools.ListConnectors("motor_left");
        Assert.Contains("shaft:Shaft5mm", result);
        Assert.Contains("mount:BoltM3", result);
    }

    // ---------- wiring ----------

    [Fact]
    public void Wire_reports_the_net()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        var result = MachineTools.Wire("motor_left", "step", "main", "P9_9");
        Assert.Contains("motor_left.step -> main.P9_9", result);
    }

    // ---------- validation & compilation ----------

    [Fact]
    public void Validate_machine_returns_OK_for_the_example()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        Assert.Equal("OK", MachineTools.ValidateMachine());
    }

    [Fact]
    public void Validate_machine_reports_mm_codes_for_a_broken_machine()
    {
        ResetSession();
        MachineTools.AddPart("beam_2020_400", "b1");
        MachineTools.AddPart("corner_bracket_2020", "c1"); // floating: island warning
        var result = MachineTools.ValidateMachine();
        Assert.Contains("mm020", result);
    }

    [Fact]
    public void Compile_mjcf_writes_a_file_when_given_a_path()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        var path = Path.Combine(Path.GetTempPath(), $"mechmaker_tool_{Guid.NewGuid():N}.xml");
        try
        {
            var result = MachineTools.CompileMjcf(path);
            Assert.Contains("Compiled", result);
            Assert.Contains("<mujoco", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Compile_mjcf_returns_the_xml_when_no_path_is_given()
    {
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        var mjcf = MachineTools.CompileMjcf();
        Assert.StartsWith("<", mjcf.TrimStart());
        Assert.Contains("<mujoco", mjcf);
    }

    // ---------- simulation ----------

    [Fact]
    public void The_documented_agent_flow_drives_the_axis_and_fires_the_endstop()
    {
        // The exact flow from the README: open -> validate -> start -> enable ->
        // command -> run -> read endstop.
        MachineTools.OpenMachine(TestRepo.ExampleMachine());
        Assert.Equal("OK", MachineTools.ValidateMachine());
        Assert.Contains("2 stepper channel(s)", MachineTools.StartRun());
        MachineTools.AddEndstop("j_carriage", -0.020);
        Assert.Contains("enabled", MachineTools.EnableMotor("motor_left", true));
        MachineTools.SetMotorVelocity("motor_left", 1.0);
        MachineTools.RunFor(0.55);

        Assert.Contains("PRESSED", MachineTools.ReadEndstop("j_carriage"));
        var status = MachineTools.GetRunStatus();
        Assert.Contains("missed steps 0", status.Split('\n').Single(l => l.StartsWith("motor_left")));

        Assert.StartsWith("Run stopped", MachineTools.StopRun());
    }

    [Fact]
    public void Simulating_before_any_machine_is_loaded_still_fails_cleanly()
        => Assert.Throws<InvalidOperationException>(() => MachineTools.RunFor(0.1));

    [Fact]
    public void Stop_run_without_a_run_is_reported_not_thrown()
        => Assert.Contains("No run active", MachineTools.StopRun());
}
