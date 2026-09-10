using MechMaker.Engine;
using MechMaker.Hil;

// Renders one or more example machines to PNGs — the headless screenshot path.
//   dotnet run --project tools/render -- examples/linear_axis_v0.json docs/img/axis.png ...

if (args.Length == 0)
{
    Console.WriteLine("usage: render <machine.json> <out.png> [more pairs...]");
    return 1;
}

var repo = FindRepo();
for (var i = 0; i + 1 < args.Length; i += 2)
{
    var machinePath = Path.GetFullPath(args[i]);
    var outPath = Path.GetFullPath(args[i + 1]);
    using var editor = new MachineEditor(
        Path.Combine(repo, "catalog"), machinePath);
    var png = SceneRenderer.RenderMachinePng(editor);
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllBytes(outPath, png);
    Console.WriteLine($"{Path.GetFileName(machinePath)} -> {outPath} ({png.Length} bytes, {editor.EditScene.Count} shapes)");
}
return 0;

static string FindRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalog")))
        dir = dir.Parent!;
    return dir!.FullName;
}