using MechMaker.Core;
using MechMaker.Core.Compilation;
using MechMaker.Core.Model;
using MechMaker.Core.Validation;
using MechMaker.Engine;
using MechMaker.Engine.Hil;
using MechMaker.Engine.Klipper;
using MechMaker.Engine.Scripting;
using MechMaker.Hil;

namespace MechMaker.Server;

/// <summary>
/// Session state behind the MCP tools: the part catalog, the machine under
/// construction, and at most one live run (compiled model + virtual MCU).
/// Instantiable for tests; the MCP layer drives the <see cref="Instance"/> singleton
/// (tool classes are constructed by the SDK without DI).
/// </summary>
public sealed class McpWorkspace : IDisposable
{
    public static readonly McpWorkspace Instance = new();

    private const int MaxRunSeconds = 10;

    private MachineDefinition _machine = new() { Name = "new_machine" };
    private MachineSimulation? _run;
    private readonly List<EndstopSpec> _endstopSpecs = [];
    private readonly Dictionary<string, EndstopChannel> _endstops = new(StringComparer.Ordinal);
    private int _connectionCounter;

    public PartCatalog Catalog { get; }
    public MachineDefinition Machine => _machine;
    public string? MachinePath { get; private set; }

    public McpWorkspace(string? catalogDirectory = null)
    {
        Catalog = PartCatalog.LoadFromDirectory(catalogDirectory ?? FindCatalogDirectory());
    }

    // ---------- catalog ----------

    private static string FindCatalogDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MECHMAKER_CATALOG");
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "catalog")))
                    return Path.Combine(dir.FullName, "catalog");
                dir = dir.Parent!;
            }
        }

        throw new InvalidOperationException(
            "No 'catalog' directory found upward from the working directory. " +
            "Set MECHMAKER_CATALOG to the catalog directory.");
    }

    // ---------- machine file ----------

    public string NewMachine(string name)
    {
        StopRun();
        _machine = new MachineDefinition { Name = name };
        _connectionCounter = 0;
        // A fresh session has no open file — default the save location to the CWD.
        MachinePath = Path.Combine(Path.GetDirectoryName(MachinePath) ?? Environment.CurrentDirectory, $"{name}.json");
        return $"Machine '{name}' created ({_machine.Parts.Count} parts).";
    }

    public string OpenMachine(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Machine file not found: {full}");

        StopRun();
        _machine = CoreJson.Deserialize<MachineDefinition>(File.ReadAllText(full));
        MachinePath = full;
        _connectionCounter = _machine.Connections.Count;
        return $"Machine '{_machine.Name}' loaded from '{full}': " +
               $"{_machine.Parts.Count} parts, {_machine.Connections.Count} connections, " +
               $"{_machine.Boards.Count} boards, {_machine.Wiring.Count} wires.";
    }

    public string SaveMachine(string? path = null)
    {
        var full = Path.GetFullPath(path ?? MachinePath
            ?? throw new InvalidOperationException("No path given and no machine file open."));
        File.WriteAllText(full, CoreJson.Serialize(_machine));
        MachinePath = full;
        return $"Machine '{_machine.Name}' saved to '{full}'.";
    }

    // ---------- validation & compilation ----------

    public ValidationReport Validate() => new MachineValidator(Catalog).Validate(_machine);

    public (string Mjcf, ValidationReport Report) Compile()
    {
        var compiler = new MjcfCompiler(Catalog);
        var mjcf = compiler.Compile(_machine).ToString();
        return (mjcf, compiler.LastReport);
    }

    // ---------- parts ----------

    public string AddPart(string catalogId, string? instanceId = null, double? x = null, double? y = null, double? z = null,
        double rx = 0, double ry = 0, double rz = 0)
    {
        var definition = Catalog.Get(catalogId); // throws with a clear message for unknown ids
        instanceId ??= UniqueInstanceId(catalogId);
        if (_machine.Parts.Any(p => p.Id == instanceId))
            throw new InvalidOperationException($"Part instance id '{instanceId}' already exists.");

        x ??= 0;
        y ??= 0;
        z ??= StackZ(); // above everything already placed, so nothing overlaps by default

        _machine = _machine with
        {
            Parts = [.. _machine.Parts, new PartInstance
            {
                Id = instanceId,
                Part = catalogId,
                Pose = new Pose
                {
                    Position = new MechMaker.Core.Mathematics.Vec3(x.Value, y.Value, z.Value),
                    RotationEulerDeg = new MechMaker.Core.Mathematics.Vec3(rx, ry, rz)
                }
            }]
        };
        return $"Placed '{instanceId}' (catalog '{catalogId}') at ({x:0.###}, {y:0.###}, {z:0.###}) m.";
    }

    private string UniqueInstanceId(string catalogId)
    {
        for (var i = 1; ; i++)
        {
            var candidate = $"{catalogId}_{i}";
            if (_machine.Parts.All(p => p.Id != candidate))
                return candidate;
        }
    }

    private double StackZ()
    {
        // Rough placement: spread parts vertically; the pose tool refines it.
        return 0.05 * _machine.Parts.Count;
    }

    public string UpdatePartPose(string instanceId, double x, double y, double z,
        double rx = 0, double ry = 0, double rz = 0)
    {
        ThrowIfUnknownPart(instanceId);
        _machine = _machine with
        {
            Parts = [.. _machine.Parts.Select(p => p.Id == instanceId
                ? p with
                {
                    Pose = new Pose
                    {
                        Position = new MechMaker.Core.Mathematics.Vec3(x, y, z),
                        RotationEulerDeg = new MechMaker.Core.Mathematics.Vec3(rx, ry, rz)
                    }
                }
                : p)]
        };
        return $"Pose of '{instanceId}' set to ({x:0.###}, {y:0.###}, {z:0.###}) m, " +
               $"rot ({rx:0.#}, {ry:0.#}, {rz:0.#}) deg.";
    }

    public string DeletePart(string instanceId)
    {
        ThrowIfUnknownPart(instanceId);
        _machine = _machine with
        {
            Parts = [.. _machine.Parts.Where(p => p.Id != instanceId)],
            Connections = [.. _machine.Connections.Where(c => c.PartA != instanceId && c.PartB != instanceId)],
            Wiring = [.. _machine.Wiring.Where(w => w.Component != instanceId)]
        };
        return $"Deleted '{instanceId}' (and its connections and wiring).";
    }

    // ---------- connections ----------

    public string AddConnection(string partA, string connectorA, string partB, string connectorB)
    {
        var connector = FindConnectorOrThrow(partA, connectorA);
        var other = FindConnectorOrThrow(partB, connectorB);

        var id = $"c{++_connectionCounter}";
        while (_machine.Connections.Any(c => c.Id == id))
            id += "x";

        _machine = _machine with
        {
            Connections = [.. _machine.Connections, new Connection
            {
                Id = id, PartA = partA, ConnectorA = connectorA, PartB = partB, ConnectorB = connectorB
            }]
        };
        return $"Connected {partA}.{connectorA} <-> {partB}.{connectorB} as '{id}' " +
               $"({connector.Type} <-> {other.Type}).";
    }

    public string DeleteConnection(string connectionId)
    {
        if (!_machine.Connections.Any(c => c.Id == connectionId))
            throw new KeyNotFoundException($"No connection '{connectionId}'.");
        _machine = _machine with { Connections = [.. _machine.Connections.Where(c => c.Id != connectionId)] };
        return $"Deleted connection '{connectionId}'.";
    }

    public IReadOnlyList<MechMaker.Core.Model.ConnectorDefinition> ConnectorsOf(string instanceId)
    {
        ThrowIfUnknownPart(instanceId);
        return Catalog.Get(_machine.Parts.First(p => p.Id == instanceId).Part).Connectors;
    }

    private MechMaker.Core.Model.ConnectorDefinition FindConnectorOrThrow(string partId, string connectorName)
    {
        ThrowIfUnknownPart(partId);
        var definition = Catalog.Get(_machine.Parts.First(p => p.Id == partId).Part);
        var connector = definition.Connectors.FirstOrDefault(c => c.Name == connectorName);
        if (connector is null)
        {
            var names = string.Join(", ", definition.Connectors.Select(c => c.Name));
            throw new KeyNotFoundException(
                $"Part '{partId}' (catalog '{definition.Id}') has no connector '{connectorName}'. Available: {names}");
        }
        return connector;
    }

    private void ThrowIfUnknownPart(string instanceId)
    {
        if (!_machine.Parts.Any(p => p.Id == instanceId))
            throw new KeyNotFoundException(
                $"No part instance '{instanceId}'. Known: {string.Join(", ", _machine.Parts.Select(p => p.Id))}");
    }

    // ---------- boards & wiring ----------

    public string AddBoard(string boardId, string type = "skr-pico")
    {
        if (_machine.Boards.Any(b => b.Id == boardId))
            throw new InvalidOperationException($"Board '{boardId}' already exists.");
        _machine = _machine with
        {
            Boards = [.. _machine.Boards, new BoardDefinition { Id = boardId, Type = type }]
        };
        return $"Board '{boardId}' ({type}) added.";
    }

    public string Wire(string component, string signal, string board, string pin)
    {
        ThrowIfUnknownPart(component);
        if (_machine.Boards.Count == 0)
            throw new InvalidOperationException("No board to wire to — add one first (add_board).");
        if (!_machine.Boards.Any(b => b.Id == board))
            throw new KeyNotFoundException($"No board '{board}'. Known: {string.Join(", ", _machine.Boards.Select(b => b.Id))}");

        _machine = _machine with
        {
            Wiring = [.. _machine.Wiring.Where(w => !(w.Component == component && w.Signal == signal)),
                new Wire { Component = component, Signal = signal, Board = board, Pin = pin }]
        };
        return $"Wired {component}.{signal} -> {board}.{pin}.";
    }

    // ---------- scenarios (Lua) ----------

    public string RunScenarioFile(string path) => RunScenario(LuaScenario.LoadFile(path));

    public string RunScenarioSource(string source) => RunScenario(LuaScenario.Parse(source));

    private string RunScenario(LuaScenario scenario)
    {
        StopRun(); // a scenario gets its own simulation session
        using var simulation = MachineSimulation.FromMachine(_machine, Catalog);
        return scenario.Run(simulation).ToString();
    }

    // ---------- Klipper protocol (M5) ----------

    private KlipperMcu? _klipper;

    /// <summary>
    /// Starts a run with a klipper-protocol MCU endpoint attached. The host
    /// (agent/test) then runs the klipper flow: identify, allocate_oids,
    /// config_stepper/config_endstop, finalize_config, queue_step/endstop_home.
    /// </summary>
    public string KlipperConnect()
    {
        StopRun();
        StartRun();
        _klipperHostSeq = 0;
        _klipper = new KlipperMcu(_run!);
        foreach (var (instanceId, stepper) in _run!.SteppersByInstance)
            _klipper.NameStepperPin(instanceId, stepper);
        foreach (var (instanceId, servo) in _run.ServosByInstance)
            _klipper.NamePwmPin(instanceId, servo);
        foreach (var (instanceId, fan) in _run.FansByInstance)
            _klipper.NamePwmPin(instanceId, fan);
        foreach (var spec in _endstopSpecs)
            _klipper.RegisterEndstopPin($"endstop_{spec.JointName}", spec.JointName, spec.TriggerPosition);
        return "Klipper MCU connected. Pin enumerations: " +
               string.Join(", ", _pinNames()) +
               (_endstopSpecs.Count == 0
                   ? ""
                   : "; endstop pins: " + string.Join(", ", _endstopSpecs.Select(s => $"endstop_{s.JointName}"))) +
               $". MCU clock = physics ticks ({KlipperMcu.ClockFrequency} Hz).";
    }

    private IEnumerable<string> _pinNames() =>
        _run!.SteppersByInstance.Keys
            .Concat(_run.ServosByInstance.Keys)
            .Concat(_run.FansByInstance.Keys);

    private static string StepperInstanceName(StepperChannel channel)
        => channel.JointName[2..].Replace("_rotor", "");

    public string KlipperSend(string command, double[] args)
    {
        var klipper = _klipper ?? throw new InvalidOperationException("No klipper session — call klipper_connect first.");
        var values = args.Select(a => (long)a).ToList();
        var wire = BuildKlipperBlock(command, values);
        var outgoing = klipper.Receive(wire);
        return DescribeKlipperResponses(outgoing);
    }

    private byte _klipperHostSeq;

    private byte[] BuildKlipperBlock(string command, List<long> values)
    {
        var id = command == "identify"
            ? KlipperMcu.CmdIdentify
            : KlipperMcu.CommandIds.TryGetValue(command, out var cid)
                ? cid
                : throw new KeyNotFoundException($"Unknown klipper command '{command}'. Known: " +
                    string.Join(", ", KlipperMcu.CommandIds.Keys.OrderBy(k => k)));
        var block = KlipperWire.Frame(KlipperWire.EncodeVlqAll([id, .. values]), _klipperHostSeq);
        _klipperHostSeq = (byte)((_klipperHostSeq + 1) & 0x0f);
        return block;
    }

    private string DescribeKlipperResponses(List<byte[]> blocks)
    {
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            if (block.Length == KlipperWire.MinBlockSize)
            {
                lines.Add("ack");
                continue;
            }
            var values = KlipperWire.DecodeVlqAll(block[2..^3]);
            lines.Add("response " + string.Join(" ", values));
        }
        return string.Join(Environment.NewLine, lines);
    }

    public string KlipperStatus()
    {
        var klipper = _klipper ?? throw new InvalidOperationException("No klipper session — call klipper_connect first.");
        var lines = new List<string> { $"clock: {_run!.Mcu.Clock} ticks" };
        foreach (var stepper in _run.Mcu.Steppers)
        {
            lines.Add($"{StepperInstanceName(stepper)}: position {stepper.SignedStepPosition} steps, " +
                      $"{stepper.QueuedSteps} queued, {(stepper.IsEnabled ? "enabled" : "disabled")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    // ---------- servo / fan / heater PWM ----------

public string SetServoAngle(string instanceId, double degrees)
    {
        var run = RequireRun();
        var servo = run.Servo(instanceId);
        servo.SetTargetAngleDeg(degrees);
        return $"Servo '{instanceId}' target {servo.TargetAngleDeg:0.#} deg " +
               $"(range {servo.MinAngleDeg:0.#}..{servo.MaxAngleDeg:0.#}).";
    }

    public string SetServoPosition(string instanceId, double metres)
    {
        var run = RequireRun();
        var servo = run.Servo(instanceId);
        servo.SetTargetPositionM(metres);
        return $"Slide servo '{instanceId}' target {servo.TargetAngleDeg * 1000:0.#} mm " +
               $"(range {servo.MinPositionM * 1000:0.#}..{servo.MaxPositionM * 1000:0.#}).";
    }

    /// <summary>Alias for the MCP tool name (set_finger_position).</summary>
    public string SetFingerPosition(string instanceId, double metres) =>
        SetServoPosition(instanceId, metres);

    public string SetFanDuty(string instanceId, double duty)
    {
        var run = RequireRun();
        var fan = run.Fan(instanceId);
        fan.SetDuty(duty);
        return $"Fan '{instanceId}' duty {fan.Duty:0.##} (target {fan.Duty * 6000:0} rpm).";
    }

    public string SetHeaterDuty(string instanceId, double duty)
    {
        var run = RequireRun();
        var heater = run.Heater(instanceId);
        heater.SetDuty(duty);
        return $"Heater '{instanceId}' duty {heater.Duty:0.##} " +
               $"(steady state {heater.SteadyStateC:0.#} °C).";
    }

    // ---------- HIL (Android emulator + vision) ----------

    private HilSession? _hil;

    /// <summary>
    /// Arms physics-contact taps: a touch_finger pressing the phone's screen in the
    /// physics dispatches emulator taps at the contact point. The bridge rides the
    /// MCU's PostTick so every run_for slice checks contacts.
    /// </summary>
    public string ArmPhysicsTaps()
    {
        if (_hil is null)
            throw new InvalidOperationException("No HIL session — call hil_connect first.");
        var phone = FindPhone() ?? throw new InvalidOperationException("No android_phone part.");
        var finger = _machine.Parts.FirstOrDefault(p => Catalog.Find(p.Part)?.Id == "touch_finger")
            ?? throw new InvalidOperationException("No touch_finger part — place one over the phone.");

        _tapBridge = new PhysicsTapBridge(RequireRun(), phone.Id, finger.Id,
            (fx, fy) => { _hil.Tap(fx, fy); });
        _tapBridge.Arm();
        _run!.Mcu.PostTick += () => _tapBridge.Tick();
        return $"Physics taps armed: '{finger.Id}' -> '{phone.Id}' screen " +
               "(one tap per contact episode; watch dispatched taps via hil_status).";
    }


    private PhysicsTapBridge? _tapBridge;

    private PartInstance? FindPhone() =>
        _machine.Parts.FirstOrDefault(p => Catalog.Find(p.Part)?.Id == "android_phone");

    /// <summary>Connects a HIL session over a fake emulator (tests/offline) or adb.</summary>
    public string HilConnect(string transport = "fake", string? adbPath = null, string? adbSerial = null)
    {
        IEmulatorTransport emulator = transport.ToLowerInvariant() switch
        {
            "fake" => new FakeEmulatorTransport(),
            "adb" => new AdbEmulatorTransport(adbPath, adbSerial, ResolvePhoneResolution()),
            _ => throw new ArgumentException($"Unknown transport '{transport}' (use 'fake' or 'adb').")
        };
        return HilConnect(emulator);
    }

    /// <summary>Connects with a caller-built transport (the test seam).</summary>
    public string HilConnect(IEmulatorTransport transport)
    {
        _hil = HilSession.Connect(transport, _machine, Catalog);
        return _hil.ToString()!;
    }

    private (int X, int Y) ResolvePhoneResolution()
    {
        var phone = _machine.Parts.FirstOrDefault(p => Catalog.Find(p.Part)?.Id == "android_phone")
            ?? throw new InvalidOperationException("No android_phone part in the machine.");
        var definition = Catalog.Get(phone.Part);
        return ((int)definition.Params.GetValueOrDefault("resolution_x", 540),
                (int)definition.Params.GetValueOrDefault("resolution_y", 1170));
    }

    public string TouchTap(double fx, double fy)
    {
        var hil = RequireHil();
        hil.Tap(fx, fy);
        return $"tap at ({fx * 100:0.#}%, {fy * 100:0.#}%) dispatched to the emulator.";
    }

    public string TouchSwipe(double fx1, double fy1, double fx2, double fy2, int durationMs)
    {
        var hil = RequireHil();
        hil.Swipe(fx1, fy1, fx2, fy2, durationMs);
        return $"swipe ({fx1 * 100:0.#}%,{fy1 * 100:0.#}%) -> ({fx2 * 100:0.#}%,{fy2 * 100:0.#}%) " +
               $"over {durationMs} ms dispatched.";
    }

    public string ScreenFind(string name, string templatePath, double threshold = ScreenDetector.DefaultThreshold)
    {
        var hil = RequireHil();
        var template = File.ReadAllBytes(Path.GetFullPath(templatePath));
        var element = hil.Find(name, template, threshold);
        return element is null
            ? $"'{name}' not found (threshold {threshold:0.##})."
            : element + $" — phone-local ({hil.FractionToMmX(element.Fx) * 1000:0.#}, " +
              $"{hil.FractionToMmY(element.Fy) * 1000:0.#}) mm";
    }

    public string ScreenWaitFor(string name, string templatePath, double timeoutS, double pollS = 0.5,
        double threshold = ScreenDetector.DefaultThreshold)
    {
        var hil = RequireHil();
        var template = File.ReadAllBytes(Path.GetFullPath(templatePath));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutS);
        while (DateTime.UtcNow < deadline)
        {
            var element = hil.Find(name, template, threshold);
            if (element is not null)
                return $"'{name}' found: {element}";
            Thread.Sleep(TimeSpan.FromSeconds(pollS));
        }
        return $"'{name}' NOT found within {timeoutS:0.#} s.";
    }

    public string ScreenScreencap(string? outputPath)
    {
        var hil = RequireHil();
        var png = hil.Transport.Screencap();
        if (outputPath is null)
            return $"Captured {png.Length} bytes.";
        File.WriteAllBytes(Path.GetFullPath(outputPath), png);
        return $"Captured {png.Length} bytes -> '{Path.GetFullPath(outputPath)}'.";
    }

private HilSession RequireHil() =>
        _hil ?? throw new InvalidOperationException("No HIL session — call hil_connect first.");

    /// <summary>
    /// One physical tap at a screen fraction: resolves the gantry (the finger's
    /// parent connection partner + the first wired stepper), measures the live
    /// geometry, positions, presses, retracts. Requires arm_physics_taps (the
    /// dispatched tap reaches the emulator through the same bridge).
    /// </summary>
    public string TapAt(double fx, double fy = 0.5)
    {
        if (_hil is null)
            throw new InvalidOperationException("No HIL session — call hil_connect first.");
        var bridge = _tapBridge ?? throw new InvalidOperationException(
            "Physics taps not armed — call arm_physics_taps first.");

        var phone = FindPhone() ?? throw new InvalidOperationException("No android_phone part.");
        var finger = _machine.Parts.FirstOrDefault(p => Catalog.Find(p.Part)?.Id == "touch_finger")
            ?? throw new InvalidOperationException("No touch_finger part.");
        var carriage = CarriageOf(finger.Id)
            ?? throw new InvalidOperationException("The finger has no parent connection (no gantry to move).");
        var stepper = _machine.Wiring
            .Where(w => w.Signal == "step")
            .Select(w => w.Component)
            .FirstOrDefault(id => _machine.Parts.Any(p => p.Id == id))
            ?? throw new InvalidOperationException("No wired stepper to drive the gantry.");

        var phoneDef = Catalog.Get(phone.Part);
        var screenWidth = phoneDef.Params.GetValueOrDefault("screen_width_m", 0.068);

        var controller = new TapAtController(RequireRun(), phone.Id, finger.Id,
            carriage, stepper, screenWidth);
        var (achievedFx, achievedFy) = controller.Tap(fx);
        return $"Physical tap dispatched at ({achievedFx * 100:0.#}%, {achievedFy * 100:0.#}%) " +
               $"(commanded ({fx * 100:0.#}%, {fy * 100:0.#}%)).";
    }

    /// <summary>The part the finger mounts to (its connection partner).</summary>
    private string? CarriageOf(string fingerId) =>
        _machine.Connections
            .Where(c => c.PartA == fingerId || c.PartB == fingerId)
            .Select(c => c.PartA == fingerId ? c.PartB : c.PartA)
            .FirstOrDefault(id => Catalog.Find(_machine.Parts
                .FirstOrDefault(p => p.Id == id)?.Part ?? "")?.Id != "touch_finger"
                && _machine.Parts.Any(p => p.Id == id));

    public string HilStatus()
    {
        var hil = RequireHil();
        var lines = new List<string> { hil.ToString() };
        if (_tapBridge is { } bridge)
        {
            lines.Add(bridge.DispatchedTaps.Count == 0
                ? "physics taps: armed, none dispatched yet"
                : "physics taps dispatched at: " + string.Join("; ", bridge.DispatchedTaps
                    .Select(t => $"({t.Fx * 100:0.#}%, {t.Fy * 100:0.#}%)")));
        }
        else
        {
            lines.Add("physics taps: not armed (arm_physics_taps)");
        }
        return string.Join(Environment.NewLine, lines);
    }

    // ---------- run mode ----------

    public string StartRun(int microsteps = 16)
    {
        StopRun();
        _run = MachineSimulation.FromMachine(_machine, Catalog, microsteps);
        // Channels start disabled, like firmware — energize the ones you drive.
        _endstops.Clear();
        foreach (var spec in _endstopSpecs)
            _endstops[spec.JointName] = _run.Mcu.AddEndstop(spec.JointName, spec.TriggerPosition, spec.DebounceTicks);

        var motors = WiredSteppers().Select(id => id).ToList();
        return $"Run started: {_run.Simulator.BuildReport().BodyCount} bodies, {motors.Count} stepper channel(s) " +
               $"({string.Join(", ", motors)}), {_endstops.Count} endstop(s).";
    }

    public string RunFor(double seconds)
    {
        var run = RequireRun();
        if (seconds is < 0.001 or > MaxRunSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), $"Run duration must be 0.001–{MaxRunSeconds} s.");
        run.RunFor(seconds);
        return $"Ran {seconds:0.###} s (t={run.Simulator.Time:0.###} s).\n{BuildStatusText()}";
    }

    public string SetMotorVelocity(string instanceId, double revPerSec)
    {
        var run = RequireRun();
        var channel = run.Stepper(instanceId); // throws if not wired
        channel.SetVelocityRevPerSec(revPerSec);
        return $"Motor '{instanceId}' target set to {revPerSec:0.###} rev/s " +
               $"(ramp {channel.AccelerationRevPerSec2:0.#} rev/s^2).";
    }

    public string EnableMotor(string instanceId, bool enabled)
    {
        var run = RequireRun();
        run.Stepper(instanceId).Enable(enabled);
        return $"Motor '{instanceId}' {(enabled ? "enabled" : "disabled")}.";
    }

    public string AddEndstop(string jointName, double triggerPosition, int debounceTicks = 3)
    {
        _endstopSpecs.RemoveAll(s => s.JointName == jointName);
        _endstopSpecs.Add(new EndstopSpec(jointName, triggerPosition, debounceTicks));
        if (_run is not null)
            _endstops[jointName] = _run.Mcu.AddEndstop(jointName, triggerPosition, debounceTicks);
        return $"Endstop on joint '{jointName}' triggers at {triggerPosition:0.####} " +
               $"(debounce {debounceTicks} ticks){(_run is null ? "; takes effect next run" : "")}.";
    }

    public string ReadEndstop(string jointName)
    {
        var run = RequireRun();
        if (!_endstops.TryGetValue(jointName, out var endstop))
            throw new KeyNotFoundException($"No endstop on joint '{jointName}' — add one first (add_endstop).");
        return $"Endstop '{jointName}': {(endstop.Pressed ? "PRESSED" : "open")} " +
               $"(joint at {run.Simulator.GetJointPos(jointName):0.####}, trigger {endstop.TriggerPosition:0.####}).";
    }

    public string GetRunStatus()
    {
        RequireRun();
        return BuildStatusText();
    }

    public string StopRun()
    {
        if (_run is null)
        {
            _klipper = null;
            return "No run active.";
        }
        var time = _run.Simulator.Time;
        _run.Dispose();
        _run = null;
        _klipper = null;
        _endstops.Clear();
        return $"Run stopped at t={time:0.###} s.";
    }

    private MachineSimulation RequireRun() =>
        _run ?? throw new InvalidOperationException("No run active — call start_run first.");

    private IEnumerable<string> WiredSteppers() =>
        _machine.Wiring.Where(w => w.Signal == "step")
            .Select(w => w.Component)
            .Distinct();

    private string BuildStatusText()
    {
        var run = _run!;
        var lines = new List<string> { $"t = {run.Simulator.Time:0.###} s" };

        foreach (var instanceId in WiredSteppers())
        {
            try
            {
                var channel = run.Stepper(instanceId);
                lines.Add(
                    $"{instanceId}{(channel.IsEnabled ? "" : " (disabled)")}: " +
                    $"commanded {channel.CommandedAngleRev:0.####} rev / actual {channel.RotorAngleRev:0.####} rev, " +
                    $"velocity {channel.RotorVelocityRevPerSec:0.###} rev/s (target {channel.TargetVelocityRevPerSec:0.###}), " +
                    $"missed steps {channel.MissedSteps}{(channel.IsStalled ? ", STALLED" : "")}");
            }
            catch (KeyNotFoundException)
            {
                // Wired to "step" but not a stepper in the catalog — skip.
            }
        }

        foreach (var (joint, endstop) in _endstops)
            lines.Add($"endstop {joint}: {(endstop.Pressed ? "PRESSED" : "open")}");

        foreach (var (instanceId, servo) in run.ServosByInstance)
            lines.Add($"servo {instanceId}: target {servo.TargetAngleDeg:0.#} deg, at {servo.AngleDeg:0.#} deg");
        foreach (var (instanceId, fan) in run.FansByInstance)
            lines.Add($"fan {instanceId}: duty {fan.Duty:0.##}, {fan.RevPerSec * 60:0} rpm");
        foreach (var (instanceId, heater) in run.HeatersByInstance)
            lines.Add($"heater {instanceId}: duty {heater.Duty:0.##}, {heater.TemperatureC:0.#} °C " +
                      $"(steady state {heater.SteadyStateC:0.#} °C)");

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record EndstopSpec(string JointName, double TriggerPosition, int DebounceTicks);

    public void Dispose() => _run?.Dispose();
}

