using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Evergine.Bindings.MuJoCo;

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

    private Simulator(mjModel* model, double timestep)
    {
        _model = model;
        Timestep = timestep;
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
        return new Simulator(model, ReadTimestep(path));
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
