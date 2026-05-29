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

    // Wire object id for an object/new_id arg (0 for null). Used by tracing so object
    // args print as ids (matching WAYLAND_DEBUG and the server tracer) rather than as
    // the low bits of a native proxy pointer.
    internal uint GetObjectId(int num) => _impl.GetObjectId(num);

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
    /// Wire object id for an object/new_id argument, or 0 if null. Used for tracing.
    /// </summary>
    uint GetObjectId(int num);

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
        if (Message.Arguments[num].Code != WaylandArgumentCodes.Fd)
            throw new InvalidOperationException(
                $"Argument {num} is {Message.Arguments[num].Code}, not Fd");
        var bit = 1ul << num;
        if ((_consumed & bit) != 0)
            throw new InvalidOperationException("FD already consumed");
        _consumed |= bit;
        return Raw(num).Int32;
    }

    public void CloseFd(int num)
    {
        if (Message.Arguments[num].Code != WaylandArgumentCodes.Fd)
            throw new InvalidOperationException(
                $"Argument {num} is {Message.Arguments[num].Code}, not Fd");
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
            return (T?)_proxy.Display.FindByNative(proxyPtr);
        }
        throw new InvalidOperationException();
    }

    public uint GetObjectId(int num)
    {
        // Both object and new_id args carry a native wl_proxy* (or null) in this slot,
        // for events (demarshalled by libwayland) and requests (the proxy Handle we wrote).
        var ptr = Raw(num).IntPtr;
        return ptr == IntPtr.Zero ? 0 : LibWayland.GetProxyId(ptr);
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
            _consumed |= bit;
            var proxyHandle = new WlProxyHandle(proxyPtr, ownsHandle: true);
            try
            {
                var proxy = Message.Arguments[num].ProxyType!.Factory(
                    new WlProxyCreationContext(_proxy.Display, _proxy.Queue,
                        Message.Arguments[num].ProxyType!.Interface,
                        proxyHandle, listener))!;
                return proxy;
            }
            catch
            {
                proxyHandle.Dispose();
                throw;
            }
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

            try
            {
                switch (Message.Arguments[c].Code)
                {
                    case WaylandArgumentCodes.NewId:
                        // Destroy unconsumed server-created proxy directly instead of
                        // creating a full managed WlProxy (avoids map registration churn)
                        var newIdPtr = Raw(c).IntPtr;
                        if (newIdPtr != IntPtr.Zero)
                            LibWayland.wl_proxy_destroy(newIdPtr);
                        break;
                    case WaylandArgumentCodes.Fd:
                        CloseFd(c);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"WlEventArgs.Dispose: failed to clean up argument {c} ({Message.Arguments[c].Code}): {ex.Message}");
            }
        }
    }
}