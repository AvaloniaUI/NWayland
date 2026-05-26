using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NWayland.Server.Interop;
using static NWayland.Server.Interop.LinuxInterop;

namespace NWayland.Server;

/// <summary>
/// A managed wrapper around a Wayland Unix domain socket connection.
/// Provides non-blocking I/O with FD passing via SCM_RIGHTS.
/// </summary>
/// <remarks>
/// The socket is set to non-blocking mode. All I/O methods are non-blocking —
/// readiness is determined externally via <see cref="WaylandEventPoll"/>.
/// </remarks>
public sealed class WaylandServerSocket : IDisposable, IWaylandSocketWriter
{
    /// <summary>
    /// Maximum file descriptors per sendmsg/recvmsg call.
    /// Matches libwayland's MAX_FDS_OUT (28).
    /// </summary>
    public const int MaxFdsPerMessage = 28;

    private readonly int _fd;
    private readonly Action<int>? _releaseCallback;
    private volatile bool _readBroken;
    private volatile bool _disposed;

    /// <summary>
    /// Wrap a raw socket file descriptor.
    /// </summary>
    /// <param name="fd">A connected Unix domain socket FD. Ownership is transferred.</param>
    /// <param name="releaseCallback">
    /// Called with the FD on Dispose. If null, <c>close(fd)</c> is used.
    /// </param>
    public WaylandServerSocket(int fd, Action<int>? releaseCallback = null)
    {
        _fd = fd;
        _releaseCallback = releaseCallback;
        SetNonBlocking(fd);
    }

    /// <summary>
    /// Wrap an existing .NET <see cref="Socket"/>. Takes ownership — the Socket
    /// is detached and will be closed on Dispose.
    /// </summary>
    public WaylandServerSocket(Socket socket)
    {
        if (socket == null) throw new ArgumentNullException(nameof(socket));

        _fd = (int)socket.SafeHandle.DangerousGetHandle();
        socket.SafeHandle.SetHandleAsInvalid();
        socket.Dispose();
        _releaseCallback = null;

        SetNonBlocking(_fd);
    }

    /// <summary>
    /// The underlying file descriptor.
    /// </summary>
    internal int Fd => _fd;

    /// <summary>
    /// True if the read side is broken (MSG_CTRUNC detected).
    /// Writes are still allowed to send error events before disconnecting.
    /// </summary>
    public bool IsReadBroken => _readBroken;

    /// <summary>
    /// Shut down the read side of the socket. Further reads will return 0.
    /// Call this on protocol errors to prevent reading corrupted data.
    /// </summary>
    internal void ShutdownRead()
    {
        if (!_readBroken)
        {
            _readBroken = true;
            shutdown(_fd, SHUT_RD);
        }
    }
    /// <summary>
    /// Non-blocking scatter read into two data buffers and two FD buffers
    /// (for ring buffer wraparound). Both data buffers are passed as separate
    /// iovecs to a single recvmsg call. FDs are scatter-copied into fdBuf1 then fdBuf2.
    /// </summary>
    /// <returns>
    /// (BytesRead, FdsRead). BytesRead=-1 means EAGAIN. BytesRead=0 means disconnect.
    /// BytesRead is the total across both data buffers.
    /// </returns>
    internal (int BytesRead, int FdsRead) TryReadNonBlocking(
        Memory<byte> buffer1, Memory<byte> buffer2,
        Memory<int> fdBuf1, Memory<int> fdBuf2)
    {
        ThrowIfDisposed();
        if (_readBroken)
            throw new WaylandConnectionException("Read side is broken (MSG_CTRUNC detected)");
        return DoRecvMsg(buffer1, buffer2, fdBuf1, fdBuf2);
    }
    /// <summary>
    /// Non-blocking write attempt. Returns bytes sent, or -1 for EAGAIN.
    /// On partial write, FDs are still sent with the first chunk.
    /// </summary>
    internal int TryWriteNonBlocking(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds)
    {
        ThrowIfDisposed();
        if (fds.Length > MaxFdsPerMessage)
            throw new ArgumentException(
                $"Cannot send more than {MaxFdsPerMessage} FDs per message, got {fds.Length}");
        return DoSendMsg(buffer, fds);
    }

    int IWaylandSocketWriter.TryWriteNonBlocking(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds)
        => TryWriteNonBlocking(buffer, fds);
    private unsafe (int BytesRead, int FdsRead) DoRecvMsg(
        Memory<byte> buffer1, Memory<byte> buffer2,
        Memory<int> fdBuf1, Memory<int> fdBuf2)
    {
        if (buffer1.Length == 0)
            return (-1, 0);

        using var buf1Pin = buffer1.Pin();
        using var buf2Pin = buffer2.Length > 0 ? buffer2.Pin() : default;

        iovec* iovecs = stackalloc iovec[2];
        int iovCount = 1;
        iovecs[0].iov_base = (IntPtr)buf1Pin.Pointer;
        iovecs[0].iov_len = buffer1.Length;

        if (buffer2.Length > 0)
        {
            iovecs[1].iov_base = (IntPtr)buf2Pin.Pointer;
            iovecs[1].iov_len = buffer2.Length;
            iovCount = 2;
        }

        int cmsgBufSize = CmsgSpace(sizeof(int) * MaxFdsPerMessage);
        byte* cmsgBufPtr = stackalloc byte[cmsgBufSize];
        new Span<byte>(cmsgBufPtr, cmsgBufSize).Clear();

        msghdr msg = default;
        msg.msg_iov = iovecs;
        msg.msg_iovlen = iovCount;
        msg.msg_control = (IntPtr)cmsgBufPtr;
        msg.msg_controllen = cmsgBufSize;

        nint n;
        do
        {
            n = recvmsg(_fd, &msg, MSG_CMSG_CLOEXEC | MSG_DONTWAIT);
        } while (n < 0 && Errno == EINTR);

        if (n < 0)
        {
            int err = Errno;
            if (err == EAGAIN)
                return (-1, 0);
            throw new WaylandConnectionException($"recvmsg failed: errno {err}");
        }

        if (n == 0)
            return (0, 0);

        var cmsgSpan = new ReadOnlySpan<byte>(cmsgBufPtr, (int)msg.msg_controllen);

        // MSG_CTRUNC — ancillary data was truncated
        if ((msg.msg_flags & MSG_CTRUNC) != 0)
        {
            CloseAllReceivedFds(cmsgSpan);
            ShutdownRead();
            throw new WaylandConnectionException(
                "Ancillary data truncated (MSG_CTRUNC). Client sent more FDs than " +
                $"MaxFdsPerMessage ({MaxFdsPerMessage}). Connection read side is broken — " +
                "leaked FDs cannot be recovered.");
        }

        // Extract FDs from cmsg headers directly into scatter-gather destination
        int totalFdSpace = fdBuf1.Length + fdBuf2.Length;
        var fdSpan1 = fdBuf1.Span;
        var fdSpan2 = fdBuf2.Span;
        int fdsRead = 0;
        int cmsgOff = 0;
        while (TryReadNextCmsg(cmsgSpan, ref cmsgOff, out int level, out int type, out var payload))
        {
            if (level != SOL_SOCKET || type != SCM_RIGHTS)
                continue;

            var fds = MemoryMarshal.Cast<byte, int>(payload);
            for (int i = 0; i < fds.Length; i++)
            {
                if (fdsRead >= totalFdSpace)
                {
                    CloseAllReceivedFds(cmsgSpan);
                    ShutdownRead();
                    throw new WaylandConnectionException(
                        "FD buffer underrun: received more FDs than ring buffer can hold");
                }
                if (fdsRead < fdBuf1.Length)
                    fdSpan1[fdsRead] = fds[i];
                else
                    fdSpan2[fdsRead - fdBuf1.Length] = fds[i];
                fdsRead++;
            }
        }

        return ((int)n, fdsRead);
    }

    private static void CloseAllReceivedFds(ReadOnlySpan<byte> controlBuf)
    {
        int offset = 0;
        while (TryReadNextCmsg(controlBuf, ref offset, out int level, out int type, out var payload))
        {
            if (level != SOL_SOCKET || type != SCM_RIGHTS)
                continue;

            var fds = MemoryMarshal.Cast<byte, int>(payload);
            for (int i = 0; i < fds.Length; i++)
                close(fds[i]);
        }
    }

    private unsafe int DoSendMsg(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds)
    {
        using var bufPin = buffer.Pin();

        iovec iov;
        iov.iov_base = (IntPtr)bufPin.Pointer;
        iov.iov_len = buffer.Length;

        msghdr msg = default;
        msg.msg_iov = &iov;
        msg.msg_iovlen = 1;

        if (fds.Length > 0)
        {
            int cmsgBufSize = CmsgSpace(sizeof(int) * fds.Length);
            byte* cmsgBufPtr = stackalloc byte[cmsgBufSize];
            var cmsgSpan = new Span<byte>(cmsgBufPtr, cmsgBufSize);
            cmsgSpan.Clear();

            WriteCmsg(cmsgSpan, SOL_SOCKET, SCM_RIGHTS,
                MemoryMarshal.AsBytes(fds.Span));

            msg.msg_control = (IntPtr)cmsgBufPtr;
            msg.msg_controllen = cmsgBufSize;
        }

        nint n;
        do
        {
            n = sendmsg(_fd, &msg, MSG_DONTWAIT | MSG_NOSIGNAL);
        } while (n < 0 && Errno == EINTR);

        if (n < 0)
        {
            int err = Errno;
            if (err == EAGAIN)
                return -1;
            throw new WaylandConnectionException($"sendmsg failed: errno {err}");
        }

        return (int)n;
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_releaseCallback != null)
            _releaseCallback(_fd);
        else
            close(_fd);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WaylandServerSocket));
    }
}
