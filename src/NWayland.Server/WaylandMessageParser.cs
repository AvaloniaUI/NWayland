using System;
using System.Runtime.CompilerServices;
using NWayland.Interop;

namespace NWayland.Server;

/// <summary>
/// Raw parsed request — the parser produces these and the server decides
/// what to do with them.
/// </summary>
internal readonly struct ParsedRequest
{
    internal readonly WlResource Resource;
    internal readonly WlMessageDescription Method;
    internal readonly ServerWlEventArgsImpl Args;

    internal ParsedRequest(WlResource resource, WlMessageDescription method, ServerWlEventArgsImpl args)
    {
        Resource = resource;
        Method = method;
        Args = args;
    }
}

/// <summary>
/// Per-client message buffer and parser. Only parses messages — does not
/// contain protocol-specific dispatch logic. The server's
/// <see cref="WaylandServer.NextEvent"/> drives I/O, parsing, and dispatch.
/// </summary>
internal sealed class WaylandMessageParser : IDisposable
{
    private const int HeaderSize = 8;
    private const int MaxMessageSize = 4096;
    private const int MaxFdsPerRead = WaylandServerSocket.MaxFdsPerMessage;

    internal const int MaxPendingFds = 28;

    private readonly WaylandClient _client;

    internal readonly RingBuffer<byte> DataBuffer = new(MaxMessageSize * 2);
    internal readonly RingBuffer<int> FdBuffer = new(MaxMessageSize /4);

    /// <summary>
    /// Set by epoll readiness, cleared on EAGAIN. Indicates the socket
    /// may have data available for reading.
    /// </summary>
    internal bool Readable;
    private bool _disposed;

    /// <summary>
    /// Whether this parser has been disposed (connection is dead).
    /// </summary>
    internal bool IsDisposed => _disposed;

    internal WaylandMessageParser(WaylandClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Whether the ring buffers have room for another socket read.
    /// </summary>
    internal bool HasBufferRoom =>
        DataBuffer.Available > 0 &&
        FdBuffer.Available >= MaxFdsPerRead;

    /// <summary>
    /// Number of FDs currently queued and not yet consumed by parsing.
    /// </summary>
    internal int PendingFdCount => FdBuffer.Count;

    /// <summary>
    /// Try to parse one complete message from the buffer.
    /// Returns null if no complete message is available.
    /// Throws <see cref="WaylandConnectionException"/> on protocol errors.
    /// </summary>
    internal ParsedRequest? TryParseOneEvent()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WaylandMessageParser));

        if (DataBuffer.Count < HeaderSize)
            return null;

        Span<byte> header = stackalloc byte[HeaderSize];
        DataBuffer.Peek(header);

        uint objectId = Unsafe.ReadUnaligned<uint>(ref header[0]);
        uint sizeAndOpcode = Unsafe.ReadUnaligned<uint>(ref header[4]);
        int messageSize = (int)(sizeAndOpcode >> 16);
        int opcode = (int)(sizeAndOpcode & 0xffff);

        if (messageSize < HeaderSize)
            throw new WaylandServerProtocolErrorException(null, 1,
                $"Message size {messageSize} < header size");

        if (messageSize % 4 != 0)
            throw new WaylandServerProtocolErrorException(null, 1,
                $"Message size {messageSize} is not 4-byte aligned");

        if (messageSize > MaxMessageSize)
            throw new WaylandServerProtocolErrorException(null, 1,
                $"Message size {messageSize} exceeds maximum {MaxMessageSize}");

        if (DataBuffer.Count < messageSize)
            return null; // Need more data

        // Read the full message out of the ring buffer (consumes it)
        Span<byte> message = stackalloc byte[messageSize];
        DataBuffer.Read(message);

        var body = message.Slice(HeaderSize);

        try
        {
            return ParseMessage(objectId, opcode, body);
        }
        catch (WaylandConnectionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WaylandServerProtocolErrorException(null, 3,
                $"Error parsing request for object {objectId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Close all unconsumed FDs in the queue.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Span<int> fds = stackalloc int[MaxFdsPerRead];
        while (FdBuffer.Count > 0)
        {
            int toRead = Math.Min(FdBuffer.Count, fds.Length);
            int read = FdBuffer.Read(fds.Slice(0, toRead));
            for (int i = 0; i < read; i++)
                Interop.LinuxInterop.close(fds[i]);
        }
    }

    private int DequeueFd()
    {
        Span<int> fd = stackalloc int[1];
        if (FdBuffer.Read(fd) == 0)
        {
            // Per the Wayland spec: "Clients and compositors should queue incoming data
            // until they have whole messages to process, as file descriptors may arrive
            // earlier or later than the corresponding data bytes."
            // We've received the full message bytes, but there is no FD in AUX data,
            // so we trigger a protocol error.
            //
            // This is consistent with libwayland-server also disconnects the client with a protocol error
            // on FD buffer underrun.
            throw new WaylandConnectionException("Expected FD in ancillary data but none available");
        }

        return fd[0];
    }

    private ParsedRequest ParseMessage(uint objectId, int opcode, ReadOnlySpan<byte> body)
    {
        var resource = _client.ObjectMap.Get(objectId)
            ?? throw MakeProtocolError(null, 0, $"Invalid object ID {objectId}");

        var methods = resource.Interface.Methods;
        if (opcode < 0 || opcode >= methods.Count)
            throw MakeProtocolError(resource, 1, $"Invalid opcode {opcode} for {resource.Interface.Name}");

        var method = methods[opcode];
        var impl = new ServerWlEventArgsImpl(resource, method, (uint)opcode, body, _client, DequeueFd);

        return new ParsedRequest(resource, method, impl);
    }

    private WaylandServerProtocolErrorException MakeProtocolError(
        WlResource? resource, uint code, string message)
    {
        return new WaylandServerProtocolErrorException(resource, code, message);
    }
}
