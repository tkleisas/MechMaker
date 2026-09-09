using MechMaker.Engine;

namespace MechMaker.App;

/// <summary>
/// Viewport-only reference geometry: a grid on the z=0 plane plus an RGB
/// axis triad at the origin. Purely visual (never compiled into the MJCF model) —
/// it exists so placement has a spatial reference: where z=0 is, what scale a
/// metre is, and which way the axes point.
/// </summary>
public static class ReferencePlane
{
    /// <summary>User-selectable grid appearance (color, thickness, spacing).</summary>
    public sealed record GridStyle(
        double Spacing,
        double Extent,
        double HalfThickness,
        double LineR, double LineG, double LineB);

    public static IReadOnlyList<SceneShape> Shapes(GridStyle? style = null)
    {
        style ??= new GridStyle(Spacing: 0.1, Extent: 0.5, HalfThickness: 0.0004,
            LineR: 0.23, LineG: 0.24, LineB: 0.27);

        var shapes = new List<SceneShape>();

        // Grid lines: thin flat bars just below z=0, lying in the z=0 plane.
        // The line through the origin along X is red-tinted, along Y green-tinted.
        for (var i = 0; i * style.Spacing <= style.Extent + 1e-9; i++)
        {
            var offset = i * style.Spacing;
            AddLine(shapes, x: offset, y: 0, alongY: true, style,
                color: i == 0 ? (0.26, 0.5, 0.26) : (style.LineR, style.LineG, style.LineB));
            AddLine(shapes, x: 0, y: offset, alongY: false, style,
                color: i == 0 ? (0.5, 0.26, 0.26) : (style.LineR, style.LineG, style.LineB));
            if (offset > 0)
            {
                AddLine(shapes, x: -offset, y: 0, alongY: true, style, color: (style.LineR, style.LineG, style.LineB));
                AddLine(shapes, x: 0, y: -offset, alongY: false, style, color: (style.LineR, style.LineG, style.LineB));
            }
        }

        // Axis triad at the origin, sitting on the plane.
        const double barR = 0.0015;
        shapes.Add(Bar(0.12, barR * 2, barR, x: 0.06, y: 0, z: 0.0018, r: 0.85, g: 0.3, b: 0.25));
        shapes.Add(Bar(barR, 0.12, barR, x: 0, y: 0.06, z: 0.0018, r: 0.3, g: 0.85, b: 0.3));
        shapes.Add(Bar(barR, barR, 0.12, x: 0, y: 0, z: 0.06, r: 0.3, g: 0.5, b: 0.95));

        return shapes;
    }

    private static void AddLine(List<SceneShape> shapes, double x, double y, bool alongY,
        GridStyle style, (double r, double g, double b) color)
    {
        shapes.Add(new SceneShape(
            Name: "ref_grid",
            Kind: SceneShapeKind.Box,
            HalfX: alongY ? style.HalfThickness : style.Extent,
            HalfY: alongY ? style.Extent : style.HalfThickness,
            HalfZ: style.HalfThickness,
            Px: x, Py: y, Pz: -style.HalfThickness,
            R00: 1, R01: 0, R02: 0,
            R10: 0, R11: 1, R12: 0,
            R20: 0, R21: 0, R22: 1,
            R: color.r, G: color.g, B: color.b, A: 1));
    }

    private static SceneShape Bar(double hx, double hy, double hz,
        double x, double y, double z, double r, double g, double b)
    {
        return new SceneShape(
            Name: "ref_axis",
            Kind: SceneShapeKind.Box,
            HalfX: hx, HalfY: hy, HalfZ: hz,
            Px: x, Py: y, Pz: z,
            R00: 1, R01: 0, R02: 0,
            R10: 0, R11: 1, R12: 0,
            R20: 0, R21: 0, R22: 1,
            R: r, G: g, B: b, A: 1);
    }
}