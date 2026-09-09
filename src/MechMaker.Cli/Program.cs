using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Model;

// MechMaker M0 CLI: compile a machine definition to MuJoCo MJCF.
//   dotnet run --project src/MechMaker.Cli -- <machine.json> [output.xml] [catalog-dir]

if (args.Length < 1)
{
    Console.WriteLine("usage: mechkmaker <machine.json> [output.xml] [catalog-dir]");
    return 1;
}

var machinePath = args[0];
var outputPath = args.Length > 1 ? args[1] : Path.ChangeExtension(machinePath, ".xml");
var catalogDir = args.Length > 2 ? args[2] : FindCatalog();

MachineDefinition machine;
try
{
    machine = CoreJson.Deserialize<MachineDefinition>(File.ReadAllText(machinePath));
}
catch (Exception e)
{
    Console.Error.WriteLine($"Failed to read machine: {e.Message}");
    return 1;
}

var compiler = new MjcfCompiler(PartCatalog.LoadFromDirectory(catalogDir));
try
{
    var doc = compiler.Compile(machine);
    doc.Save(outputPath);
    Console.WriteLine($"Compiled '{machine.Name}' -> {outputPath}");
    foreach (var diagnostic in compiler.LastReport.Diagnostics)
        Console.WriteLine($"  {diagnostic}");
    return 0;
}
catch (MjcfCompileException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

string FindCatalog()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalog")))
        dir = dir.Parent!;
    return dir is null ? "catalog" : Path.Combine(dir.FullName, "catalog");
}
