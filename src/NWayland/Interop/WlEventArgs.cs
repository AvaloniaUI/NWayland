using System;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public ref struct WlEventArgs
{
    internal WlEventArgsImpl Impl;

    public int GetInt(int num) => Impl.Raw(num).Int32;
    public uint GetUInt(int num) => Impl.Raw(num).UInt32;
    public WlFixed GetFixed(int num) => Impl.Raw(num).WlFixed;

    public unsafe ReadOnlySpan<T> GetArray<T>(int num) where T : unmanaged
    {
        if (Impl.Message.Arguments[num].Code != WaylandArgumentCodes.Array)
            throw new InvalidOperationException();
        var arr = (WlArray*)Impl.Arguments[num].IntPtr;
        if (arr ==null)
            return default;
        return arr->AsSpan<T>();
    }

    public unsafe string? GetString(int num)
    {
        if (Impl.Message.Arguments[num].Code != WaylandArgumentCodes.String)
            throw new InvalidOperationException();
        var ptr = Impl.Arguments[num].IntPtr;
        if (ptr == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(ptr);
    }

    public T GetProxy<T>(int num) where T : WlProxy => Impl.GetProxy<T>(num);
}

unsafe class WlEventArgsImpl : IDisposable
{
    private readonly WlProxy _proxy;
    public WlMessageDescription Message { get; }
    public WlArgument* Arguments { get; }

    public WlEventArgsImpl(WlArgument* arguments, WlProxy proxy, WlMessageDescription message)
    {
        Arguments = arguments;
        _proxy = proxy;
        Message = message;
    }

    // TODO: Reference proxies in advance and do something about new-id and consumption of those
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

    public NewId<T> GetNewId<T>(int num) where T : WlProxy
    {
        var proxyPtr = Raw(num).IntPtr;
        if (Message.Arguments[num].Code == WaylandArgumentCodes.NewId)
        {
            var proxy = (T)Message.Arguments[num].ProxyType?.Factory(new WlProxyContext
            {
                Display = _proxy.Display,
                Queue = _proxy.Queue
            }, proxyPtr, Message.Arguments[num].ProxyType!.Interface, true)!;
            return new NewId<T>(new NewIdImpl<T>(proxy));
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
         // TODO: "unlock" proxies
    }
}