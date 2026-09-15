using MechMaker.Core;
using MechMaker.Core.Model;
using Xunit;

namespace MechMaker.Engine.Tests;

/// <summary>
/// The editor's parametric panel seam: SetPartParams on a placed rod resizes the
/// compiled edit scene — the same code path the app's "Apply params" button uses.
/// </summary>
public class EditorParametricTests
{
    [Fact]
    public void Applying_params_resizes_the_rod_in_the_edit_scene()
    {
        var editor = NewEditorWithRod();

        var before = RodShape(editor);
        Assert.Equal(0.1, before, 6);

        editor.SetPartParams("rod1", new Dictionary<string, double> { ["length_m"] = 0.3 });
        Assert.Equal(0.3, RodShape(editor), 6);
    }

    [Fact]
    public void Unknown_param_keys_fail_validation_and_report()
    {
        var editor = NewEditorWithRod();

        // No throw: the params land on the instance, the validator rejects the
        // typo (mm041), and the edit scene holds nothing until the machine is
        // valid again (Recompile wipes the cache on errors).
        editor.SetPartParams("rod1", new Dictionary<string, double> { ["lenght_m"] = 0.3 });
        Assert.Equal(0.3, editor.Machine.Parts.Single(p => p.Id == "rod1").Params["lenght_m"]);
        Assert.Contains(editor.Validate().Diagnostics, d => d.Code == "mm041");
    }

    private static MachineEditor NewEditorWithRod()
    {
        var editor = new MachineEditor(Path.Combine(TestRepo.Root(), "catalog"),
            Path.Combine(TestRepo.Root(), "examples", "empty_machine.json"));
        // beam(tslot) → rail → carriage(bolt) → rod end(bolt): the full mating chain.
        editor.AddPart("mgn12_rail_400", "rail");
        editor.AddPart("mgn12_carriage", "adapter");
        editor.AddPart("rod_8mm", "rod1");
        editor.AddConnection("beam", "top_mid", "rail", "mount");
        editor.AddConnection("rail", "carriage_top", "adapter", "rail_side");
        editor.AddConnection("adapter", "top", "rod1", "end_b");
        return editor;
    }

    private static double RodShape(MachineEditor editor)
    {
        var shape = editor.EditScene.Single(s => s.Name.Contains("rod1"));
        return shape.HalfY * 2; // cylinder: HalfY is the half-length
    }
}
