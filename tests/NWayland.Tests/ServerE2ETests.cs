using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NWayland.Protocols.Wayland;
using NWayland.Server;
using Xunit;

namespace NWayland.Tests;

public class ServerE2ETests : ServerTestBase
{
    /// <summary>
    /// NextEventPending returns null immediately when idle (never blocks) and drains all
    /// currently-pending events one at a time until exhausted.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NextEventPending_DrainsWithoutBlocking()
    {
        await using var server = new WaylandServer();

        // Idle: returns null immediately. If it blocked in epoll_wait, the test would time out.
        Assert.Null(server.NextEventPending());

        server.Post("a");
        server.Post("b");
        server.Post("c");

        var drained = new System.Collections.Generic.List<object?>();
        WaylandServerEvent? evt;
        while ((evt = server.NextEventPending()) != null)
            drained.Add(((WaylandCustomEvent)evt).State);

        Assert.Equal(new object?[] { "a", "b", "c" }, drained);

        // Fully drained again.
        Assert.Null(server.NextEventPending());
    }

    /// <summary>
    /// NextEventPending refreshes readiness with a zero-timeout epoll, so it picks up data that
    /// arrives on a client socket without any prior blocking NextEvent — and still never blocks.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task NextEventPending_PicksUpFreshSocketData()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        using var display = WlDisplay.ConnectToFd(clientFd);
        // Client roundtrip on a background thread — blocks until the server completes the sync.
        var clientTask = Task.Run(() => display.Roundtrip());

        // Drive the server purely with NextEventPending (never the blocking NextEvent). The
        // zero-timeout poll discovers the client's freshly-arrived sync once it lands.
        WaylandServerEvent? evt = null;
        while (evt == null)
        {
            evt = server.NextEventPending();
            if (evt == null)
                await Task.Delay(5);
        }

        var sync = Assert.IsType<WaylandServerSyncEvent>(evt);
        sync.Complete(0);
        waylandClient.TryFlush();

        await clientTask;
    }

    /// <summary>
    /// Client connects, sends sync (via Roundtrip), server completes it.
    /// Verifies the full request/response cycle works end to end.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SyncRoundtrip()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        // Server event loop (blocking, on background thread)
        var serverTask = Task.Run(() =>
        {
            var evt = server.NextEvent();
            Assert.IsType<WaylandServerSyncEvent>(evt);
            var sync = (WaylandServerSyncEvent)evt;
            sync.Complete(42);
            waylandClient.TryFlush();
        });

        // Client: connect and roundtrip
        using var display = WlDisplay.ConnectToFd(clientFd);
        var result = display.Roundtrip();
        Assert.True(result >= 0, $"Roundtrip returned {result}");

        await serverTask;
    }

    /// <summary>
    /// Client does get_registry (via Roundtrip), server advertises a global.
    /// Client sees the global in its registry listener.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task RegistryGlobal()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        // Add a global before the client connects
        waylandClient.AddGlobal("wl_compositor", 5);

        string? receivedInterface = null;
        uint receivedVersion = 0;

        // Server event loop: handle sync requests (blocking, on background thread)
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try
                {
                    evt = server.NextEvent();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (evt is WaylandServerSyncEvent sync)
                {
                    sync.Complete(0);
                    waylandClient.TryFlush();
                }
            }
        });

        // Client: connect with registry listener
        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);

        // First roundtrip triggers get_registry
        display.Roundtrip();

        // Get the registry
        using var registry = display.GetRegistry(registryListener);
        display.Roundtrip(); // This should bring the global events

        receivedInterface = registryListener.LastInterface;
        receivedVersion = registryListener.LastVersion;

        // Stop server loop
        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }

        Assert.Equal("wl_compositor", receivedInterface);
        Assert.Equal(5u, receivedVersion);
    }

    /// <summary>
    /// Client binds a global, server accepts it.
    /// Verifies the bind machinery works end to end.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task RegistryBind()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("wl_compositor", 5);

        WlCompositor.Server? serverCompositor = null;

        // Server event loop (blocking, on background thread)
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try
                {
                    evt = server.NextEvent();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;

                    case WaylandServerRegistryBindEvent bind:
                        serverCompositor = bind.Accept<WlCompositor.Server>();
                        waylandClient.TryFlush();
                        break;
                }
            }
        });

        // Client: connect, get registry, bind compositor
        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);
        using var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        WlCompositor? clientCompositor = null;
        if (registryListener.LastInterface == "wl_compositor")
        {
            clientCompositor = registry.Bind<WlCompositor>(
                registryListener.LastName, registryListener.LastVersion);
            display.Roundtrip();
        }

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }

        Assert.NotNull(clientCompositor);
        Assert.NotNull(serverCompositor);
    }

    /// <summary>
    /// Verify that the server disconnects a client that sends too many
    /// file descriptors without matching complete messages (FD flooding).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FdFloodingDisconnectsClient()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        // Wait for the disconnect event (blocking, on background thread)
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try
                {
                    evt = server.NextEvent();
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (evt is WaylandClientDisconnectEvent)
                    return true;
                // Handle sync if needed
                if (evt is WaylandServerSyncEvent sync)
                {
                    sync.Complete(0);
                    waylandClient.TryFlush();
                }
            }
        });

        // Flood the socket with FDs via an incomplete message.
        // Send a partial header (only 4 bytes — need 8 for a complete header)
        // along with 29 FDs (exceeds the 28 limit).
        await Task.Run(() =>
        {
            // Create 29 pipe FDs to send
            int[] pipeFds = new int[29];
            for (int i = 0; i < 29; i++)
            {
                int[] p = new int[2];
                if (pipe(p) < 0)
                    throw new InvalidOperationException("pipe failed");
                pipeFds[i] = p[0];
                close(p[1]); // Only need the read end
            }

            // Send an incomplete header (4 bytes) with 28 FDs
            SendBytesWithFds(clientFd, new byte[] { 1, 0, 0, 0 }, pipeFds.AsSpan(0, 28));

            // Send one more byte with the 29th FD — this pushes over the limit.
            // (Deliberately a separate send so the 29th FD can arrive after the server has already
            // drained the first batch — the case that used to dead-spin the event loop.)
            SendBytesWithFds(clientFd, new byte[] { 0 }, pipeFds.AsSpan(28, 1));
        });

        var disconnected = await serverTask;
        Assert.True(disconnected, "Server should disconnect the flooding client");
    }

    /// <summary>
    /// Regression test for an event-loop livelock (CI flake in FdFloodingDisconnectsClient).
    /// A request split across two socket sends, where the server reads the INCOMPLETE first half
    /// before the completing half arrives, must still parse once the rest shows up.
    ///
    /// The bug: <c>FindReadyClient</c> treated any buffered bytes (including an incomplete message)
    /// as "ready", so after reading the partial first half the loop kept re-selecting the client and
    /// spun without ever returning to epoll — so the completing bytes were never read and the message
    /// never parsed. The 250ms gap below deterministically forces the "read first half alone"
    /// interleaving; with the bug the server spins and the sync is never seen (test times out), with
    /// the fix it blocks in epoll and wakes on the second send.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SplitMessage_IncompleteHeaderReadFirst_StillParses()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var syncReceived = new TaskCompletionSource();
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { return; }

                if (evt is WaylandServerSyncEvent sync)
                {
                    sync.Complete(0);
                    waylandClient.TryFlush();
                    syncReceived.TrySetResult();
                }
            }
        });

        // wl_display.sync(new_id=2): objectId=1, size=12, opcode=0, arg new_id=2
        var sync = new byte[12];
        WriteU32(sync, 0, 1);
        WriteU32(sync, 4, (12u << 16) | 0u);
        WriteU32(sync, 8, 2);

        await Task.Run(() =>
        {
            // Send only the first 4 bytes — half of the 8-byte header, an incomplete message.
            RawWrite(clientFd, sync[..4]);

            // Let the server read this partial message ALONE and return to waiting. With the
            // livelock bug it spins here instead of going back to epoll, so the bytes below are
            // never read; with the fix it blocks in epoll until they arrive.
            System.Threading.Thread.Sleep(250);

            // Send the rest — must wake the server and complete the message.
            RawWrite(clientFd, sync[4..]);
        });

        var done = await Task.WhenAny(syncReceived.Task, Task.Delay(8000));
        Assert.True(done == syncReceived.Task,
            "Server must parse a message split across two sends even when the incomplete first half is read alone");

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// A client that sends wl_registry.bind with a server-range object ID
    /// (>= 0xff000000) should receive a protocol error and be disconnected.
    /// Uses raw wire-format packets to craft the invalid bind request.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task BindWithServerRangeId_DisconnectsClient()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("wl_compositor", 5);

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { return false; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandClientDisconnectEvent:
                        return true;
                }
            }
        });

        await Task.Run(() =>
        {
            // Step 1: Send wl_display.get_registry(new_id=2)
            // header: objectId=1(display), size=12, opcode=1(get_registry)
            // arg: uint32 new_id = 2
            var getRegistry = new byte[12];
            WriteU32(getRegistry, 0, 1);          // objectId = wl_display
            WriteU32(getRegistry, 4, (12u << 16) | 1u); // size=12, opcode=1
            WriteU32(getRegistry, 8, 2);          // new_id = 2 (registry)
            RawWrite(clientFd, getRegistry);

            // Step 2: Read server response (global events, etc.) — just drain enough
            var buf = new byte[4096];
            RawRead(clientFd, buf);

            // Step 3: Send wl_registry.bind with server-range ID
            // bind args: uint(name=1), string("wl_compositor"), uint(ver=1), uint(new_id=0xff000000)
            string iface = "wl_compositor";
            int ifaceWireLen = iface.Length + 1; // +NUL
            int ifacePadded = (ifaceWireLen + 3) & ~3;
            int msgSize = 8 + 4 + 4 + ifacePadded + 4 + 4; // header + name + strlen + string + ver + newid
            var bind = new byte[msgSize];
            int off = 0;
            WriteU32(bind, off, 2); off += 4;    // objectId = wl_registry
            WriteU32(bind, off, ((uint)msgSize << 16) | 0u); off += 4; // size|opcode=0(bind)
            WriteU32(bind, off, 1); off += 4;    // name = 1 (first global)
            WriteU32(bind, off, (uint)ifaceWireLen); off += 4; // string length
            System.Text.Encoding.UTF8.GetBytes(iface, 0, iface.Length, bind, off);
            off += ifacePadded;
            WriteU32(bind, off, 1); off += 4;    // version = 1
            WriteU32(bind, off, 0xff000000); // new_id in server range — should be rejected
            RawWrite(clientFd, bind);

            // Read until error or EOF
            try { RawRead(clientFd, buf); } catch { }
        });

        var disconnected = await serverTask;
        Assert.True(disconnected, "Server should disconnect client that sends server-range object ID");
    }

    /// <summary>
    /// A client that sends wl_display.sync with a server-range callback ID
    /// (typed new_id) should be disconnected with a protocol error.
    /// This tests the parser-level validation for typed new_id args.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SyncWithServerRangeCallbackId_DisconnectsClient()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { return false; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandClientDisconnectEvent:
                        return true;
                }
            }
        });

        await Task.Run(() =>
        {
            // Send wl_display.sync(new_id=0xff000000) — server-range ID
            // header: objectId=1(display), size=12, opcode=0(sync)
            // arg: uint32 new_id = 0xff000000
            var sync = new byte[12];
            WriteU32(sync, 0, 1);                      // objectId = wl_display
            WriteU32(sync, 4, (12u << 16) | 0u);       // size=12, opcode=0
            WriteU32(sync, 8, 0xff000000);             // new_id in server range
            RawWrite(clientFd, sync);

            // Read until error or EOF
            var buf = new byte[4096];
            try { RawRead(clientFd, buf); } catch { }
        });

        var disconnected = await serverTask;
        Assert.True(disconnected, "Server should disconnect client that sends server-range callback ID");
    }

    /// <summary>
    /// When MaxClientObjects is set, a client that creates an object with an ID
    /// at or above the limit should be disconnected with a protocol error.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task MaxClientObjects_EnforcesLimit()
    {
        // Limit = 5: IDs 1-5 are allowed (matching libwayland's > check), ID 6+ is rejected.
        // wl_display is always ID 1. First wl_callback (sync) is ID 2.
        // wl_registry is ID 3, second callback (sync roundtrip) is ID 4.
        // Third callback is ID 5 (last allowed). Fourth callback (ID 6) should be rejected.
        await using var server = new WaylandServer(new WaylandServerOptions
        {
            MaxClientObjects = 5
        });
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { return false; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandClientDisconnectEvent:
                        return true;
                }
            }
        });

        await Task.Run(() =>
        {
            // Sync 1: callback ID = 2 (OK)
            var msg = new byte[12];
            WriteU32(msg, 0, 1);
            WriteU32(msg, 4, (12u << 16) | 0u);
            WriteU32(msg, 8, 2);
            RawWrite(clientFd, msg);

            // Read response (wl_callback.done + delete_id)
            var buf = new byte[4096];
            RawRead(clientFd, buf);

            // Get registry: ID = 3 (OK)
            WriteU32(msg, 0, 1);
            WriteU32(msg, 4, (12u << 16) | 1u);
            WriteU32(msg, 8, 3);
            RawWrite(clientFd, msg);

            // Sync 2: callback ID = 4 (OK)
            WriteU32(msg, 0, 1);
            WriteU32(msg, 4, (12u << 16) | 0u);
            WriteU32(msg, 8, 4);
            RawWrite(clientFd, msg);

            // Read responses
            RawRead(clientFd, buf);

            // Sync 3: callback ID = 5 (still within limit, OK)
            WriteU32(msg, 0, 1);
            WriteU32(msg, 4, (12u << 16) | 0u);
            WriteU32(msg, 8, 5);
            RawWrite(clientFd, msg);

            // Read responses
            RawRead(clientFd, buf);

            // Sync 4: callback ID = 6 (exceeds limit, should be rejected)
            WriteU32(msg, 0, 1);
            WriteU32(msg, 4, (12u << 16) | 0u);
            WriteU32(msg, 8, 6);
            RawWrite(clientFd, msg);

            // Read error response or EOF
            try { RawRead(clientFd, buf); } catch { }
        });

        var disconnected = await serverTask;
        Assert.True(disconnected, "Server should disconnect client that exceeds MaxClientObjects");
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static unsafe void RawWrite(int fd, byte[] data)
    {
        fixed (byte* ptr = data)
        {
            nint written = write(fd, ptr, data.Length);
            if (written < 0)
                throw new InvalidOperationException($"write failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private static unsafe int RawRead(int fd, byte[] buf)
    {
        fixed (byte* ptr = buf)
        {
            nint n = read(fd, ptr, buf.Length);
            if (n < 0)
                throw new InvalidOperationException($"read failed: {Marshal.GetLastPInvokeError()}");
            return (int)n;
        }
    }

    private static unsafe void SendBytesWithFds(int socketFd, ReadOnlySpan<byte> data, ReadOnlySpan<int> fds)
    {
        fixed (byte* dataPtr = data)
        fixed (int* fdsPtr = fds)
        {
            var iov = new Iovec { iov_base = dataPtr, iov_len = (nuint)data.Length };

            int cmsgLen = CmsgLen(fds.Length * sizeof(int));
            int cmsgSpace = CmsgSpace(fds.Length * sizeof(int));
            byte* cmsgBuf = stackalloc byte[cmsgSpace];

            var cmsghdr = (Cmsghdr*)cmsgBuf;
            cmsghdr->cmsg_len = (nuint)cmsgLen;
            cmsghdr->cmsg_level = 1; // SOL_SOCKET
            cmsghdr->cmsg_type = 1;  // SCM_RIGHTS

            Buffer.MemoryCopy(fdsPtr, cmsgBuf + CmsgDataOffset(), fds.Length * sizeof(int), fds.Length * sizeof(int));

            var msg = new Msghdr
            {
                msg_iov = &iov,
                msg_iovlen = 1,
                msg_control = cmsgBuf,
                msg_controllen = (nuint)cmsgSpace
            };

            int sent = sendmsg(socketFd, &msg, 0);
            if (sent < 0)
                throw new InvalidOperationException($"sendmsg failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int sendmsg(int sockfd, Msghdr* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint write(int fd, void* buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe nint read(int fd, void* buf, nint count);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Iovec
    {
        public byte* iov_base;
        public nuint iov_len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Msghdr
    {
        public void* msg_name;
        public uint msg_namelen;
        public Iovec* msg_iov;
        public nuint msg_iovlen;
        public void* msg_control;
        public nuint msg_controllen;
        public int msg_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Cmsghdr
    {
        public nuint cmsg_len;
        public int cmsg_level;
        public int cmsg_type;
    }

    private static int CmsgDataOffset()
    {
        // CMSG_DATA offset: size of cmsghdr aligned to sizeof(nuint)
        int hdrSize = Marshal.SizeOf<Cmsghdr>();
        int align = IntPtr.Size;
        return (hdrSize + align - 1) & ~(align - 1);
    }

    private static int CmsgLen(int dataLen) => CmsgDataOffset() + dataLen;
    private static int CmsgSpace(int dataLen)
    {
        int align = IntPtr.Size;
        return (CmsgLen(dataLen) + align - 1) & ~(align - 1);
    }
}
