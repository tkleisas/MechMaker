namespace MechMaker.Engine.Rendering;

/// <summary>
/// Painter with a z-buffer: projects triangles with a perspective camera and
/// rasterizes flat-shaded into a BGRA byte buffer (BGRA8888, premultiplied not
/// required — alpha is constant 255). Pure C#, no GPU. Sized for machine
/// assemblies (hundreds of primitives); a GL backend can replace this class
/// without touching the UI.
/// </summary>
public sealed class SoftRenderer
{
    private float[] _depth = [];

    public byte[] RenderBgra(IReadOnlyList<RenderTriangle> triangles, OrbitCamera camera, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        if (_depth.Length < width * height)
            _depth = new float[width * height];

        // Background: dark slate.
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = 0x2E;
            pixels[i * 4 + 1] = 0x2B;
            pixels[i * 4 + 2] = 0x28;
            pixels[i * 4 + 3] = 0xFF;
        }
        Array.Fill(_depth, float.PositiveInfinity, 0, width * height);

        // Camera basis (right-handed, Z up, looking from eye at target).
        var eye = camera.Eye();
        var fx = camera.TargetX - eye.X; var fy = camera.TargetY - eye.Y; var fz = camera.TargetZ - eye.Z;
        (fx, fy, fz) = Norm(fx, fy, fz);
        var cross = Cross(fx, fy, fz, 0, 0, 1);
        var (rx, ry, rz) = Norm(cross.x, cross.y, cross.z);
        var (ux, uy, uz) = Cross(rx, ry, rz, fx, fy, fz);

        var aspect = (double)width / height;
        var tanHalf = Math.Tan(camera.FovRad / 2);

        foreach (var t in triangles)
        {
            var v0 = Project(t.Ax, t.Ay, t.Az);
            var v1 = Project(t.Bx, t.By, t.Bz);
            var v2 = Project(t.Cx, t.Cy, t.Cz);
            if (v0 is null || v1 is null || v2 is null)
                continue;

            Rasterize(v0.Value, v1.Value, v2.Value, t, pixels, width, height);
        }
        return pixels;

        (double Sx, double Sy, double Depth)? Project(double wx, double wy, double wz)
        {
            var dx = wx - eye.X; var dy = wy - eye.Y; var dz = wz - eye.Z;
            var cz = dx * fx + dy * fy + dz * fz;
            if (cz <= 0.005)
                return null;
            var cx = dx * rx + dy * ry + dz * rz;
            var cy = dx * ux + dy * uy + dz * uz;
            var sx = cx / (cz * tanHalf * aspect) * (width / 2) + width / 2;
            var sy = height / 2 - cy / (cz * tanHalf) * (height / 2);
            return (sx, sy, cz);
        }

        void Rasterize((double Sx, double Sy, double Depth) a,
            (double Sx, double Sy, double Depth) b,
            (double Sx, double Sy, double Depth) c,
            in RenderTriangle t, byte[] pix, int w, int h)
        {
            var minX = Math.Max(0, (int)Math.Ceiling(Math.Min(a.Sx, Math.Min(b.Sx, c.Sx))));
            var maxX = Math.Min(w - 1, (int)Math.Floor(Math.Max(a.Sx, Math.Max(b.Sx, c.Sx))));
            var minY = Math.Max(0, (int)Math.Ceiling(Math.Min(a.Sy, Math.Min(b.Sy, c.Sy))));
            var maxY = Math.Min(h - 1, (int)Math.Floor(Math.Max(a.Sy, Math.Max(b.Sy, c.Sy))));
            if (minX > maxX || minY > maxY)
                return;

            var area = (b.Sx - a.Sx) * (c.Sy - a.Sy) - (c.Sx - a.Sx) * (b.Sy - a.Sy);
            if (Math.Abs(area) < 1e-12)
                return;

            // Flat shading: ambient + one headlight.
            var (lx, ly, lz) = Norm(-0.4, 0.5, 1.0);
            var diff = Math.Max(0, t.Nx * lx + t.Ny * ly + t.Nz * lz);
            var shade = 0.35 + 0.65 * diff;

            var br = (byte)Math.Clamp(t.R * shade, 0, 255);
            var bg = (byte)Math.Clamp(t.G * shade, 0, 255);
            var bb = (byte)Math.Clamp(t.B * shade, 0, 255);

            for (var y = minY; y <= maxY; y++)
            {
                var rowBase = y * w;
                for (var x = minX; x <= maxX; x++)
                {
                    var w0 = ((b.Sx - x) * (c.Sy - y) - (c.Sx - x) * (b.Sy - y)) / area;
                    var w1 = ((c.Sx - x) * (a.Sy - y) - (a.Sx - x) * (c.Sy - y)) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0)
                        continue;

                    var z = (float)(w0 * a.Depth + w1 * b.Depth + w2 * c.Depth);
                    var idx = rowBase + x;
                    if (z >= _depth[idx])
                        continue;
                    _depth[idx] = z;
                    var o = idx * 4;
                    pix[o] = bb;
                    pix[o + 1] = bg;
                    pix[o + 2] = br;
                    pix[o + 3] = 255;
                }
            }
        }
    }

    private static (double x, double y, double z) Cross(double ax, double ay, double az, double bx, double by, double bz)
        => (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);

    private static (double x, double y, double z) Norm(double x, double y, double z)
    {
        var len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-12)
            return (0, 0, 1);
        return (x / len, y / len, z / len);
    }
}
