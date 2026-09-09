using MechMaker.Core;
using MechMaker.Core.Model;
using MechMaker.Engine.Scripting;

namespace MechMaker.Engine.Tests;

/// <summary>
/// Lua scenarios on the closed loop — the scripting layer must reproduce the
/// engine's own test-verified behaviour (tracking, endstops, stalls) and stay
/// sandboxed.
/// </summary>
public class LuaScenarioTests
{
    private static MachineSimulation NewExample()
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json")));
        return MachineSimulation.FromMachine(machine, TestRepo.Catalog());
    }

    private static ScenarioReport Run(string source)
    {
        using var machine = LuaTests.NewMachine();
        return LuaScenario.Parse(source).Run(machine);
    }

    [Fact]
    public void A_driven_motor_tracks_its_command()
    {
        var report = Run("""
            function run(sim)
              sim.enable("motor_left", true)
              sim.velocity("motor_left", 1.0)
              sim.run(1.0)
              local m = sim.motor("motor_left")
              result = { commanded = m.commanded_rev, actual = m.actual_rev, missed = m.missed_steps }
              assert(m.missed_steps == 0, "expected zero missed steps")
              assert(m.commanded_rev > 0.96 and m.commanded_rev < 1.0, "expected ~1 rev")
            end
            """);

        Assert.True(report.Success, report.Error);
        Assert.Contains("result:", report.ToString());
        Assert.Contains("\"missed\": 0", report.ResultJson);
        Assert.True(report.SimSeconds > 0.99);
    }

    [Fact]
    public void The_endstop_fires_and_a_homing_scenario_works()
    {
        var report = LuaScenario.LoadFile(Path.Combine(TestRepo.Root(), "scripts", "axis_home.lua"));
        using var machine = LuaTests.NewMachine();
        var outcome = report.Run(machine);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("homed", string.Join("\n", outcome.Log));
        Assert.Contains("\"homed\": true", outcome.ResultJson);
    }

    [Fact]
    public void Commanding_beyond_the_torque_limit_stalls()
    {
        var report = Run("""
            function run(sim)
              sim.enable("motor_left", true)
              sim.acceleration("motor_left", 1e9)
              sim.velocity("motor_left", 20.0)
              sim.run(0.2)
              local m = sim.motor("motor_left")
              assert(m.missed_steps > 50, "expected slip, missed=" .. m.missed_steps)
              assert(m.stalled, "expected the motor to be stalled")
            end
            """);

        Assert.True(report.Success, report.Error);
    }

    [Fact]
    public void Print_output_lands_in_the_report()
    {
        var report = Run("""
            function run(sim)
              print("hello from lua", 42, true)
            end
            """);

        Assert.True(report.Success, report.Error);
        Assert.Contains(report.Log, l => l.Contains("hello from lua") && l.Contains("42") && l.Contains("true"));
    }

    [Fact]
    public void A_script_without_run_is_rejected()
    {
        var report = Run("x = 1");
        Assert.False(report.Success);
        Assert.Contains("run(sim)", report.Error);
    }

    [Fact]
    public void Script_errors_are_reported_not_thrown()
    {
        var report = Run("""
            function run(sim)
              error("boom")
            end
            """);

        Assert.False(report.Success);
        Assert.Contains("boom", report.Error);
    }

    [Fact]
    public void Unknown_motors_surface_clear_errors()
    {
        var report = Run("""
            function run(sim)
              sim.enable("ghost", true)
            end
            """);

        Assert.False(report.Success);
        Assert.Contains("ghost", report.Error);
    }

    [Fact]
    public void The_sandbox_denies_io_and_os()
    {
        var report = Run("""
            function run(sim)
              io.open("nope.txt")
            end
            """);

        Assert.False(report.Success);

        var report2 = Run("""
            function run(sim)
              os.exit(1)
            end
            """);

        Assert.False(report2.Success);
    }

    [Fact]
    public void Runaway_loops_hit_the_wall_clock_guard()
    {
        var report = LuaScenario.Parse("""
            function run(sim)
              local i = 0
              while true do i = i + 1 end
            end
            """, wallClockTimeout: TimeSpan.FromSeconds(2)).Run(LuaTests.NewMachine());

        Assert.False(report.Success);
        Assert.Contains("timed out", report.Error);
    }

    [Fact]
    public void Run_bounds_are_enforced_in_scripts()
    {
        var report = Run("""
            function run(sim)
              sim.run(11)
            end
            """);

        Assert.False(report.Success);
        Assert.Contains("0.001", report.Error);
    }
}

/// <summary>Shared helper: the example axis simulation (no motor enables).</summary>
public static class LuaTests
{
    public static MachineSimulation NewMachine()
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json")));
        return MachineSimulation.FromMachine(machine, TestRepo.Catalog());
    }
}