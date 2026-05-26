namespace NWayland.Interop;

/// <summary>
/// A snapshot of a single argument value in a traced Wayland event or request.
/// Use <see cref="WlMessageDescription.Arguments"/> to determine which field
/// to read for each argument index based on its <see cref="WaylandArgumentCodes"/>.
/// </summary>
public readonly struct WlTracedArgument
{
    /// <summary>Value for Int32/Fd arguments.</summary>
    public int Int32 { get; init; }

    /// <summary>Value for UInt32/NewId arguments.</summary>
    public uint UInt32 { get; init; }

    /// <summary>Value for Fixed arguments.</summary>
    public WlFixed Fixed { get; init; }

    /// <summary>Value for String/Array/Object arguments (may be null).</summary>
    public object? Object { get; init; }
}
