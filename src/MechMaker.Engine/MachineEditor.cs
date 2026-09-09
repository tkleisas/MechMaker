using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Model;
using MechMaker.Core.Validation;
using Vec3 = MechMaker.Core.Mathematics.Vec3;
using Transform = MechMaker.Core.Mathematics.Transform;
using Quat = MechMaker.Core.Mathematics.Quat;

namespace MechMaker.Engine;

/// <summary>
/// All editor logic, UI-agnostic: owns the machine definition, the catalog, the
/// edit-mode simulator, and the run-mode machine simulation. The window binds to
/// this and calls the mutators; every change recompiles the edit scene.
/// </summary>
public sealed class MachineEditor : IDisposable
{
    public PartCatalog Catalog { get; }
    public MachineDefinition Machine { get; private set; }
    public string MachinePath { get; private set; }

    private MachineSimulation? _running;
    public IReadOnlyList<SceneShape> EditScene => _editSceneCache;
    private IReadOnlyList<SceneShape> _editSceneCache = [];

    public event Action? MachineChanged;

    public MachineEditor(string catalogDir, string machinePath)
    {
        Catalog = PartCatalog.LoadFromDirectory(catalogDir);
        MachinePath = machinePath;
        Machine = File.Exists(machinePath)
            ? CoreJson.Deserialize<MachineDefinition>(File.ReadAllText(machinePath))
            : NewMachineDefinition();
        Recompile();
    }

    private static MachineDefinition NewMachineDefinition() => new() { Name = "new_machine" };

    public void NewMachine(string name)
    {
        MachinePath = Path.Combine(Path.GetDirectoryName(MachinePath)!, $"{name}.json");
        Machine = new MachineDefinition { Name = name };
        Recompile();
    }

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

    // ---------- snapped placement ----------

    /// <summary>
    /// The pose that mates a new part's connector onto a placed part's connector,
    /// using the exact convention the compiler applies when resolving connections:
    /// coincident frames, the incoming connector's Z anti-aligned with the target's
    /// (belts align instead). This gives correct edit-scene previews and a sane
    /// resting pose if the connection is later deleted.
    /// </summary>
    public Pose PoseForSnappedPart(string catalogId, string newConnectorName,
        string targetInstanceId, string targetConnectorName)
    {
        var targetInstance = Machine.Parts.FirstOrDefault(p => p.Id == targetInstanceId)
                             ?? throw new KeyNotFoundException($"No part instance '{targetInstanceId}'.");
        var targetDef = Catalog.Get(targetInstance.Part);
        var targetConnector = targetDef.Connectors.FirstOrDefault(c => c.Name == targetConnectorName)
                              ?? throw new KeyNotFoundException(
                                  $"Part '{targetInstanceId}' (catalog '{targetDef.Id}') has no connector '{targetConnectorName}'.");

        var newDef = Catalog.Get(catalogId);
        var newConnector = newDef.Connectors.FirstOrDefault(c => c.Name == newConnectorName)
                           ?? throw new KeyNotFoundException(
                               $"Catalog part '{catalogId}' has no connector '{newConnectorName}'.");

        var targetBodyWorld = BodyWorldFor(targetInstance, targetDef, targetConnector.Body);
        var targetConnectorWorld = targetBodyWorld * ToTransform(targetConnector.Pose);

        var jointKind = ConnectorRules.InferJoint(targetConnector.Type, newDef.Connectors
            .First(c => c.Name == newConnectorName).Type);
        // Mating: facing surfaces — the child connector's Z anti-aligns with the
        // target's Z (belts are the exception and simply align).
        var matingRotation = jointKind == JointKind.Belt
            ? targetConnectorWorld.Rotation
            : targetConnectorWorld.Rotation * Quat.FlipZ;
        var newConnectorWorld = new Transform(targetConnectorWorld.Position, matingRotation);

        var newBody = newDef.Bodies.FirstOrDefault(b => b.Name == newConnector.Body)
                      ?? throw new KeyNotFoundException(
                          $"Connector '{newConnectorName}' of '{catalogId}' references unknown body '{newConnector.Body}'.");
        var newBodyWorld = newConnectorWorld * ToTransform(newConnector.Pose).Inverse();
        var newRootWorld = newBody.Name == newDef.RootBody.Name
            ? newBodyWorld
            : newBodyWorld * ToTransform(newBody.RelativePose).Inverse();

        return Pose.FromTransform(newRootWorld);
    }

    /// <summary>World transform of a connector's host body, given the part instance pose.</summary>
    private static Transform BodyWorldFor(PartInstance instance, PartDefinition definition, string bodyName)
    {
        var rootWorld = ToTransform(instance.Pose);
        if (bodyName == definition.RootBody.Name)
            return rootWorld;
        var body = definition.Bodies.FirstOrDefault(b => b.Name == bodyName)
                   ?? throw new KeyNotFoundException(
                       $"Body '{bodyName}' not found in catalog part '{definition.Id}'.");
        return rootWorld * ToTransform(body.RelativePose);
    }

    /// <summary>
    /// Places a catalog part snapped onto a connector of a placed part and connects
    /// the two in one step — the "place, snap" interaction. Returns the instance id.
    /// </summary>
    public string AddPartSnapped(string catalogId, string newConnectorName,
        string targetInstanceId, string targetConnectorName, string? instanceId = null)
    {
        var pose = PoseForSnappedPart(catalogId, newConnectorName, targetInstanceId, targetConnectorName);
        var id = AddPart(catalogId, instanceId, pose);
        AddConnection(targetInstanceId, targetConnectorName, id, newConnectorName);
        return id;
    }

    private static Transform ToTransform(Pose pose) => new(pose.Position, pose.Rotation);

    // ---------- parts ----------

    public string AddPart(string catalogId, string? instanceId = null, Pose? pose = null)
    {
        var definition = Catalog.Get(catalogId);
        var id = instanceId is null ? UniqueInstanceId(catalogId) : instanceId;
        if (Machine.Parts.Any(p => p.Id == id))
            throw new InvalidOperationException($"Part instance id '{id}' already exists.");
        pose ??= new Pose { Position = new Vec3(0, 0, NextFreeZ(definition)) };
        Machine = Machine with
        {
            Parts = [.. Machine.Parts, new PartInstance { Id = id, Part = catalogId, Pose = pose }]
        };
        Recompile();
        return id;
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
        ThrowIfUnknownPart(instanceId);
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

    public PartDefinition PartOf(string instanceId)
    {
        ThrowIfUnknownPart(instanceId);
        return Catalog.Get(Machine.Parts.First(p => p.Id == instanceId).Part);
    }

    public void ThrowIfUnknownPart(string instanceId)
    {
        if (!Machine.Parts.Any(p => p.Id == instanceId))
            throw new KeyNotFoundException($"No part instance '{instanceId}'.");
    }

    // ---------- boards & wiring ----------

    public void AddBoard(string boardId, string type = "skr-pico")
    {
        if (Machine.Boards.Any(b => b.Id == boardId))
            throw new InvalidOperationException($"Board '{boardId}' already exists.");
        Machine = Machine with
        {
            Boards = [.. Machine.Boards, new BoardDefinition { Id = boardId, Type = type }]
        };
        MachineChanged?.Invoke();
    }

    public void Wire(string component, string signal, string board, string pin)
    {
        ThrowIfUnknownPart(component);
        if (Machine.Boards.Count == 0)
            throw new InvalidOperationException("No board to wire to — add one first.");
        Machine = Machine with
        {
            Wiring = [.. Machine.Wiring.Where(w => !(w.Component == component && w.Signal == signal)),
                new Wire { Component = component, Signal = signal, Board = board, Pin = pin }]
        };
        MachineChanged?.Invoke();
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
