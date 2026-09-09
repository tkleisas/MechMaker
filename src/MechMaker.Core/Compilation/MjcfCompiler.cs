using System.Globalization;
using System.Xml.Linq;
using MechMaker.Core.Mathematics;
using MechMaker.Core.Model;
using MechMaker.Core.Validation;

namespace MechMaker.Core.Compilation;

public sealed class MjcfCompileException : Exception
{
    public ValidationReport Report { get; }

    public MjcfCompileException(ValidationReport report)
        : base($"Machine definition failed validation:{Environment.NewLine}{report}")
        => Report = report;

    public MjcfCompileException(Diagnostic diagnostic)
        : this(NewReport(diagnostic)) { }

    private static ValidationReport NewReport(Diagnostic diagnostic)
    {
        var report = new ValidationReport();
        report.Diagnostics.Add(diagnostic);
        return report;
    }
}

/// <summary>
/// Compiles a validated machine definition into a MuJoCo MJCF model.
/// Parts become bodies; connections become the body tree (welds = nesting,
/// hinges/slides = joints on the child body, motor rotors carry their own
/// actuated joint); belts become joint couplers.
/// </summary>
public sealed class MjcfCompiler(PartCatalog catalog)
{
    private const double BeltGeomLengthDefault = 0.2;

    private readonly MachineValidator _validator = new(catalog);

    // Per-compile state.
    private XDocument _document = null!;
    private XElement _worldbody = null!;
    private XElement _equalityHost = null!;
    private XElement _actuatorHost = null!;
    private Dictionary<string, PartDefinition> _definitionOf = null!;
    private Dictionary<string, Transform> _bodyWorld = null!;
    private Dictionary<string, XElement> _bodyElement = null!;
    private Dictionary<string, string> _drivingJointOf = null!;
    private ValidationReport _report = null!;

    /// <summary>Diagnostics (including belt-coupler infos) from the most recent Compile call.</summary>
    public ValidationReport LastReport => _report;

    public XDocument Compile(MachineDefinition machine)
    {
        var report = _validator.Validate(machine);
        if (report.HasErrors)
            throw new MjcfCompileException(report);
        _report = report;
        _definitionOf = machine.Parts.ToDictionary(p => p.Id, p => catalog.Get(p.Part), StringComparer.Ordinal);
        _bodyWorld = [];
        _bodyElement = [];
        _drivingJointOf = [];

        CreateDocument(machine);
        BuildTree(machine);
        EmitBelts(machine);

        return _document;
    }

    // ---------- document scaffold ----------

    private void CreateDocument(MachineDefinition machine)
    {
        _document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("mujoco", new XAttribute("model", machine.Name),
                new XElement("compiler", new XAttribute("angle", "radian")),
                new XElement("option",
                    new XAttribute("timestep", "0.000125"),
                    new XAttribute("integrator", "implicitfast")),
                new XElement("default",
                    new XElement("geom",
                        new XAttribute("friction", "0.5 0.005 0.0001"),
                        new XAttribute("condim", "3"))),
                new XElement("visual",
                    new XElement("headlight", new XAttribute("diffuse", "0.6 0.6 0.6"),
                        new XAttribute("ambient", "0.3 0.3 0.3"))),
                new XElement("worldbody",
                    new XElement("geom", new XAttribute("name", "ground"),
                        new XAttribute("type", "plane"), new XAttribute("size", "2 2 0.05"),
                        // Visual only: assemblies are bolted to their own structure, and a
                        // colliding floor at z=0 shreds any shaft that happens to sit there.
                        new XAttribute("contype", "0"), new XAttribute("conaffinity", "0"),
                        new XAttribute("rgba", "0.25 0.25 0.28 1")),
                    new XElement("light", new XAttribute("pos", "1 -1 2"), new XAttribute("dir", "-0.3 0.3 -1"))),
                new XElement("equality"),
                new XElement("actuator")));

        _worldbody = _document.Root!.Element("worldbody")!;
        _equalityHost = _document.Root.Element("equality")!;
        _actuatorHost = _document.Root.Element("actuator")!;
    }

    // ---------- body tree ----------

    private void BuildTree(MachineDefinition machine)
    {
        var root = machine.Parts.FirstOrDefault(p => !_definitionOf[p.Id].IsTransmissionElement)
                   ?? throw new MjcfCompileException(Error("mm025", "Machine contains no mountable (non-transmission) parts."));

        var compiled = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        EmitPart(machine, root.Id, ToTransform(root.Pose));

        var structural = machine.Connections.Where(c => !TouchesTransmission(c)).ToList();
        var deferred = new List<Connection>();

        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var connection in structural)
            {
                var parentIsA = compiled.Contains(connection.PartA);
                var childIsB = !compiled.Contains(connection.PartB);
                string parentInst, childInst, parentConnectorName, childConnectorName;

                if (parentIsA && childIsB)
                    (parentInst, childInst, parentConnectorName, childConnectorName) =
                        (connection.PartA, connection.PartB, connection.ConnectorA, connection.ConnectorB);
                else if (compiled.Contains(connection.PartB) && !compiled.Contains(connection.PartA))
                    (parentInst, childInst, parentConnectorName, childConnectorName) =
                        (connection.PartB, connection.PartA, connection.ConnectorB, connection.ConnectorA);
                else
                {
                    deferred.Add(connection);
                    continue;
                }

                AttachPart(machine, connection, parentInst, parentConnectorName, childInst, childConnectorName);
                compiled.Add(childInst);
                progress = true;
            }
            structural = deferred;
            deferred = [];
        }

        foreach (var instance in machine.Parts.Where(p => !compiled.Contains(p.Id) && !_definitionOf[p.Id].IsTransmissionElement))
            _report.AddWarning("mm031",
                $"Part '{instance.Id}' is not attached to the main assembly; it is not part of the compiled model.",
                instance.Id);
    }

    private void AttachPart(MachineDefinition machine, Connection connection,
        string parentInst, string parentConnectorName, string childInst, string childConnectorName)
    {
        var parentDef = _definitionOf[parentInst];
        var childDef = _definitionOf[childInst];
        var parentConnector = FindConnector(parentDef, parentConnectorName, connection, parentInst);
        var childConnector = FindConnector(childDef, childConnectorName, connection, childInst);
        var jointKind = ConnectorRules.InferJoint(parentConnector.Type, childConnector.Type);

        var parentBodyKey = Key(parentInst, parentConnector.Body);
        if (!_bodyWorld.TryGetValue(parentBodyKey, out var parentBodyWorld))
            throw new MjcfCompileException(Error("mm027",
                $"Connection '{connection.Id}': body '{parentConnector.Body}' of '{parentInst}' is not placed yet.",
                connection.Id));

        var parentConnectorWorld = parentBodyWorld * ToTransform(parentConnector.Pose);

        // Mating: facing surfaces — the child connector's Z anti-aligns with the
        // parent connector's Z (belts are the exception and simply align).
        var matingRotation = jointKind == JointKind.Belt
            ? parentConnectorWorld.Rotation
            : parentConnectorWorld.Rotation * Quat.FlipZ;

        var childConnectorWorld = new Transform(parentConnectorWorld.Position, matingRotation);
        var childBodyWorld = childConnectorWorld * ToTransform(childConnector.Pose).Inverse();

        // If the child connector sits on a sub-body, walk back to the child's root body.
        var childBody = childDef.Bodies.FirstOrDefault(b => b.Name == childConnector.Body)
                        ?? throw new MjcfCompileException(Error("mm026",
                            $"Connector '{childConnector.Name}' of '{childInst}' references unknown body '{childConnector.Body}'."));
        Transform childRootWorld;
        if (childBody.Name == childDef.RootBody.Name)
        {
            childRootWorld = childBodyWorld;
        }
        else
        {
            if (childBody.ParentBody != childDef.RootBody.Name)
                throw new MjcfCompileException(Error("mm026",
                    $"Only one level of sub-bodies is supported (body '{childBody.Name}' of '{childInst}')."));
            childRootWorld = childBodyWorld * ToTransform(childBody.RelativePose).Inverse();
        }

        // A child mating onto an actuated parent body (motor rotor) spins with it;
        // otherwise hinge/slide children get their own joint.
        var parentBodyDef = parentDef.Bodies.First(b => b.Name == parentConnector.Body);
        var parentIsActuated = parentBodyDef.Actuated != JointActuation.None;
        var childGetsJoint = !parentIsActuated && jointKind is JointKind.Hinge or JointKind.Slide;

        var relative = parentBodyWorld.Inverse() * childRootWorld;
        var childElement = new XElement("body", new XAttribute("name", childInst));
        SetTransformAttributes(childElement, relative);
        _bodyElement[parentBodyKey].Add(childElement);

        Vec3 jointAxisLocal = Vec3.UnitZ;
        if (jointKind == JointKind.Slide)
        {
            var axisWorld = parentConnectorWorld.Rotation.Rotate(parentConnector.Axis.Normalized());
            var childWorldRotation = parentBodyWorld.Rotation * relative.Rotation;
            jointAxisLocal = childWorldRotation.InverseRotate(axisWorld).Normalized();
        }

        EmitPart(machine, childInst, childRootWorld, childElement, relative, jointKind, childGetsJoint, jointAxisLocal);

        if (parentIsActuated && _drivingJointOf.TryGetValue(parentInst, out var parentJoint))
            _drivingJointOf[childInst] = parentJoint;
    }

    /// <summary>
    /// Emits a part: root body at <paramref name="rootWorld"/> (world pose; the element
    /// carries <paramref name="relativeToParent"/> when nested), then sub-bodies, then
    /// records world transforms for connectors.
    /// </summary>
    private void EmitPart(MachineDefinition machine, string instanceId, Transform rootWorld,
        XElement? rootElement = null, Transform? relativeToParent = null, JointKind jointKind = JointKind.Weld,
        bool addAttachmentJoint = false, Vec3 jointAxisLocal = default)
    {
        var definition = _definitionOf[instanceId];

        if (rootElement is null)
        {
            rootElement = new XElement("body", new XAttribute("name", instanceId));
            SetTransformAttributes(rootElement, rootWorld);
            _worldbody.Add(rootElement);
        }

        _bodyWorld[Key(instanceId, definition.RootBody.Name)] = rootWorld;
        _bodyElement[Key(instanceId, definition.RootBody.Name)] = rootElement;

        foreach (var body in definition.Bodies)
        {
            XElement element;
            Transform world;

            if (body.Name == definition.RootBody.Name)
            {
                element = rootElement;
                world = rootWorld;
                if (relativeToParent is { } rel)
                    SetTransformAttributes(element, rel);
            }
            else
            {
                var parentKey = Key(instanceId, body.ParentBody ?? definition.RootBody.Name);
                if (!_bodyWorld.TryGetValue(parentKey, out var parentWorld))
                    throw new MjcfCompileException(Error("mm026",
                        $"Body '{body.Name}' of '{instanceId}' references unknown parent body '{body.ParentBody}'."));
                var relative = ToTransform(body.RelativePose);
                world = parentWorld * relative;
                element = new XElement("body", new XAttribute("name", BodyName(instanceId, body.Name)));
                SetTransformAttributes(element, relative);
                _bodyElement[parentKey].Add(element);
            }

            _bodyWorld[Key(instanceId, body.Name)] = world;
            _bodyElement[Key(instanceId, body.Name)] = element;

            foreach (var geom in GeomsFor(body, $"{instanceId}_{body.Name}"))
                element.Add(geom);

            if (body.Actuated != JointActuation.None)
                EmitActuatedJoint(instanceId, definition, body, element);
        }

        if (addAttachmentJoint)
        {
            rootElement.Add(new XElement("joint",
                new XAttribute("name", $"j_{instanceId}"),
                new XAttribute("type", jointKind == JointKind.Slide ? "slide" : "hinge"),
                new XAttribute("axis", Vec(jointAxisLocal == default ? Vec3.UnitZ : jointAxisLocal)),
                new XAttribute("pos", "0 0 0"),
                new XAttribute("limited", "false")));
            _drivingJointOf[instanceId] = $"j_{instanceId}";
        }
    }

    private void EmitActuatedJoint(string instanceId, PartDefinition definition, PartBody body, XElement element)
    {
        var jointName = $"j_{instanceId}_{body.Name}";
        element.Add(new XElement("joint",
            new XAttribute("name", jointName),
            new XAttribute("type", body.Actuated == JointActuation.Hinge ? "hinge" : "slide"),
            new XAttribute("axis", "0 0 1"),
            new XAttribute("pos", "0 0 0"),
            new XAttribute("limited", "false"),
            // Bearing damping keeps unconstrained joints well-conditioned. Deliberately
            // NO frictionloss: dry friction on a ZOH-driven magnetic spring ratchets
            // the rotor backwards (stick-slip rectification). The M2 motor model
            // supplies the torque; armature carries the reflected inertia.
            new XAttribute("damping", "0.01"),
            new XAttribute("frictionloss", "0"),
            new XAttribute("armature", Num(definition.Motor?.RotorInertiaKgM2 ?? 0))));
        _drivingJointOf[instanceId] = jointName;

        if (definition.Motor is { } motor)
        {
            var torque = Num(motor.HoldingTorqueNm);
            _actuatorHost.Add(new XElement("motor",
                new XAttribute("name", $"a_{instanceId}"),
                new XAttribute("joint", jointName),
                new XAttribute("ctrlrange", $"-{torque} {torque}")));
        }
    }

    // ---------- belts ----------

    private void EmitBelts(MachineDefinition machine)
    {
        foreach (var belt in machine.Parts.Where(p => _definitionOf[p.Id].IsTransmissionElement))
        {
            var beltDef = _definitionOf[belt.Id];
            var connections = machine.Connections
                .Where(c => c.PartA == belt.Id || c.PartB == belt.Id)
                .ToList();

            var pulleyInstances = new List<string>();
            string? clampedInstance = null;
            foreach (var connection in connections)
            {
                var otherId = connection.PartA == belt.Id ? connection.PartB : connection.PartA;
                var kind = InferConnectionKind(machine, connection);
                switch (kind)
                {
                    case JointKind.Belt when !pulleyInstances.Contains(otherId):
                        pulleyInstances.Add(otherId);
                        break;
                    case JointKind.Clamp:
                        clampedInstance = otherId;
                        break;
                }
            }

            if (pulleyInstances.Count != 2)
            {
                _report.AddWarning("mm021",
                    $"Belt '{belt.Id}' connects {pulleyInstances.Count} pulleys (expected 2); coupler may be missing.",
                    belt.Id);
            }
            else if (!_drivingJointOf.TryGetValue(pulleyInstances[0], out var joint1) ||
                     !_drivingJointOf.TryGetValue(pulleyInstances[1], out var joint2))
            {
                _report.AddWarning("mm028",
                    $"Belt '{belt.Id}': one or both pulleys ({pulleyInstances[0]}, {pulleyInstances[1]}) have no driving joint; no coupler emitted.",
                    belt.Id);
            }
            else
            {
                var radius1 = PulleyRadius(pulleyInstances[0]);
                var radius2 = PulleyRadius(pulleyInstances[1]);
                var ratio = radius1 / radius2;
                _equalityHost.Add(new XElement("joint",
                    new XAttribute("name", $"eq_belt_{belt.Id}"),
                    new XAttribute("joint1", joint1),
                    new XAttribute("joint2", joint2),
                    new XAttribute("solref", "0.002 1"),
                    new XAttribute("solimp", "0.95 0.99 0.001"),
                    new XAttribute("polycoef", $"0 {Num(-ratio)} 0 0 0")));
                _report.Add("mm029", Severity.Info,
                    $"Belt '{belt.Id}': coupled {pulleyInstances[0]} ({radius1 * 1000:0.##} mm) to {pulleyInstances[1]} ({radius2 * 1000:0.##} mm), ratio {ratio:0.###}.",
                    belt.Id);

                if (clampedInstance is not null)
                    EmitClampCoupler(belt.Id, beltDef, clampedInstance, joint1, radius1);
            }

            // Schematic visual strap at the belt's declared pose (no collision).
            var length = beltDef.Params.GetValueOrDefault("length", BeltGeomLengthDefault);
            var strap = new XElement("body", new XAttribute("name", belt.Id));
            SetTransformAttributes(strap, ToTransform(belt.Pose));
            strap.Add(new XElement("geom",
                new XAttribute("name", $"{belt.Id}_strap"),
                new XAttribute("type", "box"),
                new XAttribute("size", $"{Num(length / 2)} 0.0035 0.0008"),
                new XAttribute("mass", "0.001"),
                new XAttribute("contype", "0"),
                new XAttribute("conaffinity", "0"),
                new XAttribute("rgba", "0.15 0.15 0.17 1")));
            _worldbody.Add(strap);
        }
    }

    /// <summary>
    /// The clamped part (e.g. a carriage) rides the belt: its joint coordinate is
    /// rigidly coupled to the belt's arc length, i.e. pitch_radius × pulley angle.
    /// Sign depends on belt wrap direction; override with the belt's clamp_sign param.
    /// </summary>
    private void EmitClampCoupler(string beltId, PartDefinition beltDef, string clampedInstance,
        string pulleyJoint, double pulleyRadius)
    {
        var clampJoint = $"j_{clampedInstance}";
        var sign = beltDef.Params.GetValueOrDefault("clamp_sign", 1);
        var metersPerRadian = -sign * pulleyRadius;
        _equalityHost.Add(new XElement("joint",
            new XAttribute("name", $"eq_clamp_{beltId}"),
            new XAttribute("joint1", clampJoint),
            new XAttribute("joint2", pulleyJoint),
            new XAttribute("solref", "0.002 1"),
            new XAttribute("solimp", "0.95 0.99 0.001"),
            new XAttribute("polycoef", $"0 {Num(metersPerRadian)} 0 0 0")));
        _report.Add("mm033", Severity.Info,
            $"Belt '{beltId}': '{clampedInstance}' rides the belt ({Math.Abs(metersPerRadian) * 1000 * 2 * Math.PI:0.##} mm per pulley revolution).",
            beltId);
    }

    private JointKind InferConnectionKind(MachineDefinition machine, Connection connection)
    {
        var connectorA = FindConnector(_definitionOf[connection.PartA], connection.ConnectorA, connection, connection.PartA);
        var connectorB = FindConnector(_definitionOf[connection.PartB], connection.ConnectorB, connection, connection.PartB);
        return ConnectorRules.InferJoint(connectorA.Type, connectorB.Type);
    }

    private double PulleyRadius(string instanceId)
    {
        var definition = _definitionOf[instanceId];
        if (!definition.Params.TryGetValue("pitch_radius", out var radius))
            throw new MjcfCompileException(Error("mm030",
                $"Pulley '{instanceId}' (catalog '{definition.Id}') is missing the 'pitch_radius' param.", instanceId));
        return radius;
    }

    // ---------- helpers ----------

    private Diagnostic Error(string code, string message, string? subject = null)
        => new(code, Severity.Error, message, subject);

    private bool TouchesTransmission(Connection connection)
        => _definitionOf.TryGetValue(connection.PartA, out var a) && a.IsTransmissionElement
           || _definitionOf.TryGetValue(connection.PartB, out var b) && b.IsTransmissionElement;

    private ConnectorDefinition FindConnector(PartDefinition definition, string name, Connection connection, string partId)
        => definition.Connectors.FirstOrDefault(c => c.Name == name)
           ?? throw new MjcfCompileException(Error("mm003",
               $"Part '{partId}' (catalog '{definition.Id}') has no connector '{name}'.", connection.Id));

    private static string Key(string instanceId, string bodyName) => $"{instanceId}:{bodyName}";

    private static string BodyName(string instanceId, string bodyName)
        => bodyName == "root" ? instanceId : $"{instanceId}_{bodyName}";

    private static Transform ToTransform(Pose pose) => new(pose.Position, pose.Rotation);

    private static void SetTransformAttributes(XElement element, Transform transform)
    {
        element.SetAttributeValue("pos", Vec(transform.Position));
        element.SetAttributeValue("quat", QuatAttr(transform.Rotation));
    }

    private static string Vec(Vec3 v) => $"{Num(v.X)} {Num(v.Y)} {Num(v.Z)}";
    private static string QuatAttr(Quat q) => $"{Num(q.W)} {Num(q.X)} {Num(q.Y)} {Num(q.Z)}";
    private static string Num(double d)
    {
        if (Math.Abs(d) < 1e-12) d = 0; // snap float noise (-1e-17 formats as "-0")
        return d.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<XElement> GeomsFor(PartBody body, string prefix)
    {
        var volumes = body.Shapes.Select(ShapeVolume).ToList();
        var total = volumes.Sum();
        if (total <= 0) total = 1;
        for (var i = 0; i < body.Shapes.Count; i++)
        {
            var shape = body.Shapes[i];
            var geom = new XElement("geom", new XAttribute("name", $"{prefix}_g{i}"),
                new XAttribute("rgba", shape.Rgba),
                new XAttribute("mass", Num(body.MassKg * volumes[i] / total)));
            var pose = shape.RelativePose;
            if (pose.Position != Vec3.Zero)
                geom.SetAttributeValue("pos", Vec(pose.Position));
            if (pose.RotationEulerDeg != Vec3.Zero)
                geom.SetAttributeValue("quat", QuatAttr(pose.Rotation));

            switch (shape.Kind)
            {
                case ShapeKind.Box:
                    geom.SetAttributeValue("type", "box");
                    geom.SetAttributeValue("size", $"{Num(shape.Extents.X / 2)} {Num(shape.Extents.Y / 2)} {Num(shape.Extents.Z / 2)}");
                    break;
                case ShapeKind.Cylinder:
                    geom.SetAttributeValue("type", "cylinder");
                    geom.SetAttributeValue("size", $"{Num(shape.Extents.X)} {Num(shape.Extents.Y / 2)}");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape.Kind), shape.Kind, null);
            }
            yield return geom;
        }
    }

    private static double ShapeVolume(Shape shape) => shape.Kind switch
    {
        ShapeKind.Box => shape.Extents.X * shape.Extents.Y * shape.Extents.Z,
        ShapeKind.Cylinder => Math.PI * shape.Extents.X * shape.Extents.X * shape.Extents.Y,
        _ => 0
    };
}
