using System;
using System.Threading;
using System.Threading.Tasks;
using NWayland.Protocols.Wayland;
using NWayland.Server;
using NWayland.Tests.Protocols.TestSerialization;
using Xunit;

namespace NWayland.Tests;

public class TeardownTests : ServerTestBase
{
    /// <summary>
    /// Display.Dispose tears down cleanly with no proxies or queues.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DisposeEmptyDisplay()
    {
        var display = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());
        display.Dispose();
        // Double dispose is safe
        display.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Display.Dispose disposes child proxies automatically.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DisposeDisplayDisposesProxies()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    if (evt is WaylandServerSyncEvent sync)
                    {
                        sync.Complete(0);
                        waylandClient.TryFlush();
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(new RegistryCapture());
        display.Roundtrip();

        Assert.False(registry.IsDisposed);

        display.Dispose();

        Assert.True(registry.IsDisposed);

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Display.Dispose disposes queues.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DisposeDisplayDisposesQueues()
    {
        var display = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());
        var queue = display.CreateEventQueue();

        display.Dispose();

        // Queue should be disposed — accessing Handle should throw
        Assert.Throws<ObjectDisposedException>(() => queue.DispatchPending());
        await Task.CompletedTask;
    }

    /// <summary>
    /// After display starts disposing, dispatch methods throw ObjectDisposedException.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DisposeDisplayPreventsDispatch()
    {
        var display = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());
        display.Dispose();

        Assert.Throws<ObjectDisposedException>(() => display.Dispatch());
        Assert.Throws<ObjectDisposedException>(() => display.DispatchPending());
        Assert.Throws<ObjectDisposedException>(() => display.Roundtrip());
        Assert.Throws<ObjectDisposedException>(() => display.Flush());
        Assert.Throws<ObjectDisposedException>(() => display.PrepareRead());
        Assert.Throws<ObjectDisposedException>(() => display.ReadEvents());
        await Task.CompletedTask;
    }

    /// <summary>
    /// After display starts disposing, CreateEventQueue throws.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DisposeDisplayPreventsQueueCreation()
    {
        var display = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());
        display.Dispose();

        Assert.Throws<ObjectDisposedException>(() => display.CreateEventQueue());
        await Task.CompletedTask;
    }

    /// <summary>
    /// Queue.Dispose is safe when queue has no proxies.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DisposeEmptyQueue()
    {
        using var display = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());
        var queue = display.CreateEventQueue();
        queue.Dispose();
        // Double dispose is safe
        queue.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Invoke (destructor) on a disposed proxy is a no-op, not a throw.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DestructorOnDisposedProxyIsNoop()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    if (evt is WaylandServerSyncEvent sync)
                    {
                        sync.Complete(0);
                        waylandClient.TryFlush();
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        using var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(new RegistryCapture());
        display.Roundtrip();

        // Dispose registry first, then call Dispose again (destructor path)
        registry.Dispose();
        Assert.True(registry.IsDisposed);

        // Second dispose should be no-op
        registry.Dispose();

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// SetQueue throws after display starts disposing.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SetQueueThrowsAfterDispose()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    if (evt is WaylandServerSyncEvent sync)
                    {
                        sync.Complete(0);
                        waylandClient.TryFlush();
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(new RegistryCapture());
        display.Roundtrip();

        display.Dispose();

        // Registry is already disposed by display teardown
        Assert.True(registry.IsDisposed);

        // SetQueue on a disposed proxy should throw
        Assert.Throws<ObjectDisposedException>(() => registry.SetQueue(null));

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Roundtrips blocked on display and queue are aborted with ObjectDisposedException
    /// when Dispose is called from another thread.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DisposeAbortsBlockingRoundtrips()
    {
        await using var server = new WaylandServer();
        // Server deliberately does NOT respond — roundtrips will block
        var (_, clientFd) = server.CreateConnectedClient();

        var display = WlDisplay.ConnectToFd(clientFd);
        var queue = display.CreateEventQueue();

        var displayRoundtripStarted = new ManualResetEventSlim();
        var queueRoundtripStarted = new ManualResetEventSlim();

        // Start display roundtrip on a background thread — it will block
        var displayTask = Task.Run(() =>
        {
            displayRoundtripStarted.Set();
            display.Roundtrip();
        });

        // Start queue roundtrip on another background thread — it will block
        var queueTask = Task.Run(() =>
        {
            queueRoundtripStarted.Set();
            queue.Roundtrip();
        });

        // Wait for both roundtrips to start
        displayRoundtripStarted.Wait();
        queueRoundtripStarted.Wait();
        // Give them time to enter the blocking call
        await Task.Delay(100);

        // Dispose display — should shutdown socket, unblocking roundtrips
        display.Dispose();

        // Both tasks should complete (either with exception or return value)
        // The socket shutdown causes the native calls to return with error,
        // and then the IsDisposing check should kick in on subsequent calls
        await Task.WhenAll(displayTask, queueTask);
    }

    [Fact]
    public async Task CrossDisplaySetQueueThrows()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd1) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    if (evt is WaylandServerSyncEvent sync)
                    {
                        sync.Complete(0);
                        waylandClient.TryFlush();
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        using var display1 = WlDisplay.ConnectToFd(clientFd1);
        using var display2 = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());

        var registry = display1.GetRegistry(new RegistryCapture());
        display1.Roundtrip();

        var crossQueue = display2.CreateEventQueue();

        Assert.Throws<ArgumentException>(() => registry.SetQueue(crossQueue));

        crossQueue.Dispose();
        registry.Dispose();

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Queue.Dispose reparents proxies back to the default queue.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task QueueDisposeReparentsProxies()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    if (evt is WaylandServerSyncEvent sync)
                    {
                        sync.Complete(0);
                        waylandClient.TryFlush();
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        using var display = WlDisplay.ConnectToFd(clientFd);
        var queue = display.CreateEventQueue();

        var registry = display.GetRegistry(new RegistryCapture());
        display.Roundtrip();

        // Move registry to custom queue
        registry.SetQueue(queue);
        Assert.Equal(queue, registry.Queue);

        // Dispose queue — proxy should be reparented to default queue
        queue.Dispose();
        Assert.Null(registry.Queue);
        Assert.False(registry.IsDisposed);

        registry.Dispose();

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// SetQueue(display) moves proxy back to the default queue.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task SetQueueToDisplayMovesToDefault()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    if (evt is WaylandServerSyncEvent sync)
                    {
                        sync.Complete(0);
                        waylandClient.TryFlush();
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        using var display = WlDisplay.ConnectToFd(clientFd);
        using var queue = display.CreateEventQueue();

        var registry = display.GetRegistry(new RegistryCapture());
        display.Roundtrip();

        // Move to custom queue
        registry.SetQueue(queue);
        Assert.Equal(queue, registry.Queue);

        // Move back to default queue via IWlTargetQueue (display)
        registry.SetQueue(display);
        Assert.Null(registry.Queue);

        // SetQueue(null) also targets default queue
        registry.SetQueue(queue);
        registry.SetQueue(null);
        Assert.Null(registry.Queue);

        registry.Dispose();

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Import with ownsHandle:false and a listener throws InvalidOperationException.
    /// </summary>
    [Fact]
    public void ImportBorrowedWithListenerThrows()
    {
        using var display = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());

        // Use display's own handle as a dummy — we won't destroy it (ownsHandle: false)
        var dummyHandle = display.Handle;
        var listener = new TestParentCapture();

        Assert.Throws<InvalidOperationException>(() =>
            TestParent.Import(display, null, dummyHandle, ownsHandle: false, listener));
    }

    /// <summary>
    /// Import after display dispose throws ObjectDisposedException.
    /// </summary>
    [Fact]
    public void ImportAfterDisplayDisposeThrows()
    {
        var display = WlDisplay.ConnectToFd(CreateClosedSocketClientFd());
        var handle = display.Handle;
        display.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            TestParent.Import(display, null, handle, ownsHandle: false, null));
    }

    /// <summary>
    /// WlProxyHandle.Dispose is safe to call multiple times.
    /// </summary>
    [Fact]
    public void ProxyHandleDoubleDisposeIsSafe()
    {
        // Non-owning handle — Dispose should be no-op regardless
        var handle = new WlProxyHandle(IntPtr.Zero + 1, ownsHandle: false);
        handle.Dispose();
        handle.Dispose(); // no throw

        // Owning handle with zero (already taken) — also safe
        var handle2 = new WlProxyHandle(IntPtr.Zero, ownsHandle: true);
        handle2.Dispose();
        handle2.Dispose(); // no throw
    }

    /// <summary>
    /// Bind with dispatchOnQueue targets the custom queue.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task BindWithQueueTargetsCustomQueue()
    {
        await using var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    var evt = server.NextEvent();
                    switch (evt)
                    {
                        case WaylandServerSyncEvent sync:
                            sync.Complete(0);
                            waylandClient.TryFlush();
                            break;
                        case WaylandServerRegistryBindEvent bind:
                            serverParent = bind.Accept<TestParent.Server>();
                            waylandClient.TryFlush();
                            break;
                    }
                }
                catch (ObjectDisposedException) { break; }
            }
        });

        using var display = WlDisplay.ConnectToFd(clientFd);
        using var queue = display.CreateEventQueue();

        var registryListener = new RegistryCapture();
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        Assert.Equal("test_parent", registryListener.LastInterface);

        // Bind with custom queue
        var parent = TestParent.Bind(registry,
            registryListener.LastName, registryListener.LastVersion,
            new TestParentCapture(), queue);
        display.Roundtrip();

        Assert.NotNull(parent);
        Assert.Equal(queue, parent.Queue);

        parent.Dispose();
        registry.Dispose();

        await server.DisposeAsync();
        try { await serverTask; } catch (ObjectDisposedException) { }
    }

    private class TestParentCapture : TestParent.Listener
    {
        protected override void Integers(TestParent eventSender, int signedVal, uint unsignedVal) { }
        protected override void FixedPoint(TestParent eventSender, WlFixed value) { }
        protected override void StringEvent(TestParent eventSender, string text) { }
        protected override void ArrayEvent(TestParent eventSender, ReadOnlySpan<byte> data) { }
        protected override void FdEvent(TestParent eventSender, WaylandFd fd) { fd.Consume(); }
        protected override void MixedEvent(TestParent eventSender, int i, uint u, WlFixed f, string s, ReadOnlySpan<byte> a) { }
        protected override void MultiFdEvent(TestParent eventSender, WaylandFd fd1, WaylandFd fd2, string label) { fd1.Consume(); fd2.Consume(); }
        protected override void ObjectEvent(TestParent eventSender, TestChild? obj) { }
        protected override void NewIdEvent(TestParent eventSender, NewId<TestChild, TestChild.Listener> id) { id.GetAndConsume(null); }
    }
}
