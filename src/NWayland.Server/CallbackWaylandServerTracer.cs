using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using NWayland.Interop;

namespace NWayland.Server;

/// <summary>
/// Built-in tracer that formats messages in the same style as <c>WAYLAND_DEBUG=1</c>
/// and writes them to an <see cref="Action{String}"/> callback.
/// </summary>
public sealed class CallbackWaylandServerTracer : IWaylandServerTracer
{
    private readonly Action<string> _output;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public CallbackWaylandServerTracer(Action<string> output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public void TraceEvent(WlResource resource, WlMessageDescription method,
        ReadOnlySpan<WlTracedArgument> args)
    {
        var sb = FormatTimestamp();
        sb.Append(resource.Interface.Name);
        sb.Append('@');
        sb.Append(resource.ObjectId);
        sb.Append('.');
        sb.Append(method.Name);
        sb.Append('(');
        FormatArgs(sb, method, args);
        sb.Append(')');
        _output(sb.ToString());
    }

    public void TraceRequest(WlResource resource, WlMessageDescription method,
        ReadOnlySpan<WlTracedArgument> args)
    {
        var sb = FormatTimestamp();
        sb.Append(" -> ");
        sb.Append(resource.Interface.Name);
        sb.Append('@');
        sb.Append(resource.ObjectId);
        sb.Append('.');
        sb.Append(method.Name);
        sb.Append('(');
        FormatArgs(sb, method, args);
        sb.Append(')');
        _output(sb.ToString());
    }

    public void TraceDestroy(WlResource resource)
    {
        var sb = FormatTimestamp();
        sb.Append("[destroy] ");
        sb.Append(resource.Interface.Name);
        sb.Append('@');
        sb.Append(resource.ObjectId);
        _output(sb.ToString());
    }

    public void TraceUnconsumedNewId(WlResource targetResource, WlMessageDescription method,
        WlResource unconsumedResource)
    {
        var sb = FormatTimestamp();
        sb.Append("[WARNING] unconsumed new_id ");
        sb.Append(unconsumedResource.Interface.Name);
        sb.Append('@');
        sb.Append(unconsumedResource.ObjectId);
        sb.Append(" from ");
        sb.Append(targetResource.Interface.Name);
        sb.Append('@');
        sb.Append(targetResource.ObjectId);
        sb.Append('.');
        sb.Append(method.Name);
        _output(sb.ToString());
    }

    private StringBuilder FormatTimestamp()
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        long totalSeconds = (long)elapsed.TotalSeconds;
        int msec = (int)(elapsed.TotalMilliseconds % 1000);
        var sb = new StringBuilder(128);
        sb.Append('[');
        sb.Append(totalSeconds.ToString().PadLeft(7));
        sb.Append('.');
        sb.Append(msec.ToString("D3"));
        sb.Append("] ");
        return sb;
    }

    private static void FormatArgs(StringBuilder sb, WlMessageDescription method,
        ReadOnlySpan<WlTracedArgument> args)
    {
        var argDescs = method.Arguments;
        for (int i = 0; i < args.Length && i < argDescs.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");

            var desc = argDescs[i];
            var arg = args[i];

            switch (desc.Code)
            {
                case WaylandArgumentCodes.Int32:
                    sb.Append(arg.Int32);
                    break;
                case WaylandArgumentCodes.UInt32:
                    sb.Append(arg.UInt32);
                    break;
                case WaylandArgumentCodes.Fixed:
                    sb.Append(arg.Fixed);
                    break;
                case WaylandArgumentCodes.Fd:
                    sb.Append("fd ");
                    sb.Append(arg.Int32);
                    break;
                case WaylandArgumentCodes.NewId:
                    sb.Append("new id ");
                    sb.Append(arg.UInt32);
                    break;
                case WaylandArgumentCodes.Object:
                    sb.Append(arg.UInt32);
                    break;
                case WaylandArgumentCodes.String:
                    if (arg.Object is string s)
                    {
                        sb.Append('"');
                        sb.Append(s);
                        sb.Append('"');
                    }
                    else
                        sb.Append("nil");
                    break;
                case WaylandArgumentCodes.Array:
                    if (arg.Object is byte[] arr)
                    {
                        sb.Append("array[");
                        sb.Append(arr.Length);
                        sb.Append(']');
                    }
                    else
                        sb.Append("array[0]");
                    break;
            }
        }
    }
}
