namespace MechMaker.Core.Model;

using System.Runtime.Serialization;

public enum ConnectorType
{
    /// <summary>NEMA-style motor shaft, 5 mm diameter (male).</summary>
    [EnumMember(Value = "shaft_5mm")]
    Shaft5mm,

    /// <summary>Bore for a 5 mm shaft, e.g. a GT2 pulley (female).</summary>
    [EnumMember(Value = "bore_5mm")]
    Bore5mm,

    /// <summary>End of a 2020 aluminium T-slot extrusion.</summary>
    [EnumMember(Value = "tslot_2020")]
    TSlot2020,

    /// <summary>M3 bolt-hole pattern (plates, brackets, motor face).</summary>
    [EnumMember(Value = "bolt_m3")]
    BoltM3,

    /// <summary>Top mounting face of an MGN12 linear rail carriage.</summary>
    [EnumMember(Value = "mgn12_carriage")]
    Mgn12Carriage,

    /// <summary>Top surface of an MGN12 linear rail (accepts a carriage).</summary>
    [EnumMember(Value = "mgn12_rail")]
    Mgn12Rail,

    /// <summary>Loop end of a GT2 belt (transmission element).</summary>
    [EnumMember(Value = "belt_loop")]
    BeltLoop,

    /// <summary>Belt clamp face (on a belt, and on the part that grips the belt, e.g. a carriage).</summary>
    [EnumMember(Value = "belt_clamp")]
    BeltClamp,

    /// <summary>Threaded shaft of an integrated leadscrew motor (e.g. T8-8 on a NEMA 17).</summary>
    [EnumMember(Value = "screw_t8")]
    ScrewT8,

    /// <summary>Bore of a leadscrew nut (female, rides the screw thread).</summary>
    [EnumMember(Value = "nut_t8")]
    NutT8,

    /// <summary>Gear mesh face of a spur gear (teeth engage another gear's teeth).</summary>
    [EnumMember(Value = "gear_teeth")]
    GearTeeth
}

public enum JointKind
{
    /// <summary>Rigid connection (T-slot end to end, bolted faces).</summary>
    Weld,

    /// <summary>Rotation about the shared connector axis (shaft into bore).</summary>
    Hinge,

    /// <summary>Translation along the shared connector axis (carriage on rail, nut on screw).</summary>
    Slide,

    /// <summary>Flexible transmission link via a belt part (two pulleys, fixed ratio).</summary>
    Belt,

    /// <summary>The part grips the belt (carriage belt clamp) — kinematic coupling, not a tree weld.</summary>
    Clamp,

    /// <summary>A nut rides a screw: slide joint on the child, coupled to the rotor at lead/(2π).</summary>
    Screw,

    /// <summary>Two spur gears meshed: coupled hinges at the teeth ratio (opposite world direction).</summary>
    Gear
}

public static class ConnectorRules
{
    /// <summary>Pairs that may physically mate. Order-independent.</summary>
    private static readonly HashSet<(ConnectorType, ConnectorType)> CompatiblePairs = new()
    {
        (ConnectorType.Shaft5mm, ConnectorType.Bore5mm),
        (ConnectorType.TSlot2020, ConnectorType.TSlot2020),
        (ConnectorType.BoltM3, ConnectorType.BoltM3),
        (ConnectorType.Mgn12Carriage, ConnectorType.Mgn12Rail),
        (ConnectorType.BeltLoop, ConnectorType.BeltLoop),
        (ConnectorType.BeltClamp, ConnectorType.BeltClamp),
        (ConnectorType.ScrewT8, ConnectorType.NutT8),
        (ConnectorType.GearTeeth, ConnectorType.GearTeeth)
    };

    public static bool AreCompatible(ConnectorType a, ConnectorType b)
    {
        if (a == b && a is ConnectorType.TSlot2020 or ConnectorType.BoltM3 or ConnectorType.BeltLoop
            or ConnectorType.BeltClamp or ConnectorType.GearTeeth)
            return true;
        var (x, y) = (int)a <= (int)b ? (a, b) : (b, a);
        return CompatiblePairs.Contains((x, y));
    }

    public static JointKind InferJoint(ConnectorType a, ConnectorType b)
        => (a, b) switch
        {
            (ConnectorType.Shaft5mm, ConnectorType.Bore5mm) or (ConnectorType.Bore5mm, ConnectorType.Shaft5mm)
                => JointKind.Hinge,
            (ConnectorType.Mgn12Carriage, ConnectorType.Mgn12Rail) or (ConnectorType.Mgn12Rail, ConnectorType.Mgn12Carriage)
                => JointKind.Slide,
            (ConnectorType.BeltLoop, ConnectorType.BeltLoop) => JointKind.Belt,
            (ConnectorType.BeltClamp, ConnectorType.BeltClamp) => JointKind.Clamp,
            (ConnectorType.ScrewT8, ConnectorType.NutT8) or (ConnectorType.NutT8, ConnectorType.ScrewT8)
                => JointKind.Screw,
            (ConnectorType.GearTeeth, ConnectorType.GearTeeth) => JointKind.Gear,
            _ => JointKind.Weld
        };
}
