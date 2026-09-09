using MechMaker.Core.Mathematics;

namespace MechMaker.Core.Model;

public enum PartCategory
{
    Structure,
    Motor,
    Transmission,
    [System.Runtime.Serialization.EnumMember(Value = "linear_motion")]
    LinearMotion,
    Sensor,
    Electronics,
    Tool
}

public enum ShapeKind
{
    Box,
    Cylinder
}

/// <summary>Primitive collision/visual geometry, posed relative to its body.</summary>
public sealed record Shape
{
    public ShapeKind Kind { get; init; } = ShapeKind.Box;

    /// <summary>Full extents for boxes; for cylinders Radius/Length are read from Extents.X / Extents.Y.</summary>
    public Vec3 Extents { get; init; } = new(0.01, 0.01, 0.01);

    public Pose RelativePose { get; init; } = Pose.Identity;

    /// <summary>Hex colour for rendering, e.g. "0.2 0.2 0.25" is written to MJCF as rgba — keep as three-space string.</summary>
    public string Rgba { get; init; } = "0.6 0.6 0.65 1";
}

/// <summary>Connector port on a part body. Local +Z axis is the mating direction (authored pointing outward); local +Z is also the rotation axis for hinge interfaces.</summary>
public sealed record ConnectorDefinition
{
    public string Name { get; init; } = "";
    public ConnectorType Type { get; init; }
    public Pose Pose { get; init; } = Pose.Identity;

    /// <summary>Motion axis for slide-type interfaces (e.g. the rail's length direction); default +Z.</summary>
    public Vec3 Axis { get; init; } = new(0, 0, 1);

    /// <summary>Which body of the part this connector belongs to (defaults to the root body).</summary>
    public string Body { get; init; } = "root";
}

/// <summary>Rotating sub-body of a part (e.g. the rotor + shaft of a stepper motor).</summary>
public sealed record PartBody
{
    public string Name { get; init; } = "root";
    public double MassKg { get; init; }
    public List<Shape> Shapes { get; init; } = [];

    /// <summary>Parent body name within the same part; null = part root.</summary>
    public string? ParentBody { get; init; }

    /// <summary>Pose of this body relative to its parent body.</summary>
    public Pose RelativePose { get; init; } = Pose.Identity;

    /// <summary>Actuated joint carried by this body (motor rotors declare Hinge).</summary>
    public JointActuation Actuated { get; init; } = JointActuation.None;
}

public enum JointActuation
{
    None,
    Hinge,
    Slide
}

/// <summary>Electromechanical parameters for motors.</summary>
public sealed record MotorSpec
{
    public MotorKind Kind { get; init; } = MotorKind.Stepper;
    public double StepAngleDeg { get; init; } = 1.8;
    public double HoldingTorqueNm { get; init; } = 0.4;
    public double RatedCurrentA { get; init; } = 1.7;
    public double MaxSpeedRps { get; init; } = 10;

    /// <summary>
    /// Reflected inertia at the rotor joint (kg·m²). Datasheet rotor inertia of a bare
    /// NEMA 17 is ~5e-7, but shaft, coupling, and the current controller's effective
    /// inertia justify a larger value; this also keeps the magnetic spring integrable
    /// at the physics timestep.
    /// </summary>
    public double RotorInertiaKgM2 { get; init; } = 4e-5;
}

public enum MotorKind
{
    Stepper,
    Servo,
    Dc
}

public sealed record PartDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public PartCategory Category { get; init; }
    public string Description { get; init; } = "";
    public List<PartBody> Bodies { get; init; } = [];
    public List<ConnectorDefinition> Connectors { get; init; } = [];
    public MotorSpec? Motor { get; init; }

    /// <summary>Free-form spec values (pulley teeth, pitch radius, rail travel limits...).</summary>
    public Dictionary<string, double> Params { get; init; } = [];

    /// <summary>True for parts that are purely transmission (belt) and never rigidly mounted.</summary>
    public bool IsTransmissionElement { get; init; }

    public PartBody RootBody => Bodies[0];
}
