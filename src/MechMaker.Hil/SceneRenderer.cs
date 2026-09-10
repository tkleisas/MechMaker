using MechMaker.Engine;
using MechMaker.Engine.Rendering;
using OpenCvSharp;

namespace MechMaker.Hil;

/// <summary>
/// Renders a machine's compiled scene to a PNG — the headless screenshot path:
/// the same software renderer the Avalonia viewport uses, without the app. The
/// MCP tool surface exposes it so agents can see what they build (and docs/CI
/// capture without OS screenshots).
/// </summary>
public static class SceneRenderer
{
    /// <summary>Renders the editor's current edit scene, framed and lit.</summary>
    public static byte[] RenderEditScene(MachineEditor editor, int width = 960, int height = 640,
        double? yawRad = null, double? pitchRad = null)
    {
        var camera = new OrbitCamera { YawRad = yawRad ?? 0.7, PitchRad = pitchRad ?? 0.55 };
        Frame(editor.EditScene, camera);
        return Render(editor.EditScene, camera, width, height);
    }

    /// <summary>Renders a live simulation frame (kinematics refreshed; the running pose).</summary>
    public static byte[] RenderLiveScene(MachineSimulation simulation, int width = 960, int height = 640,
        double? yawRad = null, double? pitchRad = null)
    {
        simulation.Simulator.RefreshKinematics();
        var scene = simulation.Simulator.GetScene();
        var camera = new OrbitCamera { YawRad = yawRad ?? 0.7, PitchRad = pitchRad ?? 0.55 };
        Frame(scene, camera);
        return Render(scene, camera, width, height);
    }

    private static byte[] Render(IReadOnlyList<SceneShape> scene, OrbitCamera camera, int width, int height)
    {
        var triangles = SceneTessellator.Tessellate(scene);
        var pixels = new SoftRenderer().RenderBgra(triangles, camera, width, height);
        return BgraToPng(pixels, width, height);
    }

    /// <summary>Frames the scene bounds: centres the orbit target and sets the distance.</summary>
    public static void Frame(IReadOnlyList<SceneShape> scene, OrbitCamera camera)
    {
        if (scene.Count == 0)
        {
            camera.TargetX = 0; camera.TargetY = 0; camera.TargetZ = 0.05;
            camera.Distance = 0.5;
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var s in scene)
        {
            minX = Math.Min(minX, s.Px); maxX = Math.Max(maxX, s.Px);
            minY = Math.Min(minY, s.Py); maxY = Math.Max(maxY, s.Py);
            minZ = Math.Min(minZ, s.Pz); maxZ = Math.Max(maxZ, s.Pz);
        }
        camera.TargetX = (minX + maxX) / 2;
        camera.TargetY = (minY + maxY) / 2;
        camera.TargetZ = (minZ + maxZ) / 2;
        var span = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        camera.Distance = Math.Max(0.15, span * 1.25);
    }

    /// <summary>Converts the renderer's BGRA8888 buffer to PNG bytes.</summary>
    public static byte[] BgraToPng(byte[] pixels, int width, int height)
    {
        using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC4, pixels);
        using var bgr = new Mat();
        Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.Flip(bgr, bgr, FlipMode.Y); // renderer row order is bottom-up
        Cv2.ImEncode(".png", bgr, out var png);
        return png.ToArray();
    }
}