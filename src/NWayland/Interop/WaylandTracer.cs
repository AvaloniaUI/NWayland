using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland;

static class WaylandTracer
{

    static object[] ConvertAargs(WlMessageDescription msg, WlEventArgs args)
    {
        var oargs = new object[msg.Arguments.Count];
        for (var c = 0; c < msg.Arguments.Count; c++)
        {
            var desc = msg.Arguments[c];

            if (desc.Code is WaylandArgumentCodes.Fd or WaylandArgumentCodes.Int32)
                oargs[c] = args.GetInt32(c);
            else if (desc.Code == WaylandArgumentCodes.UInt32)
                oargs[c] = args.GetUInt32(c);
            else if (desc.Code == WaylandArgumentCodes.String)
                oargs[c] = args.GetString(c)!;
            else if (desc.Code is WaylandArgumentCodes.NewId or WaylandArgumentCodes.Object)
                oargs[c] = "[Object]";
            else if (desc.Code == WaylandArgumentCodes.Array)
                oargs[c] = "[Array]";
            else if (desc.Code == WaylandArgumentCodes.Fixed)
                oargs[c] = args.GetWlFixed(c);
        }

        return oargs;
    }
    
    public static void TraceEvent(WlDisplay display, WlEventArgs args)
    {
        if (display.Tracer == null)
            return;

        var ev = args.Sender.Interface.Events[(int)args.Opcode];
        
        display.Tracer.Trace(args.Sender, true, false, ev.Name, ConvertAargs(ev, args));
    }

    public static unsafe void TraceCall(WlProxy wlProxy, WaylandCallBuilder call, WlArgument* args)
    {
        var tracer = wlProxy.Display.Tracer;
        if(tracer == null)
            return;
        
        var msg =  wlProxy.Interface.Methods[(int)call.OpCode];
        var eargs = new WlEventArgs(new WlEventArgsImpl(args, wlProxy, call.OpCode, msg));
        var oargs = ConvertAargs(msg, eargs);
        tracer.Trace(wlProxy, false, msg.IsDestructor, msg.Name, oargs);
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
    void Trace(WlProxy sender, bool isEvent, bool isDestructor, string name, object[] args);
    void TraceDestroy(WlProxy proxy, bool isFromFinalizer, bool nativeCallSkipped);
}