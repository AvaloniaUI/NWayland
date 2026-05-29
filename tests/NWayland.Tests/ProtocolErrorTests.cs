using System;
using System.Threading.Tasks;
using NWayland.Interop;
using NWayland.Protocols.Wayland;
using NWayland.Server;
using Xunit;

namespace NWayland.Tests;

public class ProtocolErrorTests
{
    /// <summary>
    /// The code generator emits each interface's "error" enum into the runtime descriptor,
    /// so protocol errors can be given a name/summary (libwayland exposes only the code).
    /// </summary>
    [Fact]
    public void InterfaceDescriptionExposesErrorEnum()
    {
        var shm = WlShm.ProxyType.Interface;

        Assert.True(shm.TryGetError(0, out var e0));
        Assert.Equal(0u, e0.Code);
        Assert.Equal("invalid_format", e0.Name);
        Assert.Equal("buffer format is not known", e0.Summary);

        Assert.True(shm.TryGetError(2, out var e2));
        Assert.Equal("invalid_fd", e2.Name);

        Assert.False(shm.TryGetError(999, out _));

        // wl_compositor defines no error enum.
        Assert.False(WlCompositor.ProxyType.Interface.TryGetError(0, out _));
    }

    /// <summary>
    /// A <c>wl_display.error</c> from the compositor makes the dispatching call return -1 (rather than
    /// throwing); the error is then retrievable as a <see cref="WaylandProtocolError"/> via
    /// <see cref="WlDisplay.GetProtocolError"/>, carrying the error code.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DispatchReportsProtocolErrorViaGetProtocolError()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        // invalid_object (0) is a wl_display "global" error code; the server raises it against
        // the wl_registry object (which has no error enum of its own), exactly like a failed bind.
        const uint errorCode = 0;
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    if (evt is WaylandServerSyncEvent sync)
                    {
                        // Raise a protocol error in response to the roundtrip's sync instead of
                        // completing it. Tying the error to the sync makes the roundtrip fail
                        // deterministically (no Post/dispatch ordering race). The error references
                        // a real object (the wl_registry, id 2) because wl_display.error's
                        // object_id arg is non-nullable — a null object is rejected as EINVAL
                        // before it can become a protocol error.
                        var registry = waylandClient.ObjectMap.Get(2);
                        waylandClient.PostError(registry, errorCode, "injected protocol error");
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        using var display = WlDisplay.ConnectToFd(clientFd);
        display.GetRegistry();

        // The roundtrip's sync triggers the server-side error; the error event makes the
        // roundtrip fail. Dispatching calls never throw — the error is retrieved explicitly.
        Assert.True(display.Roundtrip() < 0);

        var error = display.GetProtocolError();
        Assert.NotNull(error);
        Assert.Equal(errorCode, error!.Code);
        // The error referenced the wl_registry object, resolved via its interface pointer.
        Assert.Equal("wl_registry", error.InterfaceName);
        Assert.Equal(2u, error.ObjectId);
        // The code is a wl_display global error; wl_registry has no error enum, so the name
        // resolves via the wl_display fallback rather than the object's own interface.
        Assert.Equal("invalid_object", error.ErrorName);

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }
}
