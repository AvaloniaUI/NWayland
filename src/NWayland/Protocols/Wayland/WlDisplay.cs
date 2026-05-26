using System;
using System.Collections.Generic;
using NWayland.Interop;

namespace NWayland.Protocols.Wayland
{
    public partial class WlDisplay : IWlTargetQueue
    {
        internal object SyncRoot { get; } = new();
        internal QueueDispatchLock DispatchLock;
        internal WlDisplayOptions Options { get; private set; } = new();
        // volatile for fast-path checks ONLY — authoritative reads are under SyncRoot or DispatchLock
        internal volatile bool IsDisposing;
        private readonly HashSet<WlEventQueue> _queues = new();
        private readonly Dictionary<uint, WlProxy> _proxyMap = new();
        private readonly object _proxyMapLock = new();

        IntPtr IWlTargetQueue.QueueHandle => IntPtr.Zero;
        WlDisplay IWlTargetQueue.Display => this;
        WlEventQueue? IWlTargetQueue.ManagedQueue => null;
        QueueDispatchLock IWlTargetQueue.DispatchLock => DispatchLock;

        private WlDisplay(IntPtr handle, WlDisplayOptions? options)
            : this(new WlProxyCreationContext(null!, null, ProxyType.Interface,
                new WlProxyHandle(handle, ownsHandle: true, WlProxyDestroyMethod.DisplayDisconnect), null))
        {
            Options = options ?? new WlDisplayOptions();
            DispatchLock = new QueueDispatchLock(this, throwOnViolation: Options.EnableDebugChecks,
                queueHandle: IntPtr.Zero);
            LibWayland.RegisterDisplay(this);
        }
        
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
        /// <para>
        /// Note: <c>wl_display</c> uses libwayland's built-in dispatcher for its events (<c>error</c>, <c>delete_id</c>)
        /// and does not support attaching a managed listener. Use the generated <c>Listener</c> class only for reference.
        /// </para>
        /// </remarks>
        public static WlDisplay Connect(string? name = null,
            WlDisplayOptions? options = null)
        {
            var handle = LibWayland.wl_display_connect(name);
            if (handle == IntPtr.Zero)
                throw new NWaylandException("Failed to connect to wayland display");
            return new WlDisplay(handle, options);
        }

        /// <summary>
        /// Connect to a Wayland display using an already-opened file descriptor.
        /// Useful for testing with socketpair or when the socket is provided externally.
        /// </summary>
        public static WlDisplay ConnectToFd(int fd,
            WlDisplayOptions? options = null)
        {
            if (fd < 0)
                throw new ArgumentOutOfRangeException(nameof(fd), "File descriptor must be non-negative");
            var handle = LibWayland.wl_display_connect_to_fd(fd);
            if (handle == IntPtr.Zero)
                throw new NWaylandException("Failed to connect to wayland display via fd");
            return new WlDisplay(handle, options);
        }
        
        /// <summary>
        /// Get a display context's file descriptor.
        /// </summary>
        /// <returns>The display object file descriptor.</returns>
        /// <remarks>
        /// Returns the file descriptor associated with a display so it can be integrated into the client's main loop.
        /// </remarks>
        public int GetFd()
        {
            if (_isDisposed) // volatile fast-path
                throw new ObjectDisposedException(nameof(WlDisplay));
            lock (SyncRoot)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(WlDisplay));
                return LibWayland.wl_display_get_fd(Handle);
            }
        }

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
        public int Dispatch()
        {
            using (DispatchLock.LockAndCheckDisplayDispose())
            using (new LibWayland.ListenerExceptionScope())
                return LibWayland.wl_display_dispatch(Handle);
        }

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
        public int DispatchPending()
        {
            using (DispatchLock.LockAndCheckDisplayDispose())
            using (new LibWayland.ListenerExceptionScope())
                return LibWayland.wl_display_dispatch_pending(Handle);
        }

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
        public int Roundtrip()
        {
            using (DispatchLock.LockAndCheckDisplayDispose())
            using (new LibWayland.ListenerExceptionScope())
                return LibWayland.wl_display_roundtrip(Handle);
        }

        /// <summary>
        /// Prepare to read events from the display's file descriptor using the default event queue.
        /// </summary>
        /// <returns>0 on success or -1 if the event queue was not empty.</returns>
        /// <remarks>
        /// This function does the same thing as <see cref="WlEventQueue.PrepareRead"/> with the default queue passed as the queue.
        /// </remarks>
        /// <seealso cref="WlEventQueue.PrepareRead"/>
        public int PrepareRead()
        {
            using (DispatchLock.LockAndCheckDisplayDispose())
                return LibWayland.wl_display_prepare_read(Handle);
        }

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
        public int ReadEvents()
        {
            // Use Lock() instead of LockAndCheckDisplayDispose() to honor the
            // prepare_read/read_events contract: every successful PrepareRead must be
            // followed by either ReadEvents or CancelRead. Throwing ODE here would
            // leak libwayland's internal reader count and deadlock other threads.
            // The native call returns -1 on a shut-down socket, which is safe.
            using (DispatchLock.Lock())
            {
                // _isDisposed means the display is fully torn down (socket disconnected,
                // all queues destroyed). The dispatch lock we just acquired is already dead
                // at this point — there is no reader count to leak.
                if (_isDisposed)
                    throw new ObjectDisposedException(GetType().FullName);
                return LibWayland.wl_display_read_events(Handle);
            }
        }

        /// <summary>
        /// Send all buffered requests on the display to the server.
        /// </summary>
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
        public int Flush()
        {
            using (DispatchLock.LockAndCheckDisplayDispose())
                return LibWayland.wl_display_flush(Handle);
        }

        
        /// <summary>
        /// Cancel read intention on the display's file descriptor.
        /// </summary>
        /// <remarks>
        /// After a thread successfully calls <c>wl_display_prepare_read()</c>, it must either call
        /// <c>wl_display_read_events()</c> or <c>wl_display_cancel_read()</c>. Failure to follow this rule will lead to a deadlock.
        /// </remarks>
        /// <seealso cref="PrepareRead"/>
        /// <seealso cref="ReadEvents"/>
        public void CancelRead()
        {
            if (_isDisposed) // volatile fast-path
                return;
            using (DispatchLock.Lock())
            {
                if (_isDisposed)
                    return;
                LibWayland.wl_display_cancel_read(Handle);
            }
        }

        // Called by WlEventQueue ctor (under SyncRoot — Monitor is reentrant)
        internal void RegisterQueue(WlEventQueue queue)
        {
            lock (SyncRoot)
                _queues.Add(queue);
        }

        // Called by WlEventQueue.Dispose — under SyncRoot
        internal void UnregisterQueue(WlEventQueue queue)
        {
            lock (SyncRoot)
                _queues.Remove(queue);
        }

        internal uint RegisterProxy(WlProxy proxy, bool setDispatcher)
        {
            lock (_proxyMapLock)
            {
                uint id;
                if (setDispatcher)
                    id = LibWayland.SetupProxyDispatcher(proxy);
                else
                    id = LibWayland.GetProxyId(proxy.Handle);
                _proxyMap[id] = proxy;
                return id;
            }
        }

        internal void UnregisterProxy(WlProxy proxy)
        {
            lock (_proxyMapLock)
            {
                if (_proxyMap.TryGetValue(proxy.Id, out var current) && ReferenceEquals(current, proxy))
                    _proxyMap.Remove(proxy.Id);
            }
        }

        internal WlProxy? FindByNative(IntPtr proxyHandle)
        {
            if (proxyHandle == IntPtr.Zero)
                return null;
            lock (_proxyMapLock)
            {
                var id = LibWayland.GetProxyId(proxyHandle);
                _proxyMap.TryGetValue(id, out var target);
                return target;
            }
        }

        internal List<WlProxy> GetAllProxies()
        {
            lock (_proxyMapLock)
                return new List<WlProxy>(_proxyMap.Values);
        }

        /// <summary>
        /// Creates a new Wayland event queue for the specified display.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if the display is being disposed.</exception>
        /// <exception cref="NWaylandException">Thrown if the native event queue creation fails.</exception>
        public WlEventQueue CreateEventQueue()
        {
            return new WlEventQueue(this);
        }

        public override void Dispose()
        {
            if (_isDisposed)
                return;

            // Step 1: Set IsDisposing under SyncRoot to prevent new dispatch/invoke/queue creation
            lock (SyncRoot)
            {
                if (_isDisposed || IsDisposing)
                    return;
                IsDisposing = true;

                // Step 2: Shutdown socket to unblock pending readers (owned only)
                if (_proxyHandle.OwnsHandle)
                    Syscall.shutdown(GetFd(), Syscall.SHUT_RDWR);
            }

            // Step 3: Dispose queues one-by-one. Each Q.Dispose() acquires display.DL → Q.DL,
            // which serializes with any in-flight default-queue dispatch that might touch
            // custom-queue proxies in callbacks. No need to hold display.DL here.
            while (true)
            {
                WlEventQueue? nextQueue;
                lock (SyncRoot)
                {
                    using var enumerator = _queues.GetEnumerator();
                    if (!enumerator.MoveNext())
                        break;
                    nextQueue = enumerator.Current;
                }
                nextQueue.Dispose();
            }

            // Step 4: Dispose remaining proxies from per-display map
            using (DispatchLock.Lock())
            {
                lock (SyncRoot)
                {
                    var proxies = GetAllProxies();
                    foreach (var proxy in proxies)
                    {
                        if (!proxy.IsDisposed && proxy != this)
                        {
                            proxy.SetQueueInternal(null);
                            proxy.Dispose();
                        }
                    }
                    _isDisposed = true;
                }
            }

            // Step 5: Unregister display and disconnect (handled by WlProxyHandle if owned)
            LibWayland.UnregisterDisplay(this);
            _proxyHandle.Dispose();
        }
        
        public INWaylandTracer? Tracer { get; set; }
    }
}
