namespace MechMaker.Engine;

public enum SceneShapeKind
{
    Box,
    Cylinder
}

/// <summary>A visual primitive in world coordinates — the unit the renderer consumes.</summary>
/// <param name="HalfX">Box half-extent X, or cylinder radius.</param>
/// <param name="HalfY">Box half-extent Y, or cylinder half-length.</param>
/// <param name="HalfZ">Box half-extent Z (unused for cylinders).</param>
public readonly record struct SceneShape(
    string Name,
    SceneShapeKind Kind,
    double HalfX, double HalfY, double HalfZ,
    // Row-major 3x3 rotation + position.
    double Px, double Py, double Pz,
    double R00, double R01, double R02,
    double R10, double R11, double R12,
    double R20, double R21, double R22,
    double R, double G, double B, double A);
