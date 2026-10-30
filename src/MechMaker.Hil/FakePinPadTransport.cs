using OpenCvSharp;

namespace MechMaker.Hil;

/// <summary>
/// Deterministic emulated POS PIN app for the robot POC: a 720 x 1440 screen with
/// a "ENTER PIN" header, a masked PIN line and a 3 x 4 keypad (1..9, CLR, 0, OK).
/// Taps are interpreted in screen fractions so the same coordinates work with
/// <c>tap_at</c>; the accepted PIN is exposed as <see cref="EnteredPin"/>.
/// </summary>
public sealed class FakePinPadTransport : IEmulatorTransport
{
    public const int DefaultWidth = 720;
    public const int DefaultHeight = 1440;
    public const int PinLength = 4;

    private readonly object _gate = new();
    private readonly List<(double Fx, double Fy)> _taps = [];
    private readonly List<(double Fx1, double Fy1, double Fx2, double Fy2, int DurationMs)> _swipes = [];

    private static readonly double[] KeyXs = [0.20, 0.50, 0.80];
    private static readonly double[] KeyYs = [0.42, 0.56, 0.70, 0.84];

    public FakePinPadTransport(int width = DefaultWidth, int height = DefaultHeight)
    {
        Resolution = (width, height);
        Reset();
    }

    public (int X, int Y) Resolution { get; }

    public bool IsAlive => true;

    public string EnteredPin { get; private set; } = "";
    public bool Accepted { get; private set; }
    public string CurrentScreen => Accepted ? "approved" : "pin";

    public IReadOnlyList<(double Fx, double Fy)> Taps
    {
        get { lock (_gate) return [.. _taps]; }
    }

    public IReadOnlyList<(double Fx1, double Fy1, double Fx2, double Fy2, int DurationMs)> Swipes
    {
        get { lock (_gate) return [.. _swipes]; }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _taps.Clear();
            _swipes.Clear();
            EnteredPin = "";
            Accepted = false;
        }
    }

    public void Tap(double fx, double fy)
    {
        lock (_gate)
        {
            _taps.Add((fx, fy));
            if (Accepted)
                return;

            if (fx < 0.08 || fx > 0.92 || fy < 0.365 || fy > 0.895)
                return;
            var col = fx < 0.35 ? 0 : fx < 0.65 ? 1 : 2;
            var row = fy < 0.49 ? 0 : fy < 0.63 ? 1 : fy < 0.77 ? 2 : 3;

            if (row == 3)
            {
                if (col == 0)
                    EnteredPin = "";
                else if (col == 1)
                    Append('0');
                else if (EnteredPin.Length == PinLength)
                    Accepted = true;
                return;
            }

            Append((char)('1' + row * 3 + col));
        }
    }

    private void Append(char digit)
    {
        if (EnteredPin.Length < PinLength)
            EnteredPin += digit;
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
            using var screen = new Mat(Resolution.Y, Resolution.X, MatType.CV_8UC3,
                new Scalar(236, 240, 245));

            DrawCentred(screen, "ENTER PIN", 0.50, 0.10, 58, new Scalar(35, 35, 45));
            var masked = EnteredPin.Length == 0 ? "----" : new string('*', EnteredPin.Length);
            DrawCentred(screen, masked, 0.50, 0.245, 54, new Scalar(40, 70, 140));

            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 3; col++)
                {
                    var label = row == 3
                        ? col == 0 ? "CLR" : col == 1 ? "0" : "OK"
                        : ((char)('1' + row * 3 + col)).ToString();
                    DrawKey(screen, label, KeyXs[col], KeyYs[row], row == 3 ? 42 : 66);
                }
            }

            if (Accepted)
                DrawCentred(screen, "APPROVED", 0.50, 0.95, 42, new Scalar(30, 120, 50));

            Cv2.ImEncode(".png", screen, out var png);
            return png.ToArray();
        }
    }


    /// <summary>
    /// An independent template of one key, rendered with the same fill, font and
    /// size as the app. The POC matches these on the live screencap to locate the
    /// keys, then taps the measured centres.
    /// </summary>
    public byte[] KeyTemplate(string label)
    {
        var halfW = (int)(Resolution.X * 0.12);
        var halfH = (int)(Resolution.Y * 0.055);
        using var key = new Mat(halfH * 2, halfW * 2, MatType.CV_8UC3,
            new Scalar(218, 225, 235));
        var heightPx = label.Length == 1 && label[0] is >= '1' and <= '9' ? 66.0 : 42.0;
        var scale = heightPx / 32.0;
        var thickness = Math.Max(2, (int)(heightPx / 18));
        var size = Cv2.GetTextSize(label, HersheyFonts.HersheyDuplex, scale, thickness, out _);
        var org = new Point(halfW - size.Width / 2, halfH + size.Height / 2);
        Cv2.PutText(key, label, org, HersheyFonts.HersheyDuplex, scale, new Scalar(25, 30, 45),
            thickness, LineTypes.AntiAlias);
        Cv2.ImEncode(".png", key, out var png);
        return png.ToArray();
    }

    private void DrawKey(Mat screen, string label, double fx, double fy, double labelHeightPx)
    {
        var cx = (int)Math.Round(fx * (Resolution.X - 1));
        var cy = (int)Math.Round(fy * (Resolution.Y - 1));
        var halfW = (int)(Resolution.X * 0.12);
        var halfH = (int)(Resolution.Y * 0.055);
        var rect = new Rect(cx - halfW, cy - halfH, halfW * 2, halfH * 2);
        Cv2.Rectangle(screen, rect, new Scalar(218, 225, 235), -1);
        DrawCentred(screen, label, fx, fy, labelHeightPx, new Scalar(25, 30, 45));
    }

    private void DrawCentred(Mat screen, string text, double fx, double fy, double heightPx, Scalar colour)
    {
        var scale = heightPx / 32.0;
        var thickness = Math.Max(2, (int)(heightPx / 18));
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheyDuplex, scale, thickness, out _);
        var org = new Point(
            (int)Math.Round(fx * (Resolution.X - 1) - size.Width / 2.0),
            (int)Math.Round(fy * (Resolution.Y - 1) + size.Height / 2.0));
        Cv2.PutText(screen, text, org, HersheyFonts.HersheyDuplex, scale, colour, thickness,
            LineTypes.AntiAlias);
    }
}
