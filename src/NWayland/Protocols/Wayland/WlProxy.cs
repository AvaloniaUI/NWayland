using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NWayland.Interop;

namespace NWayland.Protocols.Wayland
{
    public abstract unsafe class WlProxy : IDisposable
    {
        private readonly WlDisplay _display;
        private readonly WlInterfaceDescription _interface;
        private readonly uint _id;
        private bool _isDisposed;
        private WlEventQueue? _queue;

        protected WlProxy(WlProxyContext context, IntPtr handle, WlInterfaceDescription @interface, bool ownsHandle = true)
        {
            if (!ownsHandle)
                throw new NotSupportedException();
            var display = context.Display;
            _queue = context.Queue;
            
            if (display == null!)
            {
                if (this is WlDisplay)
                    display = (WlDisplay)this;
                else
                    throw new ArgumentNullException(nameof(display));
            }
            _display = display;
            _interface = @interface;
            Version = LibWayland.wl_proxy_get_version(handle);
            Handle = handle;
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

        protected abstract WlInterface* GetWlInterface();

        protected abstract void DispatchEvent(uint opcode, WlArgument* arguments);

        internal void DispatchEvent(uint opcode, ref WlMessage message, WlArgument* arguments)
        {
            // Sanity checks
            // TODO: trigger a warning or something if this happens for some weird reason
            var @interface = GetWlInterface();
            if (opcode >= @interface->EventCount)
                return;
            var protocolMsg = @interface->Events[opcode];
            if(!strcmp(protocolMsg.Name, message.Name))
                return;
            if(!strcmp(protocolMsg.Signature, message.Signature))
                return;
            DispatchEvent(opcode, arguments);
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

        protected static T? FromNative<T>(IntPtr proxy) where T : WlProxy => proxy == IntPtr.Zero ? null : LibWayland.FindByNative(proxy) as T;

        private static bool strcmp(byte* left, byte* right)
        {
            for (var c = 0;; c++)
            {
                if (left[c] != right[c])
                    return false;
                if (left[c] == 0)
                    return true;
            }
        }

        private unsafe WlProxy? InvokeCore(ref WaylandCallBuilder call, WlProxyTypeDescriptor? proxyType,
            IWlEventListener? listener, WlEventQueue? queue)
        {
            var method = this._interface.Methods[(int)call.OpCode];
            if (method.SinceVersion > Version)
                throw new InvalidOperationException(
                    $"Method {method.Name} is not supported for interface version {Version}");

            if (proxyType != null && listener == null && proxyType.Interface.Events.Count > 0)
                throw new InvalidOperationException(
                    "It's mandatory to pass a listener to an interface containing events");
            
            lock (_display.SyncRoot)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(this.GetType().FullName);
                WlArgument* args = stackalloc WlArgument[call.NormalArgs.Count + call.ObjectArgs.Count];
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
                        return proxyType.Factory(new WlProxyContext()
                        {
                            Display = _display,
                            Queue = queue ?? _queue
                        }, rv, proxyType.Interface, true);

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

        public unsafe void Invoke(ref WaylandCallBuilder call)
        {
            InvokeCore(ref call, null, null, null);
        }

        public unsafe WlProxy InvokeNewId(ref WaylandCallBuilder call, WlProxyTypeDescriptor proxyType, IWlEventListener? listener, WlEventQueue? queue)
        {
            if (proxyType == null || listener == null)
                throw new ArgumentNullException();
            return InvokeCore(ref call, proxyType, listener, queue);
        }
    }
}
