using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland;

/// <summary>
/// This struct represents a NEW object reference that has arrived as a part of an event.
/// Since user code is supposed to actually consume the reference and add a listener,
/// the reference is wrapped in this struct. If GetAndConsume is not called before this struct goes out of scope,
/// the object reference is automatically destroyed.
/// </summary>
public ref struct NewId<TProxy, TListener>
    where TListener : class, IWlEventsListener
{
    private readonly INewIdImpl<TProxy> _impl;

    internal NewId(INewIdImpl<TProxy> impl)
    {
        _impl = impl;
    }

    public TProxy GetAndConsume(TListener? listener = null) => _impl.GetAndConsume(listener);
}

interface INewIdImpl<TProxy>
{
    TProxy GetAndConsume(IWlEventsListener? listener);
}

class NewIdImpl<T>(IWlEventArgsImpl args, int index) : INewIdImpl<T> where T : WlProxy
{
    public T GetAndConsume(IWlEventsListener? listener)
    {
        return args.GetNewIdProxy<T>(index, listener);
    }
}

class ServerNewIdImpl<T>(IWlEventArgsImpl args, int index) : INewIdImpl<T> where T : NWayland.Server.WlResource
{
    public T GetAndConsume(IWlEventsListener? listener)
    {
        var resource = (T)args.GetServerNewId(index);
        resource.Listener = listener;
        return resource;
    }
}