using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;

namespace MechMaker.Core.Tests;

/// <summary>Euler/quaternion conversion: exact round trips, including the gimbal singularity
/// that axis-aligned connector poses (rot [0,-90,0]) hit constantly.</summary>
public class EulerTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, 0, 0)]
    [InlineData(0, -90, 0)]   // gimbal lock (bracket/mount connector poses)
    [InlineData(0, 90, 0)]
    [InlineData(180, 0, 0)]
    [InlineData(0, -90, 90)]  // gimbal lock with a twist
    [InlineData(30, 45, 60)]
    [InlineData(-12.5, 170, 88)]
    [InlineData(10, -90, -45)]
    public void Euler_round_trips_through_a_quaternion(double rx, double ry, double rz)
    {
        var euler = new Vec3(rx * Math.PI / 180, ry * Math.PI / 180, rz * Math.PI / 180);
        var q = Quat.FromEulerXyz(euler);

        var back = q.ToEulerXyz();

        var reconstructed = Quat.FromEulerXyz(back);
        // Same rotation (q and -q are identical rotations):
        var sameHemisphere = reconstructed.W * q.W >= 0;
        var dot = Math.Abs(reconstructed.W * q.W + reconstructed.X * q.X
                           + reconstructed.Y * q.Y + reconstructed.Z * q.Z);
        Assert.True(sameHemisphere || dot > 0.999999,
            $"round trip failed for ({rx}, {ry}, {rz}): got {reconstructed}");
        Assert.True(dot > 0.999999, $"rotation differs for ({rx}, {ry}, {rz}): dot={dot}");
    }

    [Fact]
    public void Pose_and_transform_round_trip()
    {
        var pose = new Pose { Position = new Vec3(0.1, -0.2, 0.3), RotationEulerDeg = new Vec3(10, -90, 25) };
        var transform = new Transform(pose.Position, pose.Rotation);
        var back = Pose.FromTransform(transform);

        Assert.Equal(pose.Position.X, back.Position.X, 12);
        Assert.Equal(pose.Position.Y, back.Position.Y, 12);
        Assert.Equal(pose.Position.Z, back.Position.Z, 12);
        Assert.Equal(pose.Rotation.W, back.Rotation.W, 12);
        Assert.Equal(pose.Rotation.X, back.Rotation.X, 12);
        Assert.Equal(pose.Rotation.Y, back.Rotation.Y, 12);
        Assert.Equal(pose.Rotation.Z, back.Rotation.Z, 12);
    }

    [Fact]
    public void FlipZ_is_180_degrees_about_y()
    {
        var fromEuler = Quat.FromAxisAngle(Vec3.UnitY, Math.PI);
        Assert.Equal(fromEuler.W, Quat.FlipZ.W, 12);
        Assert.Equal(fromEuler.X, Quat.FlipZ.X, 12);
        Assert.Equal(fromEuler.Y, Quat.FlipZ.Y, 12);
        Assert.Equal(fromEuler.Z, Quat.FlipZ.Z, 12);
    }
}
