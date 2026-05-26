using System;
using NWayland.Protocols.Wayland;

namespace NWayland;

/// <summary>
/// Marker interface for types that can serve as a target event queue for proxy events.
/// <see cref="WlDisplay"/> represents the default queue;
/// <see cref="WlEventQueue"/> represents a custom event queue.
/// </summary>
public interface IWlTargetQueue
{
    internal IntPtr QueueHandle { get; }
    internal WlDisplay Display { get; }
    internal WlEventQueue? ManagedQueue { get; }
    internal QueueDispatchLock DispatchLock { get; }
}
