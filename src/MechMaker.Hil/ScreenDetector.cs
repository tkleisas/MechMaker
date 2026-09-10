using OpenCvSharp;

namespace MechMaker.Hil;

/// <summary>
/// A detected screen element, in screen fractions (androidtester convention) and,
/// for convenience, in the phone screen's physical millimetres.
/// </summary>
public sealed record ScreenElement(string Name, double Fx, double Fy, double Confidence)
{
    public override string ToString() =>
        $"{Name} at ({Fx * 100:0.#}%, {Fy * 100:0.#}%), confidence {Confidence:0.###}";
}

/// <summary>
/// The vision side of the seam — the camera/OCR stand-in. Template matching over
/// screencaps (OpenCV CCOEFF_NORMED), returning the element's centre as screen
/// fractions: the same coordinates touch actions consume. Later: OCR via the
/// pluggable detector interface this class seeds.
/// </summary>
public static class ScreenDetector
{
    public const double DefaultThreshold = 0.8;

    /// <summary>
    /// Finds a template image on a screencap. Returns the best match above the
    /// threshold, or null. Both images are PNG bytes (transport.Screencap output).
    /// </summary>
    public static ScreenElement? Find(string name, byte[] screenPng, byte[] templatePng,
        double threshold = DefaultThreshold)
    {
        using var screen = Cv2.ImDecode(screenPng, ImreadModes.Color);
        using var template = Cv2.ImDecode(templatePng, ImreadModes.Color);
        if (screen.Empty() || template.Empty())
            throw new InvalidDataException("Screencap or template is not a decodable image.");
        if (template.Width > screen.Width || template.Height > screen.Height)
            return null;

        using var result = new Mat();
        Cv2.MatchTemplate(screen, template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);
        if (maxVal < threshold)
            return null;

        // Match centre → fraction. (Androidtester rectifies and matches sub-features
        // for sub-pixel accuracy; centre-of-best-match is the right fidelity here.)
        var fx = (maxLoc.X + template.Width / 2.0) / screen.Width;
        var fy = (maxLoc.Y + template.Height / 2.0) / screen.Height;
        return new ScreenElement("match", fx, fy, maxVal);
    }

    /// <summary>Extracts a template from a screencap: the rectangle around a screen
    /// fraction (e.g. an icon at [0.5, 0.85] with a given pixel half-size).</summary>
    public static byte[] ExtractTemplate(byte[] screenPng, double fx, double fy, int halfSizePx)
    {
        using var screen = Cv2.ImDecode(screenPng, ImreadModes.Color);
        if (screen.Empty())
            throw new InvalidDataException("Screencap is not a decodable image.");
        var cx = (int)Math.Round(fx * (screen.Width - 1));
        var cy = (int)Math.Round(fy * (screen.Height - 1));
        var x = Math.Clamp(cx - halfSizePx, 0, screen.Width - 1);
        var y = Math.Clamp(cy - halfSizePx, 0, screen.Height - 1);
        var width = Math.Min(2 * halfSizePx, screen.Width - x);
        var height = Math.Clamp(cy + halfSizePx, 0, screen.Height - 1) - y;
        using var region = new Mat(screen, new Rect(x, y, width, height));
        Cv2.ImEncode(".png", region, out var png);
        return png.ToArray();
    }
}