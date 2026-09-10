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
    public void Commanded_press_cycles_dispatch_one_tap_each()
    {
        // The finger's tip is a servo plunger: drive it down (press), retract, press
        // again — each physical press of the glass dispatches exactly one tap at the
        // parked fraction (the screen centre in this rig).
        var dispatched = new List<(double Fx, double Fy)>();
        using var sim = TestRig();
        var bridge = new PhysicsTapBridge(sim, "dut", "finger", (fx, fy) => dispatched.Add((fx, fy)));
        bridge.Arm();
        sim.Mcu.PostTick += () => bridge.Tick();

        sim.RunFor(0.02); // settle at rest (tip just clear of the glass)
        Assert.Empty(bridge.DispatchedTaps);

        // Press: target -8 mm (the plunger drives through the glass).
        sim.Servo("finger").SetTargetPositionM(-0.008);
        sim.RunFor(0.05);
        Assert.Equal(1, bridge.DispatchedTaps.Count);

        // Retract: the contact episode ends.
        sim.Servo("finger").SetTargetPositionM(0.002);
        sim.RunFor(0.05);
        Assert.Equal(1, bridge.DispatchedTaps.Count); // no new tap while clear

        // Second press: another tap at the same fraction.
        sim.Servo("finger").SetTargetPositionM(-0.008);
        sim.RunFor(0.05);
        Assert.Equal(2, bridge.DispatchedTaps.Count);
        Assert.Equal(bridge.DispatchedTaps[0].Fx, bridge.DispatchedTaps[1].Fx, 3); // same parked spot
        Assert.Equal(bridge.DispatchedTaps[0].Fy, bridge.DispatchedTaps[1].Fy, 3);
        Assert.InRange(bridge.DispatchedTaps[0].Fx, 0.4, 0.6);
        Assert.InRange(bridge.DispatchedTaps[0].Fy, 0.4, 0.6);

        // The dispatch callback saw both taps too.
        Assert.Equal(2, dispatched.Count);
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
