using System;

namespace NWayland.Server;

/// <summary>
/// Thrown when a Wayland connection enters an unrecoverable error state.
/// </summary>
public class WaylandConnectionException : Exception
{
    /// <inheritdoc/>
    public WaylandConnectionException(string message) : base(message)
    {
    }

    /// <inheritdoc/>
    public WaylandConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A protocol-level error that should result in a <c>wl_display.error</c>
/// event being sent to the client before disconnecting.
/// </summary>
internal class WaylandServerProtocolErrorException : WaylandConnectionException
{
    /// <summary>
    /// The resource that triggered the error, or null if not applicable.
    /// </summary>
    public WlResource? Resource { get; }

    /// <summary>
    /// The Wayland protocol error code.
    /// </summary>
    public uint Code { get; }

    public WaylandServerProtocolErrorException(WlResource? resource, uint code, string message)
        : base(message)
    {
        Resource = resource;
        Code = code;
    }
}
