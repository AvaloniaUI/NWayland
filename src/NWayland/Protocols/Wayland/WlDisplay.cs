using System;
using NWayland.Interop;

namespace NWayland.Protocols.Wayland
{
    public partial class WlDisplay
    {
        internal object SyncRoot { get; } = new();
        
        public static WlDisplay Connect(string? name = null)
        {
            var handle = LibWayland.wl_display_connect(name);
            if (handle == IntPtr.Zero)
                throw new NWaylandException("Failed to connect to wayland display");
            return new WlDisplay(handle, InterfaceVersion);
        }

        public int GetFd() => LibWayland.wl_display_get_fd(Handle);

        public int Dispatch() => LibWayland.wl_display_dispatch(Handle);

        public int DispatchPending() => LibWayland.wl_display_dispatch_pending(Handle);

        public int Roundtrip() => LibWayland.wl_display_roundtrip(Handle);

        public int PrepareRead() => LibWayland.wl_display_prepare_read(Handle);

        public int ReadEvents() => LibWayland.wl_display_read_events(Handle);

        public int Flush() => LibWayland.wl_display_flush(Handle);

        public void CancelRead() => LibWayland.wl_display_cancel_read(Handle);

        protected override void Dispose(bool disposing)
        {
            LibWayland.wl_display_disconnect(Handle);
            base.Dispose(false);
        }
    }
}
