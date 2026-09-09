using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MechMaker.Engine;
using MechMaker.Engine.Rendering;

namespace MechMaker.App;

/// <summary>
/// Software-rasterized 3D viewport: drag to orbit, wheel to zoom. The frame source
/// is a callback so the owner decides whether to render the edit scene or the live
/// simulation.
/// </summary>
public sealed class ViewportControl : Control
{
    private readonly SoftRenderer _renderer = new();
    private readonly OrbitCamera _camera = new();
    private WriteableBitmap? _bitmap;
    private Point _lastPointer;
    private bool _dragging;

    /// <summary>Produces the shapes to render (edit scene or live sim).</summary>
    public Func<IReadOnlyList<SceneShape>>? SceneProvider { get; set; }

    /// <summary>When true (simulation playing), re-renders at ~30 fps.</summary>
    public bool Continuous { get; set; }

    public ViewportControl()
    {
        ClipToBounds = true;
    }

    public void ResetCamera(double targetX, double targetY, double targetZ, double distance)
    {
        _camera.TargetX = targetX;
        _camera.TargetY = targetY;
        _camera.TargetZ = targetZ;
        _camera.Distance = distance;
        Invalidate();
    }

    public void Invalidate() => InvalidateVisual();

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        InvalidateVisual();
        return result;
    }

    public override void Render(DrawingContext context)
    {
        var width = (int)Math.Max(1, Bounds.Width);
        var height = (int)Math.Max(1, Bounds.Height);

        if (SceneProvider is null)
        {
            context.FillRectangle(Brushes.DimGray, new Rect(0, 0, width, height));
            return;
        }

        var shapes = SceneProvider();
        var triangles = SceneTessellator.Tessellate(shapes);
        var pixels = _renderer.RenderBgra(triangles, _camera, width, height);

        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using (var frame = _bitmap.Lock())
        {
            unsafe
            {
                fixed (byte* src = pixels)
                {
                    Buffer.MemoryCopy(src, (void*)frame.Address, frame.RowBytes * height, frame.RowBytes * height);
                }
            }
        }

        context.DrawImage(_bitmap, new Rect(0, 0, width, height));

        if (Continuous)
            DispatcherTimer.RunOnce(() => InvalidateVisual(), TimeSpan.FromMilliseconds(33));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _lastPointer = e.GetPosition(this);
        _dragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        var p = e.GetPosition(this);
        _camera.Orbit(p.X - _lastPointer.X, p.Y - _lastPointer.Y);
        _lastPointer = p;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _camera.Zoom(e.Delta.Y);
        InvalidateVisual();
        e.Handled = true;
    }
}
