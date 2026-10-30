using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MechMaker.Hil;
using MechMaker.Server;

// Live E2E: physical finger on the DX8000 rig -> real Android emulator app.
// Assumes the emulator is running and PinApp is installed.

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var adb = Arg("--adb") ?? Environment.GetEnvironmentVariable("ANDROID_ADB")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Android", "Sdk", "platform-tools", "adb");
var serial = Arg("--serial") ?? Environment.GetEnvironmentVariable("ANDROID_SERIAL") ?? "emulator-5554";
var expected = Arg("--pin") ?? "1234";
var reset = !args.Contains("--no-reset");
var width = int.TryParse(Arg("--width"), out var w0) ? w0 : 720;
var height = int.TryParse(Arg("--height"), out var h0) ? h0 : 1440;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

(string Output, string Error, int Code) Adb(string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = adb,
        Arguments = $"-s {serial} {arguments}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {adb}");
    var output = p.StandardOutput.ReadToEnd();
    var error = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (output, error, p.ExitCode);
}

if (reset)
{
    Adb("shell am force-stop com.tkleisas.pinapp");
    Adb("shell run-as com.tkleisas.pinapp rm -f files/pin.txt");
    Adb("shell am start -n com.tkleisas.pinapp/.MainActivity");
    Thread.Sleep(1500);
}

var transport = new AdbEmulatorTransport(adb, serial, (width, height));
if (!transport.IsAlive)
    throw new InvalidOperationException($"adb cannot see {serial}");

var workspace = new McpWorkspace(Path.Combine(root, "catalog"));
Console.WriteLine(workspace.OpenMachine(Path.Combine(root, "examples", "dx8000_hil_rig.json")));
Console.WriteLine(workspace.HilConnect(transport));
Console.WriteLine(workspace.StartRun());
Console.WriteLine(workspace.ArmPhysicsTaps());

var keys = FindKeys(transport.Screencap(), out var source);
Console.WriteLine($"keys from {source}: " + string.Join(", ",
    keys.OrderBy(k => k.Key).Select(k => $"{k.Key}=({k.Value.Fx:0.###},{k.Value.Fy:0.###})")));
if (!new[] { '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', 'O' }.All(keys.ContainsKey))
    throw new InvalidOperationException("could not locate every key");

foreach (var digit in expected)
{
    var key = keys[digit];
    Console.WriteLine(workspace.TapAt(key.Fx, key.Fy));
}
Console.WriteLine(workspace.TapAt(keys['O'].Fx, keys['O'].Fy));

string? pin = null;
var file = Adb("shell run-as com.tkleisas.pinapp cat files/pin.txt").Output.Trim();
if (file.Length > 0)
    pin = file;
else
{
    var log = Adb("logcat -d -s PinApp").Output;
    pin = Regex.Match(log, @"PIN_ACCEPTED=(\d+)").Groups[1].Value;
}

Console.WriteLine($"app PIN: '{pin}' (expected '{expected}')");
if (pin != expected)
    Environment.Exit(1);

// Verify the visible APPROVED state through the UI tree (OCR is optional here).
var uiText = DumpUiText();
var approved = uiText.Any(t => t.Contains("APPROVED", StringComparison.OrdinalIgnoreCase));
Console.WriteLine(approved ? "screen shows APPROVED" : "APPROVED not found on screen");
if (!approved)
    Environment.Exit(2);

List<string> DumpUiText()
{
    var dump = "/sdcard/window.xml";
    var local = Path.Combine(Path.GetTempPath(), "pinlive_ui.xml");
    Adb($"shell uiautomator dump {dump}");
    Adb($"pull {dump} \"{local}\"");
    if (!File.Exists(local))
        return [];
    return XDocument.Load(local).Descendants("node")
        .Select(n => (string?)n.Attribute("text"))
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Select(t => t!)
        .ToList();
}

Dictionary<char, ScreenText> FindKeys(byte[] screen, out string used)
{
    var keys = new Dictionary<char, ScreenText>();
    used = "none";
    try
    {
        var words = TextDetector.ReadWords(screen, 30, "0123456789OKCLR");
        foreach (var word in words)
        {
            var text = word.Text.Trim();
            if (text.Length == 1 && char.IsDigit(text[0]))
                keys[text[0]] = word;
            else if (text.Equals("OK", StringComparison.OrdinalIgnoreCase))
                keys['O'] = word;
        }
        used = "OCR";
    }
    catch (Exception e)
    {
        Console.WriteLine($"OCR unavailable ({e.GetType().Name}); using uiautomator.");
    }

    if (keys.Count < 11)
    {
        var dump = "/sdcard/window.xml";
        var local = Path.Combine(Path.GetTempPath(), "pinlive_window.xml");
        Adb($"shell uiautomator dump {dump}");
        Adb($"pull {dump} \"{local}\"");
        if (File.Exists(local))
        {
            var doc = XDocument.Load(local);
            foreach (var node in doc.Descendants("node"))
            {
                var text = (string?)node.Attribute("text");
                var bounds = (string?)node.Attribute("bounds");
                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(bounds))
                    continue;
                var m = Regex.Match(bounds, @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]");
                if (!m.Success) continue;
                var x = (int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[3].Value)) / 2.0 / width;
                var y = (int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[4].Value)) / 2.0 / height;
                if (text.Length == 1 && char.IsDigit(text[0]))
                    keys.TryAdd(text[0], new ScreenText(text, x, y, 100));
                else if (text.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    keys.TryAdd('O', new ScreenText(text, x, y, 100));
            }
            used += "+uiautomator";
        }
    }
    return keys;
}
