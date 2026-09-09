using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Model;
using MechMaker.Core.Validation;
using Vec3 = MechMaker.Core.Mathematics.Vec3;
using MechMaker.Engine;

namespace MechMaker.App;

/// <summary>
/// All editor logic, UI-agnostic: owns the machine definition, the catalog, the
/// edit-mode simulator, and the run-mode machine simulation. The window binds to
/// this and calls the mutators; every change recompiles the edit scene.
/// </summary>
public sealed class EditorState : IDisposable
{
    public PartCatalog Catalog { get; }
    public MachineDefinition Machine { get; private set; }
    public string MachinePath { get; private set; }

    private Simulator? _editSim;
    private MachineSimulation? _running;

    public IReadOnlyList<SceneShape> EditScene => _editSceneCache;
    private IReadOnlyList<SceneShape> _editSceneCache = [];

    public event Action? MachineChanged;

    public EditorState(string catalogDir, string machinePath)
    {
        Catalog = PartCatalog.LoadFromDirectory(catalogDir);
        MachinePath = machinePath;
        Machine = File.Exists(machinePath)
            ? CoreJson.Deserialize<MachineDefinition>(File.ReadAllText(machinePath))
            : NewMachine();
        Recompile();
    }

    private static MachineDefinition NewMachine() => new()
    {
        Name = "new_machine",
        Parts = [new PartInstance { Id = "base", Part = "beam_2020_400" }]
    };

    public void Save() => File.WriteAllText(MachinePath, CoreJson.Serialize(Machine));

    public void LoadMachine(string path)
    {
        MachinePath = path;
        Machine = CoreJson.Deserialize<MachineDefinition>(File.ReadAllText(path));
        Recompile();
    }

    // ---------- validation ----------

    public ValidationReport Validate()
        => new MachineValidator(Catalog).Validate(Machine);

    // ---------- edit-mode scene ----------

    private void Recompile()
    {
        _editSceneCache = [];
        try
        {
            var mjcf = new MjcfCompiler(Catalog).Compile(Machine);
            _editSimulator?.Dispose();
            _editSimulator = Simulator.FromMjcf(mjcf.ToString());
            _editSimulator.RefreshKinematics();
            _editSceneCache = _editSimulator.GetScene();
        }
        catch (MjcfCompileException)
        {
            // Invalid machine: keep last good scene; the diagnostics panel shows why.
        }
        MachineChanged?.Invoke();
    }

    private Simulator? _editSimulator;

    // ---------- parts ----------

    public void AddPart(string catalogId)
    {
        var definition = Catalog.Get(catalogId);
        var id = UniqueInstanceId(catalogId);
        var machine = Machine with
        {
            Parts = [.. Machine.Parts, new PartInstance
            {
                Id = id,
                Part = catalogId,
                Pose = new Pose { Position = new Core.Mathematics.Vec3(0, 0, NextFreeZ(definition)) }
            }]
        };
        Machine = machine;
        Recompile();
    }

    private string UniqueInstanceId(string catalogId)
    {
        for (var i = 1; ; i++)
        {
            var candidate = $"{catalogId}_{i}";
            if (Machine.Parts.All(p => p.Id != candidate))
                return candidate;
        }
    }

    private double NextFreeZ(PartDefinition definition)
    {
        // Stack new parts above everything already present.
        var top = 0.0;
        foreach (var shape in _editSceneCache)
            top = Math.Max(top, shape.Pz + 0.05);
        return top + 0.02;
    }

    public void UpdatePartPose(string instanceId, double x, double y, double z, double rx, double ry, double rz)
    {
        Machine = Machine with
        {
            Parts = [.. Machine.Parts.Select(p => p.Id == instanceId
                ? p with { Pose = new Pose
                    {
                        Position = new Vec3(x, y, z),
                        RotationEulerDeg = new Vec3(rx, ry, rz)
                    } }
                : p)]
        };
        Recompile();
    }

    public void DeletePart(string instanceId)
    {
        Machine = Machine with
        {
            Parts = [.. Machine.Parts.Where(p => p.Id != instanceId)],
            Connections = [.. Machine.Connections.Where(c => c.PartA != instanceId && c.PartB != instanceId)],
            Wiring = [.. Machine.Wiring.Where(w => w.Component != instanceId)]
        };
        Recompile();
    }

    // ---------- connections ----------

    public void AddConnection(string partA, string connectorA, string partB, string connectorB)
    {
        var id = $"c{Machine.Connections.Count + 1}";
        while (Machine.Connections.Any(c => c.Id == id))
            id += "x";
        Machine = Machine with
        {
            Connections = [.. Machine.Connections, new Connection
            {
                Id = id, PartA = partA, ConnectorA = connectorA, PartB = partB, ConnectorB = connectorB
            }]
        };
        Recompile();
    }

    public void DeleteConnection(string connectionId)
    {
        Machine = Machine with { Connections = [.. Machine.Connections.Where(c => c.Id != connectionId)] };
        Recompile();
    }

    // ---------- run mode ----------

    public bool IsRunning => _running is not null;

    public MachineSimulation StartRun()
    {
        StopRun();
        _running = MachineSimulation.FromMachine(Machine, Catalog);
        foreach (var stepper in _running.Mcu.Steppers)
            stepper.Enable();
        return _running;
    }

    public void StopRun()
    {
        _running?.Dispose();
        _running = null;
        Recompile(); // restore the edit scene at qpos0
    }

    public MachineSimulation RunningSimulation =>
        _running ?? throw new InvalidOperationException("Not running.");

    public void Dispose()
    {
        _editSimulator?.Dispose();
        _running?.Dispose();
    }
}
