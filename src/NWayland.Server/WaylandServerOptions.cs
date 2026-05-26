namespace NWayland.Server;

/// <summary>
/// Configuration options for <see cref="WaylandServer"/>.
/// </summary>
public class WaylandServerOptions
{
    /// <summary>
    /// Maximum number of client-allocated object IDs per connection.
    /// Matches libwayland-server's <c>WL_MAP_MAX_OBJECTS</c> (0x00f00000) by default.
    /// Set to 0 to disable the limit.
    /// </summary>
    public uint MaxClientObjects { get; set; } = 0x00f00000;

    /// <summary>
    /// When true, calling event methods (Invoke/InvokeNewId) on a disposed resource or
    /// client silently no-ops instead of throwing <see cref="System.ObjectDisposedException"/>.
    /// Any file descriptors passed in the call are still closed to prevent leaks.
    /// </summary>
    public bool DisposedServerProxyCallIsNoOp { get; set; }

    /// <summary>
    /// Optional tracer for observing outgoing events and resource lifecycle.
    /// </summary>
    public IWaylandServerTracer? Tracer { get; set; }
}
