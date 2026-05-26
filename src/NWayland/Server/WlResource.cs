using System;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland.Server;

/// <summary>
/// Opaque context passed to generated server resource constructors.
/// Holds the per-resource <see cref="IWlResourceImpl"/> which contains all state.
/// </summary>
public class WlResourceCreationContext
{
    internal WlResourceCreationContext() { }

    internal IWlResourceImpl Impl { get; init; } = null!;
}

public abstract class WlResource : IWaylandCallTarget, IDisposable
{
    private readonly IWlResourceImpl _impl;

    public uint ObjectId => _impl.ObjectId;
    public int Version => _impl.Version;
    public bool IsDisposed => _impl.IsDisposed;
    public WlInterfaceDescription Interface => _impl.Interface;

    public IWlEventsListener? Listener
    {
        get => _impl.Listener;
        internal set => _impl.Listener = value;
    }

    protected WlResource(WlResourceCreationContext context)
    {
        _impl = context.Impl;
        _impl.Register(this);
    }

    void IWaylandCallTarget.Invoke(ref WaylandCallBuilder call)
    {
        _impl.Invoke(this, ref call);
    }

    object IWaylandCallTarget.InvokeNewId(ref WaylandCallBuilder call, WlProxyTypeDescriptor proxyType,
        IWlEventsListener? listener, WlEventQueue? queue, uint? newIdVersion)
    {
        return _impl.InvokeNewId(this, ref call, proxyType, listener,
            (int)(newIdVersion ?? (uint)Version));
    }

    public void Dispose()
    {
        _impl.Destroy(this);
    }
}