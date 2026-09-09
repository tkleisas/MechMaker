using System.Xml.Linq;
using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Model;
using MechMaker.Core.Validation;
using Xunit;

namespace MechMaker.Core.Tests;

public static class TestRepo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalog")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    public static PartCatalog Catalog() => PartCatalog.LoadFromDirectory(Path.Combine(Root(), "catalog"));
}

public class CatalogTests
{
    [Fact]
    public void Seed_catalog_loads_all_parts()
    {
        var catalog = TestRepo.Catalog();

        Assert.Equal(9, catalog.All.Count);
        var motor = catalog.Get("nema17_stepper");
        Assert.NotNull(motor.Motor);
        Assert.Equal(MotorKind.Stepper, motor.Motor!.Kind);
        Assert.Equal("rotor", motor.Connectors.Single(c => c.Name == "shaft").Body);
        Assert.True(catalog.Get("gt2_belt_400").IsTransmissionElement);
    }

    [Fact]
    public void Catalog_parts_have_at_least_one_connector_and_body()
    {
        foreach (var part in TestRepo.Catalog().All)
        {
            Assert.NotEmpty(part.Bodies);
            Assert.NotEmpty(part.Connectors);
        }
    }
}

public class ValidatorTests
{
    private static MachineDefinition MinimalMachine() => new()
    {
        Name = "t",
        Parts = [new PartInstance { Id = "beam", Part = "beam_2020_400" }]
    };

    [Fact]
    public void Incompatible_connection_is_reported()
    {
        var machine = MinimalMachine() with
        {
            Parts =
            [
                new PartInstance { Id = "beam", Part = "beam_2020_400" },
                new PartInstance { Id = "pulley", Part = "gt2_pulley_20t" }
            ],
            Connections =
            [
                new Connection
                {
                    Id = "bad", PartA = "pulley", ConnectorA = "bore",
                    PartB = "beam", ConnectorB = "end_a"
                }
            ]
        };

        var report = new MachineValidator(TestRepo.Catalog()).Validate(machine);

        Assert.Contains(report.Diagnostics, d => d.Code == "mm004" && d.Severity == Severity.Error);
    }

    [Fact]
    public void Unwired_stepper_warns_and_isolated_part_warns()
    {
        var machine = MinimalMachine() with
        {
            Parts =
            [
                new PartInstance { Id = "beam", Part = "beam_2020_400" },
                new PartInstance { Id = "motor", Part = "nema17_stepper" }
            ]
        };

        var report = new MachineValidator(TestRepo.Catalog()).Validate(machine);

        Assert.Contains(report.Diagnostics, d => d.Code == "mm012");
        Assert.Contains(report.Diagnostics, d => d.Code == "mm020");
        Assert.False(report.HasErrors);
    }
}

public class CompilerTests
{
    private static MachineDefinition Example()
        => CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json")));

    private static (XDocument Doc, ValidationReport Report) Compile(MachineDefinition machine)
    {
        var compiler = new MjcfCompiler(TestRepo.Catalog());
        var doc = compiler.Compile(machine);
        return (doc, compiler.LastReport);
    }

    [Fact]
    public void Example_machine_compiles_with_expected_structure()
    {
        var (doc, report) = Compile(Example());

        Assert.False(report.HasErrors, report.ToString());
        Assert.Contains(report.Diagnostics, d => d.Code == "mm029"); // belt coupler info

        var bodies = doc.Descendants("body").ToDictionary(e => (string)e.Attribute("name")!);

        Assert.Equal("linear_axis_v0", (string)doc.Root!.Attribute("model")!);
        Assert.Contains("motor_left_rotor", bodies.Keys);
        Assert.Contains("pulley_left", bodies.Keys);
        Assert.Contains("carriage", bodies.Keys);
        Assert.Contains("endstop", bodies.Keys);
        Assert.Contains("belt", bodies.Keys);

        // Carriage rides the rail with a slide joint.
        var slideJoint = bodies["carriage"].Elements("joint").Single();
        Assert.Equal("slide", (string)slideJoint.Attribute("type")!);
        Assert.Equal("j_carriage", (string)slideJoint.Attribute("name")!);

        // Belt couples the two motor rotor joints 1:1, and the carriage rides it.
        var equality = doc.Root!.Element("equality")!.Elements().ToDictionary(e => (string)e.Attribute("name")!);
        var coupler = equality["eq_belt_belt"];
        Assert.Equal("j_motor_left_rotor", (string)coupler.Attribute("joint1")!);
        Assert.Equal("j_motor_right_rotor", (string)coupler.Attribute("joint2")!);
        Assert.Equal("0 -1 0 0 0", (string)coupler.Attribute("polycoef")!);
        var clampCoupler = equality["eq_clamp_belt"];
        Assert.Equal("j_carriage", (string)clampCoupler.Attribute("joint1")!);
        Assert.Equal("j_motor_left_rotor", (string)clampCoupler.Attribute("joint2")!);
        Assert.Equal("0 -0.006366 0 0 0", (string)clampCoupler.Attribute("polycoef")!);

        // Motors get torque actuators bounded by holding torque.
        var actuator = doc.Root!.Element("actuator")!.Elements().Single(a => (string)a.Attribute("name")! == "a_motor_left");
        Assert.Equal("j_motor_left_rotor", (string)actuator.Attribute("joint")!);
        Assert.Equal("-0.4 0.4", (string)actuator.Attribute("ctrlrange")!);

        // Pulleys are welded to their motor rotors (no own joint).
        Assert.DoesNotContain(bodies["pulley_left"].DescendantsAndSelf("joint"),
            j => (string)j.Attribute("name")! == "j_pulley_left");
    }

    [Fact]
    public void Placements_follow_the_mating_math()
    {
        var (doc, _) = Compile(Example());

        var bodies = doc.Descendants("body").ToDictionary(e => (string)e.Attribute("name")!);

        // Beam is the root: world pose straight from the instance.
        Assert.Equal("0 0 0", (string)bodies["beam"].Attribute("pos")!);

        // Rail sits on the beam top face (beam half-height 0.01 + rail half-height 0.004).
        Assert.Equal("0 0 0.014", (string)bodies["rail"].Attribute("pos")!);

        // Carriage origin at the rail's top face.
        Assert.Equal("0 0 0.004", (string)bodies["carriage"].Attribute("pos")!);

        // Plates hang off the beam ends (nested pos is parent-relative);
        // motors bolt onto the plate faces 22 mm out along the plate normal.
        Assert.Equal("-0.202 0 0", (string)bodies["motor_plate_left"].Attribute("pos")!);
        Assert.Equal("0.202 0 0", (string)bodies["motor_plate_right"].Attribute("pos")!);
        Assert.Equal("0 0 0.022", (string)bodies["motor_left"].Attribute("pos")!);
        Assert.Equal("0 0 0.022", (string)bodies["motor_right"].Attribute("pos")!);
    }

    [Fact]
    public void Machine_definition_survives_json_round_trip()
    {
        var machine = Example();
        var roundTripped = CoreJson.Deserialize<MachineDefinition>(CoreJson.Serialize(machine));

        Assert.Equal(machine.Name, roundTripped.Name);
        Assert.Equal(machine.Parts.Count, roundTripped.Parts.Count);
        Assert.Equal(machine.Connections.Count, roundTripped.Connections.Count);
        Assert.Equal(machine.Wiring.Count, roundTripped.Wiring.Count);
    }
}
