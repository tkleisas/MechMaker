using MechMaker.Core.Mathematics;

namespace MechMaker.Core.Model;

/// <summary>A position plus orientation. Rotation is authored as extrinsic XYZ Euler angles in degrees (human/LLM friendly).</summary>
public sealed record Pose
{
    public Vec3 Position { get; init; } = Vec3.Zero;
    public Vec3 RotationEulerDeg { get; init; } = Vec3.Zero;

    public static readonly Pose Identity = new();

    public Quat Rotation => Quat.FromEulerXyz(ToRadians(RotationEulerDeg));

    private static Vec3 ToRadians(Vec3 deg) => new(
        deg.X * Math.PI / 180.0,
        deg.Y * Math.PI / 180.0,
        deg.Z * Math.PI / 180.0);
}
