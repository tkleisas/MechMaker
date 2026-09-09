using MechMaker.Core;

namespace MechMaker.Server.Tests;

public static class TestRepo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalog")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    public static string CatalogDirectory() => Path.Combine(Root(), "catalog");

    public static string ExampleMachine() => Path.Combine(Root(), "examples", "linear_axis_v0.json");

    public static McpWorkspace NewWorkspace() => new(CatalogDirectory());

    /// <summary>Loads the example axis into a fresh workspace (11 parts, 2 wired steppers).</summary>
    public static McpWorkspace ExampleWorkspace()
    {
        var workspace = NewWorkspace();
        workspace.OpenMachine(ExampleMachine());
        return workspace;
    }
}
