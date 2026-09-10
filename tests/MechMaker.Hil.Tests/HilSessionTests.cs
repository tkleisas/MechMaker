using MechMaker.Core;
using MechMaker.Core.Model;
using MechMaker.Hil;
using OpenCvSharp;

namespace MechMaker.Hil.Tests;

/// <summary>
/// The HIL seam: fraction-coordinate touches routed to the emulator, and real
/// OpenCV template matching over fake-transport screencaps — no adb, no emulator.
/// </summary>
public class HilSessionTests
{
    private readonly FakeEmulatorTransport _emulator = new();
    private readonly MachineDefinition _machine = new()
    {
        Name = "hil_test",
        Parts =
        [
            new PartInstance { Id = "phone", Part = "android_phone" }
        ]
    };
    private readonly PartCatalog _catalog = CatalogWithPhone();

    private static PartCatalog CatalogWithPhone()
    {
        TestCatalogDirectory = FindCatalogDirectory();
        return PartCatalog.LoadFromDirectory(TestCatalogDirectory);
    }

    private static string TestCatalogDirectory;

    private static string FindCatalogDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalog")))
            dir = dir.Parent!;
        return dir is null ? AppContext.BaseDirectory : Path.Combine(dir.FullName, "catalog");
    }

[Fact]
    public void Connect_pairs_the_transport_with_the_phone_params()
    {
        var session = HilSession.Connect(_emulator, _machine, _catalog);

        Assert.Equal("phone", session.PhoneInstanceId);
        Assert.Equal(0.068, session.ScreenWidthM, 6);
        Assert.Equal(0.144, session.ScreenHeightM, 6);
        Assert.Equal((540, 1170), _emulator.Resolution);
    }

    [Fact]
    public void Connect_without_a_phone_part_tells_you_to_place_one()
    {
        var empty = new MachineDefinition { Name = "x" };
        Assert.Throws<InvalidOperationException>(
            () => HilSession.Connect(_emulator, empty, _catalog));
    }

    [Fact]
    public void Touch_tap_reaches_the_emulator_in_fractions()
    {
        var session = HilSession.Connect(_emulator, _machine, _catalog);
        session.Tap(0.5, 0.9);

        var tap = Assert.Single(_emulator.Taps);
        Assert.Equal(0.5, tap.Fx, 9);
        Assert.Equal(0.9, tap.Fy, 9);
    }

    [Fact]
    public void Swipe_reaches_the_emulator()
    {
        var session = HilSession.Connect(_emulator, _machine, _catalog);
        session.Swipe(0.5, 0.9, 0.5, 0.2, 300);

        var swipe = Assert.Single(_emulator.Swipes);
        Assert.Equal((0.5, 0.9, 0.5, 0.2, 300), swipe);
    }

    [Fact]
    public void Template_matching_finds_a_launcher_icon_at_its_fraction()
    {
        _emulator.AddApp("settings", 0.3, 0.25);
        var screen = _emulator.Screencap();
        var template = ScreenDetector.ExtractTemplate(screen, 0.3, 0.25, halfSizePx: 40);

        var element = ScreenDetector.Find("settings_icon", screen, template);

        Assert.NotNull(element);
        Assert.True(element.Confidence > 0.95);
        Assert.InRange(element.Fx, 0.28, 0.32);
        Assert.InRange(element.Fy, 0.23, 0.27);
    }

    [Fact]
    public void Find_returns_null_when_the_element_is_absent()
    {
        var session = HilSession.Connect(_emulator, _machine, _catalog);
        _emulator.AddApp("settings", 0.3, 0.25);
        var screen = _emulator.Screencap();
        var template = ScreenDetector.ExtractTemplate(screen, 0.3, 0.25, 40);
        _emulator.Home(); // app gone

        Assert.Null(session.Find("settings_icon", template));
    }

    [Fact]
    public void Find_locates_the_app_on_the_current_screen()
    {
        var session = HilSession.Connect(_emulator, _machine, _catalog);
        _emulator.AddApp("settings", 0.5, 0.8);
        var template = ScreenDetector.ExtractTemplate(_emulator.Screencap(), 0.5, 0.8, 40);

        session.Tap(0.5, 0.8);
        Assert.Equal("settings", _emulator.CurrentScreen); // the fake opened the app

        var element = session.Find("settings", template);
        Assert.NotNull(element);
        Assert.True(element.Confidence > 0.95);
        Assert.InRange(element.Fx, 0.45, 0.55);
        Assert.InRange(element.Fy, 0.75, 0.85);
    }

    [Fact]
    public void Fraction_maps_to_phone_local_millimetres()
    {
        var session = HilSession.Connect(_emulator, _machine, _catalog);

        Assert.Equal(34.0, session.FractionToMmX(0.5) * 1000, 6);
        Assert.Equal(144.0, session.FractionToMmY(1.0) * 1000, 6);
    }
}

