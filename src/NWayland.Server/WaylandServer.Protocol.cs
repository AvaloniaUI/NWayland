using System;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace NWayland.Server;

public sealed partial class WaylandServer
{
    /// <summary>
    /// Process a parsed request — handles wl_display, wl_registry internally,
    /// returns null for internal events (caller should loop), or a
    /// <see cref="WaylandServerEvent"/> for events to expose to the application.
    /// </summary>
    private WaylandServerEvent? ProcessParsedRequest(WaylandClient client, ParsedRequest req)
    {
        var resource = req.Resource;
        var method = req.Method;
        var args = req.Args;

        // Trace incoming request
        Options.Tracer?.TraceRequest(resource, method, args.ToTracedArgs());

        // SinceVersion check
        if (method.SinceVersion > resource.Version)
        {
            args.Dispose();
            throw new WaylandServerProtocolErrorException(resource, 1,
                $"Request '{method.Name}' requires version {method.SinceVersion} " +
                $"but resource has version {resource.Version}");
        }

        // Destructor handling — remove from object map immediately (per spec:
        // "the request shall destroy the protocol object"), then return event
        // to application for dispatch. The resource is marked disposed so no
        // further events can be sent on it, and subsequent requests targeting
        // this object ID will be rejected.
        if (method.IsDestructor)
        {
            if (resource.Interface.Name == "wl_registry")
                client.RemoveRegistry(resource.ObjectId);

            // Remove from map and send delete_id NOW, before returning to app
            resource.Dispose();

            if (resource.Listener != null)
            {
                return new WaylandServerRequestEvent(client, resource,
                    () =>
                    {
                        try
                        {
                            resource.Listener.DispatchEvent(new WlEventArgs(args));
                        }
                        finally
                        {
                            args.Dispose();
                        }
                    },
                    args, isDestructor: true);
            }

            args.Dispose();
            return null;
        }

        var interfaceName = resource.Interface.Name;

        // wl_display requests
        if (interfaceName == "wl_display")
        {
            switch (args.Opcode)
            {
                case 0: // sync
                {
                    var callback = (WlCallback.Server)args.GetServerNewId(0);
                    args.MarkDispatched();
                    args.Dispose();
                    return new WaylandServerSyncEvent(client, callback);
                }
                case 1: // get_registry
                {
                    var registry = (WlRegistry.Server)args.GetServerNewId(0);
                    uint registryId = registry.ObjectId;
                    client.AddRegistry(registryId);
                    // Send all current globals
                    foreach (var global in client.Globals)
                        client.SendGlobalToRegistry(registryId, global);
                    args.MarkDispatched();
                    args.Dispose();
                    return null; // Internal — no event to application
                }
            }
        }

        // wl_registry.bind
        // Method args (as generated): [0:uint(name)] [1:string(iface)] [2:uint(ver)] [3:new_id(null)]
        if (interfaceName == "wl_registry" && args.Opcode == 0)
        {
            uint globalName = args.GetUInt32(0);
            var untypedId = args.GetUntypedNewId(3);
            args.Dispose();

            // Look up global by its numeric name
            WaylandServerGlobal? matchingGlobal = null;
            foreach (var g in client.Globals)
            {
                if (g.Id == globalName)
                {
                    matchingGlobal = g;
                    break;
                }
            }

            if (matchingGlobal == null)
            {
                throw new WaylandServerProtocolErrorException(resource, 0,
                    $"Client tried to bind unknown global name {globalName}");
            }

            if (matchingGlobal.Interface != untypedId.Interface)
            {
                throw new WaylandServerProtocolErrorException(resource, 0,
                    $"Interface mismatch for global {globalName}: " +
                    $"server has '{matchingGlobal.Interface}' but client sent '{untypedId.Interface}'");
            }

            // ObjectId == 0 and server-range checks are handled in the parser

            if (untypedId.Version == 0)
            {
                throw new WaylandServerProtocolErrorException(resource, 0,
                    $"Client requested version 0 of '{untypedId.Interface}'");
            }

            if (untypedId.Version > (uint)matchingGlobal.Version)
            {
                throw new WaylandServerProtocolErrorException(resource, 0,
                    $"Client requested version {untypedId.Version} of '{untypedId.Interface}' " +
                    $"but server only supports version {matchingGlobal.Version}");
            }

            // Validate that the client-chosen object ID is not already in use
            if (client.ObjectMap.Get(untypedId.ObjectId) != null)
            {
                throw new WaylandServerProtocolErrorException(resource, 0,
                    $"Client-chosen object ID {untypedId.ObjectId} is already in use");
            }

            return new WaylandServerRegistryBindEvent(client,
                matchingGlobal, untypedId.Interface,
                untypedId.Version, untypedId.ObjectId);
        }

        // General request — wrap for application
        if (resource.Listener != null)
        {
            return new WaylandServerRequestEvent(client, resource,
                () => resource.Listener.DispatchEvent(new WlEventArgs(args)),
                args);
        }

        // No listener — clean up eagerly-created resources
        args.DestroyEagerResources();
        args.Dispose();
        return null;
    }

    /// <summary>
    /// Central protocol error handler. Posts wl_display.error if applicable
    /// and triggers disconnect via <see cref="WaylandClient.PostError"/>.
    /// For non-protocol errors, shuts down directly.
    /// </summary>
    private static void HandleProtocolError(
        WaylandClient client, WaylandMessageParser parser, WaylandConnectionException ex)
    {
        if (ex is WaylandServerProtocolErrorException protoError)
            client.PostError(protoError.Resource, protoError.Code, protoError.Message);
        else
        {
            client.Socket.ShutdownRead();
            parser.Dispose();
        }
    }

    /// <summary>
    /// Disconnect a client: clean up all server-side state and return the
    /// disconnect event. Uses <see cref="CleanupClient"/> which is idempotent,
    /// so this is safe even if PostError already cleaned up the client.
    /// </summary>
    private WaylandClientDisconnectEvent DisconnectClient(
        WaylandClient client, WaylandMessageParser parser)
    {
        CleanupClient(client);
        return new WaylandClientDisconnectEvent(client);
    }
}
