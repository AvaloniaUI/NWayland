using System;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public class WlProxyCreationContext
{
    internal WlProxyCreationContext(WlDisplay display, WlEventQueue? queue, 
        WlInterfaceDescription @interface, IntPtr handle, bool ownsHandle,
        IWlEventsListener? listener)
    {
        Display = display;
        Queue = queue;
        Interface = @interface;
        Handle = handle;
        OwnsHandle = ownsHandle;
        Listener = listener;
    }
    internal WlDisplay Display { get; set; }
    internal WlEventQueue? Queue { get; set; }
    internal WlInterfaceDescription Interface { get; }
    internal IntPtr Handle { get; }
    internal bool OwnsHandle { get; }
    internal IWlEventsListener? Listener { get; }
}