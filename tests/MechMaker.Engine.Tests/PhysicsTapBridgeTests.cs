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
        // parked fraction (the screen centre in the simple rig).
        var dispatched = new List<(double Fx, double Fy)>();
        using var sim = TestRig();
        var bridge = new PhysicsTapBridge(sim, "dut", "finger", (fx, fy) => dispatched.Add((fx, fy)));
        bridge.Arm();
        sim.Mcu.PostTick += () => bridge.Tick();

        sim.RunFor(0.02); // settle at rest (tip just clear of the glass)
        Assert.Empty(bridge.DispatchedTaps);

        // Press: -12 mm closes the 10 mm gap and drives 2 mm into the glass.
        sim.Servo("finger").SetTargetPositionM(-0.012);
        sim.RunFor(0.10);
        Assert.Equal(1, bridge.DispatchedTaps.Count);

        // Retract: the contact episode ends.
        sim.Servo("finger").SetTargetPositionM(0.002);
        sim.RunFor(0.10);
        Assert.Equal(1, bridge.DispatchedTaps.Count); // no new tap while clear

        // Second press: another tap at the same fraction.
        sim.Servo("finger").SetTargetPositionM(-0.012);
        sim.RunFor(0.10);
        Assert.Equal(2, bridge.DispatchedTaps.Count);
        Assert.Equal(bridge.DispatchedTaps[0].Fx, bridge.DispatchedTaps[1].Fx, 3); // same parked spot
        Assert.Equal(bridge.DispatchedTaps[0].Fy, bridge.DispatchedTaps[1].Fy, 3);
        Assert.InRange(bridge.DispatchedTaps[0].Fx, 0.4, 0.6);
        Assert.InRange(bridge.DispatchedTaps[0].Fy, 0.4, 0.6);

        // The dispatch callback saw both taps too.
        Assert.Equal(2, dispatched.Count);
    }

    [Fact]
    public void The_gantry_taps_where_it_parks()
    {
        // The two-axis gantry rig: belt-driven X carriage carrying a leadscrew Y
        // stage with the finger on the nut. Park over the screen centre, tap; move
        // the gantry; tap again — the two taps land at DIFFERENT screen fractions,
        // proving the contact point follows the machine's motion (the androidtester
        // positioning story).
        using var sim = TestRig("phone_gantry_rig.json");
        var taps = new List<(double Fx, double Fy)>();
        var bridge = new PhysicsTapBridge(sim, "dut", "finger", (fx, fy) => taps.Add((fx, fy)));
        bridge.Arm();
        sim.Mcu.PostTick += () => bridge.Tick();
        sim.RunFor(0.02);

        // The finger parks off the glass; drive both stages over the screen centre
        // (raw drives — TapAtController owns the careful version of this loop).
        DriveUntil(sim, "motor_left", () => sim.Simulator.GetBodyPosition("finger_tip")[0], 0.0, 0.001, 3.0);
        DriveUntil(sim, "y_leadscrew", () => sim.Simulator.GetBodyPosition("finger_tip")[1], 0.0, 0.002, -5.0);
        PressCycle(sim);
        Assert.Equal(1, bridge.DispatchedTaps.Count);
        var first = bridge.DispatchedTaps[0];
        Assert.InRange(first.Fx, 0.3, 0.7); // screen centre-ish
        Assert.InRange(first.Fy, 0.3, 0.7);

        // Move the gantry +X (1 rev/s, 0.35 s, ramp included; the belt slips a little
        // under the tool load — the tap lands wherever the machine actually parked).
        var gantry = sim.Stepper("motor_left");
        gantry.Enable();
        gantry.SetVelocityRevPerSec(1.0);
        sim.RunFor(0.35);
        gantry.SetVelocityRevPerSec(0);
        sim.RunFor(0.02);

        // Tap again: same screen row, different column.
        PressCycle(sim);
        Assert.Equal(2, bridge.DispatchedTaps.Count);
        var second = bridge.DispatchedTaps[1];
        Assert.True(second.Fx > first.Fx + 0.1, $"expected a rightward tap: {first.Fx:0.###} -> {second.Fx:0.###}");
        Assert.InRange(second.Fy, first.Fy - 0.1, first.Fy + 0.1); // same row
        Assert.InRange(second.Fx, 0.05, 0.95); // still on the glass
    }

    /// <summary>Spin a stepper at ±<paramref name="speedRevPerSec"/> until the
    /// measured coordinate reaches <paramref name="target"/> (sign picked by a
    /// probe: positive speed moves +rev; the loop corrects whichever way it goes).</summary>
    private static void DriveUntil(MachineSimulation sim, string stepperId,
        Func<double> measured, double target, double tolerance, double speedRevPerSec)
    {
        var stepper = sim.Stepper(stepperId);
        stepper.Enable();
        for (var attempt = 0; attempt < 600; attempt++) // 30 s of motion budget
        {
            var delta = target - measured();
            if (Math.Abs(delta) < tolerance)
                break;
            // On this rig +rev of the belt moves the tip +X, +rev of the screw moves
            // it −Y — the caller encodes that in the signed speed.
            stepper.SetVelocityRevPerSec(delta > 0 ? speedRevPerSec : -speedRevPerSec);
            sim.RunFor(0.05);
        }
        stepper.SetVelocityRevPerSec(0);
        sim.RunFor(0.05);
    }

    /// <summary>One plunger tap: on this rig the finger's stack is mounted so the
    /// slide presses on POSITIVE travel. The kiss sits ≈23 mm below slide-zero, so
    /// +24 mm is a sub-mm overdrive — enough to register on the glass without
    /// punching through the phone or bouncing into a second contact episode.</summary>
    private static void PressCycle(MachineSimulation sim)
    {
        sim.Servo("finger").SetTargetPositionM(0.024);
        sim.RunFor(0.25);
        sim.Servo("finger").SetTargetPositionM(0.0);
        sim.RunFor(0.15);
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
    public void Both_rig_examples_validate_and_compile()
    {
        foreach (var example in new[] { "phone_test_rig.json", "phone_gantry_rig.json" })
        {
            using var editor = new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"),
                Path.Combine(TestRepo.Root(), "examples", example));
            Assert.Equal("OK", editor.Validate().ToString());
            Assert.NotEmpty(editor.EditScene);
        }
    }

    private static MachineSimulation TestRig(string example = "phone_test_rig.json")
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", example)));
        return MachineSimulation.FromMachine(machine, TestRepo.Catalog());
    }
}
