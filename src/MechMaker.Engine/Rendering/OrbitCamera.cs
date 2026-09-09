namespace MechMaker.Engine.Rendering;

/// <summary>Orbit camera: yaw/pitch around a target at a distance, vertical FOV fixed.</summary>
public sealed class OrbitCamera
{
    public double YawRad = 0.7;
    public double PitchRad = 0.5;
    public double Distance = 0.9;
    public double TargetX, TargetY, TargetZ;
    public double FovRad { get; set; } = 50.0 * Math.PI / 180.0;

    public void Orbit(double dxPixels, double dyPixels)
    {
        YawRad -= dxPixels * 0.008;
        PitchRad = Math.Clamp(PitchRad + dyPixels * 0.008, -Math.PI / 2 + 0.05, Math.PI / 2 - 0.005);
    }

    public void Zoom(double wheelDelta)
    {
        Distance = Math.Clamp(Distance * (wheelDelta > 0 ? 0.9 : 1.1), 0.05, 20);
    }

    /// <summary>Eye position in world space (right-handed, Z up).</summary>
    public (double X, double Y, double Z) Eye()
    {
        var cp = Math.Cos(PitchRad);
        return (TargetX + Distance * cp * Math.Cos(YawRad),
                TargetY + Distance * cp * Math.Sin(YawRad),
                TargetZ + Distance * Math.Sin(PitchRad));
    }
}
