using NWayland.Interop;
using NWayland.Protocols.Wayland;
using NWayland.Server;

namespace NWayland.Server;

/// <summary>
/// Base class for all events produced by <see cref="WaylandServer.NextEvent"/>.
/// </summary>
public abstract class WaylandServerEvent
{
    public WaylandClient? Client { get; }

    private protected WaylandServerEvent(WaylandClient? client)
    {
        Client = client;
    }
}

/// <summary>
/// A custom event posted via <see cref="WaylandServer.Post"/>.
/// Carries an opaque state object for the application to interpret.
/// </summary>
public sealed class WaylandCustomEvent : WaylandServerEvent
{
    public object? State { get; }

    internal WaylandCustomEvent(object? state) : base(null)
    {
        State = state;
    }
}

/// <summary>
/// The client has disconnected (orderly or due to error).
/// All resources have already been cleaned up.
/// </summary>
public sealed class WaylandClientDisconnectEvent : WaylandServerEvent
{
    internal WaylandClientDisconnectEvent(WaylandClient client) : base(client) { }
}

/// <summary>
/// The client sent a <c>wl_display.sync</c> request.
/// The application should call <see cref="Complete"/> when all preceding
/// work for this client is done. This sends the <c>wl_callback.done</c> event.
/// </summary>
public sealed class WaylandServerSyncEvent : WaylandServerEvent
{
    private readonly WlCallback.Server _callback;
    private bool _completed;

    internal WaylandServerSyncEvent(WaylandClient client, WlCallback.Server callback) : base(client)
    {
        _callback = callback;
    }

    /// <summary>
    /// Send the <c>wl_callback.done</c> event with the given serial, then destroy the callback.
    /// </summary>
    public void Complete(uint serial)
    {
        if (_completed)
            throw new System.InvalidOperationException("Sync callback already completed");
        _completed = true;
        _callback.Done(serial);
        _callback.Dispose();
    }
}

/// <summary>
/// The client sent a <c>wl_registry.bind</c> request to bind a global.
/// Call <see cref="Accept{T}"/> to create the server-side resource.
/// </summary>
public sealed class WaylandServerRegistryBindEvent : WaylandServerEvent
{
    private readonly uint _newId;
    private readonly uint _requestedVersion;
    private readonly string _requestedInterface;
    private bool _accepted;

    internal WaylandServerRegistryBindEvent(WaylandClient client,
        WaylandServerGlobal global, string requestedInterface,
        uint requestedVersion, uint newId) : base(client)
    {
        Global = global;
        _requestedInterface = requestedInterface;
        _requestedVersion = requestedVersion;
        _newId = newId;
    }

    public WaylandServerGlobal Global { get; }

    /// <summary>
    /// The interface name the client requested (for validation).
    /// </summary>
    public string RequestedInterface => _requestedInterface;

    /// <summary>
    /// The protocol version the client requested.
    /// </summary>
    public uint RequestedVersion => _requestedVersion;

    /// <summary>
    /// Accept the bind request, creating a server-side resource of type <typeparamref name="T"/>.
    /// </summary>
    public T Accept<T>(IWlEventsListener? listener = null)
        where T : NWayland.Server.WlResource, IWlProxyTypeDescriptorProvider
    {
        if (_accepted)
            throw new System.InvalidOperationException("Bind request already accepted");

        var proxyType = T.ProxyType;
        if (proxyType.Interface.Name != Global.Interface)
            throw new System.ArgumentException(
                $"Interface mismatch: global advertises '{Global.Interface}' " +
                $"but Accept<{typeof(T).Name}> provides '{proxyType.Interface.Name}'");

        int version = (int)System.Math.Min(_requestedVersion, (uint)Global.Version);
        using (Client!.Server.AcquireDispatchLock())
        {
            _accepted = true;
            return (T)Client.AcceptBind(_newId, proxyType, listener, version);
        }
    }
}

/// <summary>
/// A general Wayland request from a client (not sync/bind/destructor).
/// Call <see cref="Dispatch"/> to invoke the resource's listener.
/// Must be disposed if not dispatched — undispatched events may hold open FDs.
/// </summary>
public sealed class WaylandServerRequestEvent : WaylandServerEvent, System.IDisposable
{
    private readonly NWayland.Server.WlResource _resource;
    private readonly System.Action _dispatchAction;
    private readonly ServerWlEventArgsImpl _argsImpl;
    private readonly bool _isDestructor;
    private bool _dispatched;

    internal WaylandServerRequestEvent(WaylandClient client,
        NWayland.Server.WlResource resource,
        System.Action dispatchAction,
        ServerWlEventArgsImpl argsImpl,
        bool isDestructor = false) : base(client)
    {
        _resource = resource;
        _dispatchAction = dispatchAction;
        _argsImpl = argsImpl;
        _isDestructor = isDestructor;
    }

    /// <summary>
    /// The resource this request targets.
    /// </summary>
    public NWayland.Server.WlResource Resource => _resource;

    /// <summary>
    /// Dispatch the request to the resource's listener.
    /// </summary>
    public void Dispatch()
    {
        if (_dispatched)
            throw new System.InvalidOperationException("Request already dispatched");
        _dispatched = true;
        _argsImpl.MarkDispatched();
        try
        {
            _dispatchAction();
        }
        finally
        {
            _argsImpl.Dispose();
        }
    }

    /// <summary>
    /// Dispose the event, closing any consumed FDs if the event was never dispatched.
    /// For destructor requests, also disposes the resource.
    /// </summary>
    public void Dispose()
    {
        // Destructor resources are already disposed at parse time (immediately
        // removed from object map), so no resource cleanup needed here.
        _argsImpl.Dispose();
    }
}
