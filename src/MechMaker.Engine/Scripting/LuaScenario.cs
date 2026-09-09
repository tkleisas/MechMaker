using MoonSharp.Interpreter;

namespace MechMaker.Engine.Scripting;

/// <summary>Outcome of running a Lua scenario against a machine simulation.</summary>
public sealed record ScenarioReport
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<string> Log { get; init; } = [];

    /// <summary>The script's `result` table as JSON, if it set one (machine-readable for CI/LLMs).</summary>
    public string? ResultJson { get; init; }

    /// <summary>Simulated seconds the script advanced.</summary>
    public double SimSeconds { get; init; }

    public override string ToString()
    {
        var lines = new List<string>();
        if (!Success)
            lines.Add($"FAILED: {Error}");
        lines.AddRange(Log);
        if (ResultJson is not null)
            lines.Add($"result: {ResultJson}");
        lines.Add($"sim time: {SimSeconds:0.###} s");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// A deterministic simulation scenario written in Lua. The script plays the host
/// role of the Klipper split: it commands the virtual MCU (velocities, ramps,
/// endstops), advances the physics, and observes the machine — exactly like the
/// C# tests do, but authored at runtime (by users, CI, or LLM agents).
///
/// Contract: the script must define `function run(sim) ... end`. It runs inside a
/// hard sandbox (no io/os/reflection), under an execution limit so runaway scripts
/// cannot hang the process. The engine is deterministic, so a scenario is too.
/// </summary>
public sealed class LuaScenario
{
    /// <summary>Cumulative simulated seconds a scenario may advance (guard against endless loops).</summary>
    public const double MaxTotalSimSeconds = 120;

    /// <summary>
    /// Wall-clock guard for runaway scripts: MoonSharp has no instruction limit API,
    /// so a pure-Lua `while true do end` can only be bounded by the clock. On expiry
    /// the report fails; the worker thread (BelowNormal priority, background) is
    /// abandoned and dies with the process.
    /// </summary>
    public TimeSpan WallClockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private readonly string _source;

    private LuaScenario(string source, TimeSpan? wallClockTimeout)
    {
        _source = source;
        if (wallClockTimeout is not null)
            WallClockTimeout = wallClockTimeout.Value;
    }

    public static LuaScenario Parse(string source, TimeSpan? wallClockTimeout = null) => new(source, wallClockTimeout);

    public static LuaScenario LoadFile(string path, TimeSpan? wallClockTimeout = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Scenario file not found: {path}");
        return new LuaScenario(File.ReadAllText(path), wallClockTimeout);
    }

    /// <summary>Human/LLM-readable description of the `sim` API handed to `run(sim)`.</summary>
    public const string ApiDoc = """
        sim API (passed to run(sim)):
          sim.enable(motor_id, on)              energize/de-energize a wired stepper
          sim.velocity(motor_id, rev_per_sec)   velocity target (ramps at the accel limit)
          sim.acceleration(motor_id, rev_s2)    acceleration limit (default 40 rev/s^2)
          sim.motor(motor_id)                   table: enabled, commanded_rev, actual_rev,
                                                velocity, target, missed_steps, stalled
          sim.add_endstop(joint, trigger)       limit switch at a joint position
          sim.add_endstop_debounced(joint, trigger, ticks)
          sim.endstop(joint)                    true when the endstop is pressed
          sim.joint_pos(joint) / sim.joint_vel(joint)
          sim.body_pos(body)                    table {x, y, z} in metres
          sim.run(seconds)                      advance the simulation (max 10 s per call)
          sim.time()                            simulated seconds elapsed
          print(...)                            log lines, shown in the report
          result = { ... }                      optional global, reported as JSON
        """;

    public ScenarioReport Run(MachineSimulation simulation)
    {
        var logLock = new object();
        var log = new List<string>();
        ScenarioReport? outcome = null;

        // The interpreter runs on its own thread: MoonSharp tracks thread affinity
        // for scripts, and the wall-clock watchdog needs a thread to abandon.
        var worker = new Thread(() =>
        {
            try
            {
                outcome = RunOnScriptThread(simulation, log, logLock);
            }
            catch (Exception e)
            {
                lock (logLock)
                    outcome = new ScenarioReport
                    {
                        Success = false,
                        Error = $"{e.GetType().Name}: {e.Message}",
                        Log = [.. log],
                        SimSeconds = simulation.Simulator.Time
                    };
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        worker.Start();

        if (!worker.Join(WallClockTimeout))
            return new ScenarioReport
            {
                Success = false,
                Error = $"Scenario timed out after {WallClockTimeout.TotalSeconds:0.#} s of wall clock " +
                        "(runaway script? sim.run advances bounded simulated time; loops need a bound).",
                Log = Snapshot(),
                SimSeconds = simulation.Simulator.Time
            };

        return outcome ?? Fail("Script produced no outcome.", Snapshot());

        List<string> Snapshot() { lock (logLock) return [.. log]; }
    }

    private ScenarioReport RunOnScriptThread(MachineSimulation simulation, List<string> log, object logLock)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        script.Options.DebugPrint += message => { lock (logLock) log.Add(message); };

        RegisterSimApi(script, simulation);

        script.DoString(_source);
        var run = script.Globals.Get("run");
        if (run.Type != DataType.Function)
            return Fail("The scenario must define `function run(sim) ... end`.", log);

        script.Call(run, script.Globals.Get("sim"));

        var result = TrySerializeResult(script);
        return new ScenarioReport
        {
            Success = true,
            Log = log,
            ResultJson = result,
            SimSeconds = simulation.Simulator.Time
        };

        ScenarioReport Fail(string message, List<string> lines) => new()
        {
            Success = false,
            Error = message,
            Log = lines,
            SimSeconds = simulation.Simulator.Time
        };
    }

    private static ScenarioReport Fail(string message, List<string> log) => new()
    {
        Success = false,
        Error = message,
        Log = log
    };

    private void RegisterSimApi(Script script, MachineSimulation simulation)
    {
        var sim = new Table(script);

        sim["enable"] = (Action<string, bool>)((motorId, on) => simulation.Stepper(motorId).Enable(on));
        sim["velocity"] = (Action<string, double>)((motorId, revPerSec) => simulation.Stepper(motorId).SetVelocityRevPerSec(revPerSec));
        sim["acceleration"] = (Action<string, double>)((motorId, revPerSec2) => simulation.Stepper(motorId).AccelerationRevPerSec2 = revPerSec2);
        sim["add_endstop"] = (Action<string, double>)((joint, trigger) => simulation.Mcu.AddEndstop(joint, trigger));
        sim["add_endstop_debounced"] = (Action<string, double, long>)((joint, trigger, ticks) =>
            _ = simulation.Mcu.AddEndstop(joint, trigger, (int)ticks));
        sim["endstop"] = (Func<string, bool>)(joint => simulation.Mcu.Endstops
            .FirstOrDefault(e => e.JointName == joint)?
            .Pressed ?? throw new KeyNotFoundException($"No endstop on joint '{joint}'."));
        sim["joint_pos"] = (Func<string, double>)(joint => simulation.Simulator.GetJointPos(joint));
        sim["joint_vel"] = (Func<string, double>)(joint => simulation.Simulator.GetJointVel(joint));
        sim["time"] = (Func<double>)(() => simulation.Simulator.Time);
        sim["run"] = (Action<double>)(seconds =>
        {
            if (seconds is < 0.001 or > 10)
                throw new ArgumentException("sim.run(seconds) must be within 0.001–10.");
            if (simulation.Simulator.Time + seconds > MaxTotalSimSeconds)
                throw new InvalidOperationException($"Scenario exceeded {MaxTotalSimSeconds} s of simulated time.");
            simulation.RunFor(seconds);
        });
        sim["motor"] = (Func<string, DynValue>)(motorId =>
        {
            var channel = simulation.Stepper(motorId);
            var table = new Table(script)
            {
                ["enabled"] = channel.IsEnabled,
                ["commanded_rev"] = channel.CommandedAngleRev,
                ["actual_rev"] = channel.RotorAngleRev,
                ["velocity"] = channel.RotorVelocityRevPerSec,
                ["target"] = channel.TargetVelocityRevPerSec,
                ["missed_steps"] = (double)channel.MissedSteps,
                ["stalled"] = channel.IsStalled
            };
            return DynValue.NewTable(table);
        });
        sim["body_pos"] = (Func<string, DynValue>)(bodyName =>
        {
            var p = simulation.Simulator.GetBodyPosition(bodyName);
            var table = new Table(script)
            {
                ["x"] = p[0], ["y"] = p[1], ["z"] = p[2]
            };
            return DynValue.NewTable(table);
        });

        script.Globals["sim"] = DynValue.NewTable(sim);
    }

    private static string? TrySerializeResult(Script script)
    {
        var result = script.Globals.Get("result");
        return result.Type == DataType.Nil ? null : ToJson(result);
    }

    private static string ToJson(DynValue value) => value.Type switch
    {
        DataType.Nil => "null",
        DataType.Boolean => value.Boolean ? "true" : "false",
        DataType.Number => value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DataType.String => System.Text.Json.JsonSerializer.Serialize(value.String),
        DataType.Table => "{" + string.Join(", ",
            value.Table.Pairs.Select(p => JsonKey(p.Key) + ": " + ToJson(p.Value))) + "}",
        _ => $"\"<{value.Type}>\""
    };

    private static string JsonKey(DynValue key)
        => key.Type == DataType.String
            ? System.Text.Json.JsonSerializer.Serialize(key.String)
            : $"\"{key.ToString()}\"";
}