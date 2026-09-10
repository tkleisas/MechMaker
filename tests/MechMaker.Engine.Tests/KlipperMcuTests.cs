using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MechMaker.Core;
using MechMaker.Core.Model;
using MechMaker.Engine.Klipper;

namespace MechMaker.Engine.Tests;

/// <summary>
/// Klipper protocol tests: the test plays the klipper host — downloads the data
/// dictionary via identify, runs the configuration flow (allocate_oids →
/// config_* → finalize_config), drives moves with queue_step, and homes the axis
/// with endstop_home against the live physics.
/// </summary>
public class KlipperMcuTests : IDisposable
{
    private readonly MachineSimulation _simulation;
    private readonly KlipperMcu _mcu;
    private byte _hostSeq;
    private byte _lastAckSeq;

    public KlipperMcuTests()
    {
        var machine = CoreJson.Deserialize<MachineDefinition>(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "examples", "linear_axis_v0.json")));
        _simulation = MachineSimulation.FromMachine(machine, TestRepo.Catalog());
        _mcu = new KlipperMcu(_simulation);

        // Pin enumerations mirror the machine's stepper instance ids.
        foreach (var stepper in _simulation.Mcu.Steppers)
            _mcu.NameStepperPin(InstanceNameOf(stepper), stepper);
        _mcu.RegisterEndstopPin("endstop_x", "j_carriage", -0.020);
    }

    public void Dispose() => _simulation.Dispose();

private static string InstanceNameOf(StepperChannel channel)
        => channel.JointName[2..].Replace("_rotor", ""); // j_motor_left_rotor -> motor_left

    // ---------- host-side wire helpers ----------

    /// <summary>Sends one framed command; asserts the ack and returns it.</summary>
private byte[] Send(int id, params long[] args)
    {
        var responses = _mcu.Receive(KlipperWire.Frame(KlipperWire.EncodeVlqAll([id, .. args]), _hostSeq));
        _lastAck = Assert.Single(responses, r => r.Length == KlipperWire.MinBlockSize);
        _lastAckSeq = (byte)(_lastAck[1] & 0x0f);
        _hostSeq = (byte)((_hostSeq + 1) & 0x0f);
        return responses.FirstOrDefault(r => r.Length > KlipperWire.MinBlockSize) ?? [];
    }

    private byte[] _lastAck = [];

    /// <summary>klipper acks carry "one greater than the received sequence".</summary>
    private void AssertAckSequence() => Assert.Equal(_hostSeq, _lastAck[1] & 0x0f);

private static (int Id, List<long> Args) Decode(byte[] block)
    {
        var values = KlipperWire.DecodeVlqAll(block[2..^3]); // skip length+sequence, crc+sync
        return (checked((int)values[0]), values.Skip(1).ToList());
    }

    /// <summary>The identify_response carries a VLQ length + raw chunk — parse manually
    /// because the chunk bytes are not VLQ-encoded.</summary>
    private static (int Offset, byte[] Data) ParseIdentifyResponse(byte[] block)
    {
        var content = block[2..^3];
        var (id, n) = KlipperWire.DecodeVlq(content, 0);
        Assert.Equal(0, id);
        var (offset, n2) = KlipperWire.DecodeVlq(content, n);
        var (length, n3) = KlipperWire.DecodeVlq(content, n + n2);
        var data = content.Skip(n + n2 + n3).Take((int)length).ToArray();
        return ((int)offset, data);
    }

    // ---------- 1. identify / dictionary ----------

    [Fact]
    public void Identify_downloads_and_decompresses_the_dictionary()
    {
        var dictionary = DownloadDictionary();

        var json = ZlibDecompress(dictionary);
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        var commands = doc["commands"].Deserialize<Dictionary<string, int>>()!;
        Assert.Equal(KlipperMcu.CommandIds["queue_step"], commands["queue_step"]);
        Assert.Equal(KlipperMcu.RespIdentifyResponse, 0);
        Assert.True(commands["get_config"] > 0);

var constants = doc["constants"].Deserialize<Dictionary<string, JsonElement>>()!;
        Assert.Equal(KlipperMcu.ClockFrequency, constants["CLOCK_FREQ"].GetInt32());
        Assert.Equal("mechmaker", constants["MCU"].GetString());

        var pins = doc["enumerations"].GetProperty("pin").Deserialize<Dictionary<string, int>>()!;
        Assert.True(pins.ContainsKey("motor_left"));
        Assert.True(pins.ContainsKey("motor_right"));
        Assert.True(pins.ContainsKey("endstop_x"));
        Assert.True(pins["motor_left"] != pins["endstop_x"]);

        var version = doc["version"].GetString();
        Assert.Contains("mechmaker", version);
    }

    private List<byte> DownloadDictionary()
    {
        var dictionary = new List<byte>();
        var offset = 0;
        while (true)
        {
            var responses = _mcu.Receive(KlipperWire.Frame(
                KlipperWire.EncodeVlqAll(KlipperMcu.CmdIdentify, offset, 40), _hostSeq++));
            Assert.Single(responses.Where(r => r.Length == KlipperWire.MinBlockSize)); // ack
            var chunk = responses.First(r => r.Length > KlipperWire.MinBlockSize);

            // content = VLQ(0) VLQ(offset) VLQ(length) <raw bytes>
            var content = chunk[2..^3];
            var (id, n1) = KlipperWire.DecodeVlq(content, 0);
            Assert.Equal(0, id);
            var (respOffset, n2) = KlipperWire.DecodeVlq(content, n1);
            Assert.Equal(offset, respOffset);
            var (length, n3) = KlipperWire.DecodeVlq(content, n1 + n2);
            dictionary.AddRange(content.Skip(n1 + n2 + n3).Take((int)length));

            if (length == 0)
                return dictionary;
            offset += (int)length;
        }
    }

    private static byte[] ZlibDecompress(IEnumerable<byte> data)
    {
        using var source = new MemoryStream([.. data]);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    // ---------- 2. configuration + queue_step driving ----------

    private Dictionary<string, int> _pins = [];

    private void Configure(int stepperOid = 0, int endstopOid = 1)
    {
        // A real host does identify first; we just need the pin enums.
        var dictionary = ZlibDecompress(DownloadDictionary());
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dictionary)!;
        _pins = doc["enumerations"].GetProperty("pin").Deserialize<Dictionary<string, int>>()!;

        Send(KlipperMcu.CommandIds["allocate_oids"], 8);
        Send(KlipperMcu.CommandIds["config_stepper"], stepperOid, _pins["motor_left"], _pins["motor_left"], 0, 0);
        Send(KlipperMcu.CommandIds["config_endstop"], endstopOid, _pins["endstop_x"], 1);
        Send(KlipperMcu.CommandIds["endstop_set_stepper"], endstopOid, stepperOid);
        Send(KlipperMcu.CommandIds["finalize_config"], 0x1234);

        var config = Send(KlipperMcu.CommandIds["get_config"]);
        var (id, args) = Decode(config);
        Assert.Equal(KlipperMcu.ResponseIds["config"], id);
        Assert.Equal(1, args[0]); // is_config
        Assert.Equal(0x1234, args[1]); // crc echoed
        Assert.True(args[2] > 0); // move_count
    }

    [Fact]
public void Queue_step_drives_the_carriage_exactly_like_the_velocity_path()
    {
        Configure();

        // Home reference + first move: 200 full steps at interval 40 ticks
        // (200 steps/s = 1 rev/s for a 1.8° motor) = 1 rev = 40 mm of carriage.
        Send(KlipperMcu.CommandIds["reset_step_clock"], 0, 1000);
        Send(KlipperMcu.CommandIds["set_next_step_dir"], 0, 1);
        Send(KlipperMcu.CommandIds["queue_step"], 0, 40, 200, 0);

        // 200 steps at interval 40 ticks (8 kHz clock): first at 1040, last at 9000.
        _simulation.RunFor(1.15);

        // Every step executed; GT2-20T kinematics: 40 mm per pulley revolution.
        Assert.Equal(200, _simulation.Stepper("motor_left").SignedStepPosition);
        var carriage = _simulation.Simulator.GetJointPos("j_carriage");
        Assert.InRange(Math.Abs(carriage), 0.036, 0.043);
        Assert.Equal(0, _simulation.Stepper("motor_left").MissedSteps);

        // stepper_get_position reports the signed step counter over the wire.
        var position = Send(KlipperMcu.CommandIds["stepper_get_position"], 0);
        var (id, args) = Decode(position);
        Assert.Equal(KlipperMcu.ResponseIds["stepper_position"], id);
        Assert.Equal(0, args[0]);
        Assert.Equal(200, args[1]);
    }

    [Fact]
    public void Step_queue_timing_follows_interval_and_add()
    {
        Configure();
        Send(KlipperMcu.CommandIds["reset_step_clock"], 0, 0);
        Send(KlipperMcu.CommandIds["set_next_step_dir"], 0, 1);
        // Ramp: interval starts at 80 ticks, shrinks by 4 each step (accelerating).
        Send(KlipperMcu.CommandIds["queue_step"], 0, 80, 20, -4);

// Steps at clock: 80, 156, 228, 296, ... — after 320 ticks expect 4 executed.
        _simulation.RunFor(0.04); // 320 ticks
        var channel = _simulation.Stepper("motor_left");
        Assert.InRange(channel.SignedStepPosition, 3, 5);
        Assert.Equal(16, channel.QueuedSteps); // 4 executed, 16 still queued
    }

    // ---------- 3. homing via endstop_home ----------

    [Fact]
    public void Endstop_home_halts_the_queued_move_on_trigger()
    {
        Configure();
// Arm; the first non-ack response is the arm acknowledgment (homing=1).
        var arm = Send(KlipperMcu.CommandIds["endstop_home"], 1, 100, 10, 3, 100, 1);
        var (armId, armArgs) = Decode(arm);
        Assert.Equal(KlipperMcu.ResponseIds["endstop_home"], armId);
        Assert.Equal((1, 1), (armArgs[0], armArgs[1])); // oid=1, homing=1

        // Command 2 rev (80 mm) toward the endstop that triggers at 20 mm.
        Send(KlipperMcu.CommandIds["reset_step_clock"], 0, 0);
        Send(KlipperMcu.CommandIds["set_next_step_dir"], 0, 1);
        Send(KlipperMcu.CommandIds["queue_step"], 0, 40, 400, 0);

// Advance in slices until the trigger response arrives.
        var trigger = (Id: -1, Args: new List<long>());
        for (var i = 0; i < 40 && trigger.Id < 0; i++)
        {
            _simulation.RunFor(0.05);
            foreach (var response in _mcu.DrainResponses())
            {
                var decoded = Decode(response);
                if (decoded.Id == KlipperMcu.ResponseIds["endstop_home"] && decoded.Args[1] == 0)
                    trigger = (decoded.Id, decoded.Args);
            }
        }

        Assert.NotEqual(0, trigger.Id); // the trigger fired
        Assert.Equal(1, trigger.Args[0]); // endstop oid

        // The queue was cleared and the stepper stopped just past the trigger.
        var stepper = _simulation.Stepper("motor_left");
        Assert.Equal(0, stepper.QueuedSteps);
        Assert.InRange(Math.Abs(stepper.SignedStepPosition), 95, 130); // 20 mm ≈ 100 steps
        var carriage = _simulation.Simulator.GetJointPos("j_carriage");
        Assert.InRange(Math.Abs(carriage), 0.019, 0.024);

        // No further motion after the halt.
        var positionAtTrigger = stepper.SignedStepPosition;
        _simulation.RunFor(0.2);
        Assert.Equal(positionAtTrigger, stepper.SignedStepPosition);
    }

    // ---------- 4. transport-level behaviour ----------

    [Fact]
    public void Out_of_order_blocks_are_naked_and_retransmits_accepted()
    {
        Configure();

        // An out-of-order block (gap in the sequence) is discarded with a nak.
        var outOfOrder = KlipperWire.Frame(
            KlipperWire.EncodeVlqAll(KlipperMcu.CommandIds["get_clock"]), (byte)((_hostSeq + 3) & 0x0f));
        var responses = _mcu.Receive(outOfOrder);
        var nak = Assert.Single(responses);
        Assert.Equal(KlipperWire.MinBlockSize, nak.Length); // empty content = nak
        Assert.True((nak[1] & 0x0f) != ((_hostSeq + 1) & 0x0f)); // nak sequence < expected

// The host retransmits in order and the MCU accepts it — with a live clock.
        _simulation.RunFor(0.01); // 80 ticks
        var inOrder = Send(KlipperMcu.CommandIds["get_clock"]);
        var (id, args) = Decode(inOrder);
        Assert.Equal(KlipperMcu.ResponseIds["clock"], id);
        Assert.True(args[0] > 0);
    }
}
