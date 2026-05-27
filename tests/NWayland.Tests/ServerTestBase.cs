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

    /// <summary>
    /// Creates a client-side fd with the server end immediately closed.
    /// Useful for tests that need a <see cref="WlDisplay"/> without a real server.
    /// </summary>
    protected static int CreateClosedSocketClientFd()
    {
        var (clientFd, serverFd) = WaylandServer.CreateSocketPair();
        close(serverFd);
        return clientFd;
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
