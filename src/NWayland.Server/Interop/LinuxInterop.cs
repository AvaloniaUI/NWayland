using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

// The interop structs below deliberately mirror C/libc names (msghdr, iovec, cmsghdr, …),
// which trips CS8981 (all-lowercase type name). That's intentional for a P/Invoke layer.
#pragma warning disable CS8981

namespace NWayland.Server.Interop;

internal static unsafe class LinuxInterop
{
    private const string Libc = "libc";
    [DllImport(Libc, SetLastError = true)]
    public static extern nint recvmsg(int sockfd, msghdr* msg, int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint sendmsg(int sockfd, msghdr* msg, int flags);
    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_create1(int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_ctl(int epfd, int op, int fd, void* @event);

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_wait(int epfd, void* events, int maxevents, int timeout);
    [DllImport(Libc, SetLastError = true)]
    public static extern int eventfd(uint initval, int flags);
    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern int fcntl(int fd, int cmd, int arg);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint read(int fd, void* buf, nint count);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint write(int fd, void* buf, nint count);
    [DllImport(Libc, SetLastError = true)]
    public static extern int socketpair(int domain, int type, int protocol, int* sv);

    [DllImport(Libc, SetLastError = true)]
    public static extern int shutdown(int sockfd, int how);
    // recvmsg / sendmsg flags
    public const int MSG_DONTWAIT = 0x40;
    public const int MSG_NOSIGNAL = 0x4000;
    public const int MSG_CMSG_CLOEXEC = 0x40000000;
    public const int MSG_CTRUNC = 0x8;

    // shutdown
    public const int SHUT_RD = 0;
    public const int SHUT_WR = 1;
    public const int SHUT_RDWR = 2;

    // Socket level
    public const int SOL_SOCKET = 1;
    public const int SCM_RIGHTS = 1;

    // epoll
    public const int EPOLL_CLOEXEC = 0x80000;
    public const int EPOLL_CTL_ADD = 1;
    public const int EPOLL_CTL_DEL = 2;
    public const int EPOLL_CTL_MOD = 3;
    public const uint EPOLLIN = 0x001;
    public const uint EPOLLOUT = 0x004;
    public const uint EPOLLERR = 0x008;
    public const uint EPOLLHUP = 0x010;

    // eventfd
    public const int EFD_NONBLOCK = 0x800;
    public const int EFD_CLOEXEC = 0x80000;

    // fcntl
    public const int F_GETFL = 3;
    public const int F_SETFL = 4;
    public const int O_NONBLOCK = 0x800;

    // errno
    public const int EAGAIN = 11;
    public const int EINTR = 4;

    // socketpair
    public const int AF_UNIX = 1;
    public const int SOCK_STREAM = 1;
    [StructLayout(LayoutKind.Sequential)]
    public struct msghdr
    {
        public IntPtr msg_name;
        public uint msg_namelen;
        public iovec* msg_iov;
        public nint msg_iovlen;
        public IntPtr msg_control;
        public nint msg_controllen;
        public int msg_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct iovec
    {
        public IntPtr iov_base;
        public nint iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct cmsghdr
    {
        public nint cmsg_len;
        public int cmsg_level;
        public int cmsg_type;
    }

    // Reimplementation of the CMSG_* macros from <sys/socket.h>
    // Alignment is to sizeof(nint) on 64-bit Linux (8 bytes).

    private static nint CmsgAlign(nint len) => (len + sizeof(nint) - 1) & ~(sizeof(nint) - 1);

    public static int CmsgSpace(int payloadLen) =>
        (int)(CmsgAlign(sizeof(cmsghdr)) + CmsgAlign(payloadLen));

    public static int CmsgLen(int payloadLen) =>
        (int)(CmsgAlign(sizeof(cmsghdr)) + payloadLen);

    /// <summary>
    /// Iterate cmsg headers in a control buffer span.
    /// Call repeatedly with the same <paramref name="offset"/> variable (initially 0).
    /// </summary>
    public static bool TryReadNextCmsg(ReadOnlySpan<byte> controlBuf, ref int offset,
        out int level, out int type, out ReadOnlySpan<byte> payload)
    {
        int hdrSize = Unsafe.SizeOf<cmsghdr>();
        int hdrAligned = (int)CmsgAlign(hdrSize);

        if (offset + hdrSize > controlBuf.Length)
        {
            level = 0; type = 0; payload = default;
            return false;
        }

        var hdr = MemoryMarshal.Read<cmsghdr>(controlBuf.Slice(offset));
        if (hdr.cmsg_len < hdrSize)
        {
            level = 0; type = 0; payload = default;
            return false;
        }

        level = hdr.cmsg_level;
        type = hdr.cmsg_type;

        int payloadLen = (int)hdr.cmsg_len - hdrAligned;
        payload = payloadLen > 0
            ? controlBuf.Slice(offset + hdrAligned, payloadLen)
            : ReadOnlySpan<byte>.Empty;

        offset += (int)CmsgAlign((int)hdr.cmsg_len);
        return true;
    }

    /// <summary>
    /// Write a single cmsg header + payload into a control buffer.
    /// The buffer must be pre-sized with <see cref="CmsgSpace"/>.
    /// </summary>
    public static void WriteCmsg(Span<byte> controlBuf, int level, int type,
        ReadOnlySpan<byte> payload)
    {
        int hdrAligned = (int)CmsgAlign(Unsafe.SizeOf<cmsghdr>());

        var hdr = new cmsghdr
        {
            cmsg_len = (nint)(hdrAligned + payload.Length),
            cmsg_level = level,
            cmsg_type = type
        };

        MemoryMarshal.Write(controlBuf, in hdr);
        payload.CopyTo(controlBuf.Slice(hdrAligned));
    }

    // epoll_event layout varies by architecture:
    // - x86_64: __attribute__((packed)), no padding → size=12, data@4
    // - x86 (i386): u64 has 4-byte alignment (SysV ABI), no padding → size=12, data@4
    // - ARM32/ARM64: u64 has 8-byte alignment (AAPCS), 4 bytes padding → size=16, data@8
    // We use byte-level I/O with architecture-aware offsets and
    // BinaryPrimitives for safe, alignment-correct reads/writes.

    private static readonly bool IsX86Family =
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

    public static readonly int EpollEventSize = IsX86Family ? 12 : 16;

    private static readonly int EpollEventDataOffset = IsX86Family ? 4 : 8;

    public static void WriteEpollEvent(Span<byte> buf, int index, uint events, int fd)
    {
        var span = buf.Slice(index * EpollEventSize, EpollEventSize);
        span.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, events);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(EpollEventDataOffset), fd);
    }

    public static (uint events, int fd) ReadEpollEvent(ReadOnlySpan<byte> buf, int index)
    {
        var span = buf.Slice(index * EpollEventSize, EpollEventSize);
        uint events = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span);
        int fd = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            span.Slice(EpollEventDataOffset));
        return (events, fd);
    }
    public static int Errno => Marshal.GetLastPInvokeError();

    public static void SetNonBlocking(int fd)
    {
        int flags = fcntl(fd, F_GETFL, 0);
        if (flags < 0)
            throw new InvalidOperationException($"fcntl F_GETFL failed: errno {Errno}");
        if (fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0)
            throw new InvalidOperationException($"fcntl F_SETFL O_NONBLOCK failed: errno {Errno}");
    }

    public static void ThrowErrno(string syscall)
    {
        int err = Errno;
        throw new InvalidOperationException($"{syscall} failed: errno {err}");
    }
}
