using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland
{
    public abstract unsafe class WlProxy : IDisposable
    {
        private readonly WlDisplay _display;
        private readonly WlInterfaceDescription _interface;
        private readonly uint _id;
        private bool _isDisposed;
        private IWlEventsListener? _listener;
        private WlEventQueue? _queue;
        private readonly bool _ownsHandle;
        internal WlDisplay Display => _display;
        internal WlEventQueue? Queue => _queue;

        internal WlInterfaceDescription Interface => _interface;

        protected WlProxy(WlProxyCreationContext context)
        {
            _display = context.Display;
            _ownsHandle = context.OwnsHandle;
            _interface = context.Interface;
            _listener = context.Listener;
            Handle = context.Handle;
            
            _queue = context.Queue;
            
            if (_display == null!)
            {
                if (this is WlDisplay)
                    _display = (WlDisplay)this;
                else
                    throw new ArgumentNullException("display");
            }
            Version = LibWayland.wl_proxy_get_version(Handle);
            if (Version == 0 && this is not WlDisplay and not WlRegistry)
                throw new InvalidOperationException();
            if (this is WlDisplay)
                return;
            if (_ownsHandle)
                _id = LibWayland.RegisterProxy(this, context.SetDispatcher);
        }
        
        public int Version { get; }

        public IntPtr Handle { get; }
        
        internal void DispatchEvent(uint opcode, ref WlMessage nativeMessage, WlArgument* arguments)
        {
            // Sanity checks
            // TODO: trigger a warning or something if this happens for some weird reason


            if (opcode >= Interface.Events.Count)
                return;
            var message = Interface.Events[(int)opcode];
            if(!strcmp(message.Name, nativeMessage.Name))
                return;
            if(!strcmp(message.Signature, nativeMessage.Signature))
                return;
            
            using var args = new WlEventArgsImpl(arguments, this, opcode, message);
            var rargs = new WlEventArgs(args);
            WaylandTracer.TraceEvent(Display, rargs);
            _listener?.DispatchEvent(rargs);
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        void UnregisterProxyBeforeDestroy()
        {
            GC.SuppressFinalize(this);
            LibWayland.UnregisterProxy(_id);
            _isDisposed = true;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed || !disposing)
                return;
            LibWayland.UnregisterProxy(_id);
            if (_ownsHandle)
                LibWayland.wl_proxy_destroy(Handle);
            _isDisposed = true;
        }

        private static bool strcmp(string left, byte* right)
        {
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
            IWlEventsListener? listener, WlEventQueue? queue, uint? newIdVersion)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().FullName);

            if (queue != null && listener == null)
            {
                throw new InvalidOperationException(
                    "Specifying a queue without a listener is not allowed because of possible race conditions");
            }
            
            var method = this._interface.Methods[(int)call.OpCode];
            if (method.SinceVersion > Version)
                throw new InvalidOperationException(
                    $"Method {method.Name} is not supported for interface version {Version}");

            var needReleaseQueueLock = false;
            if (queue != null && proxyType != null)
            {
                // Make sure that queue is not dispatching on another thread
                Monitor.Enter(queue.DispatchLock);
                needReleaseQueueLock = true;
            }

            try
            {
                lock (_display.SyncRoot)
                {
                    if (_isDisposed)
                        throw new ObjectDisposedException(this.GetType().FullName);

                    Span<WlArgument> args = stackalloc WlArgument[(call.NormalArgs?.Count ?? 0) + (call.ObjectArgs?.Count ?? 0)];
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
                                    args[c] = proxyArg;
                                }
                                else if (arg.Code is WaylandArgumentCodes.String)
                                {
                                    var s = (string?)objArg;
                                    if (s == null)
                                        args[c] = default;
                                    else
                                    {
                                        var ptr = Marshal.StringToHGlobalAnsi(s);
                                        (toDealloc ??= new()).Add((ptr, false));
                                        args[c] = ptr;
                                    }
                                }
                                else
                                {
                                    var arr = (byte[])objArg!;
                                    var handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
                                    (toDealloc ??= new()).Add((GCHandle.ToIntPtr(handle), true));
                                    var wlArrayPtr = (WlArray*)Marshal.AllocHGlobal(Unsafe.SizeOf<WlArray>());
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
                            LibWayland.wl_proxy_set_queue(wrapper.Value, queue.Handle);
                        }


                        // TODO: Verify that constructed entity is from the same protocol,
                        // otherwise where are we going to get a version

                        var newProxyVersion = newIdVersion ?? (uint)Version;
                        
                        if (method.IsDestructor)
                            UnregisterProxyBeforeDestroy();


                        IntPtr rv;
                        fixed (WlArgument* pargs = args)
                        {
                            WaylandTracer.TraceCall(this, call, pargs);
                            rv = LibWayland.wl_proxy_marshal_array_flags(callTargetProxy, call.OpCode,
                                proxyType != null ? proxyType.Interface.GetNative() : null, newProxyVersion, flags,
                                pargs);

                        }
                        

                        if (proxyType != null)
                            return proxyType.Factory(new WlProxyCreationContext(
                                _display, queue ?? _queue,
                                proxyType.Interface, rv, true, listener));

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
                                    Marshal.FreeHGlobal(entry.ptr);
                            }
                    }
                }
            }
            finally
            {
                if (needReleaseQueueLock)
                    Monitor.Exit(queue!.DispatchLock);
            }
        }

        internal void Invoke(ref WaylandCallBuilder call)
        {
            InvokeCore(ref call, null, null, null, null);
        }

        internal WlProxy InvokeNewId(ref WaylandCallBuilder call, WlProxyTypeDescriptor proxyType,
            IWlEventsListener? listener, WlEventQueue? queue, uint? newIdVersion)
        {
            if (proxyType == null)
                throw new ArgumentNullException();
            
            return InvokeCore(ref call, proxyType, listener, queue, newIdVersion);
        }

        protected static WlProxy Import(WlProxyTypeDescriptor descriptor, WlDisplay display, WlEventQueue? queue,
            IntPtr handle, bool ownsHandle, IWlEventsListener? listener)
        {
            return descriptor.Factory(new WlProxyCreationContext(display, queue, descriptor.Interface, handle,
                ownsHandle,
                listener, listener != null && ownsHandle));
        }
    }
}
