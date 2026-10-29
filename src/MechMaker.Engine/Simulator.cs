using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Evergine.Bindings.MuJoCo;
using MechMaker.Core.Geometry;
using MechMaker.Core.Mathematics;

namespace MechMaker.Engine;

/// <summary>
/// Safe wrapper over a native MuJoCo model + data pair. Owns the native lifetime;
/// deterministic by construction (no threads, no randomness, fixed timestep).
/// </summary>
public sealed unsafe class Simulator : IDisposable
{
    public const double DefaultTimestep = 0.001;

    private mjModel* _model;
    private mjData* _data;
    private readonly Dictionary<string, MeshAssetRef> _meshAssets;
    private readonly record struct MeshAssetRef(string File, double Sx, double Sy, double Sz);

    private Simulator(mjModel* model, double timestep, Dictionary<string, MeshAssetRef>? meshAssets = null)
    {
        _model = model;
        Timestep = timestep;
        _meshAssets = meshAssets ?? [];
        _data = MuJoCo.mj_makeData(model);
        if (_data is null)
            throw new InvalidOperationException("MuJoCo failed to allocate simulation data.");
    }

    public double Timestep { get; }
    public double Time => _data->time;
    public long StepCount { get; private set; }

    // ---------- construction ----------

    public static Simulator FromMjcfFile(string path)
    {
        byte* error = stackalloc byte[1024];
        var model = MuJoCo.mj_loadXML(path, null, error, 1024);
        if (model is null)
            throw new InvalidOperationException(LastError(error));
        return new Simulator(model, ReadTimestep(path), ReadMeshAssets(path));
    }

    private static Dictionary<string, MeshAssetRef> ReadMeshAssets(string mjcfPath)
    {
        var map = new Dictionary<string, MeshAssetRef>(StringComparer.Ordinal);
        var asset = XDocument.Load(mjcfPath).Root?.Element("asset");
        if (asset is null)
            return map;
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(mjcfPath)) ?? Directory.GetCurrentDirectory();
        foreach (var mesh in asset.Elements("mesh"))
        {
            var name = (string?)mesh.Attribute("name");
            var file = (string?)mesh.Attribute("file");
            if (name is null || file is null)
                continue;
            if (!Path.IsPathRooted(file))
                file = Path.GetFullPath(Path.Combine(baseDir, file));
            var sx = 1.0; var sy = 1.0; var sz = 1.0;
            var scale = (string?)mesh.Attribute("scale");
            if (!string.IsNullOrWhiteSpace(scale))
            {
                var parts = scale.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3)
                {
                    sx = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    sy = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    sz = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            map[name] = new MeshAssetRef(file, sx, sy, sz);
        }
        return map;
    }

    public static Simulator FromMjcf(string xml)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mechmaker_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempPath, xml, new UTF8Encoding(false));
            return FromMjcfFile(tempPath);
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }

    private static double ReadTimestep(string path)
    {
        var option = XDocument.Load(path).Root?.Element("option");
        var value = option?.Attribute("timestep")?.Value;
        return value is null ? DefaultTimestep : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string LastError(byte* error)
        => Marshal.PtrToStringUTF8((IntPtr)error) ?? "Unknown MuJoCo XML load error.";

    // ---------- stepping ----------

    /// <summary>Advances exactly one timestep.</summary>
    public void Step()
    {
        MuJoCo.mj_step(_model, _data);
        StepCount++;
    }

    /// <summary>Advances by the closest whole number of timesteps to the requested duration.</summary>
    public void RunFor(double seconds)
    {
        var steps = (long)Math.Round(seconds / Timestep);
        for (long i = 0; i < steps; i++)
            Step();
    }

    // ---------- actuation ----------

    public int ActuatorId(string name)
    {
        var id = MuJoCo.mj_name2id(_model, (int)mjtObj.mjOBJ_ACTUATOR, name);
        return id < 0 ? throw new KeyNotFoundException($"No actuator named '{name}'.") : id;
    }

    public void SetCtrl(string actuatorName, double value)
        => _data->ctrl[ActuatorId(actuatorName)] = value;

    internal void SetCtrlByIndex(int actuatorId, double value)
        => _data->ctrl[actuatorId] = value;

    /// <summary>Diagnostics: last commanded ctrl for an actuator.</summary>
    public double GetCtrl(int actuatorId) => _data->ctrl[actuatorId];

    /// <summary>Diagnostics: actuator force realized on its transmission (qfrc_actuator on the joint DOF).</summary>
    public double GetActuatorForce(int actuatorId) => _data->actuator_force[actuatorId];

    /// <summary>Diagnostics: total actuator torque on a joint's DOF (qfrc_actuator).</summary>
    public double GetJointActuatorTorque(string jointName)
    {
        var dof = _model->jnt_dofadr[JointId(jointName)];
        return _data->qfrc_actuator[dof];
    }

    /// <summary>Diagnostics: active contacts as "geom1 geom2 dist" lines.</summary>
    public IReadOnlyList<string> ContactSummary()
    {
        var list = new List<string>((int)_data->ncon);
        for (var i = 0; i < _data->ncon; i++)
        {
            var c = _data->contact[i];
            list.Add($"{GeomName(c.geom1)} <-> {GeomName(c.geom2)} dist={c.dist:0.######}");
        }
        return list;
    }

    public int ContactCount => (int)_data->ncon;

    /// <summary>Active contacts with world positions (the contact point midpoint) —
    /// the input the HIL physics-contact bridge consumes.</summary>
    public IReadOnlyList<(string Geom1, string Geom2, double X, double Y, double Z)> Contacts()
    {
        var list = new List<(string, string, double, double, double)>((int)_data->ncon);
        for (var i = 0; i < _data->ncon; i++)
        {
            var c = _data->contact[i];
            list.Add((GeomName(c.geom1), GeomName(c.geom2),
                c.pos[0], c.pos[1], c.pos[2]));
        }
        return list;
    }

    private string GeomName(int id)
        => Marshal.PtrToStringUTF8((IntPtr)MuJoCo.mj_id2name(_model, (int)mjtObj.mjOBJ_GEOM, id)) ?? $"geom{id}";

    /// <summary>The joint an actuator drives (first transmission target).</summary>
    public string JointForActuator(int actuatorId)
    {
        var jointId = _model->actuator_trnid[actuatorId * 2];
        return JointName(jointId);
    }

    // ---------- state access ----------

    public int JointId(string name)
    {
        var id = MuJoCo.mj_name2id(_model, (int)mjtObj.mjOBJ_JOINT, name);
        return id < 0 ? throw new KeyNotFoundException($"No joint named '{name}'.") : id;
    }

    public string JointName(int id)
        => Marshal.PtrToStringUTF8((IntPtr)MuJoCo.mj_id2name(_model, (int)mjtObj.mjOBJ_JOINT, id))
           ?? throw new KeyNotFoundException($"Joint id {id} has no name.");

    public string ActuatorName(int id)
        => Marshal.PtrToStringUTF8((IntPtr)MuJoCo.mj_id2name(_model, (int)mjtObj.mjOBJ_ACTUATOR, id))
           ?? throw new KeyNotFoundException($"Actuator id {id} has no name.");

    public double GetJointPos(string name) => _data->qpos[_model->jnt_qposadr[JointId(name)]];

    public double GetJointVel(string name) => _data->qvel[_model->jnt_dofadr[JointId(name)]];

    public int BodyId(string name)
    {
        var id = MuJoCo.mj_name2id(_model, (int)mjtObj.mjOBJ_BODY, name);
        return id < 0 ? throw new KeyNotFoundException($"No body named '{name}'.") : id;
    }

    public double[] GetBodyPosition(string name)
    {
        var id = BodyId(name);
        return [_data->xpos[id * 3], _data->xpos[id * 3 + 1], _data->xpos[id * 3 + 2]];
    }

    /// <summary>Body orientation as (w, x, y, z) — for computing adapter rotations
    /// between stacked frames (the gantry's screw-to-finger mounts).</summary>
    public double[] GetBodyQuaternion(string name)
    {
        var id = BodyId(name);
        return [_data->xquat[id * 4], _data->xquat[id * 4 + 1], _data->xquat[id * 4 + 2], _data->xquat[id * 4 + 3]];
    }

    /// <summary>Copy of the full position vector (deterministic snapshots for tests/reports).</summary>
    public double[] QposSnapshot()
    {
        var copy = new double[_model->nq];
        fixed (double* dst = copy)
            Buffer.MemoryCopy(_data->qpos, dst, copy.Length * sizeof(double), copy.Length * sizeof(double));
        return copy;
    }

    // ---------- report ----------

    public SimulationReport BuildReport()
    {
        var joints = new List<SimulationReport.JointInfo>();
        for (var i = 0; i < _model->njnt; i++)
        {
            joints.Add(new SimulationReport.JointInfo(
                JointName((int)i),
                (mjtJoint)_model->jnt_type[(int)i] switch
                {
                    mjtJoint.mjJNT_FREE => "free",
                    mjtJoint.mjJNT_BALL => "ball",
                    mjtJoint.mjJNT_SLIDE => "slide",
                    mjtJoint.mjJNT_HINGE => "hinge",
                    _ => "unknown"
                },
                _model->jnt_axis[(int)i * 3],
                _model->jnt_axis[(int)i * 3 + 1],
                _model->jnt_axis[(int)i * 3 + 2]));
        }

        var actuators = new List<string>();
        for (var i = 0; i < _model->nu; i++)
            actuators.Add(ActuatorName((int)i));

        var totalMass = 0.0;
        for (var i = 0; i < _model->nbody; i++)
            totalMass += _model->body_mass[(int)i];

        var report = new SimulationReport
        {
            Timestep = Timestep,
            TotalMassKg = totalMass,
            BodyCount = (int)_model->nbody,
            Joints = joints,
            Actuators = actuators
        };

        if (_model->nu == 0)
            report.Notes.Add("Model has no actuators — nothing can be driven.");
        return report;
    }

    // ---------- scene extraction (renderer input) ----------

    /// <summary>Runs kinematics only (no dynamics) so geom world poses are current.</summary>
    public void RefreshKinematics() => MuJoCo.mj_kinematics(_model, _data);

    /// <summary>All visual geoms with world pose, size, and color.</summary>
    public IReadOnlyList<SceneShape> GetScene()
    {
        RefreshKinematics();
        var shapes = new List<SceneShape>((int)_model->ngeom);
        for (var i = 0; i < _model->ngeom; i++)
        {
            if (_model->geom_rgba[i * 4 + 3] <= 0.001)
                continue; // collision-only proxy
            var type = (mjtGeom)_model->geom_type[i];
            SceneShapeKind kind;
            double hx, hy, hz;
            string? meshFile = null;
            double sx = 1, sy = 1, sz = 1;
            double mcx = 0, mcy = 0, mcz = 0;
            var px = _data->geom_xpos[i * 3];
            var py = _data->geom_xpos[i * 3 + 1];
            var pz = _data->geom_xpos[i * 3 + 2];
            var r00 = _data->geom_xmat[i * 9];
            var r01 = _data->geom_xmat[i * 9 + 1];
            var r02 = _data->geom_xmat[i * 9 + 2];
            var r10 = _data->geom_xmat[i * 9 + 3];
            var r11 = _data->geom_xmat[i * 9 + 4];
            var r12 = _data->geom_xmat[i * 9 + 5];
            var r20 = _data->geom_xmat[i * 9 + 6];
            var r21 = _data->geom_xmat[i * 9 + 7];
            var r22 = _data->geom_xmat[i * 9 + 8];

            switch (type)
            {
                case mjtGeom.mjGEOM_BOX:
                    kind = SceneShapeKind.Box;
                    hx = _model->geom_size[i * 3];
                    hy = _model->geom_size[i * 3 + 1];
                    hz = _model->geom_size[i * 3 + 2];
                    break;
                case mjtGeom.mjGEOM_CYLINDER:
                    kind = SceneShapeKind.Cylinder;
                    hx = _model->geom_size[i * 3];
                    hy = _model->geom_size[i * 3 + 1];
                    hz = 0;
                    break;
                case mjtGeom.mjGEOM_SPHERE:
                    kind = SceneShapeKind.Sphere;
                    hx = _model->geom_size[i * 3];
                    hy = hz = 0;
                    break;
                case mjtGeom.mjGEOM_CAPSULE:
                    kind = SceneShapeKind.Capsule;
                    hx = _model->geom_size[i * 3];
                    hy = _model->geom_size[i * 3 + 1];
                    hz = 0;
                    break;
                case mjtGeom.mjGEOM_ELLIPSOID:
                    kind = SceneShapeKind.Ellipsoid;
                    hx = _model->geom_size[i * 3];
                    hy = _model->geom_size[i * 3 + 1];
                    hz = _model->geom_size[i * 3 + 2];
                    break;
                case mjtGeom.mjGEOM_MESH:
                    kind = SceneShapeKind.Mesh;
                    var meshId = _model->geom_dataid[i];
                    var meshName = Marshal.PtrToStringUTF8((IntPtr)MuJoCo.mj_id2name(_model, (int)mjtObj.mjOBJ_MESH, meshId));
                    if (meshName is not null && _meshAssets.TryGetValue(meshName, out var asset))
                    {
                        meshFile = asset.File;
                        sx = asset.Sx; sy = asset.Sy; sz = asset.Sz;
                        var geo = MeshGeometry.Load(asset.File, new Vec3(sx, sy, sz));
                        hx = geo.Size.X / 2; hy = geo.Size.Y / 2; hz = geo.Size.Z / 2;
                        mcx = geo.Center.X; mcy = geo.Center.Y; mcz = geo.Center.Z;

                        // MuJoCo centers mesh assets on their inertia frame. The
                        // compiled geom_xmat therefore includes that alignment, while
                        // the source vertices are in the authored frame. Rebuild the
                        // authored frame from the body pose and the authored geom pose
                        // so the visual mesh stays where the catalog put it.
                        var bodyId = _model->geom_bodyid[i];
                        var bodyT = new Transform(
                            new Vec3(_data->xpos[bodyId * 3], _data->xpos[bodyId * 3 + 1], _data->xpos[bodyId * 3 + 2]),
                            new Quat(_data->xquat[bodyId * 4], _data->xquat[bodyId * 4 + 1],
                                     _data->xquat[bodyId * 4 + 2], _data->xquat[bodyId * 4 + 3]));
                        var geomT = new Transform(
                            new Vec3(_model->geom_pos[i * 3], _model->geom_pos[i * 3 + 1], _model->geom_pos[i * 3 + 2]),
                            new Quat(_model->geom_quat[i * 4], _model->geom_quat[i * 4 + 1],
                                     _model->geom_quat[i * 4 + 2], _model->geom_quat[i * 4 + 3]));
                        var meshT = new Transform(
                            new Vec3(_model->mesh_pos[meshId * 3], _model->mesh_pos[meshId * 3 + 1], _model->mesh_pos[meshId * 3 + 2]),
                            new Quat(_model->mesh_quat[meshId * 4], _model->mesh_quat[meshId * 4 + 1],
                                     _model->mesh_quat[meshId * 4 + 2], _model->mesh_quat[meshId * 4 + 3]));
                        var authored = bodyT * (geomT * meshT.Inverse());
                        var worldCenter = authored.TransformPoint(new Vec3(mcx, mcy, mcz));
                        px = worldCenter.X; py = worldCenter.Y; pz = worldCenter.Z;
                        var q = authored.Rotation;
                        r00 = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
                        r01 = 2 * (q.X * q.Y - q.W * q.Z);
                        r02 = 2 * (q.X * q.Z + q.W * q.Y);
                        r10 = 2 * (q.X * q.Y + q.W * q.Z);
                        r11 = 1 - 2 * (q.X * q.X + q.Z * q.Z);
                        r12 = 2 * (q.Y * q.Z - q.W * q.X);
                        r20 = 2 * (q.X * q.Z - q.W * q.Y);
                        r21 = 2 * (q.Y * q.Z + q.W * q.X);
                        r22 = 1 - 2 * (q.X * q.X + q.Y * q.Y);
                    }
                    else
                    {
                        hx = 0.01; hy = 0.01; hz = 0.01;
                    }
                    break;
                default:
                    continue;
            }

            shapes.Add(new SceneShape(
                Name: GeomName(i),
                Kind: kind,
                HalfX: hx, HalfY: hy, HalfZ: hz,
                Px: px, Py: py, Pz: pz,
                R00: r00, R01: r01, R02: r02,
                R10: r10, R11: r11, R12: r12,
                R20: r20, R21: r21, R22: r22,
                R: _model->geom_rgba[i * 4],
                G: _model->geom_rgba[i * 4 + 1],
                B: _model->geom_rgba[i * 4 + 2],
                A: _model->geom_rgba[i * 4 + 3],
                MeshFile: meshFile,
                MeshScaleX: sx, MeshScaleY: sy, MeshScaleZ: sz,
                MeshCenterX: mcx, MeshCenterY: mcy, MeshCenterZ: mcz));
        }
        return shapes;
    }

    // ---------- disposal ----------

    public void Dispose()
    {
        if (_data is not null)
        {
            MuJoCo.mj_deleteData(_data);
            _data = null;
        }
        if (_model is not null)
        {
            MuJoCo.mj_deleteModel(_model);
            _model = null;
        }
    }
}
