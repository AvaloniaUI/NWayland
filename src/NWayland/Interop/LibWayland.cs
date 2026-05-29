using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop
{
    static unsafe class LibWayland
    {
        private const string Wayland = "libwayland-client.so.0";

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true, CharSet = CharSet.Ansi)]
        internal static extern IntPtr wl_display_connect(string? name);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern IntPtr wl_display_connect_to_fd(int fd);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_get_fd(IntPtr display);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_dispatch(IntPtr display);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_dispatch_pending(IntPtr display);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_roundtrip(IntPtr display);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_prepare_read(IntPtr display);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_read_events(IntPtr display);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_flush(IntPtr display);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern void wl_display_cancel_read(IntPtr display);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern void wl_display_disconnect(IntPtr display);

        [DllImport(Wayland, SetLastError = true, ExactSpelling = true)]
        internal static extern int wl_display_get_error(IntPtr display);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern uint wl_display_get_protocol_error(IntPtr display, out IntPtr @interface, out uint id);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern void wl_proxy_marshal_array(IntPtr proxy, uint opcode, WlArgument* args);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern IntPtr wl_proxy_marshal_array_constructor_versioned(IntPtr proxy, uint opcode, WlArgument* args, ref WlInterface @interface, uint version);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern IntPtr wl_proxy_marshal_array_constructor_versioned(IntPtr proxy, uint opcode, WlArgument* args, WlInterface* @interface, uint version);

        [Flags]
        internal enum WlProxyMarshalFlags : uint
        {
            None = 0,
            Destroy = 1
        }

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern IntPtr wl_proxy_marshal_array_flags(IntPtr proxy, uint opcode, WlInterface* @interface,
            uint version, WlProxyMarshalFlags flags, WlArgument* args);
        
        
        [DllImport(Wayland, ExactSpelling = true)]
        private static extern int wl_proxy_add_dispatcher(IntPtr proxy, WlProxyDispatcherDelegate dispatcherFunc, IntPtr implementation, IntPtr data);

        [DllImport(Wayland, ExactSpelling = true)]
        private static extern uint wl_proxy_get_id(IntPtr proxy);
        
        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern int wl_proxy_get_version(IntPtr proxy);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern void wl_proxy_destroy(IntPtr proxy);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern IntPtr wl_display_create_queue(IntPtr display);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern void wl_event_queue_destroy(IntPtr queue);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern int wl_display_dispatch_queue(IntPtr display, IntPtr queue);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern int wl_display_dispatch_queue_pending(IntPtr display, IntPtr queue);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern int wl_display_prepare_read_queue(IntPtr display, IntPtr queue);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern int wl_display_roundtrip_queue(IntPtr display, IntPtr queue);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern void wl_proxy_set_queue(IntPtr proxy, IntPtr queue);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern IntPtr wl_proxy_create_wrapper(IntPtr proxy);

        [DllImport(Wayland, ExactSpelling = true)]
        internal static extern void wl_proxy_wrapper_destroy(IntPtr proxy_wrapper);

        private delegate int WlProxyDispatcherDelegate(IntPtr implementation, IntPtr target, uint opcode, ref WlMessage message, WlArgument* argument);

        private static readonly Dictionary<IntPtr, WlDisplay> _displays = new();

        [ThreadStatic]
        private static ExceptionDispatchInfo? _pendingListenerException;

        private static readonly WlProxyDispatcherDelegate _dispatcher = WlProxyDispatcher;

        private static int WlProxyDispatcher(IntPtr implementation, IntPtr target, uint opcode, ref WlMessage message, WlArgument* arguments)
        {
            WlDisplay? display;
            lock (_displays)
            {
                if (!_displays.TryGetValue(implementation, out display))
                    return 0;
            }

            var proxy = display.FindByNative(target);
            if (proxy == null)
                return 0;

            try
            {
                proxy.DispatchEvent(opcode, ref message, arguments);
            }
            catch (Exception ex)
            {
                try
                {
                    display.Tracer?.Trace(proxy, true, false,
                        proxy.Interface.Events[(int)opcode],
                        ReadOnlySpan<WlTracedArgument>.Empty);
                }
                catch
                {
                    // Tracer itself threw — ignore
                }

                var pending = _pendingListenerException;
                if (pending != null)
                {
                    var exceptions = pending.SourceException is AggregateException agg
                        ? new List<Exception>(agg.InnerExceptions)
                        : new List<Exception> { pending.SourceException };
                    exceptions.Add(ex);
                    _pendingListenerException = ExceptionDispatchInfo.Capture(new AggregateException(exceptions));
                }
                else
                {
                    _pendingListenerException = ExceptionDispatchInfo.Capture(ex);
                }
            }
            return 0;
        }


        internal static void RethrowPendingListenerException()
        {
            var ex = _pendingListenerException;
            if (ex != null)
            {
                _pendingListenerException = null;
                ex.Throw();
            }
        }

        internal struct ListenerExceptionScope : IDisposable
        {
            public void Dispose() => RethrowPendingListenerException();
        }

        internal static void RegisterDisplay(WlDisplay display)
        {
            lock (_displays)
                _displays[display.Handle] = display;
        }

        internal static void UnregisterDisplay(WlDisplay display)
        {
            lock (_displays)
            {
                if (_displays.TryGetValue(display.Handle, out var current)
                    && ReferenceEquals(current, display))
                    _displays.Remove(display.Handle);
            }
        }

        internal static uint SetupProxyDispatcher(WlProxy wlProxy)
        {
            var id = wl_proxy_get_id(wlProxy.Handle);
            var idp = (IntPtr)new UIntPtr(id).ToPointer();
            var ret = wl_proxy_add_dispatcher(wlProxy.Handle, _dispatcher, wlProxy.Display.Handle, idp);
            if (ret == -1)
                throw new NWaylandException(
                    $"Failed to add dispatcher for proxy of type {wlProxy.GetType().Name}");
            return id;
        }

        internal static uint GetProxyId(IntPtr proxyHandle) => wl_proxy_get_id(proxyHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct WlMessage
    {
        public readonly byte* Name;
        public readonly byte* Signature;
        public readonly WlInterface** Types;

        public WlMessage(string name, string signature, WlInterface*[]? types)
        {
            types ??= OneNullType;
            Types = (WlInterface**)Marshal.AllocHGlobal(IntPtr.Size * types.Length);
            for (var i = 0; i < types.Length; i++)
                Types[i] = types[i];
            Name = (byte*)Marshal.StringToHGlobalAnsi(name);
            Signature = (byte*)Marshal.StringToHGlobalAnsi(signature);
        }

        private static readonly WlInterface*[] OneNullType = { null };
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct WlInterface
    {
        public readonly IntPtr Name;
        public readonly int Version;
        public readonly int MethodCount;
        public readonly WlMessage* Methods;
        public readonly int EventCount;
        public readonly WlMessage* Events;

        public WlInterface(string name, int version, WlMessage[]? methods, WlMessage[]? events)
        {
            Name = Marshal.StringToHGlobalAnsi(name);
            Version = version;
            MethodCount = methods?.Length ?? 0;
            Methods = UnmanagedCopy(methods);
            EventCount = events?.Length ?? 0;
            Events = UnmanagedCopy(events);
        }

        public static WlInterface* GeneratorAddressOf(ref WlInterface s)
        {
            fixed (WlInterface* ptr = &s)
                return ptr;
        }

        private static WlMessage* UnmanagedCopy(WlMessage[]? messages)
        {
            if (messages is null || messages.Length == 0)
                return null;
            var ptr = (WlMessage*)Marshal.AllocHGlobal(sizeof(WlMessage) * messages.Length);
            for (var c = 0; c < messages.Length; c++)
                ptr[c] = messages[c];
            return ptr;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    unsafe struct WlArgument
    {
        [FieldOffset(0)]
        public int Int32;

        [FieldOffset(0)]
        public uint UInt32;

        [FieldOffset(0)]
        public IntPtr IntPtr;

        [FieldOffset(0)]
        public WlFixed WlFixed;

        public static implicit operator WlArgument(int value) => new() { Int32 = value };

        public static implicit operator WlArgument(uint value) => new() { UInt32 = value };

        public static implicit operator WlArgument(IntPtr value) => new() { IntPtr = value };

        public static implicit operator WlArgument(WlArray* value) => new() { IntPtr = (IntPtr)value };

        public static implicit operator WlArgument(WlFixed value) => new() { WlFixed = value };

        public static implicit operator WlArgument(WlProxy? value) => new() { IntPtr = value?.Handle ?? IntPtr.Zero };

        public static readonly WlArgument NewId;
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly unsafe struct WlArray
    {
        public readonly IntPtr Size;
        public readonly IntPtr Alloc;
        public readonly IntPtr Data;

        public WlArray(IntPtr size, IntPtr alloc, IntPtr data)
        {
            Size = size;
            Alloc = alloc;
            Data = data;
        }

        public Span<T> AsSpan<T>() where T : unmanaged
        {
            var size = Size.ToInt32() / sizeof(T);
            return size == 0 ? Span<T>.Empty : new Span<T>(Data.ToPointer(), size);
        }

        public static WlArray FromPointer<T>(T* ptr, int count) where T : unmanaged
        {
            var size = new IntPtr(checked(sizeof(T) * count));
            return new WlArray(size, size, (IntPtr)ptr);
        }

        public static Span<T> SpanFromWlArrayPtr<T>(IntPtr wlArrayPointer) where T : unmanaged
        {
            if (wlArrayPointer == IntPtr.Zero)
                return Span<T>.Empty;
            return ((WlArray*)wlArrayPointer.ToPointer())->AsSpan<T>();
        }
    }
}
