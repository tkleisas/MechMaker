using MechMaker.Core;
using MechMaker.Core.Model;

namespace MechMaker.Engine;

/// <summary>
/// The physics-contact side of the HIL seam: watches a running simulation for
/// contacts between a touch finger's tip and the phone's screen, converts the
/// contact's world position into phone-local screen fractions (androidtester
/// convention), and invokes a dispatch callback — so a *physically placed and
/// commanded* finger actually taps the emulator, exactly like androidtester's rig.
///
/// Geometry contract (from the compiled MJCF): the phone body is a box of full
/// extents <c>[w/2, h/2, t/2]</c> whose front face is +Z; the finger tip's world
/// Z within the phone slab selects the contact; X/Y map to screen fractions.
/// </summary>
public sealed class PhysicsTapBridge
{
    private readonly MachineSimulation _simulation;
    private readonly string _phoneInstanceId;
    private readonly string _fingerInstanceId;
    private readonly Action<double, double> _dispatchTap;

    /// <summary>World-space pose of the phone body when armed (front face centre +
    /// half-extents). Captured from the compile so mapping survives edits.</summary>
    private double _phoneCx, _phoneCy, _phoneCz, _halfW, _halfH, _halfT;
    private bool _armed;

        /// <summary>Minimum world-Z approach depth (m) below the screen plane for a
    /// contact to count as a deliberate press. Keep small: MuJoCo's contact point
    /// for a shallow tip overlap sits just under the plane (grazes measure ~30 µm).
    /// Default debounces nothing but pure proximity.</summary>
    public double TriggerDepthM { get; set; } = 0.00002;

    public PhysicsTapBridge(MachineSimulation simulation, string phoneInstanceId,
        string fingerInstanceId, Action<double, double> dispatchTap)
    {
        _simulation = simulation;
        _phoneInstanceId = phoneInstanceId;
        _fingerInstanceId = fingerInstanceId;
        _dispatchTap = dispatchTap;
    }

    /// <summary>Arms the bridge: computes the phone's screen frame from its compiled
    /// body pose. Call once after start_run (re-arm after edits/pose changes).</summary>
    public void Arm()
    {
        var bodyName = BodyName(_phoneInstanceId, "root");
        // Kinematics must be current before reading the compiled pose (fresh
        // simulators have identity xpos until the first mj_kinematics/step).
        _simulation.Simulator.RefreshKinematics();
        var pos = _simulation.Simulator.GetBodyPosition(bodyName);
        // The compiled box geom carries the catalog shape extents HALVED into MJCF
        // size (half-extents: 0.01775, 0.03775, 0.002 for android_phone). The screen
        // frame: front face centre is +t/2 along the body Z (the physical surface),
        // and fractions map across screen_width/height (the catalog params, which
        // are independent of the visual box).
        _phoneCx = pos[0];
        _phoneCy = pos[1];
        _phoneCz = pos[2];
        _halfW = 0.0355;  // screen_width_m / 2 (catalog params)
        _halfH = 0.0755;
        _halfT = 0.002;
        _armed = true;
    }

    /// <summary>Samples contacts (call after RunFor / each control tick). Dispatches
    /// at most one tap per contact episode (rising edge).</summary>
    public void Tick()
    {
        if (!_armed)
            return;
        var contacts = _simulation.Simulator.Contacts();
        var tipPrefix = $"{_fingerInstanceId}_tip";
        var phonePrefix = _phoneInstanceId + "_";

        foreach (var (geom1, geom2, x, y, z) in contacts)
        {
            var (tip, glass) =
                geom1.StartsWith(tipPrefix) ? (geom1, geom2) :
                geom2.StartsWith(tipPrefix) ? (geom2, geom1) :
                (null, null);
            if (tip is null || glass is null || !glass.StartsWith(phonePrefix))
                continue;

            // The press: tip below the screen plane by TriggerDepthM.
            var screenZ = _phoneCz + _halfT;
            if (screenZ - z < TriggerDepthM)
                continue;

            var fx = (x - (_phoneCx - _halfW)) / (2 * _halfW);
            var fy = 1.0 - (y - (_phoneCy - _halfH)) / (2 * _halfH); // top-left origin, y down
            if (fx is < 0 or > 1 || fy is < 0 or > 1)
                continue; // contact outside the glass (bezel, edge)

            if (!_pressing)
            {
                _pressing = true;
                _clearTicks = 0;
                DispatchedTaps.Add((fx, fy));
                _dispatchTap(fx, fy);
            }
            return;
        }

        // The episode ends only after a sustained clear interval: a tip that
        // bounces off the glass leaves it for a few ms at a time, and without a
        // refractory each bounce would dispatch another tap.
        if (_pressing && ++_clearTicks >= 800) // 100 ms at the 8 kHz MCU tick
            _pressing = false;
    }

    private bool _pressing;
    private int _clearTicks;

    /// <summary>Taps dispatched by physical contact (diagnostics/tests).</summary>
    public List<(double Fx, double Fy)> DispatchedTaps { get; } = [];

    private static string BodyName(string instanceId, string bodyName)
        => bodyName == "root" ? instanceId : $"{instanceId}_{bodyName}";
}