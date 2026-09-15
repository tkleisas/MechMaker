using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;
using MechMaker.Core.Parametric;
using MechMaker.Core.Validation;
using Xunit;

namespace MechMaker.Core.Tests;

public class ParametricTests
{
    // ---------- the evaluator ----------

    [Theory]
    [InlineData("2", 2.0)]
    [InlineData("1+2*3", 7.0)]
    [InlineData("(1+2)*3", 9.0)]
    [InlineData("length_m/2", 0.05)]
    [InlineData("length_m/2-0.01", 0.04)]
    [InlineData("-length_m/2", -0.05)]
    [InlineData("module_mm*teeth/2000", 0.02)] // 2 mm module, 20 teeth → 20 mm radius
    [InlineData("1e-2+length_m*2", 0.21)]
    public void Expressions_evaluate(string expression, double expected)
    {
        var parameters = new Dictionary<string, double> { ["length_m"] = 0.1, ["module_mm"] = 2, ["teeth"] = 20 };
        Assert.Equal(expected, ParamEval.Evaluate(expression, parameters), 10);
    }

    [Theory]
    [InlineData("lenght_m/2")] // the classic typo
    [InlineData("1+")]
    [InlineData("(length_m")]
    [InlineData("2*(3+)")]
    public void Bad_expressions_fail_with_the_parameter_named(string expression)
    {
        var parameters = new Dictionary<string, double> { ["length_m"] = 0.1 };
        var ex = Assert.Throws<ParamEvalException>(() => ParamEval.Evaluate(expression, parameters));
        Assert.NotEmpty(ex.Message);
    }

    // ---------- materialization ----------

    [Fact]
    public void Instance_params_override_catalog_defaults()
    {
        var def = new PartDefinition
        {
            Id = "rod_8mm", Name = "rod", Category = PartCategory.LinearMotion,
            Bodies = [new PartBody { Name = "root", MassKg = 0.03, Shapes = [new Shape { Kind = ShapeKind.Cylinder, Extents = new Vec3(0.004, 0.1, 0), ExtentsExpr = ["0.004", "length_m", "0"] }] }],
            Connectors = [
                new ConnectorDefinition { Name = "end_a", Pose = new Pose { Position = new Vec3(0, 0, 0.05) }, PosExpr = ["0", "0", "length_m/2"] },
                new ConnectorDefinition { Name = "end_b", Pose = new Pose { Position = new Vec3(0, 0, -0.05) }, PosExpr = ["0", "0", "-length_m/2"] }
            ],
            Params = new Dictionary<string, double> { ["length_m"] = 0.1 }
        };
        var resolved = ResolvedParts.Materialize(def, new Dictionary<string, double> { ["length_m"] = 0.25 }, "rod1");

        Assert.Equal(0.25, resolved.Bodies[0].Shapes[0].Extents.Y, 10);
        Assert.Equal(0.125, resolved.Connectors[0].Pose.Position.Z, 10);
        Assert.Equal(-0.125, resolved.Connectors[1].Pose.Position.Z, 10);
    }

    [Fact]
    public void Derived_params_chain_before_geometry()
    {
        var def = new PartDefinition
        {
            Id = "spur_gear_parametric", Name = "gear", Category = PartCategory.Transmission,
            Bodies = [new PartBody { Name = "root", MassKg = 0.015, Shapes = [new Shape { Kind = ShapeKind.Cylinder, Extents = new Vec3(0.01, 0.005, 0), ExtentsExpr = ["pitch_radius", "0.005", "0"] }] }],
            Params = new Dictionary<string, double> { ["teeth"] = 20, ["module_mm"] = 1 },
            ParamsExpr = new Dictionary<string, string> { ["pitch_radius"] = "module_mm*teeth/2000" }
        };
        var resolved = ResolvedParts.Materialize(def, new Dictionary<string, double> { ["teeth"] = 60, ["module_mm"] = 2 }, "gear2");

        Assert.Equal(0.06, resolved.Params["pitch_radius"], 10);
        Assert.Equal(0.06, resolved.Bodies[0].Shapes[0].Extents.X, 10);
    }

    [Fact]
    public void Unknown_instance_param_is_rejected_by_the_validator()
    {
        var def = new PartDefinition { Id = "rod_8mm", Name = "rod", Category = PartCategory.LinearMotion, Params = new Dictionary<string, double> { ["length_m"] = 0.1 }, Bodies = [new PartBody { Name = "root" }] };
        var machine = new MachineDefinition
        {
            SchemaVersion = MachineDefinition.CurrentSchemaVersion,
            Name = "m",
            Parts = [new PartInstance { Id = "rod1", Part = "rod_8mm", Params = new Dictionary<string, double> { ["lenght_m"] = 0.3 } }]
        };
        var catalog = new PartCatalog(new[] { def });
        var report = new MachineValidator(catalog).Validate(machine);

        Assert.Contains(report.Diagnostics, i => i.Code == "mm041");
    }

    // ---------- the closed loop: catalog parts + compiler ----------

    [Fact]
    public void Parametric_gear_ratio_follows_the_instance_params()
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(File.ReadAllText(
            Path.Combine(TestRepo.Root(), "examples", "parametric_gear_pair.json")));
        var compiler = new MjcfCompiler(TestRepo.Catalog());
        var doc = compiler.Compile(machine);

        // 60T : 20T (same module) = 3:1 — the coupler ratio is the teeth ratio,
        // from instance params, not catalog entries.
        var equality = doc.Descendants("equality").Single()
            .Elements().Single(e => ((string)e.Attribute("name")!).StartsWith("eq_gear"));
        // polycoef "0 <ratio> 0 0 0" — the ratio is the second coefficient.
        var polycoef = ((string)equality.Attribute("polycoef")!).Split(' ');
        Assert.Equal(3.0, double.Parse(polycoef[1], System.Globalization.CultureInfo.InvariantCulture), 6);
    }

    [Fact]
    public void Parametric_rod_connectors_follow_the_length()
    {
        // A 0.25 m rod with end brackets: the connectors materialize at ±length/2,
        // so the brackets bolt on exactly regardless of the length.
        var rod = TestRepo.Catalog().Get("rod_8mm");
        var resolved = ResolvedParts.Materialize(rod, new Dictionary<string, double> { ["length_m"] = 0.25 }, "rod1");

        Assert.Equal(0.25, resolved.Bodies[0].Shapes[0].Extents.Y, 10);
        Assert.Equal(0.125, resolved.Connectors.Single(c => c.Name == "end_a").Pose.Position.Z, 10);
        Assert.Equal(-0.125, resolved.Connectors.Single(c => c.Name == "end_b").Pose.Position.Z, 10);
    }
}
