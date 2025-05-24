using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland;

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