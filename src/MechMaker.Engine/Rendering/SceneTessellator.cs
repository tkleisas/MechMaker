namespace MechMaker.Engine.Rendering;

/// <summary>
/// Triangle payload: world-space corners + per-face normal + color, ready to rasterize.
/// </summary>
public readonly record struct RenderTriangle(
    double Ax, double Ay, double Az,
    double Bx, double By, double Bz,
    double Cx, double Cy, double Cz,
    double Nx, double Ny, double Nz,
    byte R, byte G, byte B);

public static class SceneTessellator
{
    /// <summary>Converts scene shapes to world-space triangles (flat shaded).</summary>
    public static List<RenderTriangle> Tessellate(IReadOnlyList<SceneShape> shapes, int cylinderSegments = 20)
    {
        var triangles = new List<RenderTriangle>(shapes.Count * 40);
        foreach (var s in shapes)
            if (s.Kind == SceneShapeKind.Box)
                AddBox(triangles, s);
            else
                AddCylinder(triangles, s, cylinderSegments);
        return triangles;
    }

    private static void AddBox(List<RenderTriangle> outTris, in SceneShape s)
    {
        // Half extents; cylinders store radius in HalfX and half-length in HalfY.
        var hx = s.HalfX; var hy = s.HalfY; var hz = s.HalfZ;
        var c = ColorOf(s);

        Span<(double x, double y, double z)> corners = stackalloc (double, double, double)[8]
        {
            (-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
            (-hx, -hy, +hz), (hx, -hy, +hz), (hx, hy, +hz), (-hx, hy, +hz)
        };

        Span<(double x, double y, double z)> world = stackalloc (double, double, double)[8];
        for (var i = 0; i < 8; i++)
            world[i] = ToWorld(s, corners[i].x, corners[i].y, corners[i].z);

        AddQuad(outTris, s, world, 0, 1, 2, 3, 0, 0, -1);   // -Z
        AddQuad(outTris, s, world, 4, 5, 6, 7, 0, 0, 1);    // +Z
        AddQuad(outTris, s, world, 0, 1, 5, 4, 0, -1, 0);   // -Y
        AddQuad(outTris, s, world, 3, 2, 6, 7, 0, 1, 0);    // +Y
        AddQuad(outTris, s, world, 0, 4, 7, 3, -1, 0, 0);   // -X
        AddQuad(outTris, s, world, 1, 2, 6, 5, 1, 0, 0);    // +X
    }

    private static void AddCylinder(List<RenderTriangle> outTris, in SceneShape s, int segments)
    {
        // Local frame: axis along local Z (MuJoCo cylinders are Z-aligned), radius HalfX,
        // half-length HalfY. The scene pose maps local -> world.
        var radius = s.HalfX;
        var halfLen = s.HalfY;
        var c = ColorOf(s);

        Span<(double x, double y, double z)> bottom = stackalloc (double, double, double)[segments];
        Span<(double x, double y, double z)> top = stackalloc (double, double, double)[segments];
        Span<(double x, double y)> ring = stackalloc (double, double)[segments];
        for (var i = 0; i < segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            ring[i] = (radius * Math.Cos(a), radius * Math.Sin(a));
            bottom[i] = ToWorld(s, ring[i].x, ring[i].y, -halfLen);
            top[i] = ToWorld(s, ring[i].x, ring[i].y, +halfLen);
        }

        // Side quads with interpolated radial normals.
        for (var i = 0; i < segments; i++)
        {
            var j = (i + 1) % segments;
            var b0 = ToWorldDir(s, ring[i].x / radius, ring[i].y / radius, 0);
            var b1 = ToWorldDir(s, ring[j].x / radius, ring[j].y / radius, 0);
            var mx = (b0.x + b1.x) * 0.5;
            var my = (b0.y + b1.y) * 0.5;
            var mz = (b0.z + b1.z) * 0.5;
            var w0 = bottom[i]; var w1 = bottom[j]; var w2 = top[j]; var w3 = top[i];
            outTris.Add(new RenderTriangle(w0.x, w0.y, w0.z, w1.x, w1.y, w1.z, w2.x, w2.y, w2.z, mx, my, mz, B(c.r), B(c.g), B(c.b)));
            outTris.Add(new RenderTriangle(w0.x, w0.y, w0.z, w2.x, w2.y, w2.z, w3.x, w3.y, w3.z, mx, my, mz, B(c.r), B(c.g), B(c.b)));
        }

        // Caps.
        var centerBottom = ToWorld(s, 0, 0, -halfLen);
        var centerTop = ToWorld(s, 0, 0, +halfLen);
        var nb = ToWorldDir(s, 0, 0, -1);
        var nt = ToWorldDir(s, 0, 0, 1);
        for (var i = 0; i < segments; i++)
        {
            var j = (i + 1) % segments;
            outTris.Add(new RenderTriangle(centerBottom.x, centerBottom.y, centerBottom.z,
                bottom[j].x, bottom[j].y, bottom[j].z, bottom[i].x, bottom[i].y, bottom[i].z,
                nb.x, nb.y, nb.z, B(c.r), B(c.g), B(c.b)));
            outTris.Add(new RenderTriangle(centerTop.x, centerTop.y, centerTop.z,
                top[i].x, top[i].y, top[i].z, top[j].x, top[j].y, top[j].z,
                nt.x, nt.y, nt.z, B(c.r), B(c.g), B(c.b)));
        }
    }

    private static (double r, double g, double b) ColorOf(in SceneShape s)
        => (Math.Clamp(s.R, 0, 1), Math.Clamp(s.G, 0, 1), Math.Clamp(s.B, 0, 1));

    private static byte B(double v) => (byte)Math.Clamp(v * 255, 0, 255);

    private static (double x, double y, double z) ToWorld(in SceneShape s, double x, double y, double z)
        => (s.Px + s.R00 * x + s.R01 * y + s.R02 * z,
            s.Py + s.R10 * x + s.R11 * y + s.R12 * z,
            s.Pz + s.R20 * x + s.R21 * y + s.R22 * z);

    private static (double x, double y, double z) ToWorldDir(in SceneShape s, double x, double y, double z)
        => (s.R00 * x + s.R01 * y + s.R02 * z,
            s.R10 * x + s.R11 * y + s.R12 * z,
            s.R20 * x + s.R21 * y + s.R22 * z);

    private static void AddQuad(List<RenderTriangle> outTris, in SceneShape s,
        ReadOnlySpan<(double x, double y, double z)> w, int i0, int i1, int i2, int i3,
        double nx, double ny, double nz)
    {
        var n = ToWorldDir(s, nx, ny, nz);
        var c = ColorOf(s);
        var a = w[i0];
        var b = w[i1];
        var d = w[i2];
        var e = w[i3];
        outTris.Add(new RenderTriangle(a.x, a.y, a.z, b.x, b.y, b.z, d.x, d.y, d.z, n.x, n.y, n.z, B(c.r), B(c.g), B(c.b)));
        outTris.Add(new RenderTriangle(a.x, a.y, a.z, d.x, d.y, d.z, e.x, e.y, e.z, n.x, n.y, n.z, B(c.r), B(c.g), B(c.b)));
    }
}
