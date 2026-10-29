using MechMaker.Core.Geometry;
using MechMaker.Core.Mathematics;

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
    public static List<RenderTriangle> Tessellate(IReadOnlyList<SceneShape> shapes, int segments = 20)
    {
        var triangles = new List<RenderTriangle>(shapes.Count * 40);
        foreach (var s in shapes)
        {
            switch (s.Kind)
            {
                case SceneShapeKind.Box:
                    AddBox(triangles, s);
                    break;
                case SceneShapeKind.Cylinder:
                    AddCylinder(triangles, s, segments);
                    break;
                case SceneShapeKind.Sphere:
                    AddEllipsoid(triangles, s, segments, s.HalfX, s.HalfX, s.HalfX);
                    break;
                case SceneShapeKind.Ellipsoid:
                    AddEllipsoid(triangles, s, segments, s.HalfX, s.HalfY, s.HalfZ);
                    break;
                case SceneShapeKind.Capsule:
                    AddCapsule(triangles, s, segments);
                    break;
                case SceneShapeKind.Mesh:
                    AddMesh(triangles, s);
                    break;
            }
        }
        return triangles;
    }

    private static void AddBox(List<RenderTriangle> outTris, in SceneShape s)
    {
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

        AddQuad(outTris, s, world, 0, 1, 2, 3, 0, 0, -1, c);
        AddQuad(outTris, s, world, 4, 5, 6, 7, 0, 0, 1, c);
        AddQuad(outTris, s, world, 0, 1, 5, 4, 0, -1, 0, c);
        AddQuad(outTris, s, world, 3, 2, 6, 7, 0, 1, 0, c);
        AddQuad(outTris, s, world, 0, 4, 7, 3, -1, 0, 0, c);
        AddQuad(outTris, s, world, 1, 2, 6, 5, 1, 0, 0, c);
    }

    private static void AddCylinder(List<RenderTriangle> outTris, in SceneShape s, int segments)
    {
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

        for (var i = 0; i < segments; i++)
        {
            var j = (i + 1) % segments;
            var b0 = ToWorldDir(s, ring[i].x / radius, ring[i].y / radius, 0);
            var b1 = ToWorldDir(s, ring[j].x / radius, ring[j].y / radius, 0);
            var mx = (b0.x + b1.x) * 0.5; var my = (b0.y + b1.y) * 0.5; var mz = (b0.z + b1.z) * 0.5;
            var w0 = bottom[i]; var w1 = bottom[j]; var w2 = top[j]; var w3 = top[i];
            outTris.Add(new RenderTriangle(w0.x, w0.y, w0.z, w1.x, w1.y, w1.z, w2.x, w2.y, w2.z, mx, my, mz, B(c.r), B(c.g), B(c.b)));
            outTris.Add(new RenderTriangle(w0.x, w0.y, w0.z, w2.x, w2.y, w2.z, w3.x, w3.y, w3.z, mx, my, mz, B(c.r), B(c.g), B(c.b)));
        }

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

    /// <summary>UV sphere/ellipsoid with radii (rx, ry, rz) along local X/Y/Z.</summary>
    private static void AddEllipsoid(List<RenderTriangle> outTris, in SceneShape s, int segments,
        double rx, double ry, double rz, int rings = 12)
    {
        var c = ColorOf(s);
        for (var lat = 0; lat < rings; lat++)
        {
            var t0 = Math.PI * lat / rings;
            var t1 = Math.PI * (lat + 1) / rings;
            for (var lon = 0; lon < segments; lon++)
            {
                var p0 = 2 * Math.PI * lon / segments;
                var p1 = 2 * Math.PI * (lon + 1) / segments;
                var a = EllipsoidPoint(rx, ry, rz, t0, p0);
                var b = EllipsoidPoint(rx, ry, rz, t0, p1);
                var d = EllipsoidPoint(rx, ry, rz, t1, p1);
                var e = EllipsoidPoint(rx, ry, rz, t1, p0);
                AddTri(outTris, s, a, b, d, EllipsoidNormal(rx, ry, rz, a), c);
                AddTri(outTris, s, a, d, e, EllipsoidNormal(rx, ry, rz, a), c);
            }
        }
    }

    private static (double x, double y, double z) EllipsoidPoint(double rx, double ry, double rz, double theta, double phi)
        => (rx * Math.Sin(theta) * Math.Cos(phi),
            ry * Math.Sin(theta) * Math.Sin(phi),
            rz * Math.Cos(theta));

    private static (double x, double y, double z) EllipsoidNormal(double rx, double ry, double rz, (double x, double y, double z) p)
    {
        var n = (p.x / (rx * rx), p.y / (ry * ry), p.z / (rz * rz));
        var len = Math.Sqrt(n.Item1 * n.Item1 + n.Item2 * n.Item2 + n.Item3 * n.Item3);
        return len < 1e-12 ? (0, 0, 1) : (n.Item1 / len, n.Item2 / len, n.Item3 / len);
    }

    /// <summary>Capsule aligned to local Z: radius HalfX, cylindrical half-length HalfY.</summary>
    private static void AddCapsule(List<RenderTriangle> outTris, in SceneShape s, int segments, int hemiRings = 8)
    {
        var r = s.HalfX;
        var h = s.HalfY;
        var c = ColorOf(s);

        // Rings from bottom pole (radius 0) through the bottom equator, top equator, to top pole.
        var rings = new List<(double z, double rad, double centerZ)>();
        for (var i = 0; i <= hemiRings; i++)
        {
            var t = Math.PI / 2 * i / hemiRings;
            rings.Add((-h - r * Math.Cos(t), r * Math.Sin(t), -h));
        }
        for (var i = 0; i <= hemiRings; i++)
        {
            var t = Math.PI / 2 * i / hemiRings;
            rings.Add((h + r * Math.Sin(t), r * Math.Cos(t), h));
        }

        var ringPoints = new List<(double x, double y, double z)>[rings.Count];
        for (var ri = 0; ri < rings.Count; ri++)
        {
            var pts = new List<(double, double, double)>(segments);
            for (var i = 0; i < segments; i++)
            {
                var a = 2 * Math.PI * i / segments;
                pts.Add((rings[ri].rad * Math.Cos(a), rings[ri].rad * Math.Sin(a), rings[ri].z));
            }
            ringPoints[ri] = pts;
        }

        for (var ri = 0; ri < rings.Count - 1; ri++)
        {
            var r0 = rings[ri];
            var r1 = rings[ri + 1];
            var p0 = ringPoints[ri];
            var p1 = ringPoints[ri + 1];
            for (var i = 0; i < segments; i++)
            {
                var j = (i + 1) % segments;
                var a = p0[i]; var b = p0[j]; var d = p1[j]; var e = p1[i];
                var n = CapsuleNormal(a, r0.centerZ, r0.rad);
                AddTri(outTris, s, a, b, d, n, c);
                AddTri(outTris, s, a, d, e, n, c);
            }
        }
    }

    private static (double x, double y, double z) CapsuleNormal((double x, double y, double z) p, double centerZ, double radius)
    {
        var z = p.z - centerZ;
        var len = Math.Sqrt(p.x * p.x + p.y * p.y + z * z);
        return len < 1e-12 ? (0, 0, 1) : (p.x / len, p.y / len, z / len);
    }

    private static void AddMesh(List<RenderTriangle> outTris, in SceneShape s)
    {
        if (s.MeshFile is null)
        {
            AddBox(outTris, s);
            return;
        }

        MeshGeometry geo;
        try
        {
            geo = MeshGeometry.Load(s.MeshFile, new Vec3(s.MeshScaleX, s.MeshScaleY, s.MeshScaleZ));
        }
        catch (Exception)
        {
            AddBox(outTris, s);
            return;
        }

        var c = ColorOf(s);
        foreach (var (ia, ib, ic) in geo.Triangles)
        {
            var a = geo.Vertices[ia];
            var b = geo.Vertices[ib];
            var d = geo.Vertices[ic];
            var wa = ToWorld(s, a.X - s.MeshCenterX, a.Y - s.MeshCenterY, a.Z - s.MeshCenterZ);
            var wb = ToWorld(s, b.X - s.MeshCenterX, b.Y - s.MeshCenterY, b.Z - s.MeshCenterZ);
            var wd = ToWorld(s, d.X - s.MeshCenterX, d.Y - s.MeshCenterY, d.Z - s.MeshCenterZ);
            var ux = wb.x - wa.x; var uy = wb.y - wa.y; var uz = wb.z - wa.z;
            var vx = wd.x - wa.x; var vy = wd.y - wa.y; var vz = wd.z - wa.z;
            var nx = uy * vz - uz * vy;
            var ny = uz * vx - ux * vz;
            var nz = ux * vy - uy * vx;
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-15)
                continue;
            nx /= len; ny /= len; nz /= len;
            outTris.Add(new RenderTriangle(wa.x, wa.y, wa.z, wb.x, wb.y, wb.z, wd.x, wd.y, wd.z,
                nx, ny, nz, B(c.r), B(c.g), B(c.b)));
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

    private static void AddTri(List<RenderTriangle> outTris, in SceneShape s,
        (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c,
        (double x, double y, double z) n, (double r, double g, double b) color)
    {
        var wa = ToWorld(s, a.x, a.y, a.z);
        var wb = ToWorld(s, b.x, b.y, b.z);
        var wc = ToWorld(s, c.x, c.y, c.z);
        var wn = ToWorldDir(s, n.x, n.y, n.z);
        var len = Math.Sqrt(wn.x * wn.x + wn.y * wn.y + wn.z * wn.z);
        if (len < 1e-15) return;
        outTris.Add(new RenderTriangle(wa.x, wa.y, wa.z, wb.x, wb.y, wb.z, wc.x, wc.y, wc.z,
            wn.x / len, wn.y / len, wn.z / len, B(color.r), B(color.g), B(color.b)));
    }

    private static void AddQuad(List<RenderTriangle> outTris, in SceneShape s,
        ReadOnlySpan<(double x, double y, double z)> w, int i0, int i1, int i2, int i3,
        double nx, double ny, double nz, (double r, double g, double b) c)
    {
        var n = ToWorldDir(s, nx, ny, nz);
        var a = w[i0]; var b = w[i1]; var d = w[i2]; var e = w[i3];
        outTris.Add(new RenderTriangle(a.x, a.y, a.z, b.x, b.y, b.z, d.x, d.y, d.z, n.x, n.y, n.z, B(c.r), B(c.g), B(c.b)));
        outTris.Add(new RenderTriangle(a.x, a.y, a.z, d.x, d.y, d.z, e.x, e.y, e.z, n.x, n.y, n.z, B(c.r), B(c.g), B(c.b)));
    }
}
