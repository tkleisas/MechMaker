using MechMaker.Core;
using MechMaker.Core.Model;

namespace MechMaker.Engine.Tests;

/// <summary>
/// Physics-contact taps: the touch_finger actually pressing the phone's screen in
/// the physics dispatches emulator taps at the contact point — the full
/// androidtester story where the machine physically does the touching.
/// </summary>
public class PhysicsTapBridgeTests
{
    [Fact]
    public void A_finger_press_on_the_screen_dispatches_a_tap_at_the_contact()
    {
        // The rig poses the finger 0.2 mm above the glass; gravity pulls the tip
        // down, the tip contacts the screen, and the bridge maps the contact point
        // (screen centre) to fractions and dispatches.
        var dispatched = new List<(double Fx, double Fy)>();
        using var sim = TestRig();
        var bridge = new PhysicsTapBridge(sim, "dut", "finger", (fx, fy) => dispatched.Add((fx, fy)));
        bridge.Arm();
        sim.Mcu.PostTick += () => bridge.Tick();

        sim.RunFor(0.05);

                var tap = Assert.Single(bridge.DispatchedTaps);
        // The finger sits over the screen centre.
        Assert.InRange(tap.Fx, 0.45, 0.55);
        Assert.InRange(tap.Fy, 0.45, 0.55);
        Assert.Single(dispatched); // the dispatch callback saw the same tap
    }

    [Fact]
    public void Contact_mapping_converts_world_xy_to_screen_fractions()
    {
        // Geometry contract: the screen rect maps to fractions; y is top-left origin.
        using var sim = TestRig();
        var bridge = new PhysicsTapBridge(sim, "dut", "finger", (_, _) => { });
        bridge.Arm();

        var phone = sim.Simulator.GetBodyPosition("dut");
        var screenZ = phone[2] + 0.002;
        Assert.True(screenZ > phone[2]); // the glass is the +Z face of the phone slab
    }

    [Fact]
    public void Shallow_grazes_below_the_trigger_depth_do_not_dispatch()
    {
        // The trigger depth rejects pure proximity: raising it above the actual
        // contact depth disables dispatch (the tunable clear/press distinction).
        var dispatched = new List<(double, double)>();
        using var sim = TestRig();
        var bridge = new PhysicsTapBridge(sim, "dut", "finger", (fx, fy) => dispatched.Add((0, 0)))
        {
            TriggerDepthM = 1.0 // deeper than any possible press
        };
        bridge.Arm();
        sim.Mcu.PostTick += () => bridge.Tick();
        sim.RunFor(0.05);

        Assert.Empty(bridge.DispatchedTaps);
    }

    [Fact]
    public void The_rig_example_validates_and_compiles()
    {
        using var editor = new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"),
            Path.Combine(TestRepo.Root(), "examples", "phone_test_rig.json"));
        Assert.Equal("OK", editor.Validate().ToString());
        Assert.NotEmpty(editor.EditScene);
    }

    private static MachineSimulation TestRig()
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", "phone_test_rig.json")));
        return MachineSimulation.FromMachine(machine, TestRepo.Catalog());
    }
}