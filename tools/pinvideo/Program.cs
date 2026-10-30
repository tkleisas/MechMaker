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

var bench = new SceneShape(
    Name: "bench", Kind: SceneShapeKind.Box,
    HalfX: 0.35, HalfY: 0.30, HalfZ: 0.006,
    Px: 0, Py: 0, Pz: -0.016,
    R00: 1, R01: 0, R02: 0, R10: 0, R11: 1, R12: 0, R20: 0, R21: 0, R22: 1,
    R: 0.15, G: 0.16, B: 0.18, A: 1);
var grid = new List<SceneShape>();
for (var i = -3; i <= 3; i++)
{
    grid.Add(new SceneShape("grid_x", SceneShapeKind.Box, 0.35, 0.001, 0.0005,
        0, i * 0.1, -0.0095, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.22, 0.24, 0.27, 1));
    grid.Add(new SceneShape("grid_y", SceneShapeKind.Box, 0.001, 0.30, 0.0005,
        i * 0.1, 0, -0.0095, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.22, 0.24, 0.27, 1));
}

int frame = 0;
double lastCapture = -1;
var recording = false;
string label = "Ready — physical finger + emulated DX8000";
double? targetFx = null, targetFy = null;
var steps = new List<FrameStep>();

void Capture()
{
    sim.Simulator.RefreshKinematics();
    var scene = sim.Simulator.GetScene().ToList();
    scene.Add(bench);
    scene.AddRange(grid);
    var tris = SceneTessellator.Tessellate(scene, 28);
    var px = new SoftRenderer().RenderBgra(tris, camera, 1000, 1000);
    File.WriteAllBytes(Path.Combine(outDir, $"robot_{frame:00}.png"),
        SceneRenderer.BgraToPng(px, 1000, 1000));

    var finger = sim.Simulator.GetBodyPosition("finger");
    var dut = sim.Simulator.GetBodyPosition("dut");
    var closeScene = sim.Simulator.GetScene()
        .Where(s => s.Name.StartsWith("finger") || s.Name.StartsWith("y_nut"))
        .ToList();
    closeScene.Add(new SceneShape(
        Name: "screen", Kind: SceneShapeKind.Box,
        HalfX: screenW / 2, HalfY: screenH / 2, HalfZ: 0.001,
        Px: dut[0], Py: dut[1], Pz: dut[2],
        R00: 1, R01: 0, R02: 0, R10: 0, R11: 1, R12: 0, R20: 0, R21: 0, R22: 1,
        R: 0.03, G: 0.07, B: 0.20, A: 1));
    var closeCam = new OrbitCamera
    {
        TargetX = finger[0],
        TargetY = finger[1] + 0.05,
        TargetZ = finger[2] - 0.02,
        Distance = 0.26,
        YawRad = 0.0,
        PitchRad = 0.12
    };
    var closePx = new SoftRenderer().RenderBgra(
        SceneTessellator.Tessellate(closeScene, 28), closeCam, 600, 600);
    File.WriteAllBytes(Path.Combine(outDir, $"closeup_{frame:00}.png"),
        SceneRenderer.BgraToPng(closePx, 600, 600));

    File.WriteAllBytes(Path.Combine(outDir, $"screen_{frame:00}.png"), app.Screencap());
    steps.Add(new FrameStep(frame, label, targetFx, targetFy, app.EnteredPin, app.Accepted,
        bridge.DispatchedTaps.Count));
    frame++;
}

sim.Mcu.PostTick += () =>
{
    if (!recording) return;
    var now = sim.Simulator.Time;
    if (now - lastCapture < 1.0 / 30.0) return;
    lastCapture = now;
    Capture();
};

recording = true;
sim.RunFor(0.40);

foreach (var digit in "1234")
{
    var template = app.KeyTemplate(digit.ToString());
    var match = ScreenDetector.Find(digit.ToString(), app.Screencap(), template)
        ?? throw new InvalidOperationException($"key {digit} not found");
    label = $"Tap {digit} — moving to a measured key centre";
    targetFx = match.Fx; targetFy = match.Fy;
    controller.Tap(match.Fx, match.Fy);
    targetFx = null; targetFy = null;
    label = $"Entered {app.EnteredPin}";
    sim.RunFor(0.15);
}

var ok = ScreenDetector.Find("OK", app.Screencap(), app.KeyTemplate("OK"))
    ?? throw new InvalidOperationException("OK key not found");
label = "Tap OK";
targetFx = ok.Fx; targetFy = ok.Fy;
controller.Tap(ok.Fx, ok.Fy);
targetFx = null; targetFy = null;
label = app.Accepted ? $"APPROVED — PIN {app.EnteredPin}" : $"PIN {app.EnteredPin}";
sim.RunFor(0.40);
recording = false;

File.WriteAllText(Path.Combine(outDir, "steps.json"),
    JsonSerializer.Serialize(steps, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    }));
Console.WriteLine($"frames={frame} pin={app.EnteredPin} accepted={app.Accepted}");

public sealed record FrameStep(int Frame, string Label, double? Fx, double? Fy, string Pin, bool Accepted, int Taps);
