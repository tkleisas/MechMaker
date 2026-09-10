using MechMaker.Hil;

namespace MechMaker.Hil.Tests;

/// <summary>
/// OCR over fake-transport screencaps: Tesseract reading text the fake emulator
/// renders, with fraction positions — androidtester's `screen.wait_for {text}`.
/// Requires tessdata/eng.traineddata (tools/fetch_tessdata.ps1); skips otherwise.
/// </summary>
public class TextDetectorTests
{
    private readonly FakeEmulatorTransport _emulator = new();

    private static void RequireOcr()
    {
        if (!TextDetector.IsAvailable)
            throw new Exception("skip: tessdata not fetched");
    }

    [Fact]
    public void Ocr_reads_a_label_at_its_fraction()
    {
        if (!TextDetector.IsAvailable) return;
        _emulator.DrawText("SETTINGS", 0.5, 0.3, heightPx: 90);
        var screen = _emulator.Screencap();

        var found = TextDetector.FindText("settings", screen);

        Assert.NotNull(found);
        Assert.InRange(found.Fx, 0.4, 0.6);
        Assert.InRange(found.Fy, 0.2, 0.4);
        Assert.True(found.Confidence > 60);
    }

    [Fact]
    public void Ocr_distinguishes_lines()
    {
        _emulator.DrawText("SETTINGS", 0.5, 0.2, heightPx: 80);
        _emulator.DrawText("DISPLAY", 0.5, 0.5, heightPx: 80);
        var screen = _emulator.Screencap();

        var display = TextDetector.FindText("display", screen);
        var settings = TextDetector.FindText("settings", screen);

        Assert.NotNull(display);
        Assert.NotNull(settings);
        Assert.InRange(display.Fy, 0.4, 0.6);
        Assert.InRange(settings.Fy, 0.1, 0.3);
        Assert.True(display.Fy > settings.Fy, "DISPLAY should be below SETTINGS");
    }

    [Fact]
    public void Ocr_returns_null_when_the_text_is_absent()
    {
        _emulator.DrawText("BATTERY", 0.5, 0.3, heightPx: 80);
        var screen = _emulator.Screencap();

        Assert.Null(TextDetector.FindText("aeroplane_mode", screen));
    }

    [Fact]
    public void Read_screen_lists_multiple_lines()
    {
        _emulator.DrawText("WELCOME", 0.5, 0.2, heightPx: 80);
        _emulator.DrawText("SETTINGS", 0.5, 0.5, heightPx: 80);
        _emulator.DrawText("ABOUT", 0.5, 0.8, heightPx: 80);
        var screen = _emulator.Screencap();

        var lines = TextDetector.ReadScreen(screen);

        Assert.True(lines.Count >= 3, $"expected >=3 lines, got {lines.Count}: {string.Join("; ", lines)}");
    }
}
