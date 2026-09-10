using MechMaker.Hil;

namespace MechMaker.Server.Tests;

/// <summary>
/// The HIL seam through the workspace: an agent pairs the machine's phone with an
/// emulator, captures templates, finds elements with real OpenCV matching, and taps
/// — the androidtester action loop, tool by tool.
/// </summary>
public class HilToolsTests : IDisposable
{
    private readonly FakeEmulatorTransport _emulator = new();
    private readonly McpWorkspace _w = TestRepo.ExampleWorkspace();
    private readonly List<string> _tempFiles = [];

    public HilToolsTests()
    {
        _w.NewMachine("hil");
        _w.AddPart("android_phone", "dut");
    }

    public void Dispose()
    {
        _w.Dispose();
        foreach (var file in _tempFiles.Where(File.Exists))
            File.Delete(file);
    }

    private string TempPath(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}_{name}");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Connect_requires_a_phone_in_the_machine()
    {
        var workspace = TestRepo.NewWorkspace(); // no phone
        try
        {
            Assert.Contains("No android_phone", Assert.Throws<InvalidOperationException>(
                () => workspace.HilConnect("fake")).Message);
        }
        finally
        {
            workspace.Dispose();
        }
    }

    [Fact]
    public void Connect_pairs_the_phone()
    {
        var result = _w.HilConnect(_emulator);
        Assert.Contains("phone 'dut'", result);
        Assert.Contains("68 x 144 mm", result);
        Assert.Contains("540x1170 px", result);
    }

    [Fact]
    public void The_phone_action_loop_tap_find_screencap()
    {
        Assert.Contains("phone 'dut'", _w.HilConnect(_emulator));

        // 1. Screen state: a settings icon on the launcher.
        _emulator.AddApp("settings", 0.5, 0.8);

        // 2. Screencap and cut a template (the screen_extract_template flow).
        var screencapPath = TempPath("screen.png");
        Assert.Contains("bytes ->", _w.ScreenScreencap(screencapPath));
        var templatePath = TempPath("icon.png");
        var template = ScreenDetector.ExtractTemplate(File.ReadAllBytes(screencapPath), 0.5, 0.8, 40);
        File.WriteAllBytes(templatePath, template);

        // 3. Vision finds the icon (real OpenCV matching).
        var found = _w.ScreenFind("settings", templatePath);
        Assert.Contains("settings at (50%", found);
        Assert.True(double.Parse(Confidence(found)) > 0.9);

        // 4. Tap where vision said, and the emulator registers it.
        _w.TouchTap(0.5, 0.8);
        Assert.Equal("settings", _emulator.CurrentScreen);
        var tap = Assert.Single(_emulator.Taps);
        Assert.Equal(0.5, tap.Fx, 9);
    }

    private static string Confidence(string foundLine)
    {
        var match = System.Text.RegularExpressions.Regex.Match(foundLine, @"confidence ([\d.]+)");
        return match.Groups[1].Value;
    }

    [Fact]
    public void Wait_for_reports_absent_elements_cleanly()
    {
        _w.HilConnect(_emulator);
        var templatePath = MissingTemplate();
        Assert.Contains("NOT found", _w.ScreenWaitFor("ghost", templatePath, timeoutS: 0.2, pollS: 0.05));
    }

    private string MissingTemplate()
    {
        var path = TempPath("missing.png");
        // A dark square with a white border and grey core: structured (so matching is
        // well-posed) but never present on the fake's light launcher.
        using var mat = new OpenCvSharp.Mat(40, 40, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(20, 20, 20));
        OpenCvSharp.Cv2.Rectangle(mat, new OpenCvSharp.Rect(0, 0, 40, 40), new OpenCvSharp.Scalar(255, 255, 255), 4);
        OpenCvSharp.Cv2.Rectangle(mat, new OpenCvSharp.Rect(12, 12, 16, 16), new OpenCvSharp.Scalar(150, 150, 160), -1);
        OpenCvSharp.Cv2.ImEncode(".png", mat, out var png);
        File.WriteAllBytes(path, png.ToArray());
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }

    [Fact]
    public void Physics_taps_dispatch_when_the_finger_presses_the_glass()
    {
        _w.OpenMachine(Path.Combine(TestRepo.Root(), "examples", "phone_test_rig.json"));
        Assert.Contains("phone 'dut'", _w.HilConnect(_emulator));
        _w.StartRun();
        Assert.Contains("armed", _w.ArmPhysicsTaps());

        _w.RunFor(0.05);
        Assert.Contains("physics taps dispatched", _w.HilStatus());
    }
}