using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NWayland.Server.Interop;
using NWayland.Server.Interop;
using static NWayland.Server.Interop.LinuxInterop;

namespace NWayland.Server;

/// <summary>
/// Thin wrapper around epoll + eventfd. Provides synchronous I/O readiness
/// notifications for the server event loop. No background thread — the caller
/// drives the loop by calling <see cref="Wait"/>.
/// </summary>
/// <remarks>
/// <see cref="Wake"/> is the only thread-safe method. All other methods must
/// be called from the same thread that calls <see cref="Wait"/>.
/// </remarks>
internal sealed class WaylandEventPoll : IDisposable
{
    private const int MaxEvents = 64;

    private readonly int _epollFd;
    private readonly int _eventFd;
    private readonly object _wakeLock = new();
    private int _wakeRequested;
    private bool _disposed;

    public unsafe WaylandEventPoll()
    {
        _epollFd = epoll_create1(EPOLL_CLOEXEC);
        if (_epollFd < 0)
            ThrowErrno("epoll_create1");

        _eventFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        if (_eventFd < 0)
        {
            close(_epollFd);
            ThrowErrno("eventfd");
        }

        // Add eventfd to epoll for wakeup notifications
        Span<byte> ev = stackalloc byte[EpollEventSize];
        WriteEpollEvent(ev, 0, EPOLLIN, _eventFd);
        fixed (byte* evPtr = ev)
            if (epoll_ctl(_epollFd, EPOLL_CTL_ADD, _eventFd, evPtr) < 0)
            {
                close(_eventFd);
                close(_epollFd);
                ThrowErrno("epoll_ctl ADD eventfd");
            }
    }

    /// <summary>
    /// Register a file descriptor with initial interest flags.
    /// </summary>
    public unsafe void AddFd(int fd, uint events)
    {
        Span<byte> ev = stackalloc byte[EpollEventSize];
        WriteEpollEvent(ev, 0, events, fd);
        fixed (byte* evPtr = ev)
            if (epoll_ctl(_epollFd, EPOLL_CTL_ADD, fd, evPtr) < 0)
                ThrowErrno("epoll_ctl ADD");
    }

    /// <summary>
    /// Update interest flags for a registered file descriptor.
    /// </summary>
    public unsafe void ModFd(int fd, uint events)
    {
        Span<byte> ev = stackalloc byte[EpollEventSize];
        WriteEpollEvent(ev, 0, events, fd);
        fixed (byte* evPtr = ev)
            if (epoll_ctl(_epollFd, EPOLL_CTL_MOD, fd, evPtr) < 0)
                ThrowErrno("epoll_ctl MOD");
    }

    /// <summary>
    /// Remove a file descriptor from epoll.
    /// </summary>
    public unsafe void RemoveFd(int fd)
    {
        epoll_ctl(_epollFd, EPOLL_CTL_DEL, fd, null);
    }

    /// <summary>
    /// Block until at least one FD is ready or <see cref="Wake"/> is called.
    /// Returns the number of ready entries written to <paramref name="results"/>.
    /// </summary>
    /// <param name="results">Buffer to receive (fd, events) pairs.</param>
    /// <param name="timeoutMs">Timeout in milliseconds (-1 = infinite).</param>
    /// <returns>Number of ready entries, 0 on timeout. Wakeup events are consumed publicly.</returns>
    public unsafe int Wait(Span<EpollResult> results, int timeoutMs = -1)
    {
        int maxEvents = Math.Min(results.Length, MaxEvents);
        Span<byte> eventsBuffer = stackalloc byte[maxEvents * EpollEventSize];

        int n;
        fixed (byte* eventsPtr = eventsBuffer)
            n = epoll_wait(_epollFd, eventsPtr, maxEvents, timeoutMs);

        if (n < 0)
        {
            if (Errno == EINTR)
                return 0;
            ThrowErrno("epoll_wait");
        }

        int resultCount = 0;
        for (int i = 0; i < n; i++)
        {
            var (evts, fd) = ReadEpollEvent(eventsBuffer, i);

            if (fd == _eventFd)
            {
                DrainWake();
                continue;
            }

            results[resultCount++] = new EpollResult(fd, evts);
        }

        return resultCount;
    }

    /// <summary>
    /// Wake up a blocking <see cref="Wait"/> call from another thread.
    /// Thread-safe — can be called concurrently.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Wake()
    {
        if (Interlocked.Exchange(ref _wakeRequested, 1) == 0)
        {
            lock (_wakeLock)
            {
                if (_disposed)
                    return;
                unsafe
                {
                    ulong val = 1;
                    write(_eventFd, &val, 8);
                }
            }
        }
    }

    private unsafe void DrainWake()
    {
        ulong val;
        read(_eventFd, &val, 8);
        Volatile.Write(ref _wakeRequested, 0);
    }

    public void Dispose()
    {
        lock (_wakeLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            close(_eventFd);
        }
        close(_epollFd);
    }
}

/// <summary>
/// Result from <see cref="WaylandEventPoll.Wait"/>.
/// </summary>
internal readonly struct EpollResult
{
    public readonly int Fd;
    public readonly uint Events;

    public EpollResult(int fd, uint events)
    {
        Fd = fd;
        Events = events;
    }

    public bool IsReadable => (Events & (EPOLLIN | EPOLLERR | EPOLLHUP)) != 0;
    public bool IsWritable => (Events & (EPOLLOUT | EPOLLERR | EPOLLHUP)) != 0;
    public bool IsError => (Events & (EPOLLERR | EPOLLHUP)) != 0;
}