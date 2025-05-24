using NWayland.Protocols.Wayland;

namespace NWayland;

public ref struct NewId<T> where T : WlProxy
{
    private readonly NewIdImpl<T> _impl;

    internal NewId(NewIdImpl<T> impl)
    {
        _impl = impl;
    }

    public T GetAndConsume() => _impl.GetAndConsume();
}

class NewIdImpl<T>(T proxy) where T : WlProxy
{
    public bool IsConsumed { get; private set; }
    public T GetAndConsume()
    {
        IsConsumed = true;
        return proxy;
    }
}