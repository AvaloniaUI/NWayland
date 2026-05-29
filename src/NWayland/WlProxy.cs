using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland
{
    public abstract unsafe class WlProxy : IWaylandCallTarget, IDisposable
    {
        private readonly WlDisplay _display;
        private readonly WlInterfaceDescription _interface;
        private readonly uint _id;
        // volatile for fast-path checks ONLY — all authoritative reads are under SyncRoot
        private protected volatile bool _isDisposed;
        private IWlEventsListener? _listener;
        // volatile for fast-path checks ONLY — avoids stale reads on weakly-ordered
        // architectures (ARM). Must always be rechecked under SyncRoot.
        private volatile WlEventQueue? _queue;
        private Dictionary<object, object>? _tags;
        private protected readonly WlProxyHandle _proxyHandle;
        public WlDisplay Display => _display;
        public WlEventQueue? Queue => _queue;

        private QueueDispatchLock CurrentDispatchLock
        {
            get
            {
                var queue = _queue;
                return queue != null ? queue.DispatchLock : _display.DispatchLock;
            }
        }

        // Used by WlEventQueue.Dispose to update queue tracking without native call
        internal void SetQueueInternal(WlEventQueue? queue) => _queue = queue;

        internal WlInterfaceDescription Interface => _interface;

        /// <summary>
        /// Acquires the dispatch lock for this proxy's current queue, retrying if
        /// SetQueue changes the queue between reading CurrentDispatchLock and validation.
        /// Safe because SetQueue must hold the old dispatch lock to change <c>_queue</c>,
        /// so the queue cannot change while the returned scope is alive.
        /// </summary>
        private QueueDispatchLock.Scope LockCurrentQueue()
        {
            while (true)
            {
                var dispatchLock = CurrentDispatchLock;
                var scope = dispatchLock.Lock();
                lock (_display.SyncRoot)
                {
                    if (CurrentDispatchLock.CompareTo(dispatchLock) != 0)
                    {
                        scope.Dispose();
                        continue;
                    }
                    return scope;
                }
            }
        }

        protected WlProxy(WlProxyCreationContext context)
        {
            _display = context.Display;
            _interface = context.Interface;
            _listener = context.Listener;
            _proxyHandle = context.ProxyHandle;
            
            _queue = context.Queue;
            
            if (_display == null!)
            {
                if (this is WlDisplay display)
                {
                    _display = display;
                }
                else
                    throw new ArgumentNullException("display");
            }
            Version = LibWayland.wl_proxy_get_version(_proxyHandle.Handle);
            if (Version == 0 && this is not WlDisplay and not WlRegistry)
                throw new InvalidOperationException();
            if (this is WlDisplay)
                return;
            if (_proxyHandle.OwnsHandle)
                _id = _display.RegisterProxy(this, context.SetDispatcher);
            _queue?.RegisterProxy(this);
        }
        
        public int Version { get; }

        public uint Id => _id;

        public bool IsDisposed => _isDisposed;

        /// <summary>
        /// General-purpose tag dictionary for associating arbitrary data with this proxy.
        /// Not thread-safe — callers must synchronize access externally if used across threads.
        /// </summary>
        public IDictionary<object, object> Tags => _tags ??= new Dictionary<object, object>();

        public IntPtr Handle => _proxyHandle.Handle;
        
        internal void DispatchEvent(uint opcode, ref WlMessage nativeMessage, WlArgument* arguments)
        {
            if (opcode >= Interface.Events.Count)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"WlProxy({GetType().Name}): received opcode {opcode} but interface only has {Interface.Events.Count} events");
                return;
            }
            var message = Interface.Events[(int)opcode];
            if(!CompareWithNativeAsciiString(message.Name, nativeMessage.Name))
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"WlProxy({GetType().Name}): event name mismatch for opcode {opcode}: expected '{message.Name}'");
                return;
            }
            if(!CompareWithNativeAsciiString(message.Signature, nativeMessage.Signature))
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"WlProxy({GetType().Name}): signature mismatch for event '{message.Name}' (opcode {opcode})");
                return;
            }
            
            using var args = new WlEventArgsImpl(arguments, this, opcode, message);
            var rargs = new WlEventArgs(args);
            WaylandTracer.TraceEvent(Display, rargs);
            _listener?.DispatchEvent(rargs);
        }
        
        /// <summary>
        /// Disposes this proxy by unregistering it and destroying the native handle.
        /// </summary>
        /// <remarks>
        /// This frees the client-side proxy via wl_proxy_destroy only; it deliberately does
        /// NOT send any protocol destructor request to the compositor. Sending the destructor
        /// is the application's responsibility via the generated destructor method
        /// (e.g. <c>wl_surface.Destroy()</c>), after which calling Dispose is a safe no-op.
        ///
        /// Dispose cannot pick the destructor automatically: which destructor to send — or
        /// whether to send one at all — can depend on runtime protocol state. For example,
        /// ext_session_lock_v1 exposes both <c>destroy</c> and <c>unlock_and_destroy</c>, and
        /// the correct choice depends on whether the <c>locked</c> event was received; picking
        /// the wrong one is a protocol error or leaves the session locked. Some destructors also
        /// take arguments. So the destructor choice must stay with the caller.
        /// </remarks>
        public virtual void Dispose()
        {
            if (_isDisposed)
                return;
            using var dispatchScope = LockCurrentQueue();
            lock (_display.SyncRoot)
            {
                if (_isDisposed)
                    return;
                _queue?.UnregisterProxy(this);
                _display.UnregisterProxy(this);
                _isDisposed = true;
                if (_proxyHandle.OwnsHandle)
                {
                    if (_display._isDisposed)
                    {
                        _proxyHandle.TakeHandle();
                        WaylandTracer.TraceDestroy(this, false, true);
                        System.Diagnostics.Trace.TraceWarning(
                            $"WlProxy of type {GetType().Name} (id={_id}) disposed after display disconnect. Native handle leaked.");
                    }
                    else
                    {
                        WaylandTracer.TraceDestroy(this, false, false);
                        _proxyHandle.Dispose();
                    }
                }
                else
                {
                    _proxyHandle.TakeHandle();
                }
            }
        }

        void UnregisterProxyBeforeDestroy()
        {
            _queue?.UnregisterProxy(this);
            _display.UnregisterProxy(this);
            _isDisposed = true;
            _proxyHandle.TakeHandle();
        }

        private static bool CompareWithNativeAsciiString(string left, byte* right)
        {
            if (right == null)
                return left.Length == 0;
            for (var c = 0;; c++)
            {
                if (left.Length <= c)
                {
                    // Check if we've reached the end of native string as well
                    return right[c] == 0;
                }
                
                // Reached the end of native before managed
                if (right[c] == 0)
                    return false;
                
                // Character mismatch
                if (left[c] != right[c])
                    return false;
            }
        }

        private unsafe WlProxy? InvokeCore(ref WaylandCallBuilder call, WlProxyTypeDescriptor? proxyType,
            IWlEventsListener? listener, IWlTargetQueue? queue, uint? newIdVersion)
        {
            var method = this._interface.Methods[(int)call.OpCode];

            if (_isDisposed || _display.IsDisposing)
            {
                if (method.IsDestructor || _isDisposed)
                    return null;
                throw new ObjectDisposedException(GetType().FullName);
            }

            if (queue != null && listener == null)
            {
                throw new InvalidOperationException(
                    "Specifying a queue without a listener is not allowed because of possible race conditions");
            }

            if (queue != null && queue.Display != _display)
                throw new ArgumentException("Queue belongs to a different WlDisplay", nameof(queue));
            
            if (method.SinceVersion > Version)
                throw new InvalidOperationException(
                    $"Method {method.Name} is not supported for interface version {Version}");

            // Lock acquisition: dispatch lock needed only when creating a new proxy.
            // Destructor requests (proxyType == null, IsDestructor) don't need the dispatch lock:
            // libwayland internally ref-counts wl_proxy instances, so the native proxy remains
            // alive for any in-flight dispatch callback even after WL_MARSHAL_FLAG_DESTROY.
            // - Inherited queue (queue == null): retry-aware LockCurrentQueue
            // - Explicit queue: queue's DispatchLock directly
            // - No new proxy (proxyType == null): no dispatch lock
            using var dispatchScope = proxyType != null
                ? (queue != null ? queue.DispatchLock.Lock() : LockCurrentQueue())
                : default;

            lock (_display.SyncRoot)
                return InvokeCoreUnderLock(ref call, method, proxyType, listener, queue, newIdVersion);
        }

        private unsafe WlProxy? InvokeCoreUnderLock(ref WaylandCallBuilder call, WlMessageDescription method,
            WlProxyTypeDescriptor? proxyType, IWlEventsListener? listener, IWlTargetQueue? queue,
            uint? newIdVersion)
        {
            if (_isDisposed || _display.IsDisposing)
            {
                if (method.IsDestructor || _isDisposed)
                    return null;
                throw new ObjectDisposedException(GetType().FullName);
            }

            var argCount = (call.NormalArgs?.Count ?? 0) + (call.ObjectArgs?.Count ?? 0);
            if (argCount > 32)
                throw new InvalidOperationException($"Too many arguments ({argCount}) for wayland call");
            Span<WlArgument> args = stackalloc WlArgument[argCount];
            int normalIndex = 0, objIndex = 0;
            List<(IntPtr ptr, bool gcHandle)>? toDealloc = null; // TODO: pool
            IntPtr? wrapper = null;
            try
            {
                for (var c = 0; c < method.Arguments.Count; c++)
                {
                    var arg = method.Arguments[c];
                    if (arg.Code is WaylandArgumentCodes.Object or WaylandArgumentCodes.String
                        or WaylandArgumentCodes.Array)
                    {
                        var objArg = call.ObjectArgs[objIndex];
                        objIndex++;
                        if (objArg == null && !arg.AllowNull)
                            throw new ArgumentNullException(); // TODO: Name
                        if (arg.Code is WaylandArgumentCodes.Object)
                        {
                            var proxyArg = (WlProxy?)objArg;
                            if (proxyArg?._isDisposed == true)
                                throw new ObjectDisposedException(objArg.GetType().FullName);
                            if (proxyArg != null && proxyArg._display != _display)
                                throw new ArgumentException("Proxy argument belongs to a different WlDisplay");
                            args[c] = proxyArg;
                        }
                        else if (arg.Code is WaylandArgumentCodes.String)
                        {
                            var s = (string?)objArg;
                            if (s == null)
                                args[c] = default;
                            else
                            {
                                var ptr = Marshal.StringToCoTaskMemUTF8(s);
                                (toDealloc ??= new()).Add((ptr, false));
                                args[c] = ptr;
                            }
                        }
                        else
                        {
                            // Per Wayland protocol spec, allow-null can only be used with "string"
                            // or "object" types — never with "array". So objArg is never null here.
                            var arr = (byte[])objArg!;
                            var handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
                            (toDealloc ??= new()).Add((GCHandle.ToIntPtr(handle), true));
                            var wlArrayPtr = (WlArray*)Marshal.AllocCoTaskMem(Unsafe.SizeOf<WlArray>());
                            *wlArrayPtr = WlArray.FromPointer<byte>((byte*)handle.AddrOfPinnedObject(),
                                arr.Length);
                            toDealloc.Add(((IntPtr)wlArrayPtr, false));

                            args[c] = wlArrayPtr;
                        }
                    }
                    // TODO: Array
                    else
                    {
                        args[c] = call.NormalArgs[normalIndex];
                        normalIndex++;
                    }
                }

                var flags = method.IsDestructor ? LibWayland.WlProxyMarshalFlags.Destroy : default;

                IntPtr callTargetProxy = Handle;
                if (proxyType != null && queue != null)
                {
                    wrapper = callTargetProxy = LibWayland.wl_proxy_create_wrapper(Handle);
                    if (wrapper.Value == IntPtr.Zero)
                        throw new OutOfMemoryException("wl_proxy_create_wrapper returned NULL");
                    LibWayland.wl_proxy_set_queue(wrapper.Value, queue.QueueHandle);
                }


                // TODO: Verify that constructed entity is from the same protocol,
                // otherwise where are we going to get a version

                var newProxyVersion = newIdVersion
                                      ?? (proxyType is { Frozen: true }
                                          ? (uint)proxyType.Interface.Version
                                          : (uint)Version);
                    
                // Unregister before marshal: wl_proxy_marshal_array_flags with the Destroy flag
                // always destroys the native proxy, even if the request itself fails to send.
                // We must detach the managed handle first to prevent double-free.
                if (method.IsDestructor)
                    UnregisterProxyBeforeDestroy();


                IntPtr rv;
                fixed (WlArgument* pargs = args)
                {
                    rv = LibWayland.wl_proxy_marshal_array_flags(callTargetProxy, call.OpCode,
                        proxyType != null ? proxyType.Interface.GetNative() : null, newProxyVersion, flags,
                        pargs);

                    // Trace after marshalling: the new_id's object id is assigned by libwayland
                    // during the marshal and is only available via the returned proxy (rv).
                    WaylandTracer.TraceCall(this, call, pargs, rv);
                }
                    

                if (proxyType != null)
                {
                    if (rv == IntPtr.Zero)
                        throw new NWaylandException(
                            $"wl_proxy_marshal_array_flags returned NULL for {proxyType.Interface.Name}");
                    var targetQueue = queue != null ? queue.ManagedQueue : _queue;
                    var proxyHandle = new WlProxyHandle(rv, ownsHandle: true);
                    try
                    {
                        var newProxy = proxyType.Factory(new WlProxyCreationContext(
                            _display, targetQueue,
                            proxyType.Interface, proxyHandle, listener));
                        return newProxy;
                    }
                    catch
                    {
                        proxyHandle.Dispose();
                        throw;
                    }
                }

                return null;

            }
            finally
            {
                if (wrapper.HasValue)
                    LibWayland.wl_proxy_wrapper_destroy(wrapper.Value);
                if (toDealloc != null)
                    foreach (var entry in toDealloc)
                    {
                        if (entry.gcHandle)
                            GCHandle.FromIntPtr(entry.ptr).Free();
                        else
                            Marshal.FreeCoTaskMem(entry.ptr);
                    }
            }
        }

        void IWaylandCallTarget.Invoke(ref WaylandCallBuilder call)
        {
            InvokeCore(ref call, null, null, null, null);
        }

        object IWaylandCallTarget.InvokeNewId(ref WaylandCallBuilder call, WlProxyTypeDescriptor proxyType,
            IWlEventsListener? listener, IWlTargetQueue? queue, uint? newIdVersion)
        {
            if (proxyType == null)
                throw new ArgumentNullException();
            
            return InvokeCore(ref call, proxyType, listener, queue, newIdVersion)
                   ?? throw new ObjectDisposedException(GetType().FullName);
        }

        protected static WlProxy Import(WlProxyTypeDescriptor descriptor, WlDisplay display, IWlTargetQueue? queue,
            IntPtr handle, bool ownsHandle, IWlEventsListener? listener)
        {
            if (!ownsHandle && listener != null)
                throw new InvalidOperationException(
                    "Cannot attach a listener to a borrowed (non-owned) proxy. " +
                    "Borrowed proxies are not registered with the dispatcher and would silently drop events.");
            if (queue != null && queue.Display != display)
                throw new ArgumentException("Queue belongs to a different WlDisplay", nameof(queue));

            var targetLock = queue?.DispatchLock ?? display.DispatchLock;
            using (targetLock.Lock())
            {
                lock (display.SyncRoot)
                {
                    if (display.IsDisposing)
                        throw new ObjectDisposedException(nameof(WlDisplay));

                    if (queue != null)
                        LibWayland.wl_proxy_set_queue(handle, queue.QueueHandle);

                    var proxyHandle = new WlProxyHandle(handle, ownsHandle);
                    try
                    {
                        return (WlProxy)descriptor.Factory(new WlProxyCreationContext(display, queue?.ManagedQueue,
                            descriptor.Interface, proxyHandle,
                            listener, listener != null && ownsHandle));
                    }
                    catch
                    {
                        proxyHandle.Dispose();
                        throw;
                    }
                }
            }
        }


        /// <summary>
        /// Changes the queue associated with this object. Acquires dispatch locks for both
        /// old and new queues in deterministic order (sorted by queue handle) to avoid deadlocks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// After changing queues, the caller should dispatch the old queue
        /// (e.g. via <see cref="WlEventQueue.DispatchPending"/>) to process any events
        /// that were already queued for this proxy before the switch took effect.
        /// </para>
        /// </remarks>
        public void SetQueue(IWlTargetQueue? queue)
        {
            if (queue != null && queue.Display != _display)
                throw new ArgumentException("Queue belongs to a different WlDisplay", nameof(queue));

            var newLock = queue?.DispatchLock ?? _display.DispatchLock;

            if (_display.IsDisposing)
                throw new ObjectDisposedException(nameof(WlProxy));
            while (true)
            {
                var oldLock = CurrentDispatchLock;

                // Sort by queue handle for deterministic lock ordering
                QueueDispatchLock first, second;
                if (oldLock.CompareTo(newLock) <= 0)
                {
                    first = oldLock;
                    second = newLock;
                }
                else
                {
                    first = newLock;
                    second = oldLock;
                }

                using (first.Lock())
                using (second.Lock())
                {
                    lock (_display.SyncRoot)
                    {
                        if (IsDisposed || _display.IsDisposing)
                            throw new ObjectDisposedException(nameof(WlProxy));

                        // Check if queue changed while we were acquiring locks
                        if (CurrentDispatchLock.CompareTo(oldLock) != 0)
                            continue; // Retry — queue was changed by another thread

                        var newQueue = queue?.ManagedQueue;
                        if (_queue == newQueue)
                            return; // Already on the requested queue

                        LibWayland.wl_proxy_set_queue(Handle, queue?.QueueHandle ?? IntPtr.Zero);
                        _queue?.UnregisterProxy(this);
                        _queue = newQueue;
                        _queue?.RegisterProxy(this);
                        return;
                    }
                }
            }
        }
    }
}
