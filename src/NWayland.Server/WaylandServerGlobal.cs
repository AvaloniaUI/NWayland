using System;

namespace NWayland.Server;

/// <summary>
/// Represents a global object advertised to a specific client via wl_registry.
/// </summary>
public sealed class WaylandServerGlobal
{
    private bool _removed;

    internal WaylandServerGlobal(WaylandClient client, uint id, string interfaceName, int version)
    {
        Client = client;
        Id = id;
        Interface = interfaceName;
        Version = version;
    }

    /// <summary>
    /// Auto-assigned numeric identifier (sent as "name" in wl_registry.global).
    /// </summary>
    public uint Id { get; }

    /// <summary>
    /// The Wayland interface name (e.g., "wl_compositor").
    /// </summary>
    public string Interface { get; }

    /// <summary>
    /// The advertised version.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// The client this global belongs to.
    /// </summary>
    public WaylandClient Client { get; }

    public bool IsRemoved => _removed;

    /// <summary>
    /// Remove this global: sends wl_registry.global_remove to all registries
    /// and unregisters from the client.
    /// </summary>
    public void Remove()
    {
        using (Client.Server.AcquireDispatchLock(allowDisposed: true))
        {
            if (_removed)
                return;
            _removed = true;

            if (Client.IsDisposed || Client.Server.IsDisposed)
                return;

            Client.RemoveGlobal(this);
        }
    }
}
