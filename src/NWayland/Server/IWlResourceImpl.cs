using NWayland.Interop;

namespace NWayland.Server;

/// <summary>
/// Backend implementation for WlResource I/O operations.
/// Per-resource: holds object ID, version, interface, listener, and dispose state.
/// Defined in NWayland so WlResource can reference it; implemented in NWayland.Server.
/// </summary>
internal interface IWlResourceImpl
{
    uint ObjectId { get; }
    int Version { get; }
    WlInterfaceDescription Interface { get; }
    IWlEventsListener? Listener { get; set; }
    bool IsDisposed { get; }

    /// <summary>
    /// Register this resource in the object map after construction.
    /// </summary>
    void Register(WlResource resource);

    /// <summary>
    /// Serialize and queue an outgoing event for the client.
    /// </summary>
    void Invoke(WlResource resource, ref WaylandCallBuilder call);

    /// <summary>
    /// Serialize an outgoing event that creates a new resource (new_id arg).
    /// Returns the newly created WlResource.
    /// </summary>
    WlResource InvokeNewId(WlResource resource, ref WaylandCallBuilder call,
        WlProxyTypeDescriptor proxyType, IWlEventsListener? listener, int version);

    /// <summary>
    /// Notify the backend that a resource is being destroyed (removed from object map, etc.).
    /// </summary>
    void Destroy(WlResource resource);
}
