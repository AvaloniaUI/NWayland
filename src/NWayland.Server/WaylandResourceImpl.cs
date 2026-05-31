using System;
using NWayland.Interop;
using static NWayland.Server.Interop.LinuxInterop;

namespace NWayland.Server;

/// <summary>
/// Per-resource implementation of <see cref="IWlResourceImpl"/> for server-side resources.
/// Holds all per-resource state: object ID, version, interface, listener, and disposed flag.
/// Serializes outgoing events into the client's outgoing buffer and
/// manages resource lifecycle (creation/destruction) in the object map.
/// </summary>
internal sealed class WaylandResourceImpl : IWlResourceImpl
{
    private readonly WaylandClient _client;
    private readonly Action<uint, WlResource>? _register;
    private bool _disposed;

    public uint ObjectId { get; }
    public int Version { get; }
    public WlInterfaceDescription Interface { get; }
    public IWlEventsListener? Listener { get; set; }

    internal WaylandResourceImpl(WaylandClient client, uint objectId, int version,
        WlInterfaceDescription iface, IWlEventsListener? listener,
        Action<uint, WlResource>? register)
    {
        _client = client;
        ObjectId = objectId;
        Version = version;
        Interface = iface;
        Listener = listener;
        _register = register;
    }

    public bool IsDisposed => _disposed || _client.IsDisposed;

    public void Register(WlResource resource)
    {
        _register?.Invoke(ObjectId, resource);
    }

    public void Invoke(WlResource resource, ref WaylandCallBuilder call)
    {
        using (_client.Server.AcquireDispatchLock(allowDisposed: true))
        {
            if (IsDisposed || _client.Server.IsDisposed)
            {
                CloseCallFds(ref call);
                if (_client.Server.Options.DisposedServerProxyCallIsNoOp)
                    return;
                throw new ObjectDisposedException(resource.GetType().FullName);
            }

            var method = Interface.Events[(int)call.OpCode];

            if (method.SinceVersion > Version)
            {
                CloseCallFds(ref call);
                throw new InvalidOperationException(
                    $"Event '{method.Name}' requires version {method.SinceVersion} " +
                    $"but resource has version {Version}");
            }

            _client.OutgoingBuffer.SerializeEvent(ObjectId, call.OpCode, method, ref call);
            try { NotifyTracer(resource, method, ref call); } catch { }

            if (method.IsDestructor)
                resource.Dispose();
        }
    }

    public WlResource InvokeNewId(WlResource resource, ref WaylandCallBuilder call,
        WlProxyTypeDescriptor proxyType, IWlEventsListener? listener, int version)
    {
        using (_client.Server.AcquireDispatchLock(allowDisposed: true))
        {
            if (IsDisposed || _client.Server.IsDisposed)
            {
                CloseCallFds(ref call);
                throw new ObjectDisposedException(resource.GetType().FullName);
            }

            var method = Interface.Events[(int)call.OpCode];

            if (method.SinceVersion > Version)
            {
                CloseCallFds(ref call);
                throw new InvalidOperationException(
                    $"Event '{method.Name}' requires version {method.SinceVersion} " +
                    $"but resource has version {Version}");
            }

            var newId = _client.ObjectMap.AllocateNextServerId();

            var newResource = proxyType.ServerFactory!(new WlResourceCreationContext
            {
                Impl = new WaylandResourceImpl(_client, newId, version,
                    proxyType.Interface, listener, _client.ObjectMap.InsertServerIdAt)
            });

            try
            {
                _client.OutgoingBuffer.SerializeEvent(ObjectId, call.OpCode, method, ref call, newId);
                try { NotifyTracer(resource, method, ref call, newId); } catch { }
            }
            catch
            {
                _client.DestroyResource(newResource);
                throw;
            }

            return newResource;
        }
    }

    public void PostError(WlResource resource, uint code, string message)
        => _client.PostError(resource, code, message);

    public void PostGlobalError(uint code, string message)
        => _client.PostError(null, code, message); // null => references the wl_display object

    public void Destroy(WlResource resource)
    {
        if (_disposed)
            return;

        using (_client.Server.AcquireDispatchLock(allowDisposed: true))
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_client.IsDisposed || _client.Server.IsDisposed)
                return;

            _client.DestroyResource(resource);
            try { _client.Server.Options.Tracer?.TraceDestroy(resource); } catch { }
        }
    }

    private void NotifyTracer(WlResource resource, WlMessageDescription method,
        ref WaylandCallBuilder call, uint? newId = null)
    {
        var tracer = _client.Server.Options.Tracer;
        if (tracer == null)
            return;

        var argDescs = method.Arguments;
        var traced = new WlTracedArgument[argDescs.Count];
        int normalIdx = 0, objIdx = 0;

        for (int i = 0; i < argDescs.Count; i++)
        {
            var arg = argDescs[i];
            switch (arg.Code)
            {
                case WaylandArgumentCodes.Int32:
                    traced[i] = new WlTracedArgument { Int32 = call.NormalArgs![normalIdx++].Int32 };
                    break;
                case WaylandArgumentCodes.UInt32:
                    traced[i] = new WlTracedArgument { UInt32 = call.NormalArgs![normalIdx++].UInt32 };
                    break;
                case WaylandArgumentCodes.NewId:
                    normalIdx++; // skip placeholder
                    traced[i] = new WlTracedArgument { UInt32 = newId ?? 0 };
                    break;
                case WaylandArgumentCodes.Fixed:
                    traced[i] = new WlTracedArgument { Fixed = call.NormalArgs![normalIdx++].WlFixed };
                    break;
                case WaylandArgumentCodes.Fd:
                    traced[i] = new WlTracedArgument { Int32 = call.NormalArgs![normalIdx++].Int32 };
                    break;
                case WaylandArgumentCodes.Object:
                {
                    var obj = call.ObjectArgs![objIdx++];
                    uint objId = obj is WlResource res ? res.ObjectId : 0u;
                    traced[i] = new WlTracedArgument { UInt32 = objId, Object = obj };
                    break;
                }
                case WaylandArgumentCodes.String:
                case WaylandArgumentCodes.Array:
                    traced[i] = new WlTracedArgument { Object = call.ObjectArgs![objIdx++] };
                    break;
            }
        }

        tracer.TraceEvent(resource, method, traced);
    }

    /// <summary>
    /// Close any file descriptors embedded in the call arguments.
    /// Prevents FD leaks when an event is silently dropped on a disposed resource.
    /// </summary>
    private void CloseCallFds(ref WaylandCallBuilder call)
    {
        if (call.NormalArgs == null)
            return;

        var method = Interface.Events[(int)call.OpCode];
        int normalIdx = 0;
        for (int i = 0; i < method.Arguments.Count; i++)
        {
            var arg = method.Arguments[i];
            switch (arg.Code)
            {
                case WaylandArgumentCodes.Int32:
                case WaylandArgumentCodes.UInt32:
                case WaylandArgumentCodes.Fixed:
                case WaylandArgumentCodes.NewId:
                    normalIdx++;
                    break;
                case WaylandArgumentCodes.Fd:
                    close(call.NormalArgs[normalIdx++].Int32);
                    break;
            }
        }
    }
}
