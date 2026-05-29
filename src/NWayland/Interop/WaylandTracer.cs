using System;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland;

static class WaylandTracer
{

    static WlTracedArgument[] ConvertArgs(WlMessageDescription msg, WlEventArgs args)
    {
        var traced = new WlTracedArgument[msg.Arguments.Count];
        for (var c = 0; c < msg.Arguments.Count; c++)
        {
            var desc = msg.Arguments[c];

            switch (desc.Code)
            {
                case WaylandArgumentCodes.Fd:
                case WaylandArgumentCodes.Int32:
                    traced[c] = new WlTracedArgument { Int32 = args.GetInt32(c) };
                    break;
                case WaylandArgumentCodes.UInt32:
                    traced[c] = new WlTracedArgument { UInt32 = args.GetUInt32(c) };
                    break;
                case WaylandArgumentCodes.NewId:
                    // Server-created new_id args (in events) hold a wl_proxy*; resolve to id.
                    // On the request path the id isn't assigned until after marshal, so this
                    // reports 0 there, which is the honest pre-marshal value.
                    traced[c] = new WlTracedArgument { UInt32 = args.GetObjectId(c) };
                    break;
                case WaylandArgumentCodes.String:
                    traced[c] = new WlTracedArgument { Object = args.GetString(c) };
                    break;
                case WaylandArgumentCodes.Object:
                    traced[c] = new WlTracedArgument { UInt32 = args.GetObjectId(c) };
                    break;
                case WaylandArgumentCodes.Array:
                    traced[c] = new WlTracedArgument { Object = null };
                    break;
                case WaylandArgumentCodes.Fixed:
                    traced[c] = new WlTracedArgument { Fixed = args.GetWlFixed(c) };
                    break;
            }
        }

        return traced;
    }
    
    public static void TraceEvent(WlDisplay display, WlEventArgs args)
    {
        var tracer = display.Tracer;
        if (tracer == null)
            return;

        try
        {
            var sender = (WlProxy)args.Sender;
            var ev = sender.Interface.Events[(int)args.Opcode];
            tracer.Trace(sender, true, false, ev, ConvertArgs(ev, args));
        }
        catch
        {
            // Tracer is advisory — must not disrupt dispatch
        }
    }

    public static unsafe void TraceCall(WlProxy wlProxy, WaylandCallBuilder call, WlArgument* args,
        IntPtr newProxyHandle)
    {
        var tracer = wlProxy.Display.Tracer;
        if(tracer == null)
            return;

        try
        {
            var msg =  wlProxy.Interface.Methods[(int)call.OpCode];
            // Non-owning view — intentionally not disposed. The args are owned by InvokeCore's stackalloc.
            var eargs = new WlEventArgs(new WlEventArgsImpl(args, wlProxy, call.OpCode, msg));
            var traced = ConvertArgs(msg, eargs);

            // The new_id argument carries no usable id in the request args (it's a placeholder
            // until marshalling assigns one). Fill it from the proxy libwayland just created.
            if (newProxyHandle != IntPtr.Zero)
                for (var c = 0; c < msg.Arguments.Count; c++)
                    if (msg.Arguments[c].Code == WaylandArgumentCodes.NewId)
                    {
                        traced[c] = new WlTracedArgument { UInt32 = LibWayland.GetProxyId(newProxyHandle) };
                        break;
                    }

            tracer.Trace(wlProxy, false, msg.IsDestructor, msg, traced);
        }
        catch
        {
            // Tracer is advisory — must not disrupt requests
        }
    }

    public static void TraceDestroy(WlProxy wlProxy, bool isFromFinalizer, bool nativeCallSkipped)
    {
        var tracer = wlProxy.Display.Tracer;
        if (tracer == null)
            return;

        try
        {
            tracer.TraceDestroy(wlProxy, isFromFinalizer, nativeCallSkipped);
        }
        catch
        {
            // Tracer is advisory — must not disrupt dispose
        }
    }
}

public interface INWaylandTracer
{
    void Trace(WlProxy sender, bool isEvent, bool isDestructor, WlMessageDescription method,
        ReadOnlySpan<WlTracedArgument> args);
    void TraceDestroy(WlProxy proxy, bool isFromFinalizer, bool nativeCallSkipped);
}