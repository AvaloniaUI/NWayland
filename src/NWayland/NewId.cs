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
    where TProxy : WlProxy 
    where TListener : class, IWlEventsListener
{
    private readonly NewIdImpl<TProxy> _impl;

    internal NewId(NewIdImpl<TProxy> impl)
    {
        _impl = impl;
    }

    public TProxy GetAndConsume(TListener? listener = null) => _impl.GetAndConsume(listener);
}

class NewIdImpl<T>(WlEventArgsImpl args, int index) where T : WlProxy
{
    public T GetAndConsume(IWlEventsListener? listener)
    {
        return args.GetNewIdProxy<T>(index, listener);
    }
}