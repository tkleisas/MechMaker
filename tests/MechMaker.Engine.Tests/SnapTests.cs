using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;

namespace MechMaker.Engine.Tests;

/// <summary>
/// Snapped placement: the pose computed by the editor must put the new part's
/// connector exactly on the target's connector frame with anti-aligned Z (belt
/// connections align instead) — the same mating the compiler applies.
/// </summary>
public class SnapTests
{
    private static MachineEditor NewEditor()
    {
        var machinePath = Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json");
        return new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"), machinePath);
    }

    /// <summary>World transform of a connector of a placed instance (mirrors editor math independently).</summary>
    private static Transform ConnectorWorld(MachineEditor editor, string instanceId, string connectorName)
    {
        var instance = editor.Machine.Parts.Single(p => p.Id == instanceId);
        var definition = editor.Catalog.Get(instance.Part);
        var connector = definition.Connectors.Single(c => c.Name == connectorName);
        var rootWorld = new Transform(instance.Pose.Position, instance.Pose.Rotation);
        var bodyWorld = connector.Body == definition.RootBody.Name
            ? rootWorld
            : rootWorld * new Transform(
                definition.Bodies.First(b => b.Name == connector.Body).RelativePose.Position,
                definition.Bodies.First(b => b.Name == connector.Body).RelativePose.Rotation);
        return bodyWorld * new Transform(connector.Pose.Position, connector.Pose.Rotation);
    }

    [Fact]
    public void Snapped_carriage_connector_lands_on_the_rail_connector_with_opposed_z()
    {
        using var editor = NewEditor();

        var pose = editor.PoseForSnappedPart("mgn12_carriage", "rail_side", "rail", "carriage_top");

        // Where does the new part's connector end up with this pose?
        var newWorld = ConnectorWorldAt(pose, "mgn12_carriage", "rail_side");
        var target = ConnectorWorld(editor, "rail", "carriage_top");

        // Coincident origin:
        var delta = newWorld.Position - target.Position;
        Assert.True(delta.Length < 1e-9, $"connector origin off by {delta.Length}");

        // Facing surfaces: +Z axes anti-aligned.
        var newZ = newWorld.Rotation.Rotate(Vec3.UnitZ);
        var targetZ = target.Rotation.Rotate(Vec3.UnitZ);
        Assert.True(Vec3.Dot(newZ, targetZ) < -0.999999, $"Z axes not opposed: dot={Vec3.Dot(newZ, targetZ)}");
    }

    [Fact]
    public void Snapped_pulley_on_the_motor_shaft_keeps_belt_alignment_absent_but_flip_present()
    {
        using var editor = NewEditor();

        var pose = editor.PoseForSnappedPart("gt2_pulley_20t", "bore", "motor_left", "shaft");
        var newWorld = ConnectorWorldAt(pose, "gt2_pulley_20t", "bore");
        var target = ConnectorWorld(editor, "motor_left", "shaft");

        Assert.True((newWorld.Position - target.Position).Length < 1e-9);
        // Hinge (shaft into bore): Z anti-aligned.
        var dot = Vec3.Dot(newWorld.Rotation.Rotate(Vec3.UnitZ), target.Rotation.Rotate(Vec3.UnitZ));
        Assert.True(dot < -0.999999, $"expected anti-aligned shaft, dot={dot}");
    }

    [Fact]
    public void Snapped_belt_onto_pulley_aligns_the_z_axes()
    {
        using var editor = NewEditor();

        var pose = editor.PoseForSnappedPart("gt2_belt_400", "end_a", "pulley_left", "belt");
        var newWorld = ConnectorWorldAt(pose, "gt2_belt_400", "end_a");
        var target = ConnectorWorld(editor, "pulley_left", "belt");

        Assert.True((newWorld.Position - target.Position).Length < 1e-9);
        // Belt joints align (the loop wraps, it does not butt against the pulley).
        var dot = Vec3.Dot(newWorld.Rotation.Rotate(Vec3.UnitZ), target.Rotation.Rotate(Vec3.UnitZ));
        Assert.True(dot > 0.999999, $"expected aligned belt Z, dot={dot}");
    }

    [Fact]
    public void AddPartSnapped_places_and_connects_in_one_step()
    {
        using var editor = NewEditor();
        editor.LoadMachine(Path.Combine(TestRepo.Root(), "examples", "empty_machine.json"));

        editor.AddPartSnapped("mgn12_rail_400", "mount", "beam", "top_mid", instanceId: "rail");
        var id = editor.AddPartSnapped("mgn12_carriage", "rail_side", "rail", "carriage_top");

        Assert.Equal(3, editor.Machine.Parts.Count);
        Assert.Equal(2, editor.Machine.Connections.Count); // rail_on_beam + the new one
        var connection = editor.Machine.Connections.Last();
        Assert.Equal("rail", connection.PartA);
        Assert.Equal(id, connection.PartB);
        Assert.DoesNotContain(editor.Validate().Diagnostics, d => d.Code == "mm020"); // connected: no island
    }

    [Fact]
    public void Snapped_rebuild_of_the_example_places_parts_where_the_reference_has_them()
    {
        // Rebuild beam + rail + carriage using only snapped adds; the compiled
        // world geometry of those parts must match the example machine exactly,
        // because snap uses the compiler's own mating convention.
        using var reference = new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"),
            Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json"));

        using var editor = new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"),
            Path.Combine(TestRepo.Root(), "examples", "empty_machine.json"));
        editor.AddPartSnapped("mgn12_rail_400", "mount", "beam", "top_mid", instanceId: "rail");
        editor.AddPartSnapped("mgn12_carriage", "rail_side", "rail", "carriage_top", instanceId: "carriage");

        foreach (var instanceId in new[] { "rail", "carriage" })
            AssertSharedGeometry(reference.EditScene, editor.EditScene, instanceId);
    }

    private static void AssertSharedGeometry(IReadOnlyList<SceneShape> reference,
        IReadOnlyList<SceneShape> candidate, string instanceId)
    {
        var prefix = instanceId + "_";
        var expected = reference.Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var actual = candidate.Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, actual.Count);

        foreach (var (e, a) in expected.Zip(actual))
        {
            var delta = new Vec3(e.Px - a.Px, e.Py - a.Py, e.Pz - a.Pz);
            Assert.True(delta.Length < 1e-9,
                $"geom '{e.Name}' position differs by {delta.Length}");
        }
    }

    private static Transform ConnectorWorldAt(Pose pose, string catalogId, string connectorName)
    {
        // Independent re-derivation for a part placed at `pose` (catalog from TestRepo).
        var catalog = TestRepo.Catalog();
        var definition = catalog.Get(catalogId);
        var connector = definition.Connectors.Single(c => c.Name == connectorName);
        var rootWorld = new Transform(pose.Position, pose.Rotation);
        var bodyWorld = connector.Body == definition.RootBody.Name
            ? rootWorld
            : rootWorld * new Transform(
                definition.Bodies.First(b => b.Name == connector.Body).RelativePose.Position,
                definition.Bodies.First(b => b.Name == connector.Body).RelativePose.Rotation);
        return bodyWorld * new Transform(connector.Pose.Position, connector.Pose.Rotation);
    }
}
