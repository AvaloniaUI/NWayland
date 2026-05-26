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
                case WaylandArgumentCodes.NewId:
                    traced[c] = new WlTracedArgument { UInt32 = args.GetUInt32(c) };
                    break;
                case WaylandArgumentCodes.String:
                    traced[c] = new WlTracedArgument { Object = args.GetString(c) };
                    break;
                case WaylandArgumentCodes.Object:
                    traced[c] = new WlTracedArgument { UInt32 = args.GetUInt32(c) };
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
        if (display.Tracer == null)
            return;

        var sender = (WlProxy)args.Sender;
        var ev = sender.Interface.Events[(int)args.Opcode];
        
        display.Tracer.Trace(sender, true, false, ev, ConvertArgs(ev, args));
    }

    public static unsafe void TraceCall(WlProxy wlProxy, WaylandCallBuilder call, WlArgument* args)
    {
        var tracer = wlProxy.Display.Tracer;
        if(tracer == null)
            return;
        
        var msg =  wlProxy.Interface.Methods[(int)call.OpCode];
        var eargs = new WlEventArgs(new WlEventArgsImpl(args, wlProxy, call.OpCode, msg));
        tracer.Trace(wlProxy, false, msg.IsDestructor, msg, ConvertArgs(msg, eargs));
    }

    public static void TraceDestroy(WlProxy wlProxy, bool isFromFinalizer, bool nativeCallSkipped)
    {
        var tracer = wlProxy.Display.Tracer;
        if (tracer == null)
            return;

        tracer.TraceDestroy(wlProxy, isFromFinalizer, nativeCallSkipped);
    }
}

public interface INWaylandTracer
{
    void Trace(WlProxy sender, bool isEvent, bool isDestructor, WlMessageDescription method,
        ReadOnlySpan<WlTracedArgument> args);
    void TraceDestroy(WlProxy proxy, bool isFromFinalizer, bool nativeCallSkipped);
}