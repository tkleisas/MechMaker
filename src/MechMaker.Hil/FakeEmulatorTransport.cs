using OpenCvSharp;

namespace MechMaker.Hil;

/// <summary>
/// A scriptable fake emulator for tests and offline development: it renders
/// synthetic screens (a light background with a home bar plus the "apps" the test
/// has placed) and records every tap/swipe it receives. Tapping an app switches
/// the current screen to that app's name — enough app flow to drive wait_for.
/// </summary>
public sealed class FakeEmulatorTransport : IEmulatorTransport
{
    public const int DefaultWidth = 540;
    public const int DefaultHeight = 1170;

    private readonly object _gate = new();
    private readonly List<(double Fx, double Fy)> _taps = [];
    private readonly List<(double Fx1, double Fy1, double Fx2, double Fy2, int DurationMs)> _swipes = [];
    private readonly List<(string Name, double Fx, double Fy, int HalfPx)> _apps = [];
    private readonly List<(string Text, double Fx, double Fy, double HeightPx)> _texts = [];

    public FakeEmulatorTransport(int width = DefaultWidth, int height = DefaultHeight)
    {
        Resolution = (width, height);
        Home();
    }

    public (int X, int Y) Resolution { get; }

    public bool IsAlive => true;

    /// <summary>Taps received, in order (screen fractions).</summary>
    public IReadOnlyList<(double Fx, double Fy)> Taps
    {
        get { lock (_gate) return [.. _taps]; }
    }

    public IReadOnlyList<(double Fx1, double Fy1, double Fx2, double Fy2, int DurationMs)> Swipes
    {
        get { lock (_gate) return [.. _swipes]; }
    }

    /// <summary>The current screen id — tests switch it to simulate app flow.</summary>
    public string CurrentScreen { get; set; } = "home";

    /// <summary>Resets the screen to the bare launcher and clears input history.</summary>
    public void Home()
    {
        lock (_gate)
        {
            _taps.Clear();
            _swipes.Clear();
            _apps.Clear();
            _texts.Clear();
            CurrentScreen = "home";
        }
    }

    /// <summary>Places a rectangular app icon on the launcher (screen fractions).</summary>
    public void AddApp(string name, double fxCentre, double fyCentre, int halfSizePx = 60)
    {
        lock (_gate)
            _apps.Add((name, fxCentre, fyCentre, halfSizePx));
    }

    /// <summary>Draws a text label on the current screen (fractions) — the OCR tests'
    /// fixture. Returns to the home bar rendering underneath.</summary>
    public void DrawText(string text, double fxCentre, double fyCentre, double heightPx = 48)
    {
        lock (_gate)
            _texts.Add((text, fxCentre, fyCentre, heightPx));
    }

    public void Tap(double fx, double fy)
    {
        lock (_gate)
        {
            _taps.Add((fx, fy));
            // The fake launcher: tapping an app opens a screen named after it.
            foreach (var (name, ax, ay, half) in _apps)
            {
                if (Math.Abs(fx - ax) * Resolution.X <= half &&
                    Math.Abs(fy - ay) * Resolution.Y <= half)
                {
                    CurrentScreen = name;
                    return;
                }
            }
        }
    }

    public void Swipe(double fx1, double fy1, double fx2, double fy2, int durationMs)
    {
        lock (_gate)
            _swipes.Add((fx1, fy1, fx2, fy2, durationMs));
    }

    public byte[] Screencap()
    {
        lock (_gate)
        {
            using var screen = new Mat(Resolution.Y, Resolution.X, MatType.CV_8UC3, new Scalar(235, 240, 245));
            // Home bar.
            Cv2.Rectangle(screen,
                new Rect(Resolution.X / 4, Resolution.Y - 12, Resolution.X / 2, 8),
                new Scalar(60, 60, 70), -1);
foreach (var (_, fx, fy, half) in _apps)
            {
                var cx = (int)Math.Round(fx * (Resolution.X - 1));
                var cy = (int)Math.Round(fy * (Resolution.Y - 1));
                // Icon: fill + border + inner glyph — non-uniform structure so
                // normalized cross-correlation has variance to lock onto.
                Cv2.Rectangle(screen, new Rect(cx - half, cy - half, half * 2, half * 2),
                    new Scalar(50, 110, 220), -1);
                Cv2.Rectangle(screen, new Rect(cx - half, cy - half, half * 2, half * 2),
                    new Scalar(30, 60, 130), 4);
                Cv2.Rectangle(screen, new Rect(cx - half / 3, cy - half / 3, half * 2 / 3, half * 2 / 3),
                    new Scalar(240, 240, 250), -1);
            }
            foreach (var (text, fx, fy, heightPx) in _texts)
            {
                // Dark text on the light background, centred on the fraction —
                // Tesseract's bread and butter.
                var scale = heightPx / 22.0;
                var thickness = Math.Max(2, (int)(heightPx / 24));
                var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, scale, thickness, out _);
                var org = new Point(
                    (int)Math.Round(fx * (Resolution.X - 1) - size.Width / 2.0),
                    (int)Math.Round(fy * (Resolution.Y - 1) + size.Height / 2.0));
                Cv2.PutText(screen, text, org, HersheyFonts.HersheySimplex,
                    scale, new Scalar(30, 30, 40), thickness, LineTypes.AntiAlias);
            }
            Cv2.ImEncode(".png", screen, out var png);
            return png.ToArray();
        }
    }
}
