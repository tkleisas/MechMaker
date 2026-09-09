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

    /// <summary>
    /// Inverse of <see cref="FromEulerXyz"/>: extrinsic XYZ Euler angles in radians.
    /// At the gimbal singularity (middle angle ±90°) the free twist is folded into
    /// the X angle with Z set to zero, so the round trip always reproduces the input.
    /// </summary>
    public Vec3 ToEulerXyz()
    {
        // R = Rz·Ry·Rx matrix entries (row, col):
        var r00 = 1 - 2 * (Y * Y + Z * Z);
        var r10 = 2 * (X * Y + W * Z);
        var r20 = 2 * (X * Z - W * Y);
        var r21 = 2 * (Y * Z + W * X);
        var r22 = 1 - 2 * (X * X + Y * Y);
        var r01 = 2 * (X * Y - W * Z);
        var r11 = 1 - 2 * (X * X + Z * Z);

        var cy = Math.Sqrt(r21 * r21 + r22 * r22);
        var x = Math.Atan2(r21, r22);
        var y = Math.Atan2(-r20, cy);
        var z = Math.Atan2(r10, r00);

        if (cy < 1e-10)
        {
            // Gimbal lock: only the X±Z combination is determined. Set Z = 0 and
            // recover the combination from R01/R11 (derived for θy = ±90°).
            var combination = Math.Atan2(r01, r11);
            x = r20 > 0 ? -combination : combination; // θy=+90° keeps (Z−X), θy=−90° keeps (Z+X)
            y = r20 < 0 ? Math.PI / 2 : -Math.PI / 2;
            z = 0;
        }
        return new Vec3(x, y, z);
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
