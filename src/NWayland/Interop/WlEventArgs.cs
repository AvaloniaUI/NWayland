using System;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public ref struct WlEventArgs
{
    private IWlEventArgsImpl _impl;

    public object Sender => _impl.Sender;
    public uint Opcode => _impl.Opcode;

    public int GetInt32(int num) => _impl.GetInt32(num);
    public uint GetUInt32(int num) => _impl.GetUInt32(num);
    public WlFixed GetWlFixed(int num) => _impl.GetWlFixed(num);
    public string? GetString(int num) => _impl.GetString(num);
    public WaylandFd GetFd(int num) => new WaylandFd(_impl, num);

    public ReadOnlySpan<T> GetArray<T>(int num) where T : unmanaged
    {
        var bytes = _impl.GetArrayBytes(num);
        if (bytes == null)
            return default;
        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    public T? GetProxy<T>(int num) where T : WlProxy => _impl.GetProxy<T>(num);

    public NewId<T, TListener> GetNewId<T, TListener>(int num)
        where T : WlProxy where TListener : class, IWlEventsListener
        => new NewId<T, TListener>(new NewIdImpl<T>(_impl, num));

    public NWayland.Server.WlResource GetServerNewId(int num) => _impl.GetServerNewId(num);
    public NewId<T, TListener> GetServerNewId<T, TListener>(int num)
        where T : NWayland.Server.WlResource where TListener : class, IWlEventsListener
        => new NewId<T, TListener>(new ServerNewIdImpl<T>(_impl, num));
    public T? GetServerResource<T>(int num) where T : NWayland.Server.WlResource
        => (T?)_impl.GetServerResource(num);
    public WlUntypedNewId GetUntypedNewId(int num) => _impl.GetUntypedNewId(num);

    internal WlEventArgs(IWlEventArgsImpl impl) => _impl = impl;
}

/// <summary>
/// Holds the wire-format fields for an untyped <c>new_id</c> argument
/// (e.g. <c>wl_registry.bind</c>). The resource has not been created yet;
/// the caller decides the concrete type.
/// </summary>
public readonly struct WlUntypedNewId
{
    public readonly string Interface;
    public readonly uint Version;
    public readonly uint ObjectId;

    internal WlUntypedNewId(string @interface, uint version, uint objectId)
    {
        Interface = @interface;
        Version = version;
        ObjectId = objectId;
    }
}

internal interface IWlEventArgsImpl : IDisposable
{
    object Sender { get; }
    uint Opcode { get; }
    WlMessageDescription Message { get; }

    int GetInt32(int num);
    uint GetUInt32(int num);
    WlFixed GetWlFixed(int num);
    string? GetString(int num);
    byte[]? GetArrayBytes(int num);

    /// <summary>
    /// Consume the FD at the given argument index. Marks it consumed so it won't be auto-closed.
    /// </summary>
    int GetFd(int num);

    /// <summary>
    /// Close an unconsumed FD at the given argument index without consuming it.
    /// </summary>
    void CloseFd(int num);

    T? GetProxy<T>(int num) where T : WlProxy;
    T GetNewIdProxy<T>(int num, IWlEventsListener? listener) where T : WlProxy;

    NWayland.Server.WlResource GetServerNewId(int num);
    NWayland.Server.WlResource? GetServerResource(int num);
    WlUntypedNewId GetUntypedNewId(int num);
}

unsafe class WlEventArgsImpl : IWlEventArgsImpl
{
    private readonly WlProxy _proxy;
    public WlMessageDescription Message { get; }
    public WlArgument* Arguments { get; }
    public uint Opcode { get; }
    public object Sender => _proxy;
    // Tracks which FD/NewId arguments have been consumed. Uses bit-per-argument-index;
    // the source generator rejects messages with ≥64 arguments (see SigGen.cs).
    private ulong _consumed;

    public WlEventArgsImpl(WlArgument* arguments, WlProxy proxy, uint opcode, WlMessageDescription message)
    {
        Arguments = arguments;
        Opcode = opcode;
        _proxy = proxy;
        Message = message;
    }

    public int GetInt32(int num) => Raw(num).Int32;
    public uint GetUInt32(int num) => Raw(num).UInt32;
    public WlFixed GetWlFixed(int num) => Raw(num).WlFixed;

    public int GetFd(int num)
    {
        var bit = 1ul << num;
        if ((_consumed & bit) != 0)
            throw new InvalidOperationException("FD already consumed");
        _consumed |= bit;
        return Raw(num).Int32;
    }

    public void CloseFd(int num)
    {
        var bit = 1ul << num;
        if ((_consumed & bit) != 0)
            return;
        _consumed |= bit;
        Syscall.close(Raw(num).Int32);
    }

    public string? GetString(int num)
    {
        if (Message.Arguments[num].Code != WaylandArgumentCodes.String)
            throw new InvalidOperationException();
        var ptr = Arguments[num].IntPtr;
        if (ptr == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(ptr);
    }

    public byte[]? GetArrayBytes(int num)
    {
        if (Message.Arguments[num].Code != WaylandArgumentCodes.Array)
            throw new InvalidOperationException();
        var arr = (WlArray*)Arguments[num].IntPtr;
        if (arr == null)
            return null;
        return arr->AsSpan<byte>().ToArray();
    }

    public T? GetProxy<T>(int num) where T : WlProxy
    {
        var proxyPtr = Raw(num).IntPtr;
        if (Message.Arguments[num].Code == WaylandArgumentCodes.Object)
        {
            // TODO: emphemeral proxies that we auto-dispose on exit
            return (T?)LibWayland.FindByNative(_proxy.Display, proxyPtr);
        }
        throw new InvalidOperationException();
    }

    public T GetNewIdProxy<T>(int num, IWlEventsListener? listener) where T : WlProxy
        => (T)GetNewIdProxy(num, listener);

    private WlProxy GetNewIdProxy(int num, IWlEventsListener? listener)
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

    public NWayland.Server.WlResource GetServerNewId(int num)
        => throw new InvalidOperationException("GetServerNewId is not available on client side");

    public NWayland.Server.WlResource? GetServerResource(int num)
        => throw new InvalidOperationException("GetServerResource is not available on client side");

    public WlUntypedNewId GetUntypedNewId(int num)
        => throw new InvalidOperationException("GetUntypedNewId is not available on client side");

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
            var bit = 1ul << c;
            if ((_consumed & bit) != 0)
                continue;

            switch (Message.Arguments[c].Code)
            {
                case WaylandArgumentCodes.NewId:
                    GetNewIdProxy(c, null).Dispose();
                    break;
                case WaylandArgumentCodes.Fd:
                    CloseFd(c);
                    break;
            }
        }
    }
}