using System;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;
using NWayland.Server;

namespace NWayland.Tests;

/// <summary>
/// Shared infrastructure for server-side tests: socket pair creation,
/// I/O thread lifecycle, and common listener implementations.
/// </summary>
public abstract class ServerTestBase : IDisposable
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int socketpair(int domain, int type, int protocol, int* sv);

    [DllImport("libc", SetLastError = true)]
    protected static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    protected static extern int close(int fd);

    protected ServerTestBase()
    {
    }

    public virtual void Dispose()
    {
    }

    protected static unsafe (int clientFd, int serverFd) CreateSocketPair()
    {
        int* sv = stackalloc int[2];
        if (socketpair(AF_UNIX, SOCK_STREAM, 0, sv) < 0)
            throw new InvalidOperationException($"socketpair failed: {Marshal.GetLastPInvokeError()}");
        return (sv[0], sv[1]);
    }

    protected class RegistryCapture : WlRegistry.Listener
    {
        public string? LastInterface { get; private set; }
        public uint LastVersion { get; private set; }
        public uint LastName { get; private set; }

        protected override void Global(WlRegistry eventSender, uint name, string @interface, uint version)
        {
            LastName = name;
            LastInterface = @interface;
            LastVersion = version;
        }
    }
}
