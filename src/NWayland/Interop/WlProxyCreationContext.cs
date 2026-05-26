using System;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public class WlProxyCreationContext
{
    internal WlProxyCreationContext(WlDisplay display, WlEventQueue? queue, 
        WlInterfaceDescription @interface, WlProxyHandle proxyHandle,
        IWlEventsListener? listener, bool setDispatcher = true)
    {
        Display = display;
        Queue = queue;
        Interface = @interface;
        ProxyHandle = proxyHandle;
        Listener = listener;
        SetDispatcher = setDispatcher;
    }
    internal WlDisplay Display { get; set; }
    internal WlEventQueue? Queue { get; set; }
    internal WlInterfaceDescription Interface { get; }
    internal WlProxyHandle ProxyHandle { get; }
    internal IWlEventsListener? Listener { get; }
    internal bool SetDispatcher { get; }
}