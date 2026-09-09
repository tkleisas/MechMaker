namespace MechMaker.Engine;

/// <summary>Electromechanical parameters of a simulated stepper channel.</summary>
public sealed record StepperSpec
{
    /// <summary>Full steps per revolution (200 for a 1.8° hybrid stepper).</summary>
    public double StepsPerRevolution { get; init; } = 200;

    /// <summary>Microsteps the driver divides each full step into (affects motion ripple, not torque).</summary>
    public int Microsteps { get; init; } = 16;

    /// <summary>Holding torque in N·m (peak of the magnetic torque sinusoid).</summary>
    public double HoldingTorqueNm { get; init; } = 0.4;

    /// <summary>Viscous loss from winding resistance/back-EMF, N·m per rad/s.</summary>
    public double BackEmfDamping { get; init; } = 0.002;

    /// <summary>Electrical pole pairs per revolution: a 200-step stepper repeats its
    /// electrical cycle every 4 full steps, i.e. 50 cycles/rev.</summary>
    public double PolePairsPerRevolution => StepsPerRevolution / 4;

    public double FullStepRev => 1.0 / StepsPerRevolution;
}
