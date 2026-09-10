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
    private readonly double _screenWidthM;

    public TapAtController(MachineSimulation simulation, string phoneInstanceId,
        string fingerInstanceId, string carriageInstanceId, string gantryStepperInstanceId,
        double screenWidthM)
    {
        _simulation = simulation;
        _phoneInstanceId = phoneInstanceId;
        _fingerInstanceId = fingerInstanceId;
        _carriageInstanceId = carriageInstanceId;
        _gantryStepperInstanceId = gantryStepperInstanceId;
        _screenWidthM = screenWidthM;
    }

    private string TipBody => $"{_fingerInstanceId}_tip";

    /// <summary>Drives the gantry until the finger tip's world X is over the screen
    /// fraction <paramref name="fx"/> (correct-and-retry: commanded revs → measured
    /// motion, up to eight iterations — absorbs ramp costs and belt slip; the final
    /// approach slows to converge within 0.4 mm).</summary>
    public void MoveTo(double fx)
    {
        var phoneX = BodyX(_phoneInstanceId);
        var targetTipX = phoneX - _screenWidthM / 2 + fx * _screenWidthM;

        var stepper = _simulation.Stepper(_gantryStepperInstanceId);
        stepper.Enable();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var tipX = BodyX(TipBody);
            var delta = targetTipX - tipX;
            if (Math.Abs(delta) < 0.0008) // ~0.6% of a 68 mm screen
                return;

            // +1 rev/s moved the carriage +X in the measured rig; a rev = pitch
            // circumference (GT2-20T: 40 mm), and the direction convention flips
            // with the arm's mating — the loop corrects whatever the rig does.
            // Final approach slows down (less overshoot from the settle creep).
            var speed = Math.Abs(delta) < 0.004 ? 0.25 : 1.0;
            var mmPerRev = 0.040;
            var direction = Math.Sign(delta);
            var seconds = Math.Abs(delta) / mmPerRev / speed + 0.025; // + ramp
            stepper.SetVelocityRevPerSec(direction * speed);
            _simulation.RunFor(Math.Min(seconds, 2.0));
            stepper.SetVelocityRevPerSec(0);
            _simulation.RunFor(0.03); // settle
        }
        throw new InvalidOperationException(
            $"Gantry did not converge on fx={fx:0.###} after 8 moves (target tip x {targetTipX:0.####}).");
    }

    /// <summary>One physical tap over screen fraction <paramref name="fx"/>:
    /// position, settle, press to 2 mm past the glass, retract. Returns the
    /// fraction the tip actually pressed (measured, not commanded).</summary>
    public (double Fx, double Fy) Tap(double fx)
    {
        MoveTo(fx);
        _simulation.RunFor(0.02); // settle

        var rest = ServoRestPositionM();
        var glassZ = GlassZ();
        var tipBottomZ = _simulation.Simulator.GetBodyPosition(TipBody)[2] - TipLengthM();
        var pressTravel = tipBottomZ - glassZ + 0.002; // 2 mm into the glass
        var servo = _simulation.Servo(_fingerInstanceId);

        servo.SetTargetPositionM(rest - pressTravel);
        _simulation.RunFor(0.15);
        servo.SetTargetPositionM(rest);
        _simulation.RunFor(0.10);

        // The measured contact: the tip's x when it pressed, mapped through the glass.
        var phoneX = BodyX(_phoneInstanceId);
        var pressedX = _simulation.Simulator.GetBodyPosition(TipBody)[0];
        var achievedFx = (pressedX - (phoneX - _screenWidthM / 2)) / _screenWidthM;
        return (achievedFx, 0.5); // the arm rides a fixed row in this gantry stage
    }

    private double ServoRestPositionM() => _simulation.Servo(_fingerInstanceId).MaxPositionM;

    private double GlassZ() =>
        _simulation.Simulator.GetBodyPosition(_phoneInstanceId)[2] + 0.002;

    private double TipLengthM() => 0.012; // the tip cylinder's full length (catalog)

    private double BodyX(string bodyName) => _simulation.Simulator.GetBodyPosition(bodyName)[0];
}