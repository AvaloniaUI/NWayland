using System;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public ref struct WlEventArgs
{
    private WlEventArgsImpl _impl;

    public WlProxy Sender => _impl.Sender;
    public uint Opcode => _impl.Opcode;
    
    public int GetInt32(int num) => _impl.Raw(num).Int32;
    public uint GetUInt32(int num) => _impl.Raw(num).UInt32;
    public WlFixed GetWlFixed(int num) => _impl.Raw(num).WlFixed;

    public unsafe ReadOnlySpan<T> GetArray<T>(int num) where T : unmanaged
    {
        if (_impl.Message.Arguments[num].Code != WaylandArgumentCodes.Array)
            throw new InvalidOperationException();
        var arr = (WlArray*)_impl.Arguments[num].IntPtr;
        if (arr ==null)
            return default;
        return arr->AsSpan<T>();
    }

    public unsafe string? GetString(int num)
    {
        if (_impl.Message.Arguments[num].Code != WaylandArgumentCodes.String)
            throw new InvalidOperationException();
        var ptr = _impl.Arguments[num].IntPtr;
        if (ptr == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(ptr);
    }

    public T GetProxy<T>(int num) where T : WlProxy => _impl.GetProxy<T>(num);
    public NewId<T, TListener> GetNewId<T, TListener>(int num) where T : WlProxy where TListener : class, IWlEventsListener => _impl.GetNewId<T, TListener>(num);

    internal WlEventArgs(WlEventArgsImpl impl) => _impl = impl;
}

unsafe class WlEventArgsImpl : IDisposable
{
    private readonly WlProxy _proxy;
    public WlMessageDescription Message { get; }
    public WlArgument* Arguments { get; }
    public uint Opcode { get; }
    public WlProxy Sender => _proxy;
    private ulong _consumed;

    public WlEventArgsImpl(WlArgument* arguments, WlProxy proxy, uint opcode, WlMessageDescription message)
    {
        Arguments = arguments;
        Opcode = opcode;
        _proxy = proxy;
        Message = message;
    }

    public T? GetProxy<T>(int num) where T : WlProxy
    {
        var proxyPtr = Raw(num).IntPtr;
        if (Message.Arguments[num].Code == WaylandArgumentCodes.Object)
        {
            // TODO: emphemeral proxies that we auto-dispose on exit
            return (T?)LibWayland.FindByNative(proxyPtr);
        }
        throw new InvalidOperationException();
    }

    public NewId<T, TListener> GetNewId<T, TListener>(int num) where T : WlProxy where TListener : class, IWlEventsListener
    {
        return new NewId<T, TListener>(new(this, num));
    }

    public T GetNewIdProxy<T>(int num, IWlEventsListener? listener) where T : WlProxy => (T)GetNewIdProxy(num, listener);
    WlProxy GetNewIdProxy(int num, IWlEventsListener? listener)
    {
        var proxyPtr = Raw(num).IntPtr;
        var bit = 1ul << num;
        if ((_consumed & bit) != 0)
            throw new InvalidOperationException("Already consumed");
        if (Message.Arguments[num].Code == WaylandArgumentCodes.NewId)
        {
            var proxy = Message.Arguments[num].ProxyType!.Factory(
                new WlProxyCreationContext(_proxy.Display, _proxy.Queue,
                    Message.Arguments[num].ProxyType!.Interface,
                    proxyPtr, true, listener))!;
            _consumed |= bit;
            return proxy;
        }
        throw new InvalidOperationException();
    }

    public ref WlArgument Raw(int offset)
    {
        if (offset < 0 || offset >= Message.Arguments.Count)
            throw new IndexOutOfRangeException();
        return ref Arguments[offset];
    }

    public void Dispose()
    {
        for (var c = 0; c < Message.Arguments.Count; c++)
        {
            if (Message.Arguments[c].Code == WaylandArgumentCodes.NewId)
            {
                var bit = 1ul << c;
                if ((_consumed & bit) == 0) 
                    GetNewIdProxy(c, null).Dispose();
            }
        }
    }
}