using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using NWayland.Interop;

namespace NWayland.Server;

/// <summary>
/// Abstraction for the write side of a socket, used by <see cref="WaylandOutgoingBuffer"/>
/// for flushing. Enables testing without real sockets.
/// </summary>
internal interface IWaylandSocketWriter
{
    /// <summary>
    /// Non-blocking write attempt. Returns bytes sent, or -1 for EAGAIN.
    /// </summary>
    int TryWriteNonBlocking(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds);
}

/// <summary>
/// Accumulates serialized Wayland event messages (bytes + FDs) for flushing to a socket.
/// Not thread-safe — only accessed from the client's dispatch context.
/// </summary>
/// <remarks>
/// Tracks per-event boundaries so that flush can guarantee:
/// - All FDs for an event travel in the same sendmsg as some bytes of that event.
/// - No more than <see cref="WaylandServerSocket.MaxFdsPerMessage"/> (28) FDs per sendmsg.
/// - At least 1 byte of data accompanies FDs (required by sendmsg).
/// Events without FDs can be freely coalesced with adjacent events.
/// </remarks>
internal sealed class WaylandOutgoingBuffer
{
    private const int MaxMessageSize = 4096;

    private byte[] _bytes;
    private int _bytesUsed;
    private int[] _fds;
    private int _fdsUsed;

    // Event boundary tracking: each entry records (byteEnd, fdEnd) after an event.
    // Only events with FDs create a boundary; FD-less events are implicitly
    // coalesced with the next FD-bearing event or the final tail.
    private List<(int ByteEnd, int FdEnd)> _fdBoundaries = new();

    public WaylandOutgoingBuffer(int initialByteCapacity = 4096, int initialFdCapacity = 32)
    {
        _bytes = new byte[initialByteCapacity];
        _fds = new int[initialFdCapacity];
    }

    public int BytesUsed => _bytesUsed;
    public int FdsUsed => _fdsUsed;

    /// <summary>
    /// Serialize a Wayland event into the buffer from a <see cref="WaylandCallBuilder"/>.
    /// </summary>
    public void SerializeEvent(uint objectId, uint opcode, WlMessageDescription method,
        ref WaylandCallBuilder call, uint? newId = null)
    {
        int savedBytesUsed = _bytesUsed;
        int savedFdsUsed = _fdsUsed;

        try
        {
            EnsureByteCapacity(8);
            _bytesUsed += 8; // reserve header

            int normalIdx = 0, objIdx = 0;

            for (int i = 0; i < method.Arguments.Count; i++)
            {
                var arg = method.Arguments[i];
                switch (arg.Code)
                {
                    case WaylandArgumentCodes.Int32:
                    case WaylandArgumentCodes.UInt32:
                    case WaylandArgumentCodes.Fixed:
                        WriteUInt32(call.NormalArgs![normalIdx++].UInt32);
                        break;

                    case WaylandArgumentCodes.NewId:
                        normalIdx++; // skip placeholder
                        if (newId == null)
                            throw new InvalidOperationException("NewId argument requires a pre-allocated ID");
                        WriteUInt32(newId.Value);
                        break;

                    case WaylandArgumentCodes.Fd:
                        AddFd(call.NormalArgs![normalIdx++].Int32);
                        break;

                    case WaylandArgumentCodes.Object:
                    {
                        var obj = call.ObjectArgs![objIdx++];
                        uint id = obj switch
                        {
                            WlResource res => res.ObjectId,
                            null => 0,
                            _ => throw new InvalidOperationException(
                                $"Unexpected object arg type in server event serialization: {obj.GetType()}")
                        };
                        WriteUInt32(id);
                        break;
                    }

                    case WaylandArgumentCodes.String:
                        WriteString((string?)call.ObjectArgs![objIdx++]);
                        break;

                    case WaylandArgumentCodes.Array:
                        WriteArray((byte[]?)call.ObjectArgs![objIdx++]);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown argument code: {arg.Code}");
                }
            }

            // Patch header: [object_id:u32] [size<<16 | opcode:u32]
            uint messageSize = (uint)(_bytesUsed - savedBytesUsed);
            if (messageSize > MaxMessageSize)
                throw new InvalidOperationException(
                    $"Serialized event size {messageSize} exceeds maximum {MaxMessageSize} bytes");
            Unsafe.WriteUnaligned(ref _bytes[savedBytesUsed], objectId);
            Unsafe.WriteUnaligned(ref _bytes[savedBytesUsed + 4], (messageSize << 16) | (opcode & 0xffff));

            // Record boundary if this event carries FDs
            if (_fdsUsed > savedFdsUsed)
                _fdBoundaries.Add((_bytesUsed, _fdsUsed));
        }
        catch
        {
            // Close any FDs that were added during this failed serialization
            for (int i = savedFdsUsed; i < _fdsUsed; i++)
                NWayland.Server.Interop.LinuxInterop.close(_fds[i]);
            _bytesUsed = savedBytesUsed;
            _fdsUsed = savedFdsUsed;
            throw;
        }
    }

    private void WriteUInt32(uint value)
    {
        EnsureByteCapacity(4);
        Unsafe.WriteUnaligned(ref _bytes[_bytesUsed], value);
        _bytesUsed += 4;
    }

    /// <summary>
    /// Write a Wayland string (length-prefixed, null-terminated, 4-byte aligned).
    /// </summary>
    private void WriteString(string? s)
    {
        if (s == null)
        {
            WriteUInt32(0);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(s);
        int totalLen = byteCount + 1; // include null terminator
        int paddedLen = (totalLen + 3) & ~3;

        WriteUInt32((uint)totalLen);
        EnsureByteCapacity(paddedLen);

        Encoding.UTF8.GetBytes(s, _bytes.AsSpan(_bytesUsed));
        _bytes[_bytesUsed + byteCount] = 0; // null terminator
        _bytes.AsSpan(_bytesUsed + totalLen, paddedLen - totalLen).Clear();
        _bytesUsed += paddedLen;
    }

    /// <summary>
    /// Write a Wayland array (length-prefixed, 4-byte aligned).
    /// </summary>
    private void WriteArray(byte[]? arr)
    {
        if (arr == null)
        {
            WriteUInt32(0);
            return;
        }

        int paddedLen = (arr.Length + 3) & ~3;
        WriteUInt32((uint)arr.Length);
        EnsureByteCapacity(paddedLen);
        arr.CopyTo(_bytes.AsSpan(_bytesUsed));
        _bytes.AsSpan(_bytesUsed + arr.Length, paddedLen - arr.Length).Clear();
        _bytesUsed += paddedLen;
    }

    private void AddFd(int fd)
    {
        EnsureFdCapacity(1);
        _fds[_fdsUsed++] = fd;
    }
    
    /// <summary>
    /// Non-blocking flush attempt. Keeps writing until either all data is sent
    /// or the socket returns EAGAIN. Returns true if everything was sent,
    /// false if data remains queued (socket returned EAGAIN).
    /// </summary>
    public bool TryFlushToSocket(IWaylandSocketWriter socket)
    {
        while (_bytesUsed > 0)
        {
            if (_fdBoundaries.Count == 0)
            {
                // No FD boundaries — plain byte send
                int sent = socket.TryWriteNonBlocking(
                    _bytes.AsMemory(0, _bytesUsed), ReadOnlyMemory<int>.Empty);
                if (sent <= 0)
                    return false; // EAGAIN (or 0 = disconnect)
                CompactBytes(sent, 0);
            }
            else
            {
                if (!TryFlushBatches(socket))
                    return false; // EAGAIN or partial — remainder already compacted
                // TryFlushBatches called Clear() on full success, _bytesUsed is 0
            }
        }

        return true;
    }

    public void Clear()
    {
        _bytesUsed = 0;
        _fdsUsed = 0;
        _fdBoundaries.Clear();
    }

    /// <summary>
    /// Close all unsent file descriptors still queued in the buffer.
    /// Call on disconnect to prevent FD leaks.
    /// </summary>
    public void CloseUnsentFds()
    {
        for (int i = 0; i < _fdsUsed; i++)
            NWayland.Server.Interop.LinuxInterop.close(_fds[i]);
        _fdsUsed = 0;
        _fdBoundaries.Clear();
    }

    /// <summary>
    /// Flush the buffer to the socket, respecting FD-per-sendmsg limits.
    /// Non-blocking batch flush. Walks FD boundaries and sends each batch
    /// via TryWriteNonBlocking. Stops on EAGAIN and compacts the remainder.
    /// </summary>
    /// <remarks>
    /// Rules enforced:
    /// 1. All FDs for an event are sent in the same sendmsg as some bytes of that event.
    /// 2. At most 28 FDs per sendmsg call.
    /// 3. At least 1 byte of data per sendmsg (required by the kernel).
    ///
    /// On partial writes (0 &lt; sent &lt; batchBytes): FDs are already delivered
    /// atomically by sendmsg, so we advance fdStart past them and only
    /// compact the unsent byte remainder.
    /// </remarks>
    private bool TryFlushBatches(IWaylandSocketWriter socket)
    {
        int byteStart = 0;
        int fdStart = 0;
        int batchFdCount = 0;
        int batchByteEnd = 0;

        for (int i = 0; i < _fdBoundaries.Count; i++)
        {
            var (byteEnd, fdEnd) = _fdBoundaries[i];
            int eventFds = fdEnd - (i == 0 ? 0 : _fdBoundaries[i - 1].FdEnd);

            if (batchFdCount > 0 && batchFdCount + eventFds > WaylandServerSocket.MaxFdsPerMessage)
            {
                int batchBytes = batchByteEnd - byteStart;
                int sent = socket.TryWriteNonBlocking(
                    _bytes.AsMemory(byteStart, batchBytes),
                    _fds.AsMemory(fdStart, batchFdCount));
                if (sent <= 0)
                {
                    CompactFrom(byteStart, fdStart);
                    return false;
                }
                // sendmsg delivers FDs atomically — close sender's copies
                CloseFdRange(fdStart, batchFdCount);
                if (sent < batchBytes)
                {
                    // Partial write — FDs already delivered, compact unsent bytes
                    CompactFrom(byteStart + sent, fdStart + batchFdCount);
                    return false;
                }
                byteStart = batchByteEnd;
                fdStart += batchFdCount;
                batchFdCount = 0;
            }

            batchFdCount += eventFds;
            batchByteEnd = byteEnd;
        }

        // Final batch (includes trailing FD-less events)
        if (byteStart < _bytesUsed)
        {
            int finalBytes = _bytesUsed - byteStart;
            int sent = socket.TryWriteNonBlocking(
                _bytes.AsMemory(byteStart, finalBytes),
                _fds.AsMemory(fdStart, batchFdCount));
            if (sent <= 0)
            {
                CompactFrom(byteStart, fdStart);
                return false;
            }
            // sendmsg delivers FDs atomically — close sender's copies
            CloseFdRange(fdStart, batchFdCount);
            if (sent < finalBytes)
            {
                // Partial write — FDs already delivered, compact unsent bytes
                CompactFrom(byteStart + sent, fdStart + batchFdCount);
                return false;
            }
        }

        Clear();
        return true;
    }

    private void CompactBytes(int byteOffset, int fdOffset)
    {
        int remainingBytes = _bytesUsed - byteOffset;
        if (remainingBytes > 0 && byteOffset > 0)
            Buffer.BlockCopy(_bytes, byteOffset, _bytes, 0, remainingBytes);
        _bytesUsed = remainingBytes;

        int remainingFds = _fdsUsed - fdOffset;
        if (remainingFds > 0 && fdOffset > 0)
            Buffer.BlockCopy(_fds, fdOffset * sizeof(int), _fds, 0, remainingFds * sizeof(int));
        _fdsUsed = remainingFds;
    }

    private void CompactFrom(int byteOffset, int fdOffset)
    {
        CompactBytes(byteOffset, fdOffset);

        // Remove boundaries whose FDs were fully consumed, adjust the rest
        int removeCount = 0;
        for (int i = 0; i < _fdBoundaries.Count; i++)
        {
            if (_fdBoundaries[i].FdEnd <= fdOffset)
                removeCount = i + 1;
            else
                break;
        }
        if (removeCount > 0)
            _fdBoundaries.RemoveRange(0, removeCount);

        for (int i = 0; i < _fdBoundaries.Count; i++)
        {
            var (be, fe) = _fdBoundaries[i];
            _fdBoundaries[i] = (be - byteOffset, fe - fdOffset);
        }
    }

    private void CloseFdRange(int start, int count)
    {
        for (int j = start; j < start + count; j++)
            NWayland.Server.Interop.LinuxInterop.close(_fds[j]);
    }

    private void EnsureByteCapacity(int additional)
    {
        int required = _bytesUsed + additional;
        if (required <= _bytes.Length)
            return;
        int newLen = _bytes.Length;
        while (newLen < required)
            newLen *= 2;
        Array.Resize(ref _bytes, newLen);
    }

    private void EnsureFdCapacity(int additional)
    {
        int required = _fdsUsed + additional;
        if (required <= _fds.Length)
            return;
        int newLen = _fds.Length;
        while (newLen < required)
            newLen *= 2;
        Array.Resize(ref _fds, newLen);
    }
}
