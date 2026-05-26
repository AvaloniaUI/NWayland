using System;

namespace NWayland.Server;

/// <summary>
/// A fixed-capacity ring buffer for stream reassembly.
/// Supports contiguous writes via <see cref="GetNextWriteBuffer"/> and
/// copy-out reads via <see cref="Read"/> / <see cref="Peek"/>.
/// </summary>
/// <remarks>
/// When the buffer is fully drained (<see cref="Count"/> reaches zero),
/// head and tail are reset to position 0 to maximize contiguous write space.
/// </remarks>
internal sealed class RingBuffer<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _tail;
    private int _count;

    internal RingBuffer(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new T[capacity];
    }

    /// <summary>Number of items available for reading.</summary>
    internal int Count => _count;

    /// <summary>Total buffer capacity.</summary>
    internal int Capacity => _buffer.Length;

    /// <summary>Number of items that can still be written.</summary>
    internal int Available => _buffer.Length - _count;

    /// <summary>
    /// Copy up to <paramref name="destination"/>.Length items from the read
    /// position without consuming them.
    /// </summary>
    /// <returns>Number of items actually copied (min of Count and destination.Length).</returns>
    internal int Peek(Span<T> destination)
    {
        int toPeek = Math.Min(_count, destination.Length);
        if (toPeek == 0)
            return 0;

        int firstChunk = Math.Min(toPeek, _buffer.Length - _head);
        _buffer.AsSpan(_head, firstChunk).CopyTo(destination);

        int secondChunk = toPeek - firstChunk;
        if (secondChunk > 0)
            _buffer.AsSpan(0, secondChunk).CopyTo(destination.Slice(firstChunk));

        return toPeek;
    }

    /// <summary>
    /// Copy up to <paramref name="destination"/>.Length items from the read
    /// position and consume them.
    /// </summary>
    /// <returns>Number of items actually read (min of Count and destination.Length).</returns>
    internal int Read(Span<T> destination)
    {
        int read = Peek(destination);
        _head = (_head + read) % _buffer.Length;
        _count -= read;

        if (_count == 0)
        {
            _head = 0;
            _tail = 0;
        }

        return read;
    }

    /// <summary>
    /// Get the next contiguous writable region. The caller writes directly
    /// into this memory, then calls <see cref="Written"/> to commit.
    /// </summary>
    /// <remarks>
    /// The returned region may be smaller than <see cref="Available"/> when
    /// the write position wraps around the buffer boundary. Multiple
    /// write/commit cycles will fill the full available space.
    /// </remarks>
    internal Memory<T> GetNextWriteBuffer()
    {
        int available = _buffer.Length - _count;
        if (available == 0)
            return Memory<T>.Empty;

        int contiguous = Math.Min(available, _buffer.Length - _tail);
        return _buffer.AsMemory(_tail, contiguous);
    }

    /// <summary>
    /// Get up to two writable regions that together cover all available space.
    /// The first region runs from tail to end-of-buffer (or to head if no wrap).
    /// The second region covers the wraparound portion from buffer start to head.
    /// Either or both may be empty.
    /// </summary>
    /// <remarks>
    /// Use this with scatter/gather I/O (e.g. recvmsg with multiple iovecs)
    /// to fill the entire available ring buffer space in a single syscall.
    /// Call <see cref="Written"/> with the total bytes written across both regions.
    /// </remarks>
    internal (Memory<T> First, Memory<T> Second) GetWriteBuffers()
    {
        int available = _buffer.Length - _count;
        if (available == 0)
            return (Memory<T>.Empty, Memory<T>.Empty);

        int tailToEnd = _buffer.Length - _tail;
        if (tailToEnd >= available)
        {
            // No wrap — all available space is contiguous from tail
            return (_buffer.AsMemory(_tail, available), Memory<T>.Empty);
        }

        // Wrap: first chunk is tail..end, second is 0..remaining
        return (
            _buffer.AsMemory(_tail, tailToEnd),
            _buffer.AsMemory(0, available - tailToEnd));
    }

    /// <summary>
    /// Commit <paramref name="count"/> items that were written into the
    /// region returned by <see cref="GetNextWriteBuffer"/>.
    /// </summary>
    internal void Written(int count)
    {
        if (count < 0 || count > _buffer.Length - _count)
            throw new InvalidOperationException("Write exceeds available buffer space");

        _tail = (_tail + count) % _buffer.Length;
        _count += count;
    }
}
