using System.Text.Json;
using MechMaker.Core;
using MechMaker.Core.Model;
using MechMaker.Engine;
using MechMaker.Engine.Hil;
using MechMaker.Engine.Rendering;
using MechMaker.Hil;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var outDir = Path.Combine(root, "tools", "pinvideo", "out");
Directory.CreateDirectory(outDir);
foreach (var old in Directory.EnumerateFiles(outDir)) File.Delete(old);

var catalog = PartCatalog.LoadFromDirectory(Path.Combine(root, "catalog"));
var machine = CoreJson.Deserialize<MachineDefinition>(
    File.ReadAllText(Path.Combine(root, "examples", "dx8000_hil_rig.json")));
var device = catalog.Get("dx8000");
var screenW = device.Params.GetValueOrDefault("screen_width_m", 0.068);
var screenH = device.Params.GetValueOrDefault("screen_height_m", 0.136);
var screenZ = device.Params.GetValueOrDefault("screen_plane_z_m", 0.0);

using var sim = MachineSimulation.FromMachine(machine, catalog);
var app = new FakePinPadTransport();
var bridge = new PhysicsTapBridge(sim, "dut", "finger", (fx, fy) => app.Tap(fx, fy),
    screenW, screenH, screenZ);
bridge.Arm();
sim.Mcu.PostTick += () => bridge.Tick();

var camera = new OrbitCamera
{
    YawRad = 0.72,
    PitchRad = 0.50,
    TargetX = 0.0,
    TargetY = 0.0,
    TargetZ = 0.045,
    Distance = 0.44
};

var controller = new TapAtController(sim, "dut", "finger", "y_nut", "motor_left",
    screenW, "y_leadscrew", screenH, screenZ);

var steps = new List<object>();
int frame = 0;
void Capture(string label, double? fx = null, double? fy = null)
{
    sim.Simulator.RefreshKinematics();
    var scene = sim.Simulator.GetScene();
    var tris = SceneTessellator.Tessellate(scene, 28);
    var px = new SoftRenderer().RenderBgra(tris, camera, 1000, 1000);
    var robot = SceneRenderer.BgraToPng(px, 1000, 1000);
    File.WriteAllBytes(Path.Combine(outDir, $"robot_{frame:00}.png"), robot);
    File.WriteAllBytes(Path.Combine(outDir, $"screen_{frame:00}.png"), app.Screencap());
    steps.Add(new { frame, label, fx, fy, pin = app.EnteredPin, accepted = app.Accepted });
    frame++;
}

Capture("Ready — physical finger + emulated DX8000");
foreach (var digit in "1234")
{
    var template = app.KeyTemplate(digit.ToString());
    var match = ScreenDetector.Find(digit.ToString(), app.Screencap(), template)
        ?? throw new InvalidOperationException($"key {digit} not found");
    var hit = controller.Tap(match.Fx, match.Fy);
    Capture($"Tap {digit} — physical contact at ({hit.Fx * 100:0.#}%, {hit.Fy * 100:0.#}%)",
        hit.Fx, hit.Fy);
}

var ok = ScreenDetector.Find("OK", app.Screencap(), app.KeyTemplate("OK"))
    ?? throw new InvalidOperationException("OK key not found");
var accepted = controller.Tap(ok.Fx, ok.Fy);
Capture($"Tap OK — PIN {app.EnteredPin} entered", accepted.Fx, accepted.Fy);
Console.WriteLine($"OK match=({ok.Fx:0.###},{ok.Fy:0.###}) hit=({accepted.Fx:0.###},{accepted.Fy:0.###}) accepted={app.Accepted}");
Console.WriteLine("taps=" + string.Join("; ", app.Taps.Select(t => $"({t.Fx:0.###},{t.Fy:0.###})")));
Console.WriteLine("bridge=" + string.Join("; ", bridge.DispatchedTaps.Select(t => $"({t.Fx:0.###},{t.Fy:0.###})")));

File.WriteAllText(Path.Combine(outDir, "steps.json"),
    JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"frames={frame} pin={app.EnteredPin} accepted={app.Accepted}");
