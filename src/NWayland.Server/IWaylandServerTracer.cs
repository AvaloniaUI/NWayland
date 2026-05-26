using System;
using NWayland.Interop;

namespace NWayland.Server;

/// <summary>
/// Optional tracer interface for observing outgoing events and resource lifecycle
/// on the server side. Analogous to <c>INWaylandTracer</c> on the client side.
/// Set via <see cref="WaylandServerOptions.Tracer"/>.
/// </summary>
public interface IWaylandServerTracer
{
    /// <summary>
    /// Called when an outgoing event is serialized for a client.
    /// Use <paramref name="method"/>.Arguments to determine the type of each argument.
    /// </summary>
    /// <param name="resource">The resource sending the event.</param>
    /// <param name="method">The event's message description (name, argument types, destructor flag).</param>
    /// <param name="args">Argument values in declaration order.</param>
    void TraceEvent(WlResource resource, WlMessageDescription method,
        ReadOnlySpan<WlTracedArgument> args);

    /// <summary>
    /// Called when an incoming request is parsed from a client.
    /// </summary>
    /// <param name="resource">The resource the request targets.</param>
    /// <param name="method">The request's message description.</param>
    /// <param name="args">Argument values in declaration order.</param>
    void TraceRequest(WlResource resource, WlMessageDescription method,
        ReadOnlySpan<WlTracedArgument> args);

    /// <summary>
    /// Called when a resource is destroyed (removed from the object map).
    /// </summary>
    /// <param name="resource">The resource being destroyed.</param>
    void TraceDestroy(WlResource resource);

    /// <summary>
    /// Called when a <c>new_id</c> resource created during request parsing was
    /// never consumed by the listener via <c>GetAndConsume()</c>. This indicates
    /// a bug in the listener implementation. The runtime destroys the resource
    /// automatically.
    /// </summary>
    /// <param name="targetResource">The resource the request was sent to.</param>
    /// <param name="method">The request that contained the unconsumed new_id arg.</param>
    /// <param name="unconsumedResource">The new_id resource that was never consumed.</param>
    void TraceUnconsumedNewId(WlResource targetResource, WlMessageDescription method,
        WlResource unconsumedResource);
}
