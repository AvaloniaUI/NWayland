using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SubcompositorHost;

/// <summary>
/// Custom Avalonia control that renders the composited surface tree.
/// Bitmaps arrive via messages from the server thread — no shared mutable state.
/// Frame callbacks fire when the server thread receives a RenderComplete signal after Render().
/// </summary>
public sealed class SurfaceControl : Control
{
    private readonly XdgToplevelState _toplevel;
    private readonly WaylandCompositor _compositor;
    private readonly Dictionary<SurfaceState, Bitmap> _bitmaps = new();
    private readonly HashSet<SurfaceState> _unconsumed = new();
    private bool _stopped;

    public SurfaceControl(XdgToplevelState toplevel, WaylandCompositor compositor)
    {
        _toplevel = toplevel;
        _compositor = compositor;
    }

    /// <summary>
    /// Called on UI thread when the server thread produces a new bitmap for a surface.
    /// </summary>
    public void UpdateBitmap(SurfaceState surface, Bitmap bitmap)
    {
        if (_stopped)
        {
            bitmap.Dispose();
            return;
        }

        if (_bitmaps.TryGetValue(surface, out var old))
            old.Dispose();
        _bitmaps[surface] = bitmap;
        _unconsumed.Add(surface);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Stop()
    {
        _stopped = true;
        foreach (var bitmap in _bitmaps.Values)
            bitmap.Dispose();
        _bitmaps.Clear();
        _unconsumed.Clear();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rootSurface = _toplevel.XdgSurface.Surface;
        var xdgSurface = _toplevel.XdgSurface;

        // Determine clipping rectangle from window geometry
        Rect clipRect;
        if (xdgSurface.HasGeometry)
            clipRect = new Rect(0, 0, xdgSurface.GeometryWidth, xdgSurface.GeometryHeight);
        else
            clipRect = new Rect(0, 0, rootSurface.Width, rootSurface.Height);

        int geoX = xdgSurface.HasGeometry ? xdgSurface.GeometryX : 0;
        int geoY = xdgSurface.HasGeometry ? xdgSurface.GeometryY : 0;

        using (context.PushClip(clipRect))
        {
            foreach (var child in rootSurface.ChildrenBelow)
                DrawSurface(context, child.Surface, child.X - geoX, child.Y - geoY);

            DrawSurface(context, rootSurface, -geoX, -geoY);

            foreach (var child in rootSurface.ChildrenAbove)
                DrawSurface(context, child.Surface, child.X - geoX, child.Y - geoY);
        }

        // Send per-surface consumption notices for bitmaps that were just rendered
        if (_unconsumed.Count > 0)
        {
            var consumed = new List<SurfaceState>(_unconsumed);
            _unconsumed.Clear();
            _compositor.Server.Post(new BitmapsConsumed(consumed));
        }
    }

    private void DrawSurface(DrawingContext context, SurfaceState surface, int offsetX, int offsetY)
    {
        if (_bitmaps.TryGetValue(surface, out var bitmap))
        {
            var destRect = new Rect(offsetX, offsetY, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            context.DrawImage(bitmap, destRect);
        }

        foreach (var child in surface.ChildrenBelow)
            DrawSurface(context, child.Surface, offsetX + child.X, offsetY + child.Y);
        foreach (var child in surface.ChildrenAbove)
            DrawSurface(context, child.Surface, offsetX + child.X, offsetY + child.Y);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var xdgSurface = _toplevel.XdgSurface;
        if (xdgSurface.HasGeometry)
            return new Size(xdgSurface.GeometryWidth, xdgSurface.GeometryHeight);

        var root = _toplevel.XdgSurface.Surface;
        if (root.Width > 0 && root.Height > 0)
            return new Size(root.Width, root.Height);

        return new Size(100, 100);
    }
}
