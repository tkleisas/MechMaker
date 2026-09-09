namespace MechMaker.Core.Mathematics;

/// <summary>Unit quaternion (w, x, y, z).</summary>
public readonly record struct Quat(double W, double X, double Y, double Z)
{
    public static readonly Quat Identity = new(1, 0, 0, 0);

    /// <summary>
    /// Rotation of 180 degrees about the Y axis. Used when mating connectors:
    /// it flips the connector Z axis (and X) while preserving the twist reference,
    /// so "Z points outward" composes naturally for butt joints.
    /// </summary>
    public static readonly Quat FlipZ = new(0, 0, 1, 0);

    public static Quat FromAxisAngle(Vec3 axis, double radians)
    {
        var a = axis.Normalized();
        var half = radians * 0.5;
        var s = Math.Sin(half);
        return new Quat(Math.Cos(half), a.X * s, a.Y * s, a.Z * s);
    }

    /// <summary>Extrinsic XYZ Euler angles in radians.</summary>
    public static Quat FromEulerXyz(Vec3 euler)
    {
        var qx = FromAxisAngle(Vec3.UnitX, euler.X);
        var qy = FromAxisAngle(Vec3.UnitY, euler.Y);
        var qz = FromAxisAngle(Vec3.UnitZ, euler.Z);
        return (qz * qy) * qx;
    }

    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    public Quat Conjugate() => new(W, -X, -Y, -Z);

    public Vec3 Rotate(Vec3 v)
    {
        var q = new Quat(0, v.X, v.Y, v.Z);
        var r = (this * q) * Conjugate();
        return new Vec3(r.X, r.Y, r.Z);
    }

    public Vec3 InverseRotate(Vec3 v) => Conjugate().Rotate(v);

    public Quat Normalized()
    {
        var len = Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
        return len < 1e-12 ? Identity : new Quat(W / len, X / len, Y / len, Z / len);
    }

    public override string ToString() => $"{W:0.######} {X:0.######} {Y:0.######} {Z:0.######}";
}
