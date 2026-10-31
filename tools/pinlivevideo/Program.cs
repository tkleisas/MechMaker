using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MechMaker.Core;
using MechMaker.Core.Model;
using MechMaker.Engine;
using MechMaker.Engine.Hil;
using MechMaker.Engine.Rendering;
using MechMaker.Hil;
using OpenCvSharp;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var outDir = Path.Combine(root, "tools", "pinlivevideo", "out");
Directory.CreateDirectory(outDir);
var oneFrame = args.Contains("--one-frame");

var adb = Arg("--adb") ?? Environment.GetEnvironmentVariable("ANDROID_ADB")!;
var serial = Arg("--serial") ?? Environment.GetEnvironmentVariable("ANDROID_SERIAL") ?? "emulator-5554";
var expected = Arg("--pin") ?? "1234";
const int W = 720, H = 1440;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
(string Output, string Error, int Code) Adb(string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = adb, Arguments = $"-s {serial} {arguments}",
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
    return (o, e, p.ExitCode);
}
void ResetApp()
{
    Adb("shell am force-stop com.tkleisas.pinapp");
    Adb("shell run-as com.tkleisas.pinapp rm -f files/pin.txt");
    Adb("shell am start -n com.tkleisas.pinapp/.MainActivity");
    Thread.Sleep(1500);
}

ResetApp();
var adbTransport = new AdbEmulatorTransport(adb, serial, (W, H));
if (!adbTransport.IsAlive) throw new InvalidOperationException($"adb cannot see {serial}");
var screenPng = Arg("--image") is { } img ? File.ReadAllBytes(img) : adbTransport.Screencap();

var catalog = PartCatalog.LoadFromDirectory(Path.Combine(root, "catalog"));
var machine = CoreJson.Deserialize<MachineDefinition>(
    File.ReadAllText(Path.Combine(root, "examples", "dx8000_hil_rig.json")));
var device = catalog.Get("dx8000");
var screenW = device.Params.GetValueOrDefault("screen_width_m", 0.068);
var screenH = device.Params.GetValueOrDefault("screen_height_m", 0.136);
var screenZ = device.Params.GetValueOrDefault("screen_plane_z_m", 0.0);

using var sim = MachineSimulation.FromMachine(machine, catalog);
var bridge = new PhysicsTapBridge(sim, "dut", "finger", adbTransport.Tap,
    screenW, screenH, screenZ);
bridge.Arm();
sim.Mcu.PostTick += () => bridge.Tick();
var controller = new TapAtController(sim, "dut", "finger", "y_nut", "motor_left",
    screenW, "y_leadscrew", screenH, screenZ);

var camera = new OrbitCamera
{
    YawRad = 0.72, PitchRad = 0.50, TargetX = 0, TargetY = 0, TargetZ = 0.045, Distance = 0.44
};
var bench = new SceneShape("bench", SceneShapeKind.Box, 0.35, 0.30, 0.006,
    0, 0, -0.016, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.15, 0.16, 0.18, 1);
var grid = new List<SceneShape>();
for (var i = -3; i <= 3; i++)
{
    grid.Add(new SceneShape("grid_x", SceneShapeKind.Box, 0.35, 0.001, 0.0005,
        0, i * 0.1, -0.0095, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.22, 0.24, 0.27, 1));
    grid.Add(new SceneShape("grid_y", SceneShapeKind.Box, 0.001, 0.30, 0.0005,
        i * 0.1, 0, -0.0095, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.22, 0.24, 0.27, 1));
}
var screenShape = sim.Simulator.GetScene().First(s =>
    s.Kind == SceneShapeKind.Mesh && (s.MeshFile ?? "").Contains("dx8000_screen"));
Console.WriteLine($"screen shape centre {screenShape.Px:0.####},{screenShape.Py:0.####},{screenShape.Pz:0.####} " +
                  $"half {screenShape.HalfX:0.####},{screenShape.HalfY:0.####}");

Mat RenderRobot(IEnumerable<SceneShape> scene, OrbitCamera cam)
{
    var px = new SoftRenderer().RenderBgra(SceneTessellator.Tessellate(scene.ToList(), 28), cam, 1000, 1000);
    return Cv2.ImDecode(SceneRenderer.BgraToPng(px, 1000, 1000), ImreadModes.Color);
}
Mat OverlayScreen(Mat robot, OrbitCamera cam, byte[] appPng, SceneShape screen)
{
    var corners = ScreenCorners(screen);
    var proj = corners.Select(p => Project(p, cam, robot.Width, robot.Height)).ToArray();
    if (proj.Any(p => p is null))
        return robot;
    var src = Cv2.ImDecode(appPng, ImreadModes.Color);
    var rotation = Arg("--rotate");
    if (rotation == "cw") Cv2.Rotate(src, src, RotateFlags.Rotate90Clockwise);
    else if (rotation == "ccw") Cv2.Rotate(src, src, RotateFlags.Rotate90Counterclockwise);
    else if (rotation == "180") Cv2.Rotate(src, src, RotateFlags.Rotate180);
    if (!args.Contains("--no-flip-x")) Cv2.Flip(src, src, FlipMode.Y);
    if (args.Contains("--flip-y")) Cv2.Flip(src, src, FlipMode.X);
    var dstPts = proj.Select(p => new Point2f((float)p!.Value.X, (float)p.Value.Y)).ToArray();
    var srcPts = new[] { new Point2f(0, 0), new Point2f(src.Width, 0),
        new Point2f(src.Width, src.Height), new Point2f(0, src.Height) };
    var m = Cv2.GetPerspectiveTransform(srcPts, dstPts);
    using var warped = new Mat();
    Cv2.WarpPerspective(src, warped, m, robot.Size());

    using var quad = new Mat(robot.Height, robot.Width, MatType.CV_8UC1, Scalar.Black);
    Cv2.FillConvexPoly(quad, dstPts.Select(p => new Point((int)p.X, (int)p.Y)).ToArray(), Scalar.White);

    var ch = Cv2.Split(robot);
    using var blue = new Mat();
    Cv2.Compare(ch[0], 18, blue, CmpType.GT);
    using var br = new Mat(); Cv2.Subtract(ch[0], ch[2], br);
    using var brMask = new Mat(); Cv2.Compare(br, 8, brMask, CmpType.GT);
    using var bg = new Mat(); Cv2.Subtract(ch[0], ch[1], bg);
    using var bgMask = new Mat(); Cv2.Compare(bg, 4, bgMask, CmpType.GT);
    using var blueMask = new Mat(); Cv2.BitwiseAnd(blue, brMask, blueMask);
    Cv2.BitwiseAnd(blueMask, bgMask, blueMask);
    using var mask = new Mat(); Cv2.BitwiseAnd(quad, blueMask, mask);
    warped.CopyTo(robot, mask);
    return robot;
}
Point2d? Project((double X, double Y, double Z) p, OrbitCamera cam, int w, int h)
{
    var eye = cam.Eye();
    var f = NormD(cam.TargetX - eye.X, cam.TargetY - eye.Y, cam.TargetZ - eye.Z);
    var r = NormT(CrossD(f, 0, 0, 1));
    var u = CrossT(r, f);
    var dx = p.X - eye.X; var dy = p.Y - eye.Y; var dz = p.Z - eye.Z;
    var cz = dx * f.X + dy * f.Y + dz * f.Z;
    if (cz <= 0.005) return null;
    var cx = dx * r.X + dy * r.Y + dz * r.Z;
    var cy = dx * u.X + dy * u.Y + dz * u.Z;
    var aspect = (double)w / h; var tanHalf = Math.Tan(cam.FovRad / 2);
    return new Point2d(cx / (cz * tanHalf * aspect) * (w / 2) + w / 2,
        h / 2 - cy / (cz * tanHalf) * (h / 2));
}
(double X, double Y, double Z) NormD(double x, double y, double z)
{
    var l = Math.Sqrt(x * x + y * y + z * z);
    return (x / l, y / l, z / l);
}
(double X, double Y, double Z) CrossD((double X, double Y, double Z) a, double bx, double by, double bz)
    => (a.Y * bz - a.Z * by, a.Z * bx - a.X * bz, a.X * by - a.Y * bx);
(double X, double Y, double Z) CrossT((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    => CrossD(a, b.X, b.Y, b.Z);
(double X, double Y, double Z) NormT((double X, double Y, double Z) v) => NormD(v.X, v.Y, v.Z);
(double X, double Y, double Z) Mul(SceneShape s, double x, double y, double z)
    => (s.R00 * x + s.R01 * y + s.R02 * z,
        s.R10 * x + s.R11 * y + s.R12 * z,
        s.R20 * x + s.R21 * y + s.R22 * z);
(double X, double Y, double Z)[] ScreenCorners(SceneShape s)
{
    (double X, double Y, double Z) At(double x, double y)
    {
        var d = Mul(s, x * s.HalfX, y * s.HalfY, 0);
        return (s.Px + d.X, s.Py + d.Y, s.Pz + d.Z);
    }
    return [At(1, -1), At(1, 1), At(-1, 1), At(-1, -1)];
}
List<string> DumpUiText()
{
    Adb("shell uiautomator dump /sdcard/window.xml");
    var local = Path.Combine(Path.GetTempPath(), "pinlivevideo_ui.xml");
    Adb($"pull /sdcard/window.xml \"{local}\"");
    if (!File.Exists(local)) return [];
    return XDocument.Load(local).Descendants("node")
        .Select(n => (string?)n.Attribute("text"))
        .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
}
Dictionary<char, (double Fx, double Fy)> FindKeys()
{
    var text = DumpUiText();
    var keys = new Dictionary<char, (double, double)>();
    Adb("shell uiautomator dump /sdcard/window.xml");
    var local = Path.Combine(Path.GetTempPath(), "pinlivevideo_ui2.xml");
    Adb($"pull /sdcard/window.xml \"{local}\"");
    if (!File.Exists(local)) return keys;
    foreach (var node in XDocument.Load(local).Descendants("node"))
    {
        var t = (string?)node.Attribute("text");
        var b = (string?)node.Attribute("bounds");
        if (string.IsNullOrWhiteSpace(t) || string.IsNullOrWhiteSpace(b)) continue;
        var m = Regex.Match(b, @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]");
        if (!m.Success) continue;
        var x = (int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[3].Value)) / 2.0 / W;
        var y = (int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[4].Value)) / 2.0 / H;
        if (t.Length == 1 && char.IsDigit(t[0])) keys[t[0]] = (x, y);
        else if (t.Equals("OK", StringComparison.OrdinalIgnoreCase)) keys['O'] = (x, y);
    }
    return keys;
}

if (oneFrame)
{
    sim.Simulator.RefreshKinematics();
    var scene = sim.Simulator.GetScene().ToList(); scene.Add(bench); scene.AddRange(grid);
    using var robot = RenderRobot(scene, camera);
    OverlayScreen(robot, camera, screenPng, screenShape);
    Cv2.ImEncode(".png", robot, out var encoded);
    File.WriteAllBytes(Path.Combine(outDir, "test_overlay.png"), encoded);
    Console.WriteLine("wrote test_overlay.png");
    return;
}

var keys = FindKeys();
if (keys.Count < 11)
    throw new InvalidOperationException("could not locate every key in the live app");
Console.WriteLine("keys: " + string.Join(", ",
    keys.OrderBy(k => k.Key).Select(k => $"{k.Key}=({k.Value.Fx:0.###},{k.Value.Fy:0.###})")));

var steps = new List<FrameStep>();
int frameNo = 0;
double lastCapture = -1;
var recording = false;
string label = "Ready — physical finger + real Android PinApp";
double? targetFx = null, targetFy = null;
string entered = "";
var accepted = false;

bool ReadAccepted(out string pin)
{
    pin = Adb("shell run-as com.tkleisas.pinapp cat files/pin.txt").Output.Trim();
    if (pin.Length == 0)
        pin = Regex.Match(Adb("logcat -d -s PinApp").Output, @"PIN_ACCEPTED=(\d+)").Groups[1].Value;
    return pin == expected;
}

void Capture()
{
    sim.Simulator.RefreshKinematics();
    var wideScene = sim.Simulator.GetScene().ToList();
    wideScene.Add(bench); wideScene.AddRange(grid);
    using var robot = RenderRobot(wideScene, camera);
    OverlayScreen(robot, camera, screenPng, screenShape);
    Cv2.ImEncode(".png", robot, out var robotPng);
    File.WriteAllBytes(Path.Combine(outDir, $"robot_{frameNo:00}.png"), robotPng);

    var closeScene = sim.Simulator.GetScene()
        .Where(x => x.Name.StartsWith("finger") || x.Name.StartsWith("y_nut"))
        .ToList();
    closeScene.Add(screenShape);
    var finger = sim.Simulator.GetBodyPosition("finger");
    var closeCam = new OrbitCamera
    {
        TargetX = finger[0], TargetY = finger[1] + 0.05, TargetZ = finger[2] - 0.02,
        Distance = 0.26, YawRad = 0.0, PitchRad = 0.12
    };
    using var close = RenderRobot(closeScene, closeCam);
    OverlayScreen(close, closeCam, screenPng, screenShape);
    Cv2.ImEncode(".png", close, out var closePng);
    File.WriteAllBytes(Path.Combine(outDir, $"closeup_{frameNo:00}.png"), closePng);

    File.WriteAllBytes(Path.Combine(outDir, $"screen_{frameNo:00}.png"), screenPng);
    steps.Add(new FrameStep(frameNo, label, targetFx, targetFy, entered, accepted,
        bridge.DispatchedTaps.Count));
    frameNo++;
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
sim.RunFor(0.4);

foreach (var digit in expected)
{
    var key = keys[digit];
    label = $"Tap {digit}";
    targetFx = key.Fx; targetFy = key.Fy;
    controller.Tap(key.Fx, key.Fy);
    targetFx = null; targetFy = null;
    entered += digit;
    label = $"Entered {entered}";
    Thread.Sleep(300);
    screenPng = adbTransport.Screencap();
    sim.RunFor(0.20);
}

label = "Tap OK";
targetFx = keys['O'].Fx; targetFy = keys['O'].Fy;
controller.Tap(keys['O'].Fx, keys['O'].Fy);
targetFx = null; targetFy = null;
Thread.Sleep(300);
screenPng = adbTransport.Screencap();
accepted = ReadAccepted(out var finalPin);
label = accepted ? $"APPROVED — PIN {finalPin}" : $"PIN {finalPin}";
sim.RunFor(0.45);
recording = false;

File.WriteAllText(Path.Combine(outDir, "steps.json"),
    System.Text.Json.JsonSerializer.Serialize(steps, new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true
    }));
Console.WriteLine($"frames={frameNo} pin={finalPin} accepted={accepted}");
if (!accepted) Environment.Exit(1);

public sealed record FrameStep(int Frame, string Label, double? Fx, double? Fy,
    string Pin, bool Accepted, int Taps);
