using System.IO.Compression;
using System.Text;
using System.Text.Json;
using static MechMaker.Engine.Klipper.KlipperWire;

namespace MechMaker.Engine.Klipper;

/// <summary>
/// The MCU side of the Klipper split: accepts framed wire blocks, dispatches the
/// low-level MCU command set (get_config / config_stepper / queue_step /
/// endstop_home / ...), and answers with responses and acks. Command bytes and
/// the data dictionary are byte-compatible with the real protocol (klippy/msgproto.py,
/// src/command.c); the response set is the subset a simulated board needs.
/// Documented approximations:
///  - trsync trigger semantics are folded into endstop_home: homing halts the
///    steppers bound to the endstop (endstop_set_stepper) and reports homing=0,
///  - the MCU clock ticks at the physics rate and is exported as CLOCK_FREQ,
///  - steppers do full steps only (step/dir pins into a microstepping driver).
/// </summary>
public sealed class KlipperMcu
{
    public const int ClockFrequency = 8000; // ticks per second = physics rate

    // ---------- command/response definitions (single source of truth) ----------

    public const int CmdIdentify = 1;          // hard-coded by the klipper protocol
    public const int RespIdentifyResponse = 0; // hard-coded by the klipper protocol

    private static readonly (string Name, string Format)[] CommandDefs =
    [
        ("get_config", "get_config"),
        ("get_clock", "get_clock"),
        ("allocate_oids", "allocate_oids count=%c"),
        ("config_stepper", "config_stepper oid=%c step_pin=%c dir_pin=%c invert_step=%c step_pulse_ticks=%u"),
        ("config_endstop", "config_endstop oid=%c pin=%c pull_up=%c"),
        ("endstop_set_stepper", "endstop_set_stepper oid=%c stepper_oid=%c"), // MechMaker glue
        ("finalize_config", "finalize_config crc=%u"),
        ("reset_step_clock", "reset_step_clock oid=%c clock=%u"),
        ("set_next_step_dir", "set_next_step_dir oid=%c dir=%c"),
        ("queue_step", "queue_step oid=%c interval=%u count=%hu add=%hi"),
        ("stepper_get_position", "stepper_get_position oid=%c"),
("endstop_home", "endstop_home oid=%c clock=%u sample_ticks=%u sample_count=%c rest_ticks=%u pin_value=%c"),
        ("endstop_query_state", "endstop_query_state oid=%c"),
        ("config_pwm_out", "config_pwm_out oid=%c pin=%c cycle_ticks=%u value=%hu default_value=%hu max_duration=%u"),
        ("set_pwm_out", "set_pwm_out pin=%u value=%hu")
    ];

    private static readonly (string Name, string Format)[] ResponseDefs =
    [
        ("config", "config is_config=%c crc=%u move_count=%hu is_shutdown=%c"),
        ("clock", "clock clock=%u"),
        ("stepper_position", "stepper_position oid=%c pos=%i"),
        ("endstop_home", "endstop_home oid=%c homing=%c"),
        ("endstop_state", "endstop_state oid=%c homing=%c pin_value=%c")
    ];

    public static IReadOnlyDictionary<string, int> CommandIds { get; } =
        CommandDefs.Select((def, i) => (def.Name, Id: 2 + i)).ToDictionary(t => t.Name, t => t.Id);

    public static IReadOnlyDictionary<string, int> ResponseIds { get; } =
        ResponseDefs.Select((def, i) => (def.Name, Id: 1 + i)).ToDictionary(p => p.Name, p => p.Id);

    private static readonly int CmdGetConfig = CommandIds["get_config"];
    private static readonly int CmdGetClock = CommandIds["get_clock"];
    private static readonly int CmdAllocateOids = CommandIds["allocate_oids"];
    private static readonly int CmdConfigStepper = CommandIds["config_stepper"];
    private static readonly int CmdConfigEndstop = CommandIds["config_endstop"];
    private static readonly int CmdEndstopSetStepper = CommandIds["endstop_set_stepper"];
    private static readonly int CmdFinalizeConfig = CommandIds["finalize_config"];
    private static readonly int CmdResetStepClock = CommandIds["reset_step_clock"];
    private static readonly int CmdSetNextStepDir = CommandIds["set_next_step_dir"];
    private static readonly int CmdQueueStep = CommandIds["queue_step"];
    private static readonly int CmdStepperGetPosition = CommandIds["stepper_get_position"];
    private static readonly int CmdEndstopHome = CommandIds["endstop_home"];
private static readonly int CmdEndstopQueryState = CommandIds["endstop_query_state"];
    private static readonly int CmdConfigPwmOut = CommandIds["config_pwm_out"];
    private static readonly int CmdSetPwmOut = CommandIds["set_pwm_out"];
    private static readonly int RespConfig = ResponseIds["config"];
    private static readonly int RespClock = ResponseIds["clock"];
    private static readonly int RespStepperPosition = ResponseIds["stepper_position"];
    private static readonly int RespEndstopHome = ResponseIds["endstop_home"];
    private static readonly int RespEndstopState = ResponseIds["endstop_state"];

    // ---------- MCU state ----------

    private readonly MachineSimulation _simulation;
    private readonly BlockParser _parser = new();
    private readonly Queue<byte[]> _outgoing = new();
    private readonly Dictionary<int, StepperChannel> _steppersByOid = new();
    private readonly Dictionary<int, EndstopBinding> _endstopsByOid = new();
private readonly Dictionary<string, StepperChannel> _steppersByPin = new();
    private readonly Dictionary<string, IPwmChannel> _pwmByPin = new();
    private readonly Dictionary<int, IPwmChannel> _pwmByOid = new();
    private readonly List<string> _pinOrder = [];

    private int _oidCount = -1;
    private bool _configured;
    private uint _configCrc;
    private int _moveCount = 128;
        private byte _lastHostSeq;
    private bool _haveHostSeq;
    private byte[]? _dictionaryBytes;

    public KlipperMcu(MachineSimulation simulation)
    {
        _simulation = simulation;
        simulation.Mcu.PostTick += OnTick;
    }

    // ---------- physical bindings (the digital twin's wiring) ----------

/// <summary>Names a stepper channel for the pin enumeration ("motor_left" etc.).
    /// The host resolves the enum int from the data dictionary and uses it in
    /// config_stepper's step_pin/dir_pin parameters.</summary>
    public void NameStepperPin(string pinName, StepperChannel channel)
    {
        _steppersByPin[pinName] = channel;
        if (!_pinOrder.Contains(pinName))
            _pinOrder.Add(pinName);
    }

    /// <summary>Names a PWM channel (servo/fan instance) for the pin enumeration, so
    /// config_pwm_out / set_pwm_out can address it.</summary>
    public void NamePwmPin(string pinName, IPwmChannel channel)
    {
        _pwmByPin[pinName] = channel;
        if (!_pinOrder.Contains(pinName))
            _pinOrder.Add(pinName);
    }

    /// <summary>Registers a named endstop pin bound to a joint and trigger position —
    /// the digital twin's stand-in for where the switch sits physically.</summary>
    public void RegisterEndstopPin(string pinName, string jointName, double triggerPosition)
    {
        _pinOrder.Add(pinName);
        _endstopPins[pinName] = (jointName, triggerPosition);
    }

    private readonly Dictionary<string, (string Joint, double Trigger)> _endstopPins = [];

    // ---------- data dictionary ----------

    /// <summary>The zlib-compressed data dictionary served via identify.</summary>
    public byte[] DictionaryBytes
    {
        get
        {
            if (_dictionaryBytes is not null)
                return _dictionaryBytes;

            var pins = new Dictionary<string, object>();
            foreach (var name in _pinOrder)
                pins.TryAdd(name, pins.Count);

            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["commands"] = CommandDefs.Select((def, i) => (def.Name, Id: 2 + i))
                    .ToDictionary(p => p.Name, p => (object)p.Id),
                ["responses"] = ResponseDefs.Select((def, i) => (def.Name, Id: 1 + i))
                    .ToDictionary(p => p.Name, p => (object)p.Id),
                ["constants"] = new Dictionary<string, object>
                {
                    ["CLOCK_FREQ"] = ClockFrequency,
                    ["MCU"] = "mechmaker",
                    ["MESSAGE_MIN"] = 5,
                    ["MESSAGE_MAX"] = 64
                },
                ["enumerations"] = new Dictionary<string, object> { ["pin"] = pins },
                ["version"] = "mechmaker-mcu-0.1"
            });
            _dictionaryBytes = ZlibCompress(json);
            return _dictionaryBytes;
        }
    }

private static byte[] ZlibCompress(string text)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new StreamWriter(zlib, new UTF8Encoding(false)))
            writer.Write(text);
        return output.ToArray();
    }

    // ---------- wire I/O ----------

    /// <summary>Feeds raw wire bytes (possibly many blocks) and returns the outgoing
    /// wire blocks (acks/naks + responses), in order.</summary>
    public List<byte[]> Receive(IEnumerable<byte> wireBytes)
    {
        _parser.Feed(wireBytes);
        MessageBlock? block;
        while ((block = _parser.TryParse()) is not null)
        {
            if (block.Content.Length == 0)
                continue; // stray ack — ignore

            if (_haveHostSeq && block.Sequence != ((_lastHostSeq + 1) & 0x0f))
            {
                SendNak(); // out of order — discard until retransmit
                continue;
            }
            _lastHostSeq = block.Sequence;
            _haveHostSeq = true;

            Dispatch(block.Content);
            SendAck(block.Sequence);
        }
        return DrainResponses();
    }

    private void SendAck(byte hostSeq) =>
        _outgoing.Enqueue(Frame(ReadOnlySpan<byte>.Empty, (byte)((hostSeq + 1) & 0x0f)));

    private void SendNak() =>
        _outgoing.Enqueue(Frame(ReadOnlySpan<byte>.Empty, (byte)((_lastHostSeq - 1) & 0x0f)));

    private void SendResponse(int responseId, params long[] args)
    {
        var content = EncodeVlqAll([responseId, .. args]);
        _outgoing.Enqueue(Frame(content, (byte)((_lastHostSeq + 1) & 0x0f)));
    }

    /// <summary>Drains queued outgoing wire blocks (acks, naks, responses).</summary>
    public List<byte[]> DrainResponses()
    {
        var list = new List<byte[]>(_outgoing.Count);
        while (_outgoing.Count > 0)
            list.Add(_outgoing.Dequeue());
        return list;
    }

    // ---------- dispatch ----------

    private void Dispatch(byte[] content)
    {
        var values = DecodeVlqAll(content);
        if (values.Count == 0)
            return;
        var id = checked((int)values[0]);
        var args = values.Skip(1).ToList();

if (id == CmdIdentify) HandleIdentify(args);
        else if (id == CmdGetConfig) SendResponse(RespConfig, _configured ? 1u : 0u, _configCrc, _moveCount, 0u);
        else if (id == CmdGetClock) SendResponse(RespClock, ClockTicks());
        else if (id == CmdAllocateOids)
        {
            if (_oidCount >= 0) throw new InvalidOperationException("allocate_oids issued twice.");
            _oidCount = checked((int)args[0]);
        }
        else if (id == CmdConfigStepper) HandleConfigStepper(args);
        else if (id == CmdConfigEndstop) HandleConfigEndstop(args);
        else if (id == CmdEndstopSetStepper) _endstopsByOid[(int)args[0]].StepperOids.Add((int)args[1]);
        else if (id == CmdFinalizeConfig)
        {
            _configCrc = (uint)args[0];
            _configured = true;
                    }
        else if (id == CmdResetStepClock) ChannelOf(args[0]).ResetStepClock(args[1]);
        else if (id == CmdSetNextStepDir) ChannelOf(args[0]).SetNextStepDir(args[1] != 0);
        else if (id == CmdQueueStep) HandleQueueStep(args);
        else if (id == CmdStepperGetPosition) HandleStepperGetPosition(args);
        else if (id == CmdEndstopHome) HandleEndstopHome(args);
else if (id == CmdEndstopQueryState) HandleEndstopQueryState(args);
        else if (id == CmdConfigPwmOut) HandleConfigPwmOut(args);
        else if (id == CmdSetPwmOut) HandleSetPwmOut(args);
        else throw new InvalidOperationException($"Unknown command id {id}.");
    }

    /// <summary>config_pwm_out: binds a pwm channel (servo/fan instance pin) to an oid
    /// and applies its initial value (0-255 → duty).</summary>
    private void HandleConfigPwmOut(List<long> args)
    {
        if (_configured) throw new InvalidOperationException("config_pwm_out after finalize_config.");
        var oid = (int)args[0];
        var channel = _pwmByPin[PinName(args[1])];
        _pwmByOid[oid] = channel;
        channel.ApplyDuty(args[3] / 255.0);
    }

    /// <summary>set_pwm_out is immediate (a startup command in real klipper); the
    /// scheduled variant (queue_pwm_out) is a documented TODO.</summary>
    private void HandleSetPwmOut(List<long> args) =>
        _pwmByPin[PinName(args[0])].ApplyDuty(args[1] / 255.0);

    private StepperChannel ChannelOf(long oid) =>
        _steppersByOid.TryGetValue(checked((int)oid), out var channel)
            ? channel
            : throw new KeyNotFoundException($"oid {oid} is not a configured stepper.");

    private long ClockTicks() => _simulation.Mcu.Clock;

    private void HandleIdentify(List<long> args)
    {
        var offset = (int)args[0];
        var count = (int)args[1];
        var dictionary = DictionaryBytes;
        var take = Math.Min(count, Math.Max(0, dictionary.Length - offset));
        var chunk = take <= 0 ? [] : dictionary[offset..(offset + take)];
        var content = EncodeVlqAll(RespIdentifyResponse, offset)
                      .Concat(EncodeVlq(chunk.Length))
                      .Concat(chunk)
                      .ToArray();
        _outgoing.Enqueue(Frame(content, (byte)((_lastHostSeq + 1) & 0x0f)));
    }

    private void HandleConfigStepper(List<long> args)
    {
        if (_configured) throw new InvalidOperationException("config_stepper after finalize_config.");
        var oid = (int)args[0];
        var stepPin = PinName(args[1]);
        if (!_steppersByPin.TryGetValue(stepPin, out var channel))
            throw new KeyNotFoundException($"No stepper bound to pin '{stepPin}'.");
        _steppersByOid[oid] = channel;
        channel.Enable(); // the MCU drives step/dir; the driver enable is implicit
    }

    private void HandleConfigEndstop(List<long> args)
    {
        if (_configured) throw new InvalidOperationException("config_endstop after finalize_config.");
        var oid = (int)args[0];
        var pinName = PinName(args[1]);
        if (!_endstopPins.TryGetValue(pinName, out var pin))
            throw new KeyNotFoundException($"Unknown endstop pin '{pinName}'.");

var channel = _simulation.Mcu.AddEndstop(pin.Joint, pin.Trigger);
        _endstopsByOid[oid] = new EndstopBinding
        {
            Channel = channel
        };
    }

    private void HandleStepperGetPosition(List<long> args) =>
        SendResponse(RespStepperPosition, args[0], ChannelOf(args[0]).SignedStepPosition);

private void HandleQueueStep(List<long> args)
    {
        // Move-queue accounting: entries in flight = sent minus completed runs.
        var inFlight = _sentStepRuns - _simulation.Mcu.Steppers.Sum(s => s.CompletedStepRuns);
        if (inFlight >= _moveCount)
            throw new InvalidOperationException("Move queue full — the host must pace queue_step.");
        ChannelOf(args[0]).QueueStep(args[1], checked((int)args[2]), args[3]);
        _sentStepRuns++;
    }

    private int _sentStepRuns;

    private void HandleEndstopHome(List<long> args)
    {
        var oid = (int)args[0];
        var binding = _endstopsByOid[oid];
        binding.SampleCount = checked((int)args[3]);
        binding.PinValue = args[5] != 0 ? 1 : 0;
        binding.Homing = binding.SampleCount != 0;
        binding.MatchCount = binding.SampleCount;
        // homing=1 acknowledges arming; homing=0 reports the trigger (approximates
        // klipper's trsync_state reporting).
        SendResponse(RespEndstopHome, oid, binding.Homing ? 1u : 0u);
    }

    private void HandleEndstopQueryState(List<long> args)
    {
        var oid = (int)args[0];
        var binding = _endstopsByOid[oid];
        SendResponse(RespEndstopState, oid, binding.Homing ? 1u : 0u, binding.Channel.Pressed ? 1u : 0u);
    }

    // ---------- endstop sampling (per physics tick) ----------

    private void OnTick()
    {
        foreach (var (oid, binding) in _endstopsByOid)
        {
            if (!binding.Homing)
                continue;
            var matches = binding.Channel.Pressed == (binding.PinValue != 0);
            binding.MatchCount = matches ? binding.MatchCount - 1 : binding.SampleCount;
            if (binding.MatchCount > 0)
                continue;

            // Trigger: halt bound steppers (clear their move queues) and report.
            binding.Homing = false;
            foreach (var stepperOid in binding.StepperOids)
                ChannelOf(stepperOid).ClearStepQueue();
            SendResponse(RespEndstopHome, oid, 0u);
        }
    }

    private sealed class EndstopBinding
    {
        public required EndstopChannel Channel;
        public int PinValue;
        public bool Homing;
        public int SampleCount;
        public int MatchCount;
        public List<int> StepperOids { get; } = [];
    }

    private string PinName(long enumValue) =>
        enumValue >= 0 && enumValue < _pinOrder.Count ? _pinOrder[(int)enumValue]
            : throw new KeyNotFoundException($"No pin enumeration for value {enumValue}.");
}



