using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Server;

namespace SubcompositorHost;

/// <summary>
/// State for a wl_subsurface. Tracks position, sync mode, and stacking order.
/// </summary>
public sealed class SubsurfaceState
{
    public WlSubsurface.Server Resource { get; private set; } = null!;
    public SurfaceState Surface { get; }
    public SurfaceState Parent { get; }

    public int X { get; private set; }
    public int Y { get; private set; }
    public bool IsSync { get; private set; } = true;

    // Cached state for sync mode
    private int _cachedX, _cachedY;
    private bool _hasCachedPosition;

    public SubsurfaceState(SurfaceState surface, SurfaceState parent)
    {
        Surface = surface;
        Parent = parent;
        Surface.Subsurface = this;

        // Default: above parent
        parent.ChildrenAbove.Add(this);
    }

    public void Attach(WlSubsurface.Server resource)
    {
        Resource = resource;
    }

    public void SetPosition(int x, int y)
    {
        if (IsSync)
        {
            _cachedX = x;
            _cachedY = y;
            _hasCachedPosition = true;
        }
        else
        {
            X = x;
            Y = y;
        }
    }

    public void PlaceAbove(SurfaceState sibling)
    {
        RemoveFromParent();
        var idx = Parent.ChildrenAbove.FindIndex(s => s.Surface == sibling);
        if (idx >= 0)
            Parent.ChildrenAbove.Insert(idx + 1, this);
        else
            Parent.ChildrenAbove.Add(this);
    }

    public void PlaceBelow(SurfaceState sibling)
    {
        RemoveFromParent();
        var idx = Parent.ChildrenBelow.FindIndex(s => s.Surface == sibling);
        if (idx >= 0)
            Parent.ChildrenBelow.Insert(idx, this);
        else
            Parent.ChildrenBelow.Add(this);
    }

    public void SetSync() => IsSync = true;
    public void SetDesync() => IsSync = false;

    public void ApplyCachedState(WaylandCompositor compositor)
    {
        if (_hasCachedPosition)
        {
            X = _cachedX;
            Y = _cachedY;
            _hasCachedPosition = false;
        }

        // Also commit the subsurface's own surface if it has pending state
        Surface.Commit(compositor);
    }

    public void Destroy()
    {
        RemoveFromParent();
        Surface.Subsurface = null;
    }

    private void RemoveFromParent()
    {
        Parent.ChildrenAbove.Remove(this);
        Parent.ChildrenBelow.Remove(this);
    }
}

/// <summary>
/// Listener for wl_subcompositor requests.
/// </summary>
public sealed class SubcompositorListener : WlSubcompositor.ServerListener
{
    private readonly ClientState _client;
    public SubcompositorListener(ClientState client) => _client = client;

    protected override void GetSubsurface(WlSubcompositor.Server resource, NewId<WlSubsurface.Server, WlSubsurface.ServerListener> @id,
        WlSurface.Server? @surface, WlSurface.Server? @parent)
    {
        var compositor = Program.Compositor;
        if (compositor == null || surface == null || parent == null) return;

        var surfaceState = compositor.GetSurface(surface);
        var parentState = compositor.GetSurface(parent);
        if (surfaceState == null || parentState == null) return;

        var state = new SubsurfaceState(surfaceState, parentState);
        var subsurfaceServer = id.GetAndConsume(new SubsurfaceListener(state));
        state.Attach(subsurfaceServer);
    }

    protected override void Destroy(WlSubcompositor.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Listener for wl_subsurface requests.
/// </summary>
public sealed class SubsurfaceListener : WlSubsurface.ServerListener
{
    private readonly SubsurfaceState _state;
    public SubsurfaceListener(SubsurfaceState state) => _state = state;

    protected override void SetPosition(WlSubsurface.Server resource, int @x, int @y)
    {
        _state.SetPosition(x, y);
    }

    protected override void PlaceAbove(WlSubsurface.Server resource, WlSurface.Server? @sibling)
    {
        if (sibling == null) return;
        var compositor = Program.Compositor;
        var siblingState = compositor?.GetSurface(sibling);
        if (siblingState != null)
            _state.PlaceAbove(siblingState);
    }

    protected override void PlaceBelow(WlSubsurface.Server resource, WlSurface.Server? @sibling)
    {
        if (sibling == null) return;
        var compositor = Program.Compositor;
        var siblingState = compositor?.GetSurface(sibling);
        if (siblingState != null)
            _state.PlaceBelow(siblingState);
    }

    protected override void SetSync(WlSubsurface.Server resource)
    {
        _state.SetSync();
    }

    protected override void SetDesync(WlSubsurface.Server resource)
    {
        _state.SetDesync();
    }

    protected override void Destroy(WlSubsurface.Server resource)
    {
        _state.Destroy();
        resource.Dispose();
    }
}
