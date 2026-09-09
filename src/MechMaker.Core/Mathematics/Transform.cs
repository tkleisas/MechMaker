using MechMaker.Core.Model;

namespace MechMaker.Core.Mathematics;

/// <summary>Position + rotation, composable. All rotations are unit quaternions.</summary>
public readonly record struct Transform(Vec3 Position, Quat Rotation)
{
    public static readonly Transform Identity = new(Vec3.Zero, Quat.Identity);

    public static Transform Compose(Transform outer, Transform inner)
        => new(outer.Position + outer.Rotation.Rotate(inner.Position), outer.Rotation * inner.Rotation);

    public static Transform operator *(Transform outer, Transform inner) => Compose(outer, inner);

    public Transform Inverse()
    {
        var inv = Rotation.Conjugate();
        return new Transform(inv.Rotate(Position * -1.0), inv);
    }

    public Vec3 TransformPoint(Vec3 point) => Position + Rotation.Rotate(point);

    public Vec3 TransformDirection(Vec3 direction) => Rotation.Rotate(direction);
}
