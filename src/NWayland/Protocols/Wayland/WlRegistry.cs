using System;
using System.Runtime.InteropServices;
using NWayland.Interop;

namespace NWayland.Protocols.Wayland
{
    public unsafe partial class WlRegistry
    {
        public T Bind<T>(uint name, WlProxyTypeDescriptor type, int version, IWlEventsListener? listener = null,
            WlEventQueue? queue = null) where T : WlProxy
        {
            var iface = type.Interface;
            if (iface.Version < version)
                throw new ArgumentException(
                    $"Requested version {version} of {iface.Name} is not supported by this version of NWayland. Bindings were generated for version {iface.Version}");

            using var call = WaylandCallBuilder.Create(this, 0);
            call.Arg(name);
            call.Arg(iface.Name);
            call.Arg(version);
            call.ArgNewId();
            return (T)call.InvokeNewId(type, listener, queue);
        }
    }
}
