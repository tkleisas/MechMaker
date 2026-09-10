using MechMaker.Core;
using MechMaker.Core.Model;

namespace MechMaker.Hil;

/// <summary>
/// The live HIL session behind the MCP tools: the emulator transport paired with
/// the machine's phone part. Touch actions take screen fractions (androidtester
/// convention); detection returns screen elements in fractions plus phone-local
/// millimetres so results map back onto the physics phone.
/// </summary>
public sealed class HilSession
{
    public IEmulatorTransport Transport { get; }
    public string PhoneInstanceId { get; }
    public double ScreenWidthM { get; }
    public double ScreenHeightM { get; }

    private HilSession(IEmulatorTransport transport, string phoneInstanceId,
        double screenWidthM, double screenHeightM)
    {
        Transport = transport;
        PhoneInstanceId = phoneInstanceId;
        ScreenWidthM = screenWidthM;
        ScreenHeightM = screenHeightM;
    }

    /// <summary>
    /// Pairs a transport with the machine's phone part: its screen params come from
    /// the catalog definition (screen_width_m, screen_height_m, resolution). Pass a
    /// phone instance id when the machine places several.
    /// </summary>
    public static HilSession Connect(IEmulatorTransport transport, MachineDefinition machine,
        PartCatalog catalog, string? phoneInstanceId = null)
    {
        var phone = phoneInstanceId is null
            ? machine.Parts.FirstOrDefault(p => catalog.Find(p.Part)?.Id == "android_phone")
            : machine.Parts.FirstOrDefault(p => p.Id == phoneInstanceId);
        if (phone is null)
            throw new InvalidOperationException(
                "No android_phone part in the machine — place one (catalog 'android_phone') before connecting.");

        var definition = catalog.Get(phone.Part);
        return new HilSession(transport, phone.Id,
            definition.Params.GetValueOrDefault("screen_width_m", 0.068),
            definition.Params.GetValueOrDefault("screen_height_m", 0.144));
    }

    public void Tap(double fx, double fy) => Transport.Tap(fx, fy);

    public void Swipe(double fx1, double fy1, double fx2, double fy2, int durationMs) =>
        Transport.Swipe(fx1, fy1, fx2, fy2, durationMs);

    /// <summary>Screen fraction → phone-local metres (X, from the left edge).</summary>
    public double FractionToMmX(double fx) => fx * ScreenWidthM;

    /// <summary>Screen fraction → phone-local millimetres (y, from the top edge).</summary>
    public double FractionToMmY(double fy) => fy * ScreenHeightM;

    /// <summary>Finds a template on the current screen; null when absent.</summary>
    public ScreenElement? Find(string name, byte[] templatePng, double threshold = ScreenDetector.DefaultThreshold)
    {
        var match = ScreenDetector.Find(name, Transport.Screencap(), templatePng, threshold);
        if (match is null)
            return null;
        return match with
        {
            Name = name,
            Fx = match.Fx,
            Fy = match.Fy
        };
    }

    public override string ToString() =>
        $"HIL session: phone '{PhoneInstanceId}' screen {ScreenWidthM * 1000:0.#} x {ScreenHeightM * 1000:0.#} mm, " +
        $"emulator {Transport.Resolution.X}x{Transport.Resolution.Y} px ({Transport.GetType().Name})";
}