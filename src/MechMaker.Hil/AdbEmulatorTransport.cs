using System.Diagnostics;

namespace MechMaker.Hil;

/// <summary>
/// The seam between the simulated machine's phone and an Android device. All
/// coordinates are screen fractions [0..1]² (androidtester convention: origin
/// top-left, x right, y down) so scenarios and vision results are resolution- and
/// size-independent. Transports: the live adb path and a fake emulator for tests.
/// </summary>
public interface IEmulatorTransport
{
    /// <summary>Emulator display resolution in pixels (for diagnostics).</summary>
    (int X, int Y) Resolution { get; }

    /// <summary>A single tap at the given screen fraction.</summary>
    void Tap(double fx, double fy);

    /// <summary>A swipe from one screen fraction to another, over durationMs.</summary>
    void Swipe(double fx1, double fy1, double fx2, double fy2, int durationMs);

    /// <summary>Captures the current screen as PNG bytes.</summary>
    byte[] Screencap();

    /// <summary>Whether the emulator session is reachable.</summary>
    bool IsAlive { get; }
}

/// <summary>
/// The live transport: shells out to the adb CLI (Android platform-tools). Built for
/// a running emulator (`adb devices` lists it as emulator-5554 by default); commands
/// target the serial so multiple devices coexist. Not exercised in unit tests — the
/// fake transport covers the seam's logic.
/// </summary>
public sealed class AdbEmulatorTransport : IEmulatorTransport
{
    /// <summary>The default adb path: ANDROID_ADB env, then the standard SDK location.</summary>
    public static string DefaultAdbPath =>
        Environment.GetEnvironmentVariable("ANDROID_ADB")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");

    /// <summary>The default emulator serial: ANDROID_SERIAL env, else the first emulator.</summary>
    public static string DefaultSerial =>
        Environment.GetEnvironmentVariable("ANDROID_SERIAL") ?? "emulator-5554";

    private readonly string _adbPath;
    private readonly string _serial;
    private readonly (int X, int Y) _resolution;

    public AdbEmulatorTransport(string? adbPath = null, string? serial = null,
        (int X, int Y) resolution = default)
    {
        _adbPath = adbPath ?? DefaultAdbPath;
        _serial = serial ?? DefaultSerial;
        _resolution = resolution;
    }

    public (int X, int Y) Resolution => _resolution;

    public bool IsAlive
    {
        get
        {
            var (_, error, code) = Run("devices");
            return code == 0 && error.Length == 0;
        }
    }

    public void Tap(double fx, double fy)
    {
        var (x, y) = Pixels(fx, fy);
        Run($"shell input tap {x} {y}");
    }

    public void Swipe(double fx1, double fy1, double fx2, double fy2, int durationMs) =>
        Run($"shell input swipe {Pixels(fx1, fy1).Item1} {Pixels(fx1, fy1).Item2} " +
            $"{Pixels(fx2, fy2).Item1} {Pixels(fx2, fy2).Item2} {durationMs}");

    public byte[] Screencap()
    {
        // adb's exec-out corrupts binary on Windows (CRLF translation in the console
        // layer). The robust path: screencap to a device file, adb pull to a temp
        // file, clean up — file transfer is byte-exact.
        var devicePath = "/data/local/tmp/mechmaker_screencap.png";
        var localPath = Path.Combine(Path.GetTempPath(), $"mechmaker_cap_{Guid.NewGuid():N}.png");
        try
        {
            Run($"shell screencap -p {devicePath}");
            Run($"pull {devicePath} \"{localPath}\"");
            var png = File.ReadAllBytes(localPath);
            try { Run($"shell rm {devicePath}"); } catch { /* best effort */ }
            return png;
        }
        finally
        {
            try { File.Delete(localPath); } catch (IOException) { }
        }
    }

    private (int X, int Y) Pixels(double fx, double fy) =>
        (Math.Clamp((int)Math.Round(fx * (_resolution.X - 1)), 0, _resolution.X - 1),
         Math.Clamp((int)Math.Round(fy * (_resolution.Y - 1)), 0, _resolution.Y - 1));

    private (byte[] Output, string Error, int Code) Run(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = $"-s {_serial} {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start adb at '{_adbPath}'.");
        var output = ReadAllBytes(process.StandardOutput.BaseStream);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output, error, process.ExitCode);
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}