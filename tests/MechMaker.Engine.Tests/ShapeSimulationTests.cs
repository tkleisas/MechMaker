using System.Globalization;
using System.Text;
using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;
using MechMaker.Engine;
using MechMaker.Engine.Rendering;
using Xunit;

namespace MechMaker.Engine.Tests;

public class ShapeSimulationTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mechmaker_engine_shapes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PartDefinition Part(double mass, params Shape[] shapes) => new()
    {
        Id = "shape_part",
        Name = "Shape part",
        Category = PartCategory.Structure,
        Bodies = [new PartBody { Name = "root", MassKg = mass, Shapes = [.. shapes] }],
        Connectors = [new ConnectorDefinition { Name = "mount", Type = ConnectorType.BoltM3 }]
    };

    private static MachineDefinition Machine() => new()
    {
        Name = "shapes",
        Parts = [new PartInstance { Id = "shape_part", Part = "shape_part" }]
    };

    [Fact]
    public void Primitive_shapes_load_in_mujoco_with_expected_mass_and_scene()
    {
        var catalog = new PartCatalog([Part(1.0,
            new Shape { Kind = ShapeKind.Sphere, Extents = new Vec3(0.02, 0, 0) },
            new Shape { Kind = ShapeKind.Capsule, Extents = new Vec3(0.01, 0.04, 0) },
            new Shape { Kind = ShapeKind.Ellipsoid, Extents = new Vec3(0.02, 0.03, 0.04) },
            new Shape { Kind = ShapeKind.Cylinder, Extents = new Vec3(0.01, 0.04, 0) })], TempDir());

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        using var sim = Simulator.FromMjcf(doc.ToString());
        var report = sim.BuildReport();
        var scene = sim.GetScene();
        var tris = SceneTessellator.Tessellate(scene, 8);

        Assert.Equal(1.0, report.TotalMassKg, 3);
        Assert.Contains(scene, s => s.Kind == SceneShapeKind.Sphere);
        Assert.Contains(scene, s => s.Kind == SceneShapeKind.Capsule);
        Assert.Contains(scene, s => s.Kind == SceneShapeKind.Ellipsoid);
        Assert.Contains(scene, s => s.Kind == SceneShapeKind.Cylinder);
        Assert.NotEmpty(tris);
    }

    [Fact]
    public void Mesh_shape_loads_and_renders_real_triangles()
    {
        var dir = TempDir();
        WriteCubeStl(Path.Combine(dir, "cube.stl"), 20);
        var catalog = new PartCatalog([Part(0.1,
            new Shape
            {
                Kind = ShapeKind.Mesh,
                MeshFile = "cube.stl",
                MeshScale = new Vec3(0.001, 0.001, 0.001)
            })], dir);

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        using var sim = Simulator.FromMjcf(doc.ToString());
        var report = sim.BuildReport();
        var scene = sim.GetScene();
        var mesh = scene.Single(s => s.Kind == SceneShapeKind.Mesh);
        var tris = SceneTessellator.Tessellate(scene);
        Assert.Equal(0.1, report.TotalMassKg, 3);
        Assert.NotNull(mesh.MeshFile);
        Assert.Equal(0.01, mesh.HalfX, 6);
        Assert.Equal(0.01, mesh.HalfY, 6);
        Assert.Equal(0.01, mesh.HalfZ, 6);
        Assert.Equal(12, tris.Count); // cube has 12 triangles
    }


    [Fact]
    public void Obj_mesh_loads_in_mujoco_and_renders()
    {
        var dir = TempDir();
        WriteCubeObj(Path.Combine(dir, "cube.obj"), 20);
        var catalog = new PartCatalog([Part(0.1,
            new Shape
            {
                Kind = ShapeKind.Mesh,
                MeshFile = "cube.obj",
                MeshScale = new Vec3(0.001, 0.001, 0.001)
            })], dir);

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        using var sim = Simulator.FromMjcf(doc.ToString());
        var scene = sim.GetScene();
        var mesh = scene.Single(s => s.Kind == SceneShapeKind.Mesh);
        var tris = SceneTessellator.Tessellate(scene);

        Assert.Equal(0.1, sim.BuildReport().TotalMassKg, 3);
        Assert.Equal(12, tris.Count);
        Assert.Equal(0.01, mesh.HalfX, 6);
    }


    [Fact]
    public void Visual_mesh_can_ride_a_collision_proxy_without_double_mass()
    {
        var dir = TempDir();
        WriteCubeStl(Path.Combine(dir, "cube.stl"), 20);
        var catalog = new PartCatalog([Part(0.1,
            new Shape { Kind = ShapeKind.Box, Extents = new Vec3(0.02, 0.02, 0.02), Visible = false },
            new Shape
            {
                Kind = ShapeKind.Mesh,
                MeshFile = "cube.stl",
                MeshScale = new Vec3(0.001, 0.001, 0.001),
                Collision = false,
                ContributesMass = false
            })], dir);

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        using var sim = Simulator.FromMjcf(doc.ToString());
        var scene = sim.GetScene();
        var tris = SceneTessellator.Tessellate(scene);

        Assert.Equal(0.1, sim.BuildReport().TotalMassKg, 3);
        Assert.DoesNotContain(scene, s => s.Kind == SceneShapeKind.Box); // hidden proxy
        Assert.Contains(scene, s => s.Kind == SceneShapeKind.Mesh);
        Assert.Equal(12, tris.Count);
    }


    [Fact]
    public void Authored_mesh_pose_survives_mujoco_recentering()
    {
        var dir = TempDir();
        WriteCubeStl(Path.Combine(dir, "cube.stl"), 20);
        var catalog = new PartCatalog([Part(0.1,
            new Shape
            {
                Kind = ShapeKind.Mesh,
                MeshFile = "cube.stl",
                MeshScale = new Vec3(0.001, 0.001, 0.001),
                RelativePose = new Pose
                {
                    Position = new Vec3(0.1, 0, 0.2),
                    RotationEulerDeg = new Vec3(0, 0, 90)
                }
            })], dir);

        var doc = new MjcfCompiler(catalog).Compile(Machine());
        using var sim = Simulator.FromMjcf(doc.ToString());
        var mesh = sim.GetScene().Single(s => s.Kind == SceneShapeKind.Mesh);

        Assert.Equal(0.1, mesh.Px, 4);
        Assert.Equal(0.0, mesh.Py, 4);
        Assert.Equal(0.2, mesh.Pz, 4);
        Assert.Equal(0.0, mesh.R00, 4);
        Assert.Equal(-1.0, mesh.R01, 4);
        Assert.Equal(1.0, mesh.R10, 4);
        Assert.Equal(0.0, mesh.R11, 4);
    }

    private static void WriteCubeObj(string path, double sizeMm)
    {
        var s = sizeMm / 2.0;
        var sb = new StringBuilder();
        foreach (var (x, y, z) in new[]
        {
            (-s, -s, -s), (s, -s, -s), (s, s, -s), (-s, s, -s),
            (-s, -s, s), (s, -s, s), (s, s, s), (-s, s, s)
        })
            sb.Append(CultureInfo.InvariantCulture, $"v {x} {y} {z}\n");
        foreach (var (a, b, c) in new[]
        {
            (4,5,6),(4,6,7),(1,0,3),(1,3,2),(0,4,7),(0,7,3),
            (5,1,2),(5,2,6),(0,1,5),(0,5,4),(3,7,6),(3,6,2)
        })
            sb.Append(CultureInfo.InvariantCulture, $"f {a + 1} {b + 1} {c + 1}\n");
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteCubeStl(string path, double sizeMm)
    {
        var s = sizeMm / 2.0;
        var sb = new StringBuilder("solid cube\n");
        void Tri((double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c)
        {
            sb.Append("  facet normal 0 0 0\n    outer loop\n");
            foreach (var v in new[] { a, b, c })
                sb.Append(CultureInfo.InvariantCulture, $"      vertex {v.x} {v.y} {v.z}\n");
            sb.Append("    endloop\n  endfacet\n");
        }
        var p = new (double x, double y, double z)[]
        {
            (-s, -s, -s), (s, -s, -s), (s, s, -s), (-s, s, -s),
            (-s, -s, s), (s, -s, s), (s, s, s), (-s, s, s)
        };
        Tri(p[0], p[1], p[2]); Tri(p[0], p[2], p[3]);
        Tri(p[4], p[6], p[5]); Tri(p[4], p[7], p[6]);
        Tri(p[0], p[4], p[5]); Tri(p[0], p[5], p[1]);
        Tri(p[3], p[2], p[6]); Tri(p[3], p[6], p[7]);
        Tri(p[0], p[3], p[7]); Tri(p[0], p[7], p[4]);
        Tri(p[1], p[5], p[6]); Tri(p[1], p[6], p[2]);
        sb.Append("endsolid cube\n");
        File.WriteAllText(path, sb.ToString());
    }
}
