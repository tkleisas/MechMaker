using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MechMaker.Core.Mathematics;

namespace MechMaker.Core.Geometry;

/// <summary>
/// Triangle mesh loaded from STL or OBJ, with scaled bounds and signed volume.
/// Shared by the compiler (mass/AABB/asset validation) and the software renderer.
/// </summary>
public sealed class MeshGeometry
{
    private static readonly ConcurrentDictionary<MeshKey, MeshGeometry> Cache = new();

    public IReadOnlyList<Vec3> Vertices { get; }
    public IReadOnlyList<(int A, int B, int C)> Triangles { get; }
    public Vec3 Min { get; }
    public Vec3 Max { get; }

    /// <summary>Absolute signed volume of the triangle soup. Zero for open meshes.</summary>
    public double Volume { get; }

    public Vec3 Size => Max - Min;
    public Vec3 Center => (Min + Max) * 0.5;
    public double BoundingVolume => Math.Max(0, Size.X) * Math.Max(0, Size.Y) * Math.Max(0, Size.Z);

    private MeshGeometry(IReadOnlyList<Vec3> vertices, IReadOnlyList<(int A, int B, int C)> triangles, Vec3 min, Vec3 max, double volume)
    {
        Vertices = vertices;
        Triangles = triangles;
        Min = min;
        Max = max;
        Volume = volume;
    }

    public static MeshGeometry Load(string path) => Load(path, new Vec3(1, 1, 1));

    public static MeshGeometry Load(string path, Vec3 scale)
    {
        var full = Path.GetFullPath(path);
        var key = new MeshKey(full, scale.X, scale.Y, scale.Z);
        return Cache.GetOrAdd(key, _ => LoadUncached(full, scale));
    }

    private static MeshGeometry LoadUncached(string path, Vec3 scale)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Mesh file not found: {path}", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var (vertices, triangles) = ext switch
        {
            ".stl" => LoadStl(path),
            ".obj" => LoadObj(path),
            _ => throw new NotSupportedException($"Unsupported mesh format '{ext}' (use STL or OBJ).")
        };

        if (triangles.Count == 0)
            throw new InvalidDataException($"Mesh has no triangles: {path}");

        var scaled = new Vec3[vertices.Count];
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = new Vec3(vertices[i].X * scale.X, vertices[i].Y * scale.Y, vertices[i].Z * scale.Z);
            scaled[i] = v;
            min = new Vec3(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
            max = new Vec3(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
        }

        var volume = Math.Abs(SignedVolume(scaled, triangles));
        return new MeshGeometry(scaled, triangles, min, max, volume);
    }

    private static double SignedVolume(IReadOnlyList<Vec3> v, IReadOnlyList<(int A, int B, int C)> tris)
    {
        var sum = 0.0;
        foreach (var (a, b, c) in tris)
            sum += Vec3.Dot(v[a], Vec3.Cross(v[b], v[c]));
        return sum / 6.0;
    }

    /// <summary>
    /// MuJoCo's STL decoder accepts binary STL only. ASCII STL is converted once
    /// into a content-addressed binary cache file; other formats pass through.
    /// </summary>
    public static string EnsureMujocoCompatible(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".stl", StringComparison.OrdinalIgnoreCase))
            return full;

        var bytes = File.ReadAllBytes(full);
        if (IsBinaryStl(bytes))
            return full;

        var (vertices, triangles) = ParseAsciiStl(File.ReadAllText(full));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..24];
        var cacheDir = Path.Combine(Path.GetTempPath(), "mechmaker_meshes");
        Directory.CreateDirectory(cacheDir);
        var outPath = Path.Combine(cacheDir, $"{hash}.stl");
        var expectedLength = 84L + 50L * triangles.Count;
        if (!File.Exists(outPath) || new FileInfo(outPath).Length != expectedLength)
        {
            // Atomic publish: parallel compiles can race on the same content hash.
            var tmp = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            WriteBinaryStl(tmp, vertices, triangles);
            File.Move(tmp, outPath, overwrite: true);
        }
        return outPath;
    }

    private static void WriteBinaryStl(string path, IReadOnlyList<Vec3> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[80]);
        writer.Write((uint)triangles.Count);
        foreach (var (ia, ib, ic) in triangles)
        {
            var a = vertices[ia]; var b = vertices[ib]; var c = vertices[ic];
            var n = Vec3.Cross(b - a, c - a);
            var len = n.Length;
            if (len > 1e-12) n = n * (1.0 / len);
            writer.Write((float)n.X); writer.Write((float)n.Y); writer.Write((float)n.Z);
            foreach (var v in new[] { a, b, c })
            {
                writer.Write((float)v.X); writer.Write((float)v.Y); writer.Write((float)v.Z);
            }
            writer.Write((ushort)0);
        }
    }

    private static (List<Vec3>, List<(int, int, int)>) LoadStl(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (IsBinaryStl(bytes))
            return ParseBinaryStl(bytes);
        return ParseAsciiStl(File.ReadAllText(path));
    }

    private static bool IsBinaryStl(byte[] bytes)
    {
        if (bytes.Length < 84)
            return false;
        var count = BitConverter.ToUInt32(bytes, 80);
        var expected = 84L + 50L * count;
        if (expected == bytes.Length)
            return true;
        // "solid" ASCII files can also be 84+ bytes; the length check above is the
        // reliable discriminator. A binary file starting with "solid" is rare but valid.
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length));
        return !head.Equals("solid", StringComparison.OrdinalIgnoreCase) && expected <= bytes.Length;
    }

    private static (List<Vec3>, List<(int, int, int)>) ParseBinaryStl(byte[] bytes)
    {
        var count = (int)BitConverter.ToUInt32(bytes, 80);
        var vertices = new List<Vec3>(count * 3);
        var triangles = new List<(int, int, int)>(count);
        var offset = 84;
        for (var i = 0; i < count; i++)
        {
            offset += 12; // normal
            var baseIndex = vertices.Count;
            for (var j = 0; j < 3; j++)
            {
                var x = BitConverter.ToSingle(bytes, offset);
                var y = BitConverter.ToSingle(bytes, offset + 4);
                var z = BitConverter.ToSingle(bytes, offset + 8);
                offset += 12;
                vertices.Add(new Vec3(x, y, z));
            }
            offset += 2; // attribute byte count
            triangles.Add((baseIndex, baseIndex + 1, baseIndex + 2));
        }
        return (vertices, triangles);
    }

    private static (List<Vec3>, List<(int, int, int)>) ParseAsciiStl(string text)
    {
        var vertices = new List<Vec3>();
        var triangles = new List<(int, int, int)>();
        var pending = new List<Vec3>(3);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex ", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                continue;
            pending.Add(new Vec3(
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture)));
            if (pending.Count == 3)
            {
                var i = vertices.Count;
                vertices.AddRange(pending);
                triangles.Add((i, i + 1, i + 2));
                pending.Clear();
            }
        }
        return (vertices, triangles);
    }

    private static (List<Vec3>, List<(int, int, int)>) LoadObj(string path)
    {
        var vertices = new List<Vec3>();
        var triangles = new List<(int, int, int)>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;
            if (parts[0] == "v" && parts.Length >= 4)
            {
                vertices.Add(new Vec3(
                    double.Parse(parts[1], CultureInfo.InvariantCulture),
                    double.Parse(parts[2], CultureInfo.InvariantCulture),
                    double.Parse(parts[3], CultureInfo.InvariantCulture)));
            }
            else if (parts[0] == "f" && parts.Length >= 4)
            {
                var indices = new List<int>(parts.Length - 1);
                for (var i = 1; i < parts.Length; i++)
                {
                    var token = parts[i];
                    var slash = token.IndexOf('/');
                    if (slash >= 0)
                        token = token[..slash];
                    if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                        continue;
                    index = index < 0 ? vertices.Count + index : index - 1;
                    if (index >= 0 && index < vertices.Count)
                        indices.Add(index);
                }
                for (var i = 2; i < indices.Count; i++)
                    triangles.Add((indices[0], indices[i - 1], indices[i]));
            }
        }
        return (vertices, triangles);
    }

    private readonly record struct MeshKey(string Path, double Sx, double Sy, double Sz);
}
