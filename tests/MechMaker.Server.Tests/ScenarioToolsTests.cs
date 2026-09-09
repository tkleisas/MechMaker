namespace MechMaker.Server.Tests;

/// <summary>Lua scenario tools: the deterministic scripted-simulation seam for agents.</summary>
public class ScenarioToolsTests : IDisposable
{
    private readonly McpWorkspace _w = TestRepo.NewWorkspace();

    public void Dispose() => _w.Dispose();

    [Fact]
    public void An_inline_scenario_runs_against_the_session_machine()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var result = _w.RunScenarioSource("""
            function run(sim)
              sim.enable("motor_left", true)
              sim.velocity("motor_left", 1.0)
              sim.run(1.0)
              local m = sim.motor("motor_left")
              assert(m.missed_steps == 0, "expected zero missed steps")
              result = { commanded = m.commanded_rev, missed = m.missed_steps }
            end
            """);

        Assert.DoesNotContain("FAILED", result);
        Assert.Contains("result:", result);
        Assert.Contains("\"missed\": 0", result);
        Assert.Contains("sim time: 1 s", result);
    }

    [Fact]
    public void A_failing_scenario_reports_instead_of_throwing()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var result = _w.RunScenarioSource("""
            function run(sim)
              error("deliberate failure")
            end
            """);

        Assert.Contains("FAILED", result);
        Assert.Contains("deliberate failure", result);
    }

    [Fact]
    public void Scenario_file_loading_reports_missing_files()
        => Assert.Throws<FileNotFoundException>(() => _w.RunScenarioFile("no/such.lua"));

    [Fact]
    public void The_bundled_homing_scenario_passes_on_the_example_axis()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        var result = _w.RunScenarioFile(Path.Combine(TestRepo.Root(), "scripts", "axis_home.lua"));
        Assert.DoesNotContain("FAILED", result);
        Assert.Contains("homed", result);
    }

    [Fact]
    public void A_scenario_stops_the_active_run_and_leaves_edits_intact()
    {
        _w.OpenMachine(TestRepo.ExampleMachine());
        _w.StartRun();
        _w.RunScenarioSource("function run(sim) end");
        Assert.Contains("No run active", _w.StopRun()); // scenario took over the run slot
        Assert.Equal(11, _w.Machine.Parts.Count);        // machine untouched
    }
}