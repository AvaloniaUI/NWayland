using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public interface IWaylandCallTarget
{
    internal void Invoke(ref WaylandCallBuilder call);

    internal object InvokeNewId(ref WaylandCallBuilder call, WlProxyTypeDescriptor proxyType,
        IWlEventsListener? listener, IWlTargetQueue? queue, uint? newIdVersion);
}

public ref struct WaylandCallBuilder : IDisposable
{
    [ThreadStatic] private static Stack<List<WlArgument>>? _normalArgsPool;
    [ThreadStatic] private static Stack<List<object?>>? _objectArgsPool;
    private static T GetPooled<T>(ref Stack<T>? pool) where T : new()
    {
        pool ??= new Stack<T>();
        if (pool.TryPop(out var result))
            return result;
        return new T();
    }

    private static void ReturnToPool<T>(ref Stack<List<T>>? pool, ref List<T>? value)
    {
        if (value is null)
            return;
        pool ??= new();
        value.Clear();
        pool.Push(value);
        value = null;
    }

    internal List<WlArgument>? NormalArgs;
    internal List<object?>? ObjectArgs;
    private IWaylandCallTarget _target;
    internal uint OpCode;

    public static WaylandCallBuilder Create(IWaylandCallTarget target, uint opcode)
    {
        return new WaylandCallBuilder()
        {
            _target = target,
            OpCode = opcode
        };
    }
    
    private void ObjectArg(object? arg)
    {
        (ObjectArgs ??= GetPooled(ref _objectArgsPool)).Add(arg);
    }

    public void Arg(WlProxy? arg) => ObjectArg(arg);

    public void Arg(NWayland.Server.WlResource? arg) => ObjectArg(arg);
    
    public void Arg(string arg) => ObjectArg(arg);

    private void Add(WlArgument arg) => (NormalArgs ??= GetPooled(ref _normalArgsPool)).Add(arg);
    
    public void ArgNewId() => Add(WlArgument.NewId);

    public void Arg(int i) => Add(new WlArgument
    {
        Int32 = i
    });

    public void Arg(uint i) => Add(new WlArgument()
    {
        UInt32 = i
    });

    public void Arg(WlFixed wlFixed) => Add(new WlArgument()
    {
        WlFixed = wlFixed
    });

    public void Arg<T>(ReadOnlySpan<T> array) where T : unmanaged
    {
        ObjectArg(MemoryMarshal.Cast<T, byte>(array).ToArray());
    }

    public void Invoke()
    {
        _target.Invoke(ref this);
    }

    public T InvokeNewId<T>(IWlEventsListener? listener, IWlTargetQueue? queue, uint? version = null) where T : IWlProxyTypeDescriptorProvider
    {
        return (T)_target.InvokeNewId(ref this, T.ProxyType, listener, queue, version);
    }
    
    public void Dispose()
    {
        ReturnToPool(ref _normalArgsPool, ref NormalArgs);
        ReturnToPool(ref _objectArgsPool, ref ObjectArgs);
    }
}