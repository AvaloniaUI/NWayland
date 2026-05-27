using Avalonia.Threading;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgShell;
using NWayland.Server;

namespace SubcompositorHost;

/// <summary>
/// State for an xdg_surface. Manages configure/ack lifecycle and window geometry.
/// </summary>
public sealed class XdgSurfaceState
{
    public XdgSurface.Server Resource { get; }
    public SurfaceState Surface { get; }
    public XdgToplevelState? Toplevel { get; set; }

    // Window geometry (defaults to full surface bounds)
    public int GeometryX { get; private set; }
    public int GeometryY { get; private set; }
    public int GeometryWidth { get; private set; }
    public int GeometryHeight { get; private set; }
    public bool HasGeometry { get; private set; }

    // Configure serial tracking
    private uint _lastConfigureSerial;
    private bool _configureAcked;
    private bool _initialConfigureSent;

    public XdgSurfaceState(XdgSurface.Server resource, SurfaceState surface)
    {
        Resource = resource;
        Surface = surface;
        Surface.XdgSurface = this;
    }

    public void SetWindowGeometry(int x, int y, int width, int height)
    {
        GeometryX = x;
        GeometryY = y;
        GeometryWidth = width;
        GeometryHeight = height;
        HasGeometry = true;
    }

    public void AckConfigure(uint serial)
    {
        if (serial == _lastConfigureSerial)
            _configureAcked = true;
    }

    /// <summary>
    /// Send a configure event. No UI roundtrip — runs entirely on server thread.
    /// </summary>
    public uint SendConfigure(WaylandCompositor compositor)
    {
        var serial = compositor.NextSerial();
        _lastConfigureSerial = serial;
        Resource.Configure(serial);
        return serial;
    }

    /// <summary>
    /// Send the initial configure sequence immediately when toplevel role is assigned.
    /// No roundtrip to Avalonia thread required.
    /// </summary>
    public void SendInitialConfigure(WaylandCompositor compositor)
    {
        if (_initialConfigureSent) return;
        _initialConfigureSent = true;

        // Width/height 0 = unconstrained, client picks its preferred size
        // Don't send activated yet — will be sent when the window is actually activated
        Toplevel?.SendConfigure(0, 0, ReadOnlySpan<byte>.Empty);
        SendConfigure(compositor);
    }

    /// <summary>
    /// Called when the underlying wl_surface commits.
    /// </summary>
    public void OnSurfaceCommit(WaylandCompositor compositor)
    {
        if (Toplevel == null) return;

        if (!Toplevel.IsMapped && _configureAcked)
        {
            Toplevel.Map(compositor);
        }

        // Update window title if changed
        Toplevel.UpdateWindowIfNeeded();
    }

    public void Destroy(WaylandCompositor compositor)
    {
        Surface.XdgSurface = null;
        Toplevel?.Unmap(compositor);
    }
}

/// <summary>
/// State for an xdg_toplevel. Manages title, app_id, and the Avalonia window lifecycle.
/// </summary>
public sealed class XdgToplevelState
{
    public XdgToplevel.Server Resource { get; }
    public XdgSurfaceState XdgSurface { get; }

    public string Title { get; private set; } = "Untitled";
    public string AppId { get; private set; } = "";
    public bool IsMapped { get; private set; }
    private bool _titleDirty;

    // The Avalonia window (created on map, closed on unmap)
    public ToplevelHostWindow? Window { get; private set; }

    public XdgToplevelState(XdgToplevel.Server resource, XdgSurfaceState xdgSurface)
    {
        Resource = resource;
        XdgSurface = xdgSurface;
        XdgSurface.Toplevel = this;
    }

    public void SetTitle(string title)
    {
        Title = title;
        _titleDirty = true;
    }

    public void SetAppId(string appId)
    {
        AppId = appId;
    }

    public void SendConfigure(int width, int height, ReadOnlySpan<byte> states)
    {
        Resource.Configure(width, height, states);
    }

    public void SendClose()
    {
        Resource.Close();
    }

    /// <summary>
    /// Map the toplevel: create an Avalonia window on the UI thread (fire-and-forget).
    /// </summary>
    public void Map(WaylandCompositor compositor)
    {
        if (IsMapped) return;
        IsMapped = true;
        compositor.RegisterToplevel(this);

        var self = this;
        Dispatcher.UIThread.Post(() =>
        {
            var window = new ToplevelHostWindow(self, compositor);
            self.Window = window;
            window.Show();
        });
    }

    /// <summary>
    /// Unmap the toplevel: close the Avalonia window (fire-and-forget).
    /// </summary>
    public void Unmap(WaylandCompositor compositor)
    {
        if (!IsMapped) return;
        IsMapped = false;
        compositor.UnregisterToplevel(this);
    }

    public void UpdateWindowIfNeeded()
    {
        if (_titleDirty && Window != null)
        {
            _titleDirty = false;
            var title = Title;
            var window = Window;
            Dispatcher.UIThread.Post(() => window.Title = title);
        }
    }

    public void Destroy(WaylandCompositor compositor)
    {
        XdgSurface.Toplevel = null;
        Unmap(compositor);
    }
}

/// <summary>
/// Listener for xdg_wm_base requests.
/// </summary>
public sealed class XdgWmBaseListener : XdgWmBase.ServerListener
{
    private readonly ClientState _client;
    public XdgWmBaseListener(ClientState client) => _client = client;

    protected override void GetXdgSurface(XdgWmBase.Server resource, NewId<XdgSurface.Server, XdgSurface.ServerListener> @id,
        WlSurface.Server? @surface)
    {
        var compositor = Program.Compositor;
        if (compositor == null || surface == null) return;

        var surfaceState = compositor.GetSurface(surface);
        if (surfaceState == null) return;

        var xdgSurfaceListener = new XdgSurfaceListener();
        var xdgSurfaceServer = id.GetAndConsume(xdgSurfaceListener);
        var state = new XdgSurfaceState(xdgSurfaceServer, surfaceState);
        xdgSurfaceListener.Init(state);
    }

    protected override void CreatePositioner(XdgWmBase.Server resource, NewId<XdgPositioner.Server, XdgPositioner.ServerListener> @id)
    {
        // Positioner is used for popups; create a stub
        id.GetAndConsume(new XdgPositionerListener());
    }

    protected override void Pong(XdgWmBase.Server resource, uint @serial)
    {
        // Client responded to ping — good
    }

    protected override void Destroy(XdgWmBase.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Listener for xdg_surface requests.
/// </summary>
public sealed class XdgSurfaceListener : XdgSurface.ServerListener
{
    private XdgSurfaceState _state = null!;

    public void Init(XdgSurfaceState state) => _state = state;

    protected override void GetToplevel(XdgSurface.Server resource, NewId<XdgToplevel.Server, XdgToplevel.ServerListener> @id)
    {
        var compositor = Program.Compositor;
        if (compositor == null) return;

        var toplevelListener = new XdgToplevelListener();
        var toplevelServer = id.GetAndConsume(toplevelListener);
        var state = new XdgToplevelState(toplevelServer, _state);
        toplevelListener.Init(state);

        // Send initial configure immediately — no UI roundtrip needed
        _state.SendInitialConfigure(compositor);
    }

    protected override void GetPopup(XdgSurface.Server resource, NewId<XdgPopup.Server, XdgPopup.ServerListener> @id,
        XdgSurface.Server? @parent, XdgPositioner.Server? @positioner)
    {
        // Popups not supported in this sample — send a configure and that's it
        var popupServer = id.GetAndConsume(new XdgPopupListener());
        popupServer.Configure(0, 0, 100, 100);

        var compositor = Program.Compositor;
        if (compositor != null)
            _state.SendConfigure(compositor);
    }

    protected override void SetWindowGeometry(XdgSurface.Server resource,
        int @x, int @y, int @width, int @height)
    {
        _state.SetWindowGeometry(x, y, width, height);
    }

    protected override void AckConfigure(XdgSurface.Server resource, uint @serial)
    {
        _state.AckConfigure(serial);
    }

    protected override void Destroy(XdgSurface.Server resource)
    {
        var compositor = Program.Compositor;
        if (compositor != null)
            _state.Destroy(compositor);
        resource.Dispose();
    }
}

/// <summary>
/// Listener for xdg_toplevel requests.
/// </summary>
public sealed class XdgToplevelListener : XdgToplevel.ServerListener
{
    private XdgToplevelState _state = null!;

    public void Init(XdgToplevelState state) => _state = state;

    protected override void SetTitle(XdgToplevel.Server resource, string @title)
    {
        _state.SetTitle(title);
    }

    protected override void SetAppId(XdgToplevel.Server resource, string @appId)
    {
        _state.SetAppId(appId);
    }

    protected override void SetParent(XdgToplevel.Server resource, XdgToplevel.Server? @parent)
    {
        // Parent tracking not implemented in this sample
    }

    protected override void ShowWindowMenu(XdgToplevel.Server resource,
        WlSeat.Server? @seat, uint @serial, int @x, int @y)
    {
        // Window menu not implemented
    }

    protected override void Move(XdgToplevel.Server resource, WlSeat.Server? @seat, uint @serial)
    {
        // Interactive move not implemented (Avalonia window handles its own)
    }

    protected override void Resize(XdgToplevel.Server resource,
        WlSeat.Server? @seat, uint @serial, XdgToplevel.ResizeEdgeEnum @edges)
    {
        // Interactive resize not implemented
    }

    protected override void SetMaxSize(XdgToplevel.Server resource, int @width, int @height)
    {
        // Size constraints tracked but not enforced in this sample
    }

    protected override void SetMinSize(XdgToplevel.Server resource, int @width, int @height)
    {
        // Size constraints tracked but not enforced in this sample
    }

    protected override void SetMaximized(XdgToplevel.Server resource) { }
    protected override void UnsetMaximized(XdgToplevel.Server resource) { }
    protected override void SetFullscreen(XdgToplevel.Server resource, WlOutput.Server? @output) { }
    protected override void UnsetFullscreen(XdgToplevel.Server resource) { }
    protected override void SetMinimized(XdgToplevel.Server resource) { }

    protected override void Destroy(XdgToplevel.Server resource)
    {
        var compositor = Program.Compositor;
        if (compositor != null)
            _state.Destroy(compositor);
        resource.Dispose();
    }
}

/// <summary>
/// Stub listener for xdg_positioner (needed for GetPopup but not actively used).
/// </summary>
public sealed class XdgPositionerListener : XdgPositioner.ServerListener
{
    protected override void SetSize(XdgPositioner.Server resource, int @width, int @height)
    {
    }

    protected override void SetAnchorRect(XdgPositioner.Server resource, int @x, int @y, int @width, int @height)
    {
    }

    protected override void SetAnchor(XdgPositioner.Server resource, XdgPositioner.AnchorEnum @anchor)
    {
    }

    protected override void SetGravity(XdgPositioner.Server resource, XdgPositioner.GravityEnum @gravity)
    {
    }

    protected override void SetConstraintAdjustment(XdgPositioner.Server resource, XdgPositioner.ConstraintAdjustmentEnum @constraintAdjustment)
    {
    }

    protected override void SetOffset(XdgPositioner.Server resource, int @x, int @y)
    {
    }

    protected override void SetReactive(XdgPositioner.Server resource)
    {
    }

    protected override void SetParentSize(XdgPositioner.Server resource, int @parentWidth, int @parentHeight)
    {
    }

    protected override void SetParentConfigure(XdgPositioner.Server resource, uint @serial)
    {
    }

    protected override void Destroy(XdgPositioner.Server resource) => resource.Dispose();
}

/// <summary>
/// Stub listener for xdg_popup (minimal support).
/// </summary>
public sealed class XdgPopupListener : XdgPopup.ServerListener
{
    protected override void Grab(XdgPopup.Server resource, WlSeat.Server? @seat, uint @serial)
    {
    }

    protected override void Reposition(XdgPopup.Server resource, XdgPositioner.Server? @positioner, uint @token)
    {
    }

    protected override void Destroy(XdgPopup.Server resource) => resource.Dispose();
}
