using System;
using System.Collections.Generic;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland.Server;

/// <summary>
/// Represents a connected Wayland client.
/// </summary>
public sealed class WaylandClient : IDisposable
{
    private readonly WaylandServer _server;
    private readonly WaylandServerSocket _socket;
    private readonly WlObjectMap _objectMap = new();
    private readonly List<WaylandServerGlobal> _globals = new();
    private readonly List<uint> _registryIds = new();
    private readonly WaylandOutgoingBuffer _outgoingBuffer = new();
    private readonly WlDisplay.Server _displayResource;
    private uint _nextGlobalId;
    private volatile bool _disposed;

    internal WaylandMessageParser? Parser { get; set; }

    /// <summary>
    /// Whether this client has been disposed (disconnected and cleaned up).
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Whether this client's socket has a pending EAGAIN for writes.
    /// Managed by the server event loop for backpressure via epoll EPOLLOUT.
    /// </summary>
    internal bool PendingWrite { get; set; }

    internal WaylandClient(WaylandServer server, WaylandServerSocket socket)
    {
        _server = server;
        _socket = socket;

        // wl_display is always object ID 1
        _displayResource = new WlDisplay.Server(new WlResourceCreationContext
        {
            Impl = new WaylandResourceImpl(this, 1, WlDisplay.ProxyType.Interface.Version,
                WlDisplay.ProxyType.Interface, null, _objectMap.InsertClientId)
        });
    }

    /// <summary>
    /// Number of FDs in the pending outgoing queue.
    /// </summary>
    public int PendingFdQueueLen => _outgoingBuffer.FdsUsed;

    /// <summary>
    /// Number of bytes in the pending outgoing queue.
    /// </summary>
    public int PendingByteQueueLen => _outgoingBuffer.BytesUsed;

    /// <summary>
    /// Register a global object for this client. Sends wl_registry.global
    /// to all active registries.
    /// </summary>
    public WaylandServerGlobal AddGlobal(string interfaceName, int version)
    {
        using (_server.AcquireDispatchLock())
        {
            var global = new WaylandServerGlobal(this, ++_nextGlobalId, interfaceName, version);
            _globals.Add(global);
            SendGlobalToRegistries(global);
            return global;
        }
    }

    /// <summary>
    /// Non-blocking flush hint. Attempts to send queued outgoing messages
    /// to the client socket without blocking. Data that cannot be sent
    /// immediately remains queued for the next flush.
    /// </summary>
    /// <returns>True if all pending data was sent, false if data remains.</returns>
    public bool TryFlush()
    {
        using (_server.AcquireDispatchLock())
            return _outgoingBuffer.TryFlushToSocket(_socket);
    }

    /// <summary>
    /// Post a protocol error and initiate disconnect.
    /// Queues a <c>wl_display.error</c> event, does a best-effort flush,
    /// shuts down the read side, disposes the parser (closing pending FDs),
    /// and enqueues a disconnect event for <see cref="WaylandServer.NextEvent"/>.
    /// </summary>
    /// <remarks>
    /// This is the low-level transport. Prefer <c>WlResource.PostError</c> (the protected base
    /// overload, or the generated strongly-typed public overload on a resource class), which
    /// auto-resolves the error message from the interface's <c>error</c> enum.
    /// </remarks>
    internal void PostError(WlResource? resource, uint code, string message)
    {
        using (_server.AcquireDispatchLock())
        {
            // Trim to 0xf80 bytes to guarantee the error event fits within
            // the 4096-byte max message size (header + objectId + code + string overhead).
            if (message.Length > 0xf80)
                message = message.Substring(0, 0xf80);
            // A null resource references the wl_display object itself (id 1). wl_display.error's
            // object_id arg is non-nullable, so we must pass a real object, and the display is the
            // canonical target for global errors (matches libwayland's wl_resource_post_no_memory).
            _displayResource.Error(resource ?? _displayResource, code, message);
            try { _outgoingBuffer.TryFlushToSocket(_socket); } catch { /* best-effort */ }
            _socket.ShutdownRead();
            if (Parser != null)
                Parser.Dispose();
            _server.EnqueueDisconnect(this);
        }
    }

    internal void RemoveGlobal(WaylandServerGlobal global)
    {
        _globals.Remove(global);
        if (!_disposed)
            SendGlobalRemoveToRegistries(global);
    }

    internal WlResource AcceptBind(uint newId, WlProxyTypeDescriptor proxyType,
        IWlEventsListener? listener, int version)
    {
        return proxyType.ServerFactory!(new WlResourceCreationContext
        {
            Impl = new WaylandResourceImpl(this, newId, version,
                proxyType.Interface, listener, _objectMap.InsertClientId)
        });
    }
    
    /// <summary>
    /// Destroy a resource: remove from object map and send delete_id if client-allocated.
    /// </summary>
    internal void DestroyResource(WlResource resource)
    {
        uint id = resource.ObjectId;
        _objectMap.Remove(id);

        // Send wl_display.delete_id for client-allocated IDs (skip during teardown)
        if (!_disposed && id < WlObjectMap.ServerIdBase)
            _displayResource.DeleteId(id);
    }

    /// <summary>
    /// Register a wl_registry object ID for this client.
    /// </summary>
    internal void AddRegistry(uint registryId)
    {
        _registryIds.Add(registryId);
    }

    /// <summary>
    /// Unregister a wl_registry object ID for this client.
    /// </summary>
    internal void RemoveRegistry(uint registryId)
    {
        _registryIds.Remove(registryId);
    }

    internal WlObjectMap ObjectMap => _objectMap;
    internal WaylandOutgoingBuffer OutgoingBuffer => _outgoingBuffer;
    internal WaylandServerSocket Socket => _socket;
    internal WaylandServer Server => _server;
    internal IReadOnlyList<WaylandServerGlobal> Globals => _globals;

    /// <summary>
    /// Returns a snapshot of all resources currently in this client's object map.
    /// Safe to call after disconnection — acquires a dispose-safe dispatch lock so
    /// user code can enumerate resources to clean up listener-owned unmanaged state.
    /// </summary>
    public IReadOnlyList<WlResource> GetResources()
    {
        using (_server.AcquireDispatchLock(allowDisposed: true))
        {
            var list = new List<WlResource>();
            _objectMap.ForEach((_, resource) => list.Add(resource));
            return list;
        }
    }

    /// <summary>
    /// Returns a snapshot of all globals registered for this client.
    /// Safe to call after disconnection.
    /// </summary>
    public IReadOnlyList<WaylandServerGlobal> GetGlobals()
    {
        using (_server.AcquireDispatchLock(allowDisposed: true))
            return _globals.ToArray();
    }

    private void SendGlobalToRegistries(WaylandServerGlobal global)
    {
        foreach (var registryId in _registryIds)
        {
            var registry = (WlRegistry.Server)_objectMap.Get(registryId)!;
            registry.Global(global.Id, global.Interface, (uint)global.Version);
        }
    }

    private void SendGlobalRemoveToRegistries(WaylandServerGlobal global)
    {
        foreach (var registryId in _registryIds)
        {
            var registry = (WlRegistry.Server)_objectMap.Get(registryId)!;
            registry.GlobalRemove(global.Id);
        }
    }

    /// <summary>
    /// Serialize wl_registry.global to a specific registry (used by parser on get_registry).
    /// </summary>
    internal void SendGlobalToRegistry(uint registryId, WaylandServerGlobal global)
    {
        var registry = (WlRegistry.Server)_objectMap.Get(registryId)!;
        registry.Global(global.Id, global.Interface, (uint)global.Version);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Close unsent outgoing FDs before disposing resources
        _outgoingBuffer.CloseUnsentFds();

        // Snapshot resources before disposing — Dispose calls DestroyResource
        // which mutates the object map. Iterating + mutating would throw.
        // Resources and globals remain in their collections after disposal so
        // user code can enumerate them (e.g. to clean up listener-owned unmanaged state)
        // via GetResources()/GetGlobals() when handling the disconnect event.
        var toDispose = new List<WlResource>();
        _objectMap.ForEach((_, resource) => toDispose.Add(resource));

        foreach (var resource in toDispose)
            resource.Dispose();

        _socket.Dispose();
    }
}
