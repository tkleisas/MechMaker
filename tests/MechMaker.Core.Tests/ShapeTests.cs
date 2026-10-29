using System.Globalization;
using System.Text;
using System.Xml.Linq;
using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;
using MechMaker.Core.Validation;
using Xunit;

namespace MechMaker.Core.Tests;

public class ShapeCompilerTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mechmaker_shapes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PartDefinition Part(params Shape[] shapes) => new()
    {
        Id = "shape_part",
        Name = "Shape part",
        Category = PartCategory.Structure,
        Bodies = [new PartBody { Name = "root", MassKg = 1.0, Shapes = [.. shapes] }],
        Connectors = [new ConnectorDefinition { Name = "mount", Type = ConnectorType.BoltM3 }]
    };

    private static MachineDefinition Machine() => new()
    {
        Name = "shapes",
        Parts = [new PartInstance { Id = "shape_part", Part = "shape_part" }]
    };

    [Fact]
    public void Primitive_kinds_compile_to_mujoco_geoms()
    {
        var catalog = new PartCatalog([Part(
            new Shape { Kind = ShapeKind.Sphere, Extents = new Vec3(0.02, 0, 0) },
            new Shape { Kind = ShapeKind.Capsule, Extents = new Vec3(0.01, 0.04, 0) },
            new Shape { Kind = ShapeKind.Ellipsoid, Extents = new Vec3(0.02, 0.03, 0.04) },
            new Shape { Kind = ShapeKind.Cylinder, Extents = new Vec3(0.01, 0.04, 0) })], TempDir());

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        var mjcf = doc.ToString();

        Assert.Contains("type=\"sphere\"", mjcf);
        Assert.Contains("type=\"capsule\"", mjcf);
        Assert.Contains("type=\"ellipsoid\"", mjcf);
        Assert.Contains("type=\"cylinder\"", mjcf);
        Assert.Contains("size=\"0.02\"", mjcf);       // sphere radius
        Assert.Contains("size=\"0.01 0.02\"", mjcf);  // capsule radius + half-length
        Assert.Contains("size=\"0.02 0.03 0.04\"", mjcf); // ellipsoid radii
    }

    [Fact]
    public void Visual_only_shapes_get_zero_collision_masks()
    {
        var catalog = new PartCatalog([Part(
            new Shape { Kind = ShapeKind.Sphere, Extents = new Vec3(0.02, 0, 0), Collision = false })], TempDir());

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        var geom = doc.Descendants("geom").Single(e => (string?)e.Attribute("type") == "sphere");

        Assert.Equal("0", (string?)geom.Attribute("contype"));
        Assert.Equal("0", (string?)geom.Attribute("conaffinity"));
    }

    [Fact]
    public void Mesh_shape_emits_asset_and_geom()
    {
        var dir = TempDir();
        WriteCubeStl(Path.Combine(dir, "cube.stl"), 20);
        var catalog = new PartCatalog([Part(
            new Shape
            {
                Kind = ShapeKind.Mesh,
                MeshFile = "cube.stl",
                MeshScale = new Vec3(0.001, 0.001, 0.001),
                Rgba = "0.7 0.7 0.7 1"
            })], dir);

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        var mesh = doc.Descendants("mesh").Single();
        var geom = doc.Descendants("geom").Single(e => (string?)e.Attribute("type") == "mesh");

        Assert.Equal("mesh_shape_part_root_0", (string?)mesh.Attribute("name"));
        Assert.True(Path.IsPathRooted((string?)mesh.Attribute("file")));
        Assert.Equal("0.001 0.001 0.001", (string?)mesh.Attribute("scale"));
        Assert.Equal("mesh_shape_part_root_0", (string?)geom.Attribute("mesh"));
    }


    [Fact]
    public void Massless_visual_shape_gets_zero_mass_and_is_excluded_from_distribution()
    {
        var catalog = new PartCatalog([Part(
            new Shape { Kind = ShapeKind.Box, Extents = new Vec3(0.1, 0.1, 0.1) },
            new Shape
            {
                Kind = ShapeKind.Sphere,
                Extents = new Vec3(0.05, 0, 0),
                Collision = false,
                ContributesMass = false
            })], TempDir());

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        var geoms = doc.Descendants("geom").Where(e => e.Attribute("mass") is not null).ToList();
        var box = geoms.Single(e => (string?)e.Attribute("type") == "box");
        var sphere = geoms.Single(e => (string?)e.Attribute("type") == "sphere");

        Assert.Equal("1", (string?)box.Attribute("mass"));
        Assert.Equal("0", (string?)sphere.Attribute("mass"));
        Assert.Equal("0", (string?)sphere.Attribute("contype"));
    }

    [Fact]
    public void Missing_mesh_file_is_a_validation_error()
    {
        var catalog = new PartCatalog([Part(
            new Shape { Kind = ShapeKind.Mesh, MeshFile = "nope.stl" })], TempDir());
        var report = new MachineValidator(catalog).Validate(Machine());

        Assert.Contains(report.Diagnostics, d => d.Code == "mm042");
    }


    [Fact]
    public void Mesh_fields_round_trip_through_catalog_json()
    {
        var dir = TempDir();
        WriteCubeStl(Path.Combine(dir, "cube.stl"), 20);
        File.WriteAllText(Path.Combine(dir, "part.json"), """
        {
          "id": "mesh_part",
          "name": "Mesh part",
          "bodies": [
            {
              "name": "root",
              "mass_kg": 0.1,
              "shapes": [
                {
                  "kind": "mesh",
                  "mesh_file": "cube.stl",
                  "mesh_scale": [0.001, 0.001, 0.001],
                  "collision": false,
                  "smooth_normal": true
                }
              ]
            }
          ],
          "connectors": [
            { "name": "mount", "type": "bolt_m3" }
          ]
        }
        """);

        var catalog = PartCatalog.LoadFromDirectory(dir);
        var shape = catalog.Get("mesh_part").RootBody.Shapes.Single();

        Assert.Equal(ShapeKind.Mesh, shape.Kind);
        Assert.Equal("cube.stl", shape.MeshFile);
        Assert.Equal(new Vec3(0.001, 0.001, 0.001), shape.MeshScale);
        Assert.False(shape.Collision);
        Assert.True(shape.SmoothNormal);
        Assert.Equal(Path.GetFullPath(dir), catalog.RootDirectory);
    }

    private static void WriteCubeStl(string path, double sizeMm)
    {
        var s = sizeMm / 2.0;
        var sb = new StringBuilder("solid cube\n");
        void Tri((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  facet normal 0 0 0\n    outer loop\n");
            foreach (var v in new[] { a, b, c })
                sb.Append(CultureInfo.InvariantCulture, $"      vertex {v.x} {v.y} {v.z}\n");
            sb.Append("    endloop\n  endfacet\n");
        }

        var p = new (double x, double y, double z)[]
        {
            (-s, -s, -s), (s, -s, -s), (s, s, -s), (-s, s, -s),
            (-s, -s, s), (s, -s, s), (s, s, s), (-s, s, s)
        };
        Tri(p[0], p[1], p[2]); Tri(p[0], p[2], p[3]); // -Z
        Tri(p[4], p[6], p[5]); Tri(p[4], p[7], p[6]); // +Z
        Tri(p[0], p[4], p[5]); Tri(p[0], p[5], p[1]); // -Y
        Tri(p[3], p[2], p[6]); Tri(p[3], p[6], p[7]); // +Y
        Tri(p[0], p[3], p[7]); Tri(p[0], p[7], p[4]); // -X
        Tri(p[1], p[5], p[6]); Tri(p[1], p[6], p[2]); // +X
        sb.Append("endsolid cube\n");
        File.WriteAllText(path, sb.ToString());
    }
}
