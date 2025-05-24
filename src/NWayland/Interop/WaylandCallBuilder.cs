using System;
using System.Collections.Generic;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public ref struct WaylandCallBuilder : IDisposable
{
    // TODO: Pooling
    internal List<WlArgument>? NormalArgs;
    internal List<object>? ObjectArgs;
    private WlProxy _target;
    internal uint OpCode;

    public static WaylandCallBuilder Create(WlProxy target, uint opcode)
    {
        return new WaylandCallBuilder()
        {
            _target = target,
            OpCode = opcode
        };
    }
    
    private void ObjectArg(object arg)
    {
        (ObjectArgs ??= new()).Add(arg);
    }

    public void Arg(WlProxy arg) => ObjectArg(arg);
    
    public void Arg(string arg) => ObjectArg(arg);

    private void Add(WlArgument arg) => (NormalArgs ??= new()).Add(arg);
    
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

    public void Arg(ReadOnlySpan<byte> array)
    {
        //TODO
        throw new NotSupportedException();
    }

    public void Invoke()
    {
        _target.Invoke(ref this);
    }

    // TODO: Queue management
    public WlProxy InvokeNewId(WlProxyTypeDescriptor proxyType, IWlEventListener? listener, WlEventQueue? queue)
    {
        return _target.InvokeNewId(ref this, proxyType, listener, queue);
    }
    
    public void Dispose()
    {
        // TODO: Free pooled resources
    }
}