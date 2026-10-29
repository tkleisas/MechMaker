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
            // Rotation-aware AABB from the shape's local half extents. Cylinders and
            // capsules run along local Z, so their radius occupies X/Y and their
            // length occupies Z. Mesh shapes carry their mesh AABB in the fields.
            var (hx, hy, hz) = s.Kind switch
            {
                SceneShapeKind.Cylinder => (s.HalfX, s.HalfX, s.HalfY),
                SceneShapeKind.Capsule => (s.HalfX, s.HalfX, s.HalfY + s.HalfX),
                SceneShapeKind.Sphere => (s.HalfX, s.HalfX, s.HalfX),
                _ => (s.HalfX, s.HalfY, s.HalfZ)
            };
            var ex = Math.Abs(s.R00) * hx + Math.Abs(s.R01) * hy + Math.Abs(s.R02) * hz;
            var ey = Math.Abs(s.R10) * hx + Math.Abs(s.R11) * hy + Math.Abs(s.R12) * hz;
            var ez = Math.Abs(s.R20) * hx + Math.Abs(s.R21) * hy + Math.Abs(s.R22) * hz;
            minX = Math.Min(minX, s.Px - ex); maxX = Math.Max(maxX, s.Px + ex);
            minY = Math.Min(minY, s.Py - ey); maxY = Math.Max(maxY, s.Py + ey);
            minZ = Math.Min(minZ, s.Pz - ez); maxZ = Math.Max(maxZ, s.Pz + ez);
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