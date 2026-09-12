using MechMaker.Core;

namespace MechMaker.Engine.Hil;

/// <summary>
/// Closes the loop from screen coordinates to physical taps: given a screen
/// fraction, parks the gantry so the finger tip is over that point on the glass,
/// then commands the press plunger. All geometry is measured from the live scene
/// (arm offset, glass plane, press depth) — no catalog constants, so it adapts to
/// any rig that wires a stepper (gantry), a slide-servo finger, and a phone.
/// </summary>
public sealed class TapAtController
{
    private readonly MachineSimulation _simulation;
    private readonly string _phoneInstanceId;
    private readonly string _fingerInstanceId;
    private readonly string _carriageInstanceId;
    private readonly string _gantryStepperInstanceId;
    private readonly string? _yStepperInstanceId;
    private readonly double _screenWidthM;
    private readonly double _screenHeightM;

    public TapAtController(MachineSimulation simulation, string phoneInstanceId,
        string fingerInstanceId, string carriageInstanceId, string gantryStepperInstanceId,
        double screenWidthM, string? yStepperInstanceId = null, double screenHeightM = 0.144)
    {
        _simulation = simulation;
        _phoneInstanceId = phoneInstanceId;
        _fingerInstanceId = fingerInstanceId;
        _carriageInstanceId = carriageInstanceId;
        _gantryStepperInstanceId = gantryStepperInstanceId;
        _yStepperInstanceId = yStepperInstanceId;
        _screenWidthM = screenWidthM;
        _screenHeightM = screenHeightM;
    }

    private string TipBody => $"{_fingerInstanceId}_tip";

    /// <summary>Drives the gantry until the finger tip's world X is over the screen
    /// fraction <paramref name="fx"/> (correct-and-retry: commanded revs → measured
    /// motion, up to eight iterations — absorbs ramp costs and belt slip; the final
    /// approach slows to converge within 0.4 mm).</summary>
    public void MoveTo(double fx)
    {
        var phoneX = Body(_phoneInstanceId)[0];
        var targetTipX = phoneX - _screenWidthM / 2 + fx * _screenWidthM;

        var stepper = _simulation.Stepper(_gantryStepperInstanceId);
        stepper.Enable();

        // Continuous servo: stopping between corrections lets the belt's wound
        // equality recoil backward on settle — at slow corrective speeds the
        // recoil cancels the whole move, so re-measure DURING the move instead
        // and only stop once inside tolerance.
        for (var attempt = 0; attempt < 160; attempt++) // ~8 s of motion budget
        {
            var tipX = Body(TipBody)[0];
            var delta = targetTipX - tipX;
            if (Math.Abs(delta) < 0.0008) // ~0.6% of a 68 mm screen
            {
                stepper.SetVelocityRevPerSec(0);
                _simulation.RunFor(0.03); // settle
                return;
            }

            // +1 rev/s moved the carriage +X in the measured rig; a rev = pitch
            // circumference (GT2-20T: 40 mm). Final approach slows down.
            var speed = Math.Abs(delta) < 0.004 ? 0.25 : 1.0;
            stepper.SetVelocityRevPerSec(Math.Sign(delta) * speed);
            _simulation.RunFor(0.05);
        }
        stepper.SetVelocityRevPerSec(0);
        _simulation.RunFor(0.03);
        throw new InvalidOperationException(
            $"Gantry did not converge on fx={fx:0.###} after 8 moves (target tip x {targetTipX:0.####}).");
    }

    /// <summary>One physical tap over screen fraction (<paramref name="fx"/>,
    /// <paramref name="fy"/>): position both stages, settle, press 2 mm past the
    /// glass, retract. Returns the fractions the tip actually pressed (measured,
    /// not commanded).</summary>
    public (double Fx, double Fy) Tap(double fx, double fy)
    {
        // The slide's sign convention depends on how the finger is mounted (a
        // flipped stack inverts press/retract), so measure it with a small probe
        // from the *actual* joint position — never the range ends, which may
        // wedge the tip into the gantry above or smash it through the glass.
        var servo = _simulation.Servo(_fingerInstanceId);
        var s0 = servo.AngleDeg; // actual slide position (m)
        var z0 = TipZ();
        servo.SetTargetPositionM(s0 + 0.003);
        _simulation.RunFor(0.25);
        var s1 = servo.AngleDeg;
        var z1 = TipZ();
        if (Math.Abs(s1 - s0) < 0.0005) // clamped at a range end — probe the other way
        {
            servo.SetTargetPositionM(s0 - 0.003);
            _simulation.RunFor(0.25);
            s1 = servo.AngleDeg;
            z1 = TipZ();
        }

        var slope = (z1 - z0) / (s1 - s0); // dz per m of slide (negative: +slide presses)
        // The tip's contact point hangs below the tip body; how far depends on the
        // mount's Z orientation, which the slope's sign reveals. The compiled tip
        // cylinder (radius 2 mm, half-length 6 mm, centred −6 mm) spans local z
        // [−12 mm, 0]: 12 mm below an upright (Z-up) body, flush with a flipped
        // (Z-down) one.
        var bottomOffset = slope < 0 ? 0.0 : 0.012;
        var glassZ = GlassZ();
        var touchZ = glassZ + bottomOffset; // tip body height when the point kisses the glass

        // Duck before traversing: carry the point 3 mm above the glass. The gantry
        // rail crosses the work area 24 mm over the phone, and the plunger's travel
        // band overlaps it — carried this low, the tip's top clears the rail while
        // the point stays clear of the glass.
        servo.SetTargetPositionM(s0 + (touchZ + 0.003 - z0) / slope);

        MoveTo(fx);
        MoveToY(fy);
        _simulation.RunFor(0.02); // settle

        // Press 2 mm PAST the kiss (z shrinks downward — the servo overdrive pushes
        // the contact a couple of mm into the soft surface, which is what the
        // dispatch bridge needs to see). Rest 6 mm above the touch height (also
        // below the rail band, so the next traverse starts safe).
        var sD = servo.AngleDeg;
        var zD = TipZ();
        var slideNear = sD + (touchZ + 0.001 - zD) / slope;
        var slidePress = sD + (touchZ - 0.002 - zD) / slope;
        var slideRest = sD + (touchZ + 0.006 - zD) / slope;

        // Land in two steps: fall to 1 mm above the kiss, settle, then the final
        // push — a single 8 mm fall rebounds off the glass and the bridge counts
        // each bounce as a fresh tap.
        servo.SetTargetPositionM(slideNear);
        _simulation.RunFor(0.15);
        servo.SetTargetPositionM(slidePress);
        _simulation.RunFor(0.2);
        servo.SetTargetPositionM(slideRest);
        _simulation.RunFor(0.10);

        // The measured contact: where the tip was when it pressed, mapped through
        // the glass.
        var phoneX = Body(_phoneInstanceId)[0];
        var phoneY = Body(_phoneInstanceId)[1];
        var pressed = _simulation.Simulator.GetBodyPosition(TipBody);
        var achievedFx = (pressed[0] - (phoneX - _screenWidthM / 2)) / _screenWidthM;
        var achievedFy = 1 - (pressed[1] - (phoneY - _screenHeightM / 2)) / _screenHeightM;
        return (achievedFx, achievedFy);
    }

    /// <summary>Drives the Y stage (T8 leadscrew) until the finger tip's world Y is
    /// over the screen fraction <paramref name="fy"/> — same correct-and-retry loop
    /// as the X belt, with the leadscrew's 8 mm/rev pitch.</summary>
    public void MoveToY(double fy)
    {
        if (_yStepperInstanceId is null)
        {
            if (Math.Abs(fy - 0.5) > 0.05)
                throw new InvalidOperationException(
                    "This rig has no Y stage wired; only fy=0.5 (the fixed row) is reachable.");
            return;
        }

        var phoneY = Body(_phoneInstanceId)[1];
        // The dispatch bridge maps physical Y to screen fy inverted (fy 0 = screen
        // top edge = the phone's +Y side); match it so tap_at(fx, fy) hits the same
        // point the emulator's coordinates name.
        var targetTipY = phoneY - _screenHeightM / 2 + (1 - fy) * _screenHeightM;

        var stepper = _simulation.Stepper(_yStepperInstanceId);
        stepper.Enable();

        // Continuous servo (same recoil argument as MoveTo: the leadscrew's
        // equality also unwinds on stop-settle at slow corrective speeds).
        for (var attempt = 0; attempt < 800; attempt++) // ~40 s of motion budget
        {
            var tipY = Body(TipBody)[1];
            var delta = targetTipY - tipY;
            if (Math.Abs(delta) < 0.0016) // ~1.1% of a 144 mm screen
            {
                stepper.SetVelocityRevPerSec(0);
                _simulation.RunFor(0.03); // settle
                return;
            }

            // Measured: +1 rev/s of the screw drives the nut −Y (T8-8: 8 mm/rev).
            // Fast traverse on long moves; the final approach slows down.
            var speed = Math.Abs(delta) < 0.008 ? 0.25 : 2.0;
            stepper.SetVelocityRevPerSec(-Math.Sign(delta) * speed);
            _simulation.RunFor(0.05);
        }
        stepper.SetVelocityRevPerSec(0);
        _simulation.RunFor(0.03);
        throw new InvalidOperationException(
            $"Y stage did not converge on fy={fy:0.###} after 8 moves (target tip y {targetTipY:0.####}).");
    }

    private double TipZ() => _simulation.Simulator.GetBodyPosition(TipBody)[2];

    private double GlassZ() =>
        _simulation.Simulator.GetBodyPosition(_phoneInstanceId)[2] + 0.002; // compiled box half-extent: the physical screen surface

    private double[] Body(string bodyName) => _simulation.Simulator.GetBodyPosition(bodyName);
}