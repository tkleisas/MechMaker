namespace MechMaker.Engine.Tests;

/// <summary>
/// The editor's run mode (the seam behind the app's ▶ Run button) must reproduce
/// the closed-loop behaviour the MCP/CLI flow already verifies: only the driven
/// stepper is energized, and the belt-driven carriage actually moves.
/// </summary>
public class EditorRunTests
{
    private static MachineEditor NewEditor(string example)
        => new(Path.Combine(TestRepo.Root(), "catalog"),
            Path.Combine(TestRepo.Root(), "examples", example));

    [Fact]
    public void Start_run_leaves_all_channels_de_energized()
    {
        using var editor = NewEditor("linear_axis_v0.json");
        using var sim = editor.StartRun();

        Assert.All(sim.Mcu.Steppers, s => Assert.False(s.IsEnabled));
    }

    [Fact]
    public void Run_mode_drives_the_carriage_through_the_belt()
    {
        // Regression for the app wobble: energizing and commanding *both* belt-coupled
        // motors the same way makes them fight through the equality constraint — the
        // axis judders without moving. The host drives one motor; the belt back-drives
        // the released slave.
        using var editor = NewEditor("linear_axis_agent.json");
        using var sim = editor.StartRun();

        var driven = sim.Mcu.Steppers[0];
        driven.Enable();
        driven.SetVelocityRevPerSec(1.0);
        sim.RunFor(0.55);

        var carriage = sim.Simulator.GetJointPos("j_carriage");
        Assert.InRange(Math.Abs(carriage), 0.019, 0.023); // ~21.5 mm (40 mm/rev, ramp included)
        Assert.Equal(0, driven.MissedSteps);

        var idle = sim.Mcu.Steppers[1];
        Assert.False(idle.IsEnabled); // released, back-driven by the belt
        Assert.True(Math.Abs(idle.RotorAngleRev) > 0.3, "slave rotor should be back-driven");
    }
}
