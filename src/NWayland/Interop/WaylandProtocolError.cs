namespace NWayland.Interop
{
    /// <summary>
    /// Details of a fatal Wayland protocol error reported by the compositor via <c>wl_display.error</c>.
    /// After such an error the display is unusable and must be disposed.
    /// </summary>
    /// <remarks>
    /// This is a plain data type, not an exception: the dispatching calls
    /// (<c>Dispatch</c>/<c>DispatchPending</c>/<c>Roundtrip</c>) keep returning <c>-1</c> on failure
    /// rather than throwing, so callers can handle errors with a single return-code style.
    /// Retrieve this via <see cref="NWayland.Protocols.Wayland.WlDisplay.GetProtocolError"/> after a
    /// dispatching call returns <c>-1</c>.
    ///
    /// libwayland-client does not retain the compositor's free-form error message (it only logs it via
    /// <c>wl_log</c>), so <see cref="Message"/> is synthesized from the offending interface and its
    /// <c>error</c>-enum entry: name and spec summary when known.
    /// </remarks>
    public sealed class WaylandProtocolError
    {
        public WaylandProtocolError(uint code, string? interfaceName, uint objectId,
            string? errorName, string? errorSummary)
        {
            Code = code;
            InterfaceName = interfaceName;
            ObjectId = objectId;
            ErrorName = errorName;
            ErrorSummary = errorSummary;
        }

        /// <summary>The error code, as defined in the offending interface's <c>error</c> enum.</summary>
        public uint Code { get; }

        /// <summary>The interface of the object that triggered the error, or null if unknown (destroyed object).</summary>
        public string? InterfaceName { get; }

        /// <summary>The id of the object that triggered the error, or 0 if unknown.</summary>
        public uint ObjectId { get; }

        /// <summary>The <c>error</c>-enum entry name for <see cref="Code"/>, if the binding knows it.</summary>
        public string? ErrorName { get; }

        /// <summary>The spec summary for the error entry, if any.</summary>
        public string? ErrorSummary { get; }

        /// <summary>A human-readable description synthesized from the protocol definition.</summary>
        public string Message => Format(Code, InterfaceName, ObjectId, ErrorName, ErrorSummary);

        public override string ToString() => Message;

        private static string Format(uint code, string? interfaceName, uint objectId,
            string? errorName, string? errorSummary)
        {
            var obj = interfaceName != null ? $"{interfaceName}#{objectId}" : "[destroyed object]";
            var name = errorName != null ? $" ({errorName})" : "";
            var summary = errorSummary != null ? $": {errorSummary}" : "";
            return $"Wayland protocol error on {obj}: error {code}{name}{summary}";
        }
    }
}
