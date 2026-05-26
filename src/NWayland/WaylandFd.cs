using NWayland.Interop;

namespace NWayland;

/// <summary>
/// A ref struct wrapping a file descriptor received from a Wayland event.
/// The caller must call <see cref="Consume"/> to take ownership of the FD.
/// If not consumed before going out of scope, the FD is automatically closed.
/// </summary>
public ref struct WaylandFd
{
    private readonly IWlEventArgsImpl _impl;
    private readonly int _index;
    private bool _consumed;

    internal WaylandFd(IWlEventArgsImpl impl, int index)
    {
        _impl = impl;
        _index = index;
        _consumed = false;
    }

    /// <summary>
    /// Take ownership of the file descriptor. Returns the raw FD value.
    /// Can only be called once.
    /// </summary>
    public int Consume()
    {
        if (_consumed)
            throw new System.InvalidOperationException("FD already consumed");
        _consumed = true;
        return _impl.GetFd(_index);
    }

    /// <summary>
    /// Closes the file descriptor if it was not consumed.
    /// </summary>
    public void Dispose()
    {
        if (!_consumed)
        {
            _consumed = true;
            _impl.CloseFd(_index);
        }
    }
}
