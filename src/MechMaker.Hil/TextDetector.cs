using OpenCvSharp;
using Tesseract;

namespace MechMaker.Hil;

/// <summary>
/// A text element found on a screen: the matched string, its centre as a screen
/// fraction (androidtester convention), and the recognition confidence.
/// </summary>
public sealed record ScreenText(string Text, double Fx, double Fy, double Confidence)
{
    public override string ToString() =>
        $"'{Text}' at ({Fx * 100:0.#}%, {Fy * 100:0.#}%), confidence {Confidence:0.##}";
}

/// <summary>
/// The OCR side of the HIL seam (androidtester's pluggable OCR): Tesseract over
/// screencaps with OpenCV preprocessing (upscale + grayscale + Otsu threshold —
/// phone text is small and low-contrast). Returns word boxes as screen fractions
/// so `tap_at`/`touch_tap` can act on what the screen says.
/// Needs the fast English model: tools/fetch_tessdata.ps1 (tessdata/eng.traineddata).
/// </summary>
public static class TextDetector
{
    /// <summary>Repo-root tessdata directory, overridable with MECHMAKER_TESSDATA.
    /// Walks up from the working directory / app base (the repo root has tessdata/).</summary>
    public static string TessdataRoot { get; set; } = FindTessdataRoot();

    private static string FindTessdataRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MECHMAKER_TESSDATA");
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tessdata", "eng.traineddata")))
                    return Path.Combine(dir.FullName, "tessdata");
                dir = dir.Parent!;
            }
        }
        // Default: repo-root tessdata (the OCR call fails with a clear error until
        // tools/fetch_tessdata.ps1 downloads the model).
        return Path.Combine(Directory.GetCurrentDirectory(), "tessdata");
    }

    /// <summary>Whether the OCR engine is usable (model downloaded).</summary>
    public static bool IsAvailable => File.Exists(Path.Combine(TessdataRoot, "eng.traineddata"));

    /// <summary>
    /// Recognises all text on a screencap, returning one entry per word above the
    /// confidence floor (words merged per line by Tesseract's layout analysis).
    /// </summary>
    public static IReadOnlyList<ScreenText> ReadScreen(byte[] screenPng, double minConfidence = 60)
    {
        using var engine = new TesseractEngine(TessdataRoot, "eng", EngineMode.Default);
        // Phone text is small and low-contrast: upscale + binarize first.
        using var pix = Pix.LoadFromMemory(Preprocess(screenPng));
        using var page = engine.Process(pix);
        var results = new List<ScreenText>();
        using var iterator = page.GetIterator();
        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine);
            var confidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
            if (string.IsNullOrWhiteSpace(text) || confidence < minConfidence)
                continue;

            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect))
                continue;

            // Centre of the line's bounding box → screen fractions.
            var fx = (pix.Width > 0) ? (rect.X1 + rect.Width / 2.0) / pix.Width : 0;
            var fy = (pix.Height > 0) ? (rect.Y1 + rect.Height / 2.0) / pix.Height : 0;
            results.Add(new ScreenText(text.Trim(), fx, fy, confidence));
        }
        while (iterator.Next(PageIteratorLevel.TextLine));
        return results;
    }

    /// <summary>
    /// Finds a line containing the expected text (case-insensitive) —
    /// androidtester's `screen.wait_for {text: ...}`. Returns the best match
    /// (highest confidence), or null when absent.
    /// </summary>
    public static ScreenText? FindText(string expected, byte[] screenPng, double minConfidence = 60)
    {
        var expected_ = expected.Trim();
        return ReadScreen(screenPng, minConfidence)
            .Where(t => t.Text.Contains(expected_, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Confidence)
            .FirstOrDefault();
    }

    /// <summary>OpenCV preprocessing helper exposed for tests: upscale 2x + grayscale
    /// + Otsu (small phone text needs the help).</summary>
    public static byte[] Preprocess(byte[] screenPng)
    {
        using var src = Cv2.ImDecode(screenPng, ImreadModes.Color);
        if (src.Empty())
            throw new InvalidDataException("Screencap is not a decodable image.");
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Resize(gray, gray, new Size(src.Width * 2, src.Height * 2), 0, 0, InterpolationFlags.Cubic);
        using var bin = new Mat();
        Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
        Cv2.ImEncode(".png", bin, out var png);
        return png.ToArray();
    }
}