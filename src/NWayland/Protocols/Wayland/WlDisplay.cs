using System;
using NWayland.Interop;

namespace NWayland.Protocols.Wayland
{
    public partial class WlDisplay
    {
        internal object SyncRoot { get; } = new();
        
        /// <summary>
        /// Connect to a Wayland display.
        /// </summary>
        /// <param name="name">
        /// Name of the Wayland display to connect to.
        /// </param>
        /// <returns>
        /// A <c>wl_display</c> object or <c>null</c> on failure.
        /// </returns>
        /// <remarks>
        /// Connects to the Wayland display named <paramref name="name"/>. If <paramref name="name"/> is <c>null</c>,
        /// its value will be replaced with the <c>WAYLAND_DISPLAY</c> environment variable if it is set, otherwise
        /// display "wayland-0" will be used.
        /// <para>
        /// If <c>WAYLAND_SOCKET</c> is set, it is interpreted as a file descriptor number referring to an already
        /// opened socket. In this case, the socket is used as-is and <paramref name="name"/> is ignored.
        /// </para>
        /// <para>
        /// If <paramref name="name"/> is a relative path, the socket is opened relative to the <c>XDG_RUNTIME_DIR</c> directory.
        /// </para>
        /// <para>
        /// If <paramref name="name"/> is an absolute path, that path is used as-is for the location of the socket at which
        /// the Wayland server is listening; no qualification inside <c>XDG_RUNTIME_DIR</c> is attempted.
        /// </para>
        /// <para>
        /// If <paramref name="name"/> is <c>null</c> and the <c>WAYLAND_DISPLAY</c> environment variable is set to an absolute
        /// pathname, then that pathname is used as-is for the socket in the same manner as if <paramref name="name"/> held an
        /// absolute path. Support for absolute paths in <paramref name="name"/> and <c>WAYLAND_DISPLAY</c> is present since
        /// Wayland version 1.15.
        /// </para>
        /// </remarks>
        public static WlDisplay Connect(WlDisplay.Listener? listener = null, string? name = null)
        {
            var handle = LibWayland.wl_display_connect(name);
            if (handle == IntPtr.Zero)
                throw new NWaylandException("Failed to connect to wayland display");
            return new WlDisplay(new WlProxyCreationContext(null!, // special case
                null, ProxyType.Interface, handle, true, listener));
        }
        
        /// <summary>
        /// Get a display context's file descriptor.
        /// </summary>
        /// <returns>The display object file descriptor.</returns>
        /// <remarks>
        /// Returns the file descriptor associated with a display so it can be integrated into the client's main loop.
        /// </remarks>
        public int GetFd() => LibWayland.wl_display_get_fd(Handle);

        /// <summary>
        /// Process incoming events on the default event queue.
        /// </summary>
        /// <returns>The number of dispatched events on success or -1 on failure</returns>
        /// <remarks>
        /// If the default event queue is empty, this function blocks until there are
        /// events to be read from the display fd. Events are read and queued on
        /// the appropriate event queues. Finally, events on the default event queue
        /// are dispatched. On failure -1 is returned and errno set appropriately.
        /// 
        /// In a multi threaded environment, do not manually wait using poll() (or
        /// equivalent) before calling this function, as doing so might cause a dead
        /// lock. If external reliance on poll() (or equivalent) is required, see
        /// <see cref="WlEventQueue.PrepareRead"/> of how to do so.
        /// 
        /// This function is thread safe as long as it dispatches the right queue on the
        /// right thread. It is also compatible with the multi thread event reading
        /// preparation API (see <see cref="WlEventQueue.PrepareRead"/>), and uses the
        /// equivalent functionality internally. It is not allowed to call this function
        /// while the thread is being prepared for reading events, and doing so will
        /// cause a dead lock.
        /// 
        /// It is not possible to check if there are events on the queue
        /// or not. For dispatching default queue events without blocking, see
        /// <see cref="WlDisplay.DispatchPending"/>.
        /// </remarks>
        /// <seealso cref="WlDisplay.DispatchPending"/>
        /// <seealso cref="WlEventQueue.Dispatch"/>
        /// <seealso cref="WlDisplay.ReadEvents"/>
        public int Dispatch() => LibWayland.wl_display_dispatch(Handle);
        
        /// <summary>
        /// Dispatch default queue events without reading from the display fd.
        /// </summary>
        /// <returns>The number of dispatched events or -1 on failure</returns>
        /// <remarks>
        /// This function dispatches events on the main event queue. It does not
        /// attempt to read the display fd and simply returns zero if the main
        /// queue is empty, i.e., it doesn't block.
        /// </remarks>
        /// <seealso cref="WlDisplay.Dispatch"/>
        /// <seealso cref="WlEventQueue.Dispatch"/>
        /// <seealso cref="WlDisplay.Flush"/>
        public int DispatchPending() => LibWayland.wl_display_dispatch_pending(Handle);
        
        /// <summary>
        /// Block until all pending request are processed by the server
        ///
        /// This function blocks until the server has processed all currently issued
        /// requests by sending a request to the display server and waiting for a
        /// reply before returning.
        ///
        /// This function uses<seealso cref="WlEventQueue.Dispatch"/> internally. It is not allowed
        /// to call this function while the thread is being prepared for reading events,
        /// and doing so will cause a dead lock.
        /// </summary>
        /// <returns>The number of dispatched events on success or -1 on failure</returns>
        public int Roundtrip() => LibWayland.wl_display_roundtrip(Handle);

        /// <summary>
        /// Prepare to read events from the display's file descriptor using the default event queue.
        /// </summary>
        /// <returns>0 on success or -1 if the event queue was not empty.</returns>
        /// <remarks>
        /// This function does the same thing as <see cref="WlEventQueue.PrepareRead"/> with the default queue passed as the queue.
        /// </remarks>
        /// <seealso cref="WlEventQueue.PrepareRead"/>
        public int PrepareRead() => LibWayland.wl_display_prepare_read(Handle);

        /// <summary>
        /// Read events from the display file descriptor and queue them on their corresponding event queues.
        /// </summary>
        /// <returns>0 on success or -1 on error. In case of error, <c>errno</c> will be set accordingly.</returns>
        /// <remarks>
        /// Calling this method reads data available on the display file descriptor and queues the read events
        /// on their corresponding event queues.
        ///
        /// Before calling this method, depending on the thread,
        /// <see cref="WlEventQueue.PrepareRead"/> or <see cref="WlDisplay.PrepareRead"/> must be called.
        /// See <see cref="WlEventQueue.PrepareRead"/> for more details.
        ///
        /// When called while other threads have been prepared to read
        /// (using <see cref="WlEventQueue.PrepareRead"/> or <see cref="WlDisplay.PrepareRead"/>),
        /// this method will sleep until all other prepared threads have either been cancelled
        /// (using <see cref="WlDisplay.CancelRead"/>) or have themselves entered this method.
        ///
        /// The last thread to call this method will read and queue the events,
        /// then wake up all other <see cref="WlDisplay.ReadEvents"/> calls, causing them to return.
        ///
        /// If a thread cancels a read preparation when all other threads that have prepared to read
        /// have either called <see cref="WlDisplay.CancelRead"/> or <see cref="WlDisplay.ReadEvents"/>,
        /// all reader threads will return without having read any data.
        ///
        /// To dispatch events that may have been queued, call
        /// <see cref="WlDisplay.DispatchPending"/> or <see cref="WlEventQueue.DispatchPending"/>.
        /// </remarks>
        /// <seealso cref="WlDisplay.PrepareRead"/>
        /// <seealso cref="WlDisplay.CancelRead"/>
        /// <seealso cref="WlDisplay.DispatchPending"/>
        /// <seealso cref="WlDisplay.Dispatch"/>
        public int ReadEvents() => LibWayland.wl_display_read_events(Handle);

        /// <summary>
        /// Send all buffered requests on the display to the server.
        /// </summary>
        /// <param name="display">The display context object.</param>
        /// <returns>The number of bytes sent on success or -1 on failure.</returns>
        /// <remarks>
        /// Sends all buffered data on the client side to the server.
        /// Clients should always call this function before blocking on input from the display file descriptor.
        /// On success, the number of bytes sent to the server is returned.
        /// On failure, this function returns -1 and <c>errno</c> is set appropriately.
        /// <para>
        /// <c>Flush()</c> never blocks.
        /// It will write as much data as possible, but if all data could not be written,
        /// <c>errno</c> will be set to <c>EAGAIN</c> and -1 returned.
        /// In that case, use <c>poll</c> on the display file descriptor to wait for it to become writable again.
        /// </para>
        /// </remarks>
        public int Flush() => LibWayland.wl_display_flush(Handle);

        
        /// <summary>
        /// Cancel read intention on the display's file descriptor.
        /// </summary>
        /// <remarks>
        /// After a thread successfully calls <c>wl_display_prepare_read()</c>, it must either call
        /// <c>wl_display_read_events()</c> or <c>wl_display_cancel_read()</c>. Failure to follow this rule will lead to a deadlock.
        /// </remarks>
        /// <seealso cref="PrepareRead"/>
        /// <seealso cref="ReadEvents"/>
        public void CancelRead() => LibWayland.wl_display_cancel_read(Handle);

        /// <summary>
        /// Creates a new Wayland event queue for the specified display.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown if display is null.</exception>
        /// <exception cref="NWaylandException">Thrown if the native event queue creation fails.</exception>
        public WlEventQueue CreateEventQueue() => new(this);

        protected override void Dispose(bool disposing)
        {
            LibWayland.wl_display_disconnect(Handle);
            base.Dispose(false);
        }
        
        public INWaylandTracer? Tracer { get; set; }
    }
}
