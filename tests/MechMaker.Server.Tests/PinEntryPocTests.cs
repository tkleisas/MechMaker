using MechMaker.Hil;
using Xunit;

namespace MechMaker.Server.Tests;

/// <summary>
/// End-to-end POC: the physical finger on the DX8000 HIL gantry enters a PIN in
/// the emulated POS app. Vision locates each keypad word, tap_at physically
/// presses it, and the emulator reports the accepted PIN.
/// </summary>
public sealed class PinEntryPocTests : IDisposable
{
    private readonly McpWorkspace _w = TestRepo.NewWorkspace();

    public void Dispose() => _w.Dispose();

    [Fact]
    public void Finger_enters_a_pin_on_the_emulated_dx8000()
    {
        _w.OpenMachine(Path.Combine(TestRepo.Root(), "examples", "dx8000_hil_rig.json"));
        var app = new FakePinPadTransport();
        Assert.Contains("device 'dut'", _w.HilConnect(app));
        _w.StartRun();
        _w.ArmPhysicsTaps();

        var log = new List<string>();
        // Template matching: one independent key template per label, searched on
        // the live screencap. The measured match centres feed tap_at.
        var templates = new Dictionary<char, byte[]>();
        foreach (var digit in "0123456789")
            templates[digit] = app.KeyTemplate(digit.ToString());
        var okTemplate = app.KeyTemplate("OK");

        foreach (var digit in "1234")
        {
            var match = ScreenDetector.Find(digit.ToString(), app.Screencap(), templates[digit]);
            Assert.NotNull(match);
            var result = _w.TapAt(match!.Fx, match.Fy);
            log.Add($"{digit}: match=({match.Fx:0.###},{match.Fy:0.###}) {result} " +
                    $"status={_w.HilStatus()} entered={app.EnteredPin}");
            Assert.Contains("Physical tap dispatched", result);
        }

        var ok = ScreenDetector.Find("OK", app.Screencap(), okTemplate);
        Assert.NotNull(ok);
        var accept = _w.TapAt(ok!.Fx, ok.Fy);
        Assert.Contains("Physical tap dispatched", accept);
        Assert.True(app.EnteredPin == "1234",
            "PIN entry failed. " + string.Join(" || ", log) + $" final={app.EnteredPin}");
    }
}
