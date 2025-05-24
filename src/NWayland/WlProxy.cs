using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        internal WlDisplay Display => _display;
        internal WlEventQueue? Queue => _queue;

        internal WlInterfaceDescription Interface => _interface;

        protected WlProxy(WlProxyCreationContext context)
        {
            if (!context.OwnsHandle)
                throw new NotSupportedException();
            _display = context.Display;
            
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
            if (this is WlDisplay)
                return;
            // TODO: adjust
            _id = LibWayland.RegisterProxy(this);
        }
        
        protected WlProxy(IntPtr handle, int version)
        {
            Version = version;
            Handle = handle;
            if (this is WlDisplay)
                return;
            // TODO: adjust
            _id = LibWayland.RegisterProxy(this);
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
            _listener?.DispatchEvent(new (args));
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed || !disposing)
                return;
            LibWayland.UnregisterProxy(_id);
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
            IWlEventsListener? listener, WlEventQueue? queue)
        {
            var method = this._interface.Methods[(int)call.OpCode];
            if (method.SinceVersion > Version)
                throw new InvalidOperationException(
                    $"Method {method.Name} is not supported for interface version {Version}");
            
            lock (_display.SyncRoot)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(this.GetType().FullName);
                WlArgument* args = stackalloc WlArgument[call.NormalArgs?.Count ?? 0 + call.ObjectArgs?.Count ?? 0];
                int normalIndex = 0, objIndex = 0;
                List<IntPtr>? toDealloc = null; // TODO: pool
                try
                {
                    for (var c = 0; c < method.Arguments.Count; c++)
                    {
                        var arg = method.Arguments[c];
                        if (arg.Code is WaylandArgumentCodes.Object or WaylandArgumentCodes.String)
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
                            else
                            {
                                var s = (string?)objArg;
                                if (s == null)
                                    args[c] = default;
                                else
                                {
                                    var ptr = Marshal.StringToHGlobalAnsi(s);
                                    (toDealloc ??= new()).Add(ptr);
                                    args[c] = ptr;
                                }
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

                    // TODO: Verify that constructed entity is from the same protocol,
                    // otherwise where are we going to get a version
                    var rv = LibWayland.wl_proxy_marshal_array_flags(Handle, call.OpCode,
                        proxyType != null ? proxyType.Interface.GetNative() : null, (uint)Version, flags, args);

                    if (proxyType != null)
                        return proxyType.Factory(new WlProxyCreationContext(
                            _display, queue ?? _queue,
                            proxyType.Interface, rv, true, listener));

                    return null;
                }
                finally
                {
                    if(toDealloc != null)
                        foreach (var ptr in toDealloc)
                            Marshal.FreeHGlobal(ptr);
                }
            }
        }

        internal void Invoke(ref WaylandCallBuilder call)
        {
            InvokeCore(ref call, null, null, null);
        }

        internal WlProxy InvokeNewId(ref WaylandCallBuilder call, WlProxyTypeDescriptor proxyType, IWlEventsListener? listener, WlEventQueue? queue)
        {
            if (proxyType == null || listener == null)
                throw new ArgumentNullException();
            return InvokeCore(ref call, proxyType, listener, queue);
        }
    }
}
