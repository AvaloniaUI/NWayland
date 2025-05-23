using System;
using System.Collections.Generic;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public ref struct WaylandCallBuilder : IDisposable
{
    // TODO: Pooling
    internal List<WlArgument>? NormalArgs;
    internal List<WlProxy>? ProxyArgs;
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

    

    public void Arg(WlProxy arg)
    {
        (ProxyArgs ??= new()).Add(arg);
    }

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
    public void InvokeNewId(WlProxyTypeDescriptor proxyType, IWlEventListener listener, WlEventQueue? queue)
    {
        _target.InvokeNewId(ref this, proxyType, listener, queue);
    }
    
    public void Dispose()
    {
        // TODO: Free pooled resources
    }
}