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
    private readonly string _adbPath;
    private readonly string _serial;
    private readonly (int X, int Y) _resolution;

    public AdbEmulatorTransport(string adbPath, string serial, (int X, int Y) resolution)
    {
        _adbPath = adbPath;
        _serial = serial;
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

    public byte[] Screencap() => Run("shell screencap -p").Output;

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