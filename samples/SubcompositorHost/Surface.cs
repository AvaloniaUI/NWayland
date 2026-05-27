using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Server;

namespace SubcompositorHost;

/// <summary>
/// Tracks the state of a wl_surface. Implements double-buffered pending/committed state.
/// </summary>
public sealed class SurfaceState
{
    public WlSurface.Server Resource { get; }
    public ClientState ClientState { get; }

    // Pending state (accumulated between commits)
    private ShmBufferState? _pendingBuffer;
    private int _pendingBufferX, _pendingBufferY;
    private bool _pendingBufferAttached;
    private readonly List<PixelRect> _pendingDamage = new();
    private readonly List<WlCallback.Server> _pendingFrameCallbacks = new();

    // Committed state
    public ShmBufferState? CommittedBuffer { get; private set; }
    public int BufferOffsetX { get; private set; }
    public int BufferOffsetY { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    // Frame callbacks waiting to be sent (server thread only)
    private readonly List<WlCallback.Server> _activeFrameCallbacks = new();

    // True when a bitmap has been posted to UI but not yet consumed by Render()
    private bool _hasPendingBitmap;

    // Role
    public XdgSurfaceState? XdgSurface { get; set; }
    public SubsurfaceState? Subsurface { get; set; }

    // Subsurface children
    public List<SubsurfaceState> ChildrenAbove { get; } = new();
    public List<SubsurfaceState> ChildrenBelow { get; } = new();

    public SurfaceState(WlSurface.Server resource, ClientState clientState)
    {
        Resource = resource;
        ClientState = clientState;
    }

    public void Attach(ShmBufferState? buffer, int x, int y)
    {
        _pendingBuffer = buffer;
        _pendingBufferX = x;
        _pendingBufferY = y;
        _pendingBufferAttached = true;
        buffer?.Reattach();
    }

    public void Damage(int x, int y, int width, int height)
    {
        _pendingDamage.Add(new PixelRect(x, y, width, height));
    }

    public void AddFrameCallback(WlCallback.Server callback)
    {
        _pendingFrameCallbacks.Add(callback);
    }

    public void Commit(WaylandCompositor compositor)
    {
        // Apply pending buffer
        if (_pendingBufferAttached)
        {
            var oldBuffer = CommittedBuffer;

            CommittedBuffer = _pendingBuffer;
            BufferOffsetX = _pendingBufferX;
            BufferOffsetY = _pendingBufferY;
            _pendingBufferAttached = false;
            _pendingBuffer = null;

            // Release old buffer
            if (oldBuffer != null && oldBuffer != CommittedBuffer)
                oldBuffer.Release();
        }

        // Move frame callbacks to active list
        _activeFrameCallbacks.AddRange(_pendingFrameCallbacks);
        _pendingFrameCallbacks.Clear();

        _pendingDamage.Clear();

        // Create bitmap from committed buffer (copies data, then releases buffer)
        Bitmap? newBitmap = null;
        if (CommittedBuffer != null)
        {
            var buffer = CommittedBuffer;
            Width = buffer.Width;
            Height = buffer.Height;

            var src = buffer.GetData();
            if (src != IntPtr.Zero)
            {
                var isOpaque = buffer.Format == NWayland.Protocols.Wayland.WlShm.FormatEnum.Xrgb8888;
                newBitmap = new Bitmap(
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    isOpaque ? AlphaFormat.Opaque : AlphaFormat.Premul,
                    src,
                    new PixelSize(buffer.Width, buffer.Height),
                    new Vector(96, 96),
                    buffer.Stride);
            }

            buffer.Release();
            CommittedBuffer = null;
        }

        // Handle XDG commit first (may trigger Map → posts window creation to UI)
        XdgSurface?.OnSurfaceCommit(compositor);

        // Apply subsurface cached state (their commits may also post to UI)
        foreach (var child in ChildrenAbove.Concat(ChildrenBelow))
        {
            if (child.IsSync)
                child.ApplyCachedState(compositor);
        }

        // Frame callback + bitmap delivery logic
        if (newBitmap != null)
        {
            // Bitmap in flight — defer frame callbacks until UI consumes it
            _hasPendingBitmap = true;
            var toplevel = FindToplevel();
            if (toplevel != null)
            {
                var self = this;
                var bitmap = newBitmap;
                Dispatcher.UIThread.Post(() =>
                    toplevel.Window?.SurfaceControl?.UpdateBitmap(self, bitmap));
            }
        }
        else if (_activeFrameCallbacks.Count > 0)
        {
            if (!_hasPendingBitmap)
            {
                // No bitmap in flight — fire frame callbacks immediately
                SendPendingFrameCallbacks(compositor);
            }
            // else: bitmap still pending in UI — callbacks will fire when consumed
        }
    }

    /// <summary>
    /// Walk up subsurface parents to find the toplevel that owns this surface tree.
    /// </summary>
    public XdgToplevelState? FindToplevel()
    {
        if (XdgSurface?.Toplevel != null)
            return XdgSurface.Toplevel;
        if (Subsurface != null)
            return Subsurface.Parent.FindToplevel();
        return null;
    }

    /// <summary>
    /// Called on server thread when the UI has consumed (rendered) the pending bitmap.
    /// Clears the pending flag and fires any deferred frame callbacks.
    /// </summary>
    public void OnBitmapConsumed(WaylandCompositor compositor)
    {
        _hasPendingBitmap = false;
        if (_activeFrameCallbacks.Count > 0)
            SendPendingFrameCallbacks(compositor);
    }

    /// <summary>
    /// Send pending frame callbacks. Called from server thread.
    /// </summary>
    public void SendPendingFrameCallbacks(WaylandCompositor compositor)
    {
        var timestamp = (uint)(Environment.TickCount64 & 0xFFFFFFFF);
        foreach (var callback in _activeFrameCallbacks)
        {
            try
            {
                callback.Done(timestamp);
                callback.Dispose();
            }
            catch { }
        }
        _activeFrameCallbacks.Clear();
    }

    public void Destroy(WaylandCompositor compositor)
    {
        // Destroy XDG role (closes Avalonia window via Unmap)
        if (XdgSurface != null)
        {
            XdgSurface.Destroy(compositor);
            XdgSurface = null;
        }

        // Clean up frame callbacks
        foreach (var cb in _activeFrameCallbacks)
        {
            try { cb.Dispose(); } catch { }
        }
        _activeFrameCallbacks.Clear();

        foreach (var cb in _pendingFrameCallbacks)
        {
            try { cb.Dispose(); } catch { }
        }
        _pendingFrameCallbacks.Clear();

        compositor.UnregisterSurface(Resource);
        ClientState.Surfaces.Remove(this);
    }
}

/// <summary>
/// Minimal wl_region state: accumulates add/subtract rectangles.
/// </summary>
public sealed class RegionState
{
    private readonly List<PixelRect> _rects = new();

    public void Add(int x, int y, int width, int height)
        => _rects.Add(new PixelRect(x, y, width, height));

    public void Subtract(int x, int y, int width, int height)
    {
        // Simplified: we don't actually compute the subtraction for this sample
    }

    public bool Contains(int x, int y)
        => _rects.Any(r => r.Contains(new PixelPoint(x, y)));
}

/// <summary>
/// Listener for wl_compositor requests.
/// </summary>
public sealed class CompositorListener : WlCompositor.ServerListener
{
    private readonly ClientState _client;
    public CompositorListener(ClientState client) => _client = client;

    protected override void CreateSurface(WlCompositor.Server resource, NewId<WlSurface.Server, WlSurface.ServerListener> @surface)
    {
        var surfaceListener = new SurfaceListener();
        var surfaceServer = surface.GetAndConsume(surfaceListener);
        var state = new SurfaceState(surfaceServer, _client);
        surfaceListener.Init(state);
        _client.Surfaces.Add(state);

        // Find compositor from client state
        var compositor = GetCompositor();
        compositor?.RegisterSurface(surfaceServer, state);
    }

    protected override void CreateRegion(WlCompositor.Server resource, NewId<WlRegion.Server, WlRegion.ServerListener> @region)
    {
        var state = new RegionState();
        region.GetAndConsume(new RegionListener(state));
    }

    protected override void Release(WlCompositor.Server resource)
    {
        resource.Dispose();
    }

    private WaylandCompositor? GetCompositor()
    {
        // Walk up to find the compositor via the field stored on Program
        return Program.Compositor;
    }
}

/// <summary>
/// Listener for wl_surface requests.
/// </summary>
public sealed class SurfaceListener : WlSurface.ServerListener
{
    private SurfaceState _state = null!;
    public void Init(SurfaceState state) => _state = state;

    protected override void Attach(WlSurface.Server resource, WlBuffer.Server? @buffer, int @x, int @y)
    {
        if (buffer != null)
        {
            var bufferState = ShmBufferState.Get(buffer);
            _state.Attach(bufferState, x, y);
        }
        else
        {
            _state.Attach(null, x, y);
        }
    }

    protected override void Damage(WlSurface.Server resource, int @x, int @y, int @width, int @height)
    {
        _state.Damage(x, y, width, height);
    }

    protected override void Frame(WlSurface.Server resource, NewId<WlCallback.Server, WlCallback.ServerListener> @callback)
    {
        var cb = callback.GetAndConsume();
        _state.AddFrameCallback(cb);
    }

    protected override void GetRelease(WlSurface.Server resource, NewId<WlCallback.Server, WlCallback.ServerListener> @callback)
    {
        callback.GetAndConsume();
        resource.Dispose();
    }

    protected override void DamageBuffer(WlSurface.Server resource, int @x, int @y, int @width, int @height)
    {
        _state.Damage(x, y, width, height);
    }

    protected override void SetOpaqueRegion(WlSurface.Server resource, WlRegion.Server? @region)
    {
        // Tracked but not actively used for rendering in this sample
    }

    protected override void SetBufferTransform(WlSurface.Server resource, WlOutput.TransformEnum @transform)
    {
    }

    protected override void SetBufferScale(WlSurface.Server resource, int @scale)
    {
    }

    protected override void Offset(WlSurface.Server resource, int @x, int @y)
    {
    }

    protected override void SetInputRegion(WlSurface.Server resource, WlRegion.Server? @region)
    {
        // Tracked but not actively used in this sample
    }

    protected override void Commit(WlSurface.Server resource)
    {
        var compositor = Program.Compositor;
        if (compositor != null)
            _state.Commit(compositor);
    }

    protected override void Destroy(WlSurface.Server resource)
    {
        var compositor = Program.Compositor;
        if (compositor != null)
            _state.Destroy(compositor);
        resource.Dispose();
    }
}

/// <summary>
/// Listener for wl_region requests.
/// </summary>
public sealed class RegionListener : WlRegion.ServerListener
{
    private readonly RegionState _state;
    public RegionListener(RegionState state) => _state = state;

    protected override void Add(WlRegion.Server resource, int @x, int @y, int @width, int @height)
    {
        _state.Add(x, y, width, height);
    }

    protected override void Subtract(WlRegion.Server resource, int @x, int @y, int @width, int @height)
    {
        _state.Subtract(x, y, width, height);
    }

    protected override void Destroy(WlRegion.Server resource)
    {
        resource.Dispose();
    }
}
