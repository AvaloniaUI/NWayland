using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NWayland.Protocols.Wayland;
using NWayland.Server;
using Xunit;

namespace NWayland.Tests;

/// <summary>
/// Tests for wrapping an externally-created (non-owned) wl_display* via WlDisplay.FromHandle.
/// </summary>
public class WlDisplayImportTests
{
    // Create the wl_display* directly via libwayland so NWayland is genuinely not the owner.
    [DllImport("libwayland-client.so.0", ExactSpelling = true)]
    private static extern IntPtr wl_display_connect_to_fd(int fd);

    [DllImport("libwayland-client.so.0", ExactSpelling = true)]
    private static extern void wl_display_disconnect(IntPtr display);

    /// <summary>
    /// A borrowed WlDisplay (ownsHandle: false) must not disconnect the connection when disposed —
    /// the original owner keeps it alive.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task FromHandle_NonOwned_DoesNotDisconnectOnDispose()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    if (server.NextEvent() is WaylandServerSyncEvent sync)
                    {
                        sync.Complete(0);
                        waylandClient.TryFlush();
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        // wl_display* owned by us (the test), not by NWayland.
        var handle = wl_display_connect_to_fd(clientFd);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            using (var borrowed = WlDisplay.FromHandle(handle, ownsHandle: false))
                Assert.True(borrowed.Roundtrip() >= 0);

            // If disposing the borrowed view had disconnected, this second roundtrip over the
            // same still-open connection would fail. It succeeding proves the connection survived.
            using (var borrowed2 = WlDisplay.FromHandle(handle, ownsHandle: false))
                Assert.True(borrowed2.Roundtrip() >= 0);
        }
        finally
        {
            wl_display_disconnect(handle); // we own it — clean up exactly once
        }

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }
}
