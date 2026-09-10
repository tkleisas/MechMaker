using System.ComponentModel;
using MechMaker.Core;
using MechMaker.Core.Validation;
using MechMaker.Engine;
using MechMaker.Engine.Scripting;
using MechMaker.Hil;
using ModelContextProtocol.Server;

namespace MechMaker.Server;

/// <summary>
/// The MCP tool surface: an LLM agent assembles machines from the part catalog,
/// wires them to the virtual board, validates, compiles MJCF, and runs the
/// physics closed loop (drive motors, watch endstops, catch stalls).
/// All tools operate on the single session machine in <see cref="McpWorkspace"/>.
/// Every tool body goes through one gate because the MCP layer dispatches
/// concurrent calls and the workspace state is not thread-safe.
/// </summary>
[McpServerToolType]
public static class MachineTools
{
    private static McpWorkspace W => McpWorkspace.Instance;
    private static readonly object Gate = new();

    private static T Locked<T>(Func<T> tool)
    {
        lock (Gate)
            return tool();
    }

    // ---------- catalog ----------

    [McpServerTool(Name = "list_catalog_parts")]
    [Description("List all parts available in the MechMaker part catalog with their connectors, " +
                 "motor specs, and transmission flags. Call this first to see what you can build with.")]
    public static string ListCatalogParts() => Locked(() =>
    {
        var lines = W.Catalog.All
            .OrderBy(p => p.Id)
            .Select(p =>
            {
                var connectors = p.Connectors.Count == 0
                    ? "no connectors"
                    : string.Join(", ", p.Connectors.Select(c => $"{c.Name}:{c.Type}"));
                var motor = p.Motor is null ? "" : $" | motor: {p.Motor.Kind} {p.Motor.HoldingTorqueNm} N*m";
                var transmission = p.IsTransmissionElement ? " | transmission element" : "";
                return $"{p.Id} — {connectors}{motor}{transmission}";
            });
        return string.Join(Environment.NewLine, lines);
    });

    [McpServerTool(Name = "get_part_info")]
    [Description("Full JSON definition of one catalog part (bodies, shapes, connectors, motor specs).")]
    public static string GetPartInfo(
        [Description("Catalog part id, e.g. 'nema17_stepper'")] string catalogId)
        => Locked(() => CoreJson.Serialize(W.Catalog.Get(catalogId)));

    // ---------- machine file ----------

    [McpServerTool(Name = "new_machine")]
    [Description("Start building a new, empty machine, replacing whatever is in the session.")]
    public static string NewMachine([Description("Machine name")] string name)
        => Locked(() => W.NewMachine(name));

    [McpServerTool(Name = "open_machine")]
    [Description("Load a machine.json file into the session, replacing the current machine.")]
    public static string OpenMachine([Description("Path to machine.json")] string path)
        => Locked(() => W.OpenMachine(path));

    [McpServerTool(Name = "save_machine")]
    [Description("Save the session machine to a machine.json file. Omit the path to overwrite the file last opened.")]
    public static string SaveMachine([Description("Destination path (optional)")] string? path = null)
        => Locked(() => W.SaveMachine(path));

    [McpServerTool(Name = "get_machine_json")]
    [Description("The current session machine as machine.json (parts, connections, boards, wiring).")]
    public static string GetMachineJson() => Locked(() => CoreJson.Serialize(W.Machine));

    // ---------- parts ----------

    [McpServerTool(Name = "add_part")]
    [Description("Place a catalog part into the machine. Positions are metres; omit x/y/z to " +
                 "auto-stack the part above everything already placed. Returns the instance id.")]
    public static string AddPart(
        [Description("Catalog part id, e.g. 'beam_2020_400'")] string catalogId,
        [Description("Instance id for the placed part (optional; auto-generated if omitted)")] string? instanceId = null,
        [Description("X position in metres")] double? x = null,
        [Description("Y position in metres")] double? y = null,
        [Description("Z position in metres")] double? z = null,
        [Description("Rotation around X, degrees")] double rx = 0,
        [Description("Rotation around Y, degrees")] double ry = 0,
        [Description("Rotation around Z, degrees")] double rz = 0)
        => Locked(() => W.AddPart(catalogId, instanceId, x, y, z, rx, ry, rz));

    [McpServerTool(Name = "update_part_pose")]
    [Description("Set the pose of a placed part. Positions in metres, rotations in degrees.")]
    public static string UpdatePartPose(
        [Description("Part instance id")] string instanceId,
        double x, double y, double z,
        [Description("Rotation around X, degrees")] double rx = 0,
        [Description("Rotation around Y, degrees")] double ry = 0,
        [Description("Rotation around Z, degrees")] double rz = 0)
        => Locked(() => W.UpdatePartPose(instanceId, x, y, z, rx, ry, rz));

    [McpServerTool(Name = "delete_part")]
    [Description("Remove a placed part, including its connections and wiring.")]
    public static string DeletePart([Description("Part instance id")] string instanceId)
        => Locked(() => W.DeletePart(instanceId));

    // ---------- connections ----------

    [McpServerTool(Name = "add_connection")]
    [Description("Mate two connectors of two placed parts (e.g. mount a stepper onto a mount " +
                 "plate, clamp a carriage onto a belt). Use list_catalog_parts / get_part_info " +
                 "to find connector names. Connection compatibility is checked by validate_machine.")]
    public static string AddConnection(
        [Description("First part instance id")] string partA,
        [Description("Connector name on the first part")] string connectorA,
        [Description("Second part instance id")] string partB,
        [Description("Connector name on the second part")] string connectorB)
        => Locked(() => W.AddConnection(partA, connectorA, partB, connectorB));

    [McpServerTool(Name = "delete_connection")]
    [Description("Remove a connection by its id (ids are returned by add_connection and appear in diagnostics).")]
    public static string DeleteConnection([Description("Connection id, e.g. 'c1'")] string connectionId)
        => Locked(() => W.DeleteConnection(connectionId));

    [McpServerTool(Name = "list_connectors")]
    [Description("Connectors of a placed part instance (name:type), so you can pick mates for connections.")]
    public static string ListConnectors([Description("Part instance id")] string instanceId)
        => Locked(() => string.Join(", ", W.ConnectorsOf(instanceId).Select(c => $"{c.Name}:{c.Type}")));

    // ---------- boards & wiring ----------

    [McpServerTool(Name = "add_board")]
    [Description("Add a simulated control board to the machine.")]
    public static string AddBoard(
        [Description("Board id, e.g. 'main_board'")] string boardId,
        [Description("Board type (default 'skr-pico')")] string type = "skr-pico")
        => Locked(() => W.AddBoard(boardId, type));

    [McpServerTool(Name = "wire")]
    [Description("Wire a component signal to a board pin. A stepper motor needs a 'step' signal " +
                 "to get a simulated motor channel (typical signals: step, dir, en, endstop).")]
    public static string Wire(
        [Description("Part instance id, e.g. the motor")] string component,
        [Description("Signal name, e.g. 'step'")] string signal,
        [Description("Board id")] string board,
        [Description("Pin name on the board, e.g. 'stepper_x'")] string pin)
        => Locked(() => W.Wire(component, signal, board, pin));

    // ---------- validation & compilation ----------

    [McpServerTool(Name = "validate_machine")]
    [Description("Validate the machine: unknown parts/connector, duplicate ids, incompatible mates, " +
                 "unwired steppers, disconnected islands, belt sanity. Returns mmNNN diagnostics or OK.")]
    public static string ValidateMachine() => Locked(() =>
    {
        var report = W.Validate();
        return report.Diagnostics.Count == 0 ? "OK" : report.ToString();
    });

    [McpServerTool(Name = "compile_mjcf")]
    [Description("Compile the machine to a MuJoCo MJCF model (what the physics engine runs). " +
                 "Fails with diagnostics if the machine has validation errors.")]
    public static string CompileMjcf(
        [Description("Write the XML to this file instead of returning it")] string? outputPath = null)
        => Locked(() =>
        {
            var (mjcf, report) = W.Compile();
            if (outputPath is not null)
            {
                var full = Path.GetFullPath(outputPath);
                File.WriteAllText(full, mjcf);
                return $"Compiled '{W.Machine.Name}' -> '{full}'" +
                       (report.Diagnostics.Count == 0 ? "" : $"\n{report}");
            }
            return mjcf + (report.Diagnostics.Count == 0 ? "" : $"\n{report}");
        });

    // ---------- simulation ----------

    [McpServerTool(Name = "start_run")]
    [Description("Compile the machine and start a live physics run with the virtual MCU " +
                 "(a stepper channel for every wired 'step' signal, 8 kHz deterministic loop). " +
                 "Channels start disabled — enable and command each motor you want to drive " +
                 "(enable_motor + set_motor_velocity). Fails with diagnostics if the machine does not compile.")]
    public static string StartRun([Description("Microsteps per full step (default 16)")] int microsteps = 16)
        => Locked(() => W.StartRun(microsteps));

    [McpServerTool(Name = "run_for")]
    [Description("Advance the live simulation, then report motor states and endstops.")]
    public static string RunFor([Description("Simulated seconds to advance (0.001–10)")] double seconds)
        => Locked(() => W.RunFor(seconds));

    [McpServerTool(Name = "set_motor_velocity")]
    [Description("Command a wired stepper's shaft velocity in rev/s. The channel ramps at its " +
                 "acceleration limit like real firmware; commanding full speed instantly stalls the motor.")]
    public static string SetMotorVelocity(
        [Description("Motor part instance id")] string instanceId,
        [Description("Target velocity in revolutions per second")] double revPerSec)
        => Locked(() => W.SetMotorVelocity(instanceId, revPerSec));

    [McpServerTool(Name = "enable_motor")]
    [Description("Enable or disable a wired stepper (disabled = no torque, like de-energized coils).")]
    public static string EnableMotor(
        [Description("Motor part instance id")] string instanceId,
        [Description("True to energize, false to release")] bool enabled)
        => Locked(() => W.EnableMotor(instanceId, enabled));

    [McpServerTool(Name = "add_endstop")]
    [Description("Add a limit switch watching a joint position (pressed at or below the trigger " +
                 "position). Active immediately if a run is live, otherwise on the next run.")]
    public static string AddEndstop(
        [Description("Joint name, e.g. the carriage slide 'j_my_carriage'")] string jointName,
        [Description("Trigger position in the joint's coordinate (metres)")] double triggerPosition,
        [Description("Debounce ticks (default 3)")] int debounceTicks = 3)
        => Locked(() => W.AddEndstop(jointName, triggerPosition, debounceTicks));

    [McpServerTool(Name = "read_endstop")]
    [Description("Read a limit switch's debounced state and the joint position it watches.")]
    public static string ReadEndstop([Description("Joint name the endstop watches")] string jointName)
        => Locked(() => W.ReadEndstop(jointName));

    [McpServerTool(Name = "get_run_status")]
    [Description("Current live-run state: time, per-motor commanded/actual angle, missed steps, " +
                 "stalls, endstop states, servo angles, fan speeds, heater temperatures.")]
    public static string GetRunStatus() => Locked(() => W.GetRunStatus());

    [McpServerTool(Name = "set_servo_angle")]
    [Description("Command a wired hinge servo's target angle in degrees (its duty sweeps the " +
                 "catalog-declared range; 0 = authored neutral).")]
    public static string SetServoAngle(
        [Description("Servo part instance id")] string instanceId,
        [Description("Target angle in degrees")] double degrees)
        => Locked(() => W.SetServoAngle(instanceId, degrees));

    [McpServerTool(Name = "set_finger_position")]
    [Description("Command a slide servo (e.g. the touch_finger press plunger) in metres: " +
                 "negative = press toward the glass, positive/zero = retract. Each physical " +
                 "press dispatches an emulator tap through the HIL bridge; hold the press " +
                 "for long-press timing.")]
    public static string SetFingerPosition(
        [Description("Slide servo part instance id, e.g. 'finger'")] string instanceId,
        [Description("Target joint position in metres (negative presses down)")] double metres)
        => Locked(() => W.SetServoPosition(instanceId, metres));

    [McpServerTool(Name = "set_fan_duty")]
    [Description("Set a DC fan's drive duty (0..1); the actuator tracks duty × rated rpm.")]
    public static string SetFanDuty(
        [Description("Fan part instance id")] string instanceId,
        [Description("Duty 0..1")] double duty)
        => Locked(() => W.SetFanDuty(instanceId, duty));

    [McpServerTool(Name = "set_heater_duty")]
    [Description("Set a heater's duty (0..1) — the lumped thermal model heats the block " +
                 "toward duty × power / cooling + ambient. Watch it with get_run_status.")]
    public static string SetHeaterDuty(
        [Description("Heater part instance id")] string instanceId,
        [Description("Heater duty 0..1")] double duty)
        => Locked(() => W.SetHeaterDuty(instanceId, duty));

    // ---------- HIL: Android emulator + vision (M6) ----------

    [McpServerTool(Name = "hil_connect")]
    [Description("Pair the machine's android_phone part with an emulator. Transport 'fake' " +
                 "renders synthetic screens (offline/tests); 'adb' drives a live emulator " +
                 "(ANDROID_ADB/ANDROID_SERIAL env or explicit paths). Touch actions take " +
                 "screen fractions [0..1], like androidtester's touch.tap.")]
    public static string HilConnect(
        [Description("'fake' for the synthetic emulator, 'adb' for a live one")] string transport = "fake",
        [Description("Path to adb (adb transport only)")] string? adbPath = null,
        [Description("Emulator serial (adb transport only, default emulator-5554)")] string? adbSerial = null)
        => Locked(() => W.HilConnect(transport, adbPath, adbSerial));

    [McpServerTool(Name = "arm_physics_taps")]
    [Description("Arm physics-contact taps: place a touch_finger over the phone, start a run, " +
                 "then arm — when the finger's tip presses the phone's screen in the physics " +
                 "(command the finger's joint or drive it with a stepper), the contact point is " +
                 "mapped to screen fractions and dispatched to the emulator. One tap per press.")]
    public static string ArmPhysicsTaps() => Locked(() => W.ArmPhysicsTaps());

    [McpServerTool(Name = "hil_status")]
    [Description("HIL session state: phone pairing, emulator transport, physics-contact taps dispatched.")]
    public static string HilStatus() => Locked(() => W.HilStatus());

    [McpServerTool(Name = "touch_tap")]
    [Description("Tap the phone screen at a fraction position [0..1]² (origin top-left) — " +
                 "dispatched to the emulator over adb/fake transport. Find positions with screen_find.")]
    public static string TouchTap(
        [Description("Fraction X across the screen")] double fx,
        [Description("Fraction Y down the screen")] double fy)
        => Locked(() => W.TouchTap(fx, fy));

    [McpServerTool(Name = "touch_swipe")]
    [Description("Swipe between two screen fractions over a duration (ms).")]
    public static string TouchSwipe(
        [Description("Start fraction X")] double fx1,
        [Description("Start fraction Y")] double fy1,
        [Description("End fraction X")] double fx2,
        [Description("End fraction Y")] double fy2,
        [Description("Swipe duration in milliseconds")] int durationMs = 300)
        => Locked(() => W.TouchSwipe(fx1, fy1, fx2, fy2, durationMs));

    [McpServerTool(Name = "screen_find")]
    [Description("Vision: find a UI element by template image on the emulator's current screen " +
                 "(OpenCV CCOEFF_NORMED matching). Returns fraction coords + confidence + " +
                 "phone-local mm — tap the fractions with touch_tap. Capture templates from " +
                 "screen_screencaps with screen_extract_template.")]
    public static string ScreenFind(
        [Description("Element name for the report")] string name,
        [Description("Path to the template PNG (a crop of a previous screencap)")] string templatePath,
        [Description("Match confidence threshold (default 0.8)")] double threshold = 0.8)
        => Locked(() => W.ScreenFind(name, templatePath, threshold));

    [McpServerTool(Name = "screen_wait_for")]
    [Description("Poll the screen until a template appears (or the timeout expires) — " +
                 "androidtester's screen.wait_for.")]
    public static string ScreenWaitFor(
        [Description("Element name for the report")] string name,
        [Description("Template PNG path")] string templatePath,
        [Description("Timeout in seconds")] double timeoutS,
        [Description("Poll interval in seconds (default 0.5)")] double pollS = 0.5,
        [Description("Match confidence threshold (default 0.8)")] double threshold = 0.8)
        => Locked(() => W.ScreenWaitFor(name, templatePath, timeoutS, pollS, threshold));

    [McpServerTool(Name = "screen_screencap")]
    [Description("Capture the emulator screen as a PNG (writes a file when a path is given).")]
    public static string ScreenScreencap(
        [Description("Output PNG path (optional; without it only the byte count is reported)")] string? outputPath = null)
        => Locked(() => W.ScreenScreencap(outputPath));

    [McpServerTool(Name = "screen_extract_template")]
    [Description("Cut a template image out of a screencap PNG around a fraction position — " +
                 "the crop becomes the template for screen_find/screen_wait_for.")]
    public static string ScreenExtractTemplate(
        [Description("Screencap PNG path to crop from")] string screencapPath,
        [Description("Fraction X of the element centre")] double fx,
        [Description("Fraction Y of the element centre")] double fy,
        [Description("Half-size of the crop in pixels (default 60)")] int halfSizePx = 60,
        [Description("Output template PNG path")] string outputPath = "template.png")
        => Locked(() =>
        {
            var png = File.ReadAllBytes(Path.GetFullPath(screencapPath));
            var template = ScreenDetector.ExtractTemplate(png, fx, fy, halfSizePx);
            File.WriteAllBytes(Path.GetFullPath(outputPath), template);
            return $"Template {2 * halfSizePx}px around ({fx * 100:0.#}%, {fy * 100:0.#}%) -> '{outputPath}'.";
        });

    [McpServerTool(Name = "stop_run")]
    [Description("Stop the live simulation and discard it (machine edits apply to the next run).")]
    public static string StopRun() => Locked(() => W.StopRun());

    // ---------- Lua scenarios ----------

    [McpServerTool(Name = "get_scenario_api")]
    [Description("Documents the `sim` API passed to `run(sim)` in MechMaker Lua scenarios " +
                 "(deterministic scripted simulations: drive motors, watch endstops, detect stalls).")]
    public static string GetScenarioApi() => LuaScenario.ApiDoc;

    // ---------- Klipper protocol (M5) ----------

    [McpServerTool(Name = "klipper_connect")]
    [Description("Start a run with a klipper-protocol MCU endpoint attached. Returns the pin " +
                 "enumerations (stepper instance ids, endstop pins). Then send klipper MCU " +
                 "commands with klipper_send: identify, allocate_oids, config_stepper, " +
                 "config_endstop, endstop_set_stepper, finalize_config, reset_step_clock, " +
                 "set_next_step_dir, queue_step, stepper_get_position, endstop_home.")]
    public static string KlipperConnect() => Locked(() => W.KlipperConnect());

    [McpServerTool(Name = "klipper_send")]
    [Description("Send a klipper MCU command to the connected simulated board (encoded on the real " +
                 "wire format). Typical flow: allocate_oids(count) → config_stepper(oid, step_pin, " +
                 "dir_pin, invert_step, step_pulse_ticks) → config_endstop(oid, pin, pull_up) → " +
                 "endstop_set_stepper(oid, stepper_oid) → finalize_config(crc) → reset_step_clock(oid, " +
                 "clock) → set_next_step_dir(oid, dir) → queue_step(oid, interval, count, add). " +
                 "Returns the ack and any response as decoded VLQ integers.")]
    public static string KlipperSend(
        [Description("Command name, e.g. 'queue_step' ('identify' also accepted)")] string command,
        [Description("Integer arguments in order, e.g. [0, 40, 200, 0]")] double[] args)
        => Locked(() => W.KlipperSend(command, args));

    [McpServerTool(Name = "klipper_status")]
    [Description("Klipper session state: MCU clock, per-stepper signed step position, queued steps.")]
    public static string KlipperStatus() => Locked(() => W.KlipperStatus());

    [McpServerTool(Name = "run_scenario_source")]
    [Description("Run a Lua scenario against the session machine. The script must define " +
                 "`function run(sim) ... end`; call sim.run(seconds) to advance, sim.enable / " +
                 "sim.velocity to drive motors, sim.endstop to sense. Returns the print log, " +
                 "the `result` table as JSON, and sim time. Call get_scenario_api for the full API.")]
    public static string RunScenarioSource([Description("Lua source of the scenario")] string source)
        => Locked(() => W.RunScenarioSource(source));

    [McpServerTool(Name = "run_scenario")]
    [Description("Run a Lua scenario file (.lua) against the session machine — see get_scenario_api.")]
    public static string RunScenario([Description("Path to the .lua scenario file")] string path)
        => Locked(() => W.RunScenarioFile(path));
}
