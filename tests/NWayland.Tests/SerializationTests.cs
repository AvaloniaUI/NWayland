using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NWayland.Interop;
using NWayland.Protocols.Wayland;
using NWayland.Server;
using NWayland.Tests.Protocols.TestSerialization;
using Xunit;

namespace NWayland.Tests;

/// <summary>
/// Tests that exercise wire format serialization/deserialization for all argument types
/// using a custom test protocol that has int, uint, fixed, string, array, fd, object, and new_id args.
/// </summary>
public class SerializationTests : ServerTestBase
{
    /// <summary>
    /// Helper that sets up a connected client+server pair with a bound TestParent resource.
    /// Returns all the pieces needed for testing.
    /// </summary>
    private async Task<TestFixture> SetupBoundTestParent(TestParent.ServerListener? serverListener = null)
    {
        var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;
        var serverReady = new TaskCompletionSource();

        // Start server event loop (blocking, on background thread)
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { break; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandServerRegistryBindEvent bind:
                        serverParent = bind.Accept<TestParent.Server>(serverListener);
                        waylandClient.TryFlush();
                        serverReady.TrySetResult();
                        break;
                    case WaylandServerRequestEvent req:
                        req.Dispatch();
                        waylandClient.TryFlush();
                        break;
                    case WaylandCustomEvent custom:
                        if (custom.State is Action action) action();
                        waylandClient.TryFlush();
                        break;
                }
            }
        });

        // Client: connect, get registry, bind test_parent
        var registryListener = new RegistryCapture();
        var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        Assert.Equal("test_parent", registryListener.LastInterface);

        var clientListener = new TestParentCapture();
        var clientParent = registry.Bind<TestParent>(
            registryListener.LastName, registryListener.LastVersion, clientListener);
        display.Roundtrip();

        await serverReady.Task;

        return new TestFixture(server, waylandClient, display, registry, clientParent,
            clientListener, serverParent!, serverTask);
    }
    [Fact(Timeout = 10000)]
    public async Task IntegersSerialization()
    {
        await using var f = await SetupBoundTestParent();

        f.Server.Post(() => f.ServerParent.Integers(-42, 12345u));
        f.Display.Roundtrip();

        Assert.Equal(-42, f.ClientListener.LastSignedVal);
        Assert.Equal(12345u, f.ClientListener.LastUnsignedVal);
    }

    [Fact(Timeout = 10000)]
    public async Task IntegerEdgeCases()
    {
        await using var f = await SetupBoundTestParent();

        // Min/max values
        f.Server.Post(() => f.ServerParent.Integers(int.MinValue, uint.MaxValue));
        f.Display.Roundtrip();

        Assert.Equal(int.MinValue, f.ClientListener.LastSignedVal);
        Assert.Equal(uint.MaxValue, f.ClientListener.LastUnsignedVal);
    }

    [Fact(Timeout = 10000)]
    public async Task FixedPointSerialization()
    {
        await using var f = await SetupBoundTestParent();

        var fixedVal = new WlFixed(3.14);
        f.Server.Post(() => f.ServerParent.FixedPoint(fixedVal));
        f.Display.Roundtrip();

        Assert.Equal(fixedVal, f.ClientListener.LastFixed);
    }

    [Fact(Timeout = 10000)]
    public async Task StringSerialization()
    {
        await using var f = await SetupBoundTestParent();

        f.Server.Post(() => f.ServerParent.StringEvent("Hello, Wayland!"));
        f.Display.Roundtrip();

        Assert.Equal("Hello, Wayland!", f.ClientListener.LastString);
    }

    [Fact(Timeout = 10000)]
    public async Task EmptyStringSerialization()
    {
        await using var f = await SetupBoundTestParent();

        f.Server.Post(() => f.ServerParent.StringEvent(""));
        f.Display.Roundtrip();

        Assert.Equal("", f.ClientListener.LastString);
    }

    [Fact(Timeout = 10000)]
    public async Task UnicodeStringSerialization()
    {
        await using var f = await SetupBoundTestParent();

        f.Server.Post(() => f.ServerParent.StringEvent("Привет мир 🌍"));
        f.Display.Roundtrip();

        Assert.Equal("Привет мир 🌍", f.ClientListener.LastString);
    }

    [Fact(Timeout = 10000)]
    public async Task ArraySerialization()
    {
        await using var f = await SetupBoundTestParent();

        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        f.Server.Post(() => f.ServerParent.ArrayEvent(data));
        f.Display.Roundtrip();

        Assert.NotNull(f.ClientListener.LastArrayData);
        Assert.Equal(data, f.ClientListener.LastArrayData);
    }

    [Fact(Timeout = 10000)]
    public async Task EmptyArraySerialization()
    {
        await using var f = await SetupBoundTestParent();

        f.Server.Post(() => f.ServerParent.ArrayEvent(Array.Empty<byte>()));
        f.Display.Roundtrip();

        Assert.NotNull(f.ClientListener.LastArrayData);
        Assert.Empty(f.ClientListener.LastArrayData!);
    }

    [Fact(Timeout = 10000)]
    public async Task NonAlignedArraySerialization()
    {
        await using var f = await SetupBoundTestParent();

        // 5 bytes — not 4-byte aligned, tests padding
        byte[] data = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        f.Server.Post(() => f.ServerParent.ArrayEvent(data));
        f.Display.Roundtrip();

        Assert.Equal(data, f.ClientListener.LastArrayData);
    }

    [Fact(Timeout = 10000)]
    public async Task MixedArgsSerialization()
    {
        await using var f = await SetupBoundTestParent();

        var fixedVal = new WlFixed(-1.5);
        byte[] arrayData = [10, 20, 30];
        f.Server.Post(() => f.ServerParent.MixedEvent(99, 200u, fixedVal, "mixed test", arrayData));
        f.Display.Roundtrip();

        Assert.Equal(99, f.ClientListener.MixedI);
        Assert.Equal(200u, f.ClientListener.MixedU);
        Assert.Equal(fixedVal, f.ClientListener.MixedF);
        Assert.Equal("mixed test", f.ClientListener.MixedS);
        Assert.Equal(arrayData, f.ClientListener.MixedA);
    }
    [Fact(Timeout = 10000)]
    public async Task RequestIntegersSerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        f.ClientParent.SendIntegers(-99, 42u);
        f.Display.Roundtrip();

        Assert.Equal(-99, serverListener.LastSignedVal);
        Assert.Equal(42u, serverListener.LastUnsignedVal);
    }

    [Fact(Timeout = 10000)]
    public async Task RequestFixedSerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        var fixedVal = new WlFixed(2.718);
        f.ClientParent.SendFixed(fixedVal);
        f.Display.Roundtrip();

        Assert.Equal(fixedVal, serverListener.LastFixed);
    }

    [Fact(Timeout = 10000)]
    public async Task RequestStringSerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        f.ClientParent.SendString("request string test");
        f.Display.Roundtrip();

        Assert.Equal("request string test", serverListener.LastString);
    }

    [Fact(Timeout = 10000)]
    public async Task RequestArraySerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
        f.ClientParent.SendArray(data);
        f.Display.Roundtrip();

        Assert.Equal(data, serverListener.LastArrayData);
    }

    [Fact(Timeout = 10000)]
    public async Task RequestMixedSerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        var fixedVal = new WlFixed(0.5);
        byte[] arrayData = [1, 2, 3, 4, 5, 6, 7];
        f.ClientParent.SendMixed(77, 88u, fixedVal, "mixed request", arrayData);
        f.Display.Roundtrip();

        Assert.Equal(77, serverListener.MixedI);
        Assert.Equal(88u, serverListener.MixedU);
        Assert.Equal(fixedVal, serverListener.MixedF);
        Assert.Equal("mixed request", serverListener.MixedS);
        Assert.Equal(arrayData, serverListener.MixedA);
    }
    [Fact(Timeout = 10000)]
    public async Task NewIdEventSerialization()
    {
        await using var f = await SetupBoundTestParent();

        // Server creates a child via NewIdEvent (must run on dispatch thread)
        TestChild.Server? serverChild = null;
        f.Server.Post(() => { serverChild = f.ServerParent.NewIdEvent(); });
        f.Display.Roundtrip();

        // Client should have received the new child proxy
        Assert.NotNull(f.ClientListener.LastNewIdChild);
        Assert.False(f.ClientListener.LastNewIdChild!.IsDisposed);

        // Verify round-trip: server sends a value on the child, client receives it
        f.Server.Post(() => serverChild!.Value(42u));
        f.Display.Roundtrip();

        Assert.Equal(42u, f.ClientListener.LastChildValue);
    }

    [Fact(Timeout = 10000)]
    public async Task ObjectEventSerialization()
    {
        await using var f = await SetupBoundTestParent();

        // First create a child via NewIdEvent so both sides have it
        TestChild.Server? serverChild = null;
        f.Server.Post(() => { serverChild = f.ServerParent.NewIdEvent(); });
        f.Display.Roundtrip();
        var clientChild = f.ClientListener.LastNewIdChild;
        Assert.NotNull(clientChild);

        // Now send the object reference
        f.Server.Post(() => f.ServerParent.ObjectEvent(serverChild));
        f.Display.Roundtrip();

        Assert.True(f.ClientListener.ObjectEventReceived);
        Assert.Same(clientChild, f.ClientListener.LastObjectChild);
    }

    [Fact(Timeout = 10000)]
    public async Task ObjectEventNullSerialization()
    {
        await using var f = await SetupBoundTestParent();

        f.Server.Post(() => f.ServerParent.ObjectEvent(null));
        f.Display.Roundtrip();

        Assert.True(f.ClientListener.ObjectEventReceived);
        Assert.Null(f.ClientListener.LastObjectChild);
    }

    [Fact(Timeout = 10000)]
    public async Task RequestSendNewIdSerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        var clientChild = f.ClientParent.SendNewId();
        f.Display.Roundtrip();

        Assert.NotNull(clientChild);
        Assert.NotNull(serverListener.LastNewIdResource);
        Assert.Equal("test_child", serverListener.LastNewIdResource!.Interface.Name);
    }

    [Fact(Timeout = 10000)]
    public async Task RequestSendObjectSerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        // First create a child via SendNewId so both sides have it
        var clientChild = f.ClientParent.SendNewId();
        f.Display.Roundtrip();
        Assert.NotNull(serverListener.LastNewIdResource);
        var serverChild = serverListener.LastNewIdResource;

        // Now send the object reference
        f.ClientParent.SendObject(clientChild);
        f.Display.Roundtrip();

        Assert.True(serverListener.SendObjectReceived);
        Assert.NotNull(serverListener.LastObjectChild);
        Assert.Same(serverChild, serverListener.LastObjectChild);
    }

    [Fact(Timeout = 10000)]
    public async Task RequestSendObjectNullSerialization()
    {
        var serverListener = new TestParentServerCapture();
        await using var f = await SetupBoundTestParent(serverListener);

        f.ClientParent.SendObject(null);
        f.Display.Roundtrip();

        Assert.True(serverListener.SendObjectReceived);
        Assert.Null(serverListener.LastObjectChild);
    }
    /// <summary>
    /// Verifies that data written from a custom event handler (via Post) is
    /// auto-flushed before the server blocks on epoll — no manual TryFlush needed.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CustomEventAutoFlush()
    {
        var server = new WaylandServer();
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;
        var serverReady = new TaskCompletionSource();

        // Server event loop — NO manual TryFlush anywhere
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { break; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        // Deliberately no TryFlush — rely on auto-flush
                        break;
                    case WaylandServerRegistryBindEvent bind:
                        serverParent = bind.Accept<TestParent.Server>(null);
                        serverReady.TrySetResult();
                        break;
                    case WaylandCustomEvent custom:
                        ((Action)custom.State!)();
                        break;
                    case WaylandServerRequestEvent req:
                        req.Dispatch();
                        break;
                }
            }
        });

        // Client: connect, get registry, bind
        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        var clientListener = new TestParentCapture();
        var clientParent = registry.Bind<TestParent>(
            registryListener.LastName, registryListener.LastVersion, clientListener);
        display.Roundtrip();

        await serverReady.Task;

        // Post a custom event that writes a server event — no manual flush
        server.Post(() => serverParent!.Integers(123, 456u));

        // Client roundtrip to receive the event (also relies on auto-flush for sync response)
        display.Roundtrip();

        Assert.Equal(123, clientListener.LastSignedVal);
        Assert.Equal(456u, clientListener.LastUnsignedVal);

        clientParent.Dispose();
        registry.Dispose();
        display.Dispose();
        await server.DisposeAsync();
        try { await serverTask; } catch { }
    }
    private class TestParentCapture : TestParent.Listener
    {
        // Integers event
        public int? LastSignedVal { get; private set; }
        public uint? LastUnsignedVal { get; private set; }

        protected override void Integers(TestParent eventSender, int signedVal, uint unsignedVal)
        {
            LastSignedVal = signedVal;
            LastUnsignedVal = unsignedVal;
        }

        // Fixed event
        public WlFixed? LastFixed { get; private set; }

        protected override void FixedPoint(TestParent eventSender, WlFixed value)
        {
            LastFixed = value;
        }

        // String event
        public string? LastString { get; private set; }

        protected override void StringEvent(TestParent eventSender, string text)
        {
            LastString = text;
        }

        // Array event
        public byte[]? LastArrayData { get; private set; }

        protected override void ArrayEvent(TestParent eventSender, ReadOnlySpan<byte> data)
        {
            LastArrayData = data.ToArray();
        }

        // FD event
        public int? LastFd { get; private set; }

        protected override void FdEvent(TestParent eventSender, WaylandFd fd)
        {
            LastFd = fd.Consume();
        }

        // Mixed event
        public int? MixedI { get; private set; }
        public uint? MixedU { get; private set; }
        public WlFixed? MixedF { get; private set; }
        public string? MixedS { get; private set; }
        public byte[]? MixedA { get; private set; }

        protected override void MixedEvent(TestParent eventSender, int i, uint u, WlFixed f, string s, ReadOnlySpan<byte> a)
        {
            MixedI = i;
            MixedU = u;
            MixedF = f;
            MixedS = s;
            MixedA = a.ToArray();
        }

        // MultiFd event
        public int? MultiFd1 { get; private set; }
        public int? MultiFd2 { get; private set; }
        public string? MultiFdLabel { get; private set; }

        protected override void MultiFdEvent(TestParent eventSender, WaylandFd fd1, WaylandFd fd2, string label)
        {
            MultiFd1 = fd1.Consume();
            MultiFd2 = fd2.Consume();
            MultiFdLabel = label;
        }

        // ObjectEvent
        public bool ObjectEventReceived { get; private set; }
        public TestChild? LastObjectChild { get; private set; }

        protected override void ObjectEvent(TestParent eventSender, TestChild? obj)
        {
            ObjectEventReceived = true;
            LastObjectChild = obj;
        }

        // NewIdEvent
        public TestChild? LastNewIdChild { get; private set; }
        public uint? LastChildValue { get; private set; }

        protected override void NewIdEvent(TestParent eventSender, NewId<TestChild, TestChild.Listener> id)
        {
            LastNewIdChild = id.GetAndConsume(new TestChildCapture(this));
        }

        internal void SetChildValue(uint val) => LastChildValue = val;
    }

    private class TestChildCapture : TestChild.Listener
    {
        private readonly TestParentCapture _parent;

        internal TestChildCapture(TestParentCapture parent) => _parent = parent;

        protected override void Value(TestChild eventSender, uint val)
        {
            _parent.SetChildValue(val);
        }
    }

    private class TestParentServerCapture : TestParent.ServerListener
    {
        // SendIntegers
        public int? LastSignedVal { get; private set; }
        public uint? LastUnsignedVal { get; private set; }

        protected override void SendIntegers(TestParent.Server resource, int signedVal, uint unsignedVal)
        {
            LastSignedVal = signedVal;
            LastUnsignedVal = unsignedVal;
        }

        // SendFixed
        public WlFixed? LastFixed { get; private set; }

        protected override void SendFixed(TestParent.Server resource, WlFixed value)
        {
            LastFixed = value;
        }

        // SendString
        public string? LastString { get; private set; }

        protected override void SendString(TestParent.Server resource, string text)
        {
            LastString = text;
        }

        // SendArray
        public byte[]? LastArrayData { get; private set; }

        protected override void SendArray(TestParent.Server resource, ReadOnlySpan<byte> data)
        {
            LastArrayData = data.ToArray();
        }

        // SendFd
        public int? LastFd { get; private set; }

        protected override void SendFd(TestParent.Server resource, WaylandFd fd)
        {
            LastFd = fd.Consume();
        }

        // SendMixed
        public int? MixedI { get; private set; }
        public uint? MixedU { get; private set; }
        public WlFixed? MixedF { get; private set; }
        public string? MixedS { get; private set; }
        public byte[]? MixedA { get; private set; }

        protected override void SendMixed(TestParent.Server resource, int i, uint u, WlFixed f, string s, ReadOnlySpan<byte> a)
        {
            MixedI = i;
            MixedU = u;
            MixedF = f;
            MixedS = s;
            MixedA = a.ToArray();
        }

        // SendMultiFd
        public int? MultiFd1 { get; private set; }
        public int? MultiFd2 { get; private set; }
        public string? MultiFdLabel { get; private set; }

        protected override void SendMultiFd(TestParent.Server resource, WaylandFd fd1, WaylandFd fd2, string label)
        {
            MultiFd1 = fd1.Consume();
            MultiFd2 = fd2.Consume();
            MultiFdLabel = label;
        }

        // SendObject
        public bool SendObjectReceived { get; private set; }
        public TestChild.Server? LastObjectChild { get; private set; }

        protected override void SendObject(TestParent.Server resource, TestChild.Server? obj)
        {
            SendObjectReceived = true;
            LastObjectChild = obj;
        }

        // SendNewId
        public TestChild.Server? LastNewIdResource { get; private set; }

        protected override void SendNewId(TestParent.Server resource, NewId<TestChild.Server, TestChild.ServerListener> id)
        {
            LastNewIdResource = id.GetAndConsume();
        }

        // Destroy
        protected override void Destroy(TestParent.Server resource)
        {
        }
    }

    private class TestFixture : IAsyncDisposable
    {
        public WaylandServer Server { get; }
        public WaylandClient WaylandClient { get; }
        public WlDisplay Display { get; }
        public WlRegistry Registry { get; }
        public TestParent ClientParent { get; }
        public TestParentCapture ClientListener { get; }
        public TestParent.Server ServerParent { get; }
        public Task ServerTask { get; }

        public TestFixture(WaylandServer server, WaylandClient waylandClient,
            WlDisplay display, WlRegistry registry, TestParent clientParent,
            TestParentCapture clientListener, TestParent.Server serverParent,
            Task serverTask)
        {
            Server = server;
            WaylandClient = waylandClient;
            Display = display;
            Registry = registry;
            ClientParent = clientParent;
            ClientListener = clientListener;
            ServerParent = serverParent;
            ServerTask = serverTask;
        }

        public async ValueTask DisposeAsync()
        {
            ClientParent.Dispose();
            Registry.Dispose();
            Display.Dispose();
            await Server.DisposeAsync();
            try { await ServerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private class RecordingTracer : IWaylandServerTracer
    {
        public List<(WlResource Resource, string EventName, WlTracedArgument[] Args)> Events { get; } = new();
        public List<(WlResource Resource, string RequestName, WlTracedArgument[] Args)> Requests { get; } = new();
        public List<(WlResource Resource, string InterfaceName)> Destroys { get; } = new();
        public List<(WlResource Target, string MethodName, WlResource Unconsumed)> UnconsumedNewIds { get; } = new();

        public void TraceEvent(WlResource resource, WlMessageDescription method,
            ReadOnlySpan<WlTracedArgument> args)
        {
            Events.Add((resource, method.Name, args.ToArray()));
        }

        public void TraceRequest(WlResource resource, WlMessageDescription method,
            ReadOnlySpan<WlTracedArgument> args)
        {
            Requests.Add((resource, method.Name, args.ToArray()));
        }

        public void TraceDestroy(WlResource resource)
        {
            Destroys.Add((resource, resource.Interface.Name));
        }

        public void TraceUnconsumedNewId(WlResource targetResource, WlMessageDescription method,
            WlResource unconsumedResource)
        {
            UnconsumedNewIds.Add((targetResource, method.Name, unconsumedResource));
        }
    }

    /// <summary>
    /// When a destructor request is dispatched normally, the resource should be
    /// removed from the object map and a wl_display.delete_id event should be sent.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DestructorDispatch_RemovesResourceAndSendsDeleteId()
    {
        var tracer = new RecordingTracer();
        var server = new WaylandServer(new WaylandServerOptions { Tracer = tracer });
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;
        var serverReady = new TaskCompletionSource();
        var destroyDispatched = new TaskCompletionSource();

        var serverListener = new DestructorTestListener
        {
            OnDestroyAction = resource =>
            {
                serverParent = null;
                destroyDispatched.TrySetResult();
            }
        };

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { break; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandServerRegistryBindEvent bind:
                        serverParent = bind.Accept<TestParent.Server>(serverListener);
                        waylandClient.TryFlush();
                        serverReady.TrySetResult();
                        break;
                    case WaylandServerRequestEvent req:
                        req.Dispatch();
                        waylandClient.TryFlush();
                        break;
                }
            }
        });

        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        var clientListener = new TestParentCapture();
        var clientParent = registry.Bind<TestParent>(
            registryListener.LastName, registryListener.LastVersion, clientListener);
        display.Roundtrip();

        await serverReady.Task;
        var parentObjectId = serverParent!.ObjectId;

        // Client sends destroy
        clientParent.Destroy();
        display.Roundtrip();

        await destroyDispatched.Task;

        // Verify resource was removed from object map
        Assert.Null(waylandClient.ObjectMap.Get(parentObjectId));

        // Verify tracer saw the destroy
        Assert.Contains(tracer.Destroys, d => d.InterfaceName == "test_parent");

        // Verify delete_id was sent with the correct object ID
        var deleteIdEvent = tracer.Events.Find(e => e.EventName == "delete_id");
        Assert.True(deleteIdEvent.Args.Length > 0);
        Assert.Equal(parentObjectId, deleteIdEvent.Args[0].UInt32);

        registry.Dispose();
        display.Dispose();
        await server.DisposeAsync();
        try { await serverTask; } catch { }
    }

    /// <summary>
    /// When a destructor request event is disposed without being dispatched,
    /// the resource should still be removed and delete_id sent.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UndispatchedDestructor_StillRemovesResource()
    {
        var tracer = new RecordingTracer();
        var server = new WaylandServer(new WaylandServerOptions { Tracer = tracer });
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;
        var serverReady = new TaskCompletionSource();
        var destroyReceived = new TaskCompletionSource();

        // Use a listener so the destructor comes as a WaylandServerRequestEvent
        var serverListener = new DestructorTestListener
        {
            OnDestroyAction = _ => { }
        };

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { break; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandServerRegistryBindEvent bind:
                        serverParent = bind.Accept<TestParent.Server>(serverListener);
                        waylandClient.TryFlush();
                        serverReady.TrySetResult();
                        break;
                    case WaylandServerRequestEvent req:
                        // Deliberately do NOT dispatch — just Dispose
                        req.Dispose();
                        waylandClient.TryFlush();
                        destroyReceived.TrySetResult();
                        break;
                }
            }
        });

        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        var clientListener = new TestParentCapture();
        var clientParent = registry.Bind<TestParent>(
            registryListener.LastName, registryListener.LastVersion, clientListener);
        display.Roundtrip();

        await serverReady.Task;
        var parentObjectId = serverParent!.ObjectId;

        // Client sends destroy
        clientParent.Destroy();
        display.Roundtrip();

        await destroyReceived.Task;

        // Even without Dispatch(), resource should be removed
        Assert.Null(waylandClient.ObjectMap.Get(parentObjectId));

        // Tracer should still see the destroy
        Assert.Contains(tracer.Destroys, d => d.InterfaceName == "test_parent");

        // delete_id should still be sent with the correct object ID
        var deleteIdEvent = tracer.Events.Find(e => e.EventName == "delete_id");
        Assert.True(deleteIdEvent.Args.Length > 0);
        Assert.Equal(parentObjectId, deleteIdEvent.Args[0].UInt32);

        registry.Dispose();
        display.Dispose();
        await server.DisposeAsync();
        try { await serverTask; } catch { }
    }

    /// <summary>
    /// If the listener throws during destructor dispatch, the resource should
    /// still be removed and delete_id sent (try/finally semantics).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DestructorException_StillRemovesResource()
    {
        var tracer = new RecordingTracer();
        var server = new WaylandServer(new WaylandServerOptions { Tracer = tracer });
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;
        var serverReady = new TaskCompletionSource();
        var destroyHandled = new TaskCompletionSource();

        var serverListener = new DestructorTestListener
        {
            OnDestroyAction = _ => throw new InvalidOperationException("boom!")
        };

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { break; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandServerRegistryBindEvent bind:
                        serverParent = bind.Accept<TestParent.Server>(serverListener);
                        waylandClient.TryFlush();
                        serverReady.TrySetResult();
                        break;
                    case WaylandServerRequestEvent req:
                        try
                        {
                            req.Dispatch();
                        }
                        catch (InvalidOperationException)
                        {
                            // Expected — listener throws
                        }
                        waylandClient.TryFlush();
                        destroyHandled.TrySetResult();
                        break;
                }
            }
        });

        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        var clientListener = new TestParentCapture();
        var clientParent = registry.Bind<TestParent>(
            registryListener.LastName, registryListener.LastVersion, clientListener);
        display.Roundtrip();

        await serverReady.Task;
        var parentObjectId = serverParent!.ObjectId;

        // Client sends destroy
        clientParent.Destroy();
        display.Roundtrip();

        await destroyHandled.Task;

        // Despite the exception, resource should be removed
        Assert.Null(waylandClient.ObjectMap.Get(parentObjectId));

        // Tracer should see the destroy
        Assert.Contains(tracer.Destroys, d => d.InterfaceName == "test_parent");

        // delete_id should be sent with the correct object ID
        var deleteIdEvent = tracer.Events.Find(e => e.EventName == "delete_id");
        Assert.True(deleteIdEvent.Args.Length > 0);
        Assert.Equal(parentObjectId, deleteIdEvent.Args[0].UInt32);

        registry.Dispose();
        display.Dispose();
        await server.DisposeAsync();
        try { await serverTask; } catch { }
    }

    /// <summary>
    /// Verify that a destructor request removes the resource from the object map
    /// immediately at parse time, even before Dispatch() is called. This ensures
    /// that subsequent requests targeting the destroyed object ID are rejected.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task DestructorRemovesFromMapImmediately()
    {
        var tracer = new RecordingTracer();
        var server = new WaylandServer(new WaylandServerOptions { Tracer = tracer });
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;
        var serverReady = new TaskCompletionSource();
        var destructorParsed = new TaskCompletionSource();
        var continueDispatching = new TaskCompletionSource();

        var serverListener = new DestructorTestListener
        {
            OnDestroyAction = _ => { }
        };

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { break; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandServerRegistryBindEvent bind:
                        serverParent = bind.Accept<TestParent.Server>(serverListener);
                        waylandClient.TryFlush();
                        serverReady.TrySetResult();
                        break;
                    case WaylandServerRequestEvent req:
                        // Signal that destructor was parsed (before Dispatch)
                        destructorParsed.TrySetResult();
                        // Wait for test assertions, then dispatch
                        continueDispatching.Task.Wait();
                        req.Dispatch();
                        waylandClient.TryFlush();
                        break;
                }
            }
        });

        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        var clientListener = new TestParentCapture();
        var clientParent = registry.Bind<TestParent>(
            registryListener.LastName, registryListener.LastVersion, clientListener);
        display.Roundtrip();

        await serverReady.Task;
        var parentObjectId = serverParent!.ObjectId;

        // Client sends destroy
        clientParent.Destroy();
        display.Flush();

        // Wait for server to parse the destructor (but NOT dispatch it yet)
        await destructorParsed.Task;

        // The resource should already be removed from the object map
        // (immediate removal at parse time, not deferred until Dispatch)
        Assert.Null(waylandClient.ObjectMap.Get(parentObjectId));

        // The resource should be marked as disposed
        Assert.True(serverParent.IsDisposed);

        // Let the server thread dispatch and flush
        continueDispatching.TrySetResult();

        registry.Dispose();
        display.Dispose();
        await server.DisposeAsync();
        try { await serverTask; } catch { }
    }

    private class DestructorTestListener : TestParent.ServerListener
    {
        public Action<TestParent.Server>? OnDestroyAction { get; set; }

        protected override void Destroy(TestParent.Server resource) =>
            OnDestroyAction?.Invoke(resource);

        protected override void SendIntegers(TestParent.Server resource, int signedVal, uint unsignedVal) { }
        protected override void SendFixed(TestParent.Server resource, WlFixed value) { }
        protected override void SendString(TestParent.Server resource, string text) { }
        protected override void SendArray(TestParent.Server resource, ReadOnlySpan<byte> data) { }
        protected override void SendFd(TestParent.Server resource, WaylandFd fd) => fd.Consume();
        protected override void SendMixed(TestParent.Server resource, int i, uint u, WlFixed f, string s, ReadOnlySpan<byte> a) { }
        protected override void SendMultiFd(TestParent.Server resource, WaylandFd fd1, WaylandFd fd2, string label) { fd1.Consume(); fd2.Consume(); }
        protected override void SendObject(TestParent.Server resource, TestChild.Server? obj) { }
        protected override void SendNewId(TestParent.Server resource, NewId<TestChild.Server, TestChild.ServerListener> id) => id.GetAndConsume();
    }

    /// <summary>
    /// If a listener ignores a new_id argument (never calls GetAndConsume()),
    /// the runtime should destroy the orphaned resource, send delete_id,
    /// and log the offense via the tracer.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UnconsumedNewId_IsDestroyedAndLogged()
    {
        var tracer = new RecordingTracer();
        var server = new WaylandServer(new WaylandServerOptions { Tracer = tracer });
        var (waylandClient, clientFd) = server.CreateConnectedClient();
        waylandClient.AddGlobal("test_parent", 1);

        TestParent.Server? serverParent = null;
        var serverReady = new TaskCompletionSource();
        var newIdHandled = new TaskCompletionSource();

        // Listener that deliberately does NOT consume the new_id
        var serverListener = new IgnoreNewIdListener
        {
            OnNewIdHandled = () => newIdHandled.TrySetResult()
        };

        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                WaylandServerEvent evt;
                try { evt = server.NextEvent(); }
                catch (ObjectDisposedException) { break; }

                switch (evt)
                {
                    case WaylandServerSyncEvent sync:
                        sync.Complete(0);
                        waylandClient.TryFlush();
                        break;
                    case WaylandServerRegistryBindEvent bind:
                        serverParent = bind.Accept<TestParent.Server>(serverListener);
                        waylandClient.TryFlush();
                        serverReady.TrySetResult();
                        break;
                    case WaylandServerRequestEvent req:
                        req.Dispatch();
                        waylandClient.TryFlush();
                        break;
                }
            }
        });

        var registryListener = new RegistryCapture();
        using var display = WlDisplay.ConnectToFd(clientFd);
        var registry = display.GetRegistry(registryListener);
        display.Roundtrip();

        var clientListener = new TestParentCapture();
        var clientParent = registry.Bind<TestParent>(
            registryListener.LastName, registryListener.LastVersion, clientListener);
        display.Roundtrip();

        await serverReady.Task;

        // Client triggers send_new_id — server listener ignores the new_id
        clientParent.SendNewId();
        display.Roundtrip();

        await newIdHandled.Task;

        // The unconsumed resource should have been destroyed (delete_id sent)
        var deleteIdEvent = tracer.Events.Find(e => e.EventName == "delete_id");
        Assert.NotNull(deleteIdEvent.Args);
        Assert.True(deleteIdEvent.Args.Length > 0);

        // The offense should be logged via the tracer
        Assert.Single(tracer.UnconsumedNewIds);
        Assert.Equal("send_new_id", tracer.UnconsumedNewIds[0].MethodName);

        registry.Dispose();
        display.Dispose();
        await server.DisposeAsync();
        try { await serverTask; } catch { }
    }

    private class IgnoreNewIdListener : TestParent.ServerListener
    {
        public Action? OnNewIdHandled { get; set; }

        protected override void Destroy(TestParent.Server resource) { }
        protected override void SendIntegers(TestParent.Server resource, int signedVal, uint unsignedVal) { }
        protected override void SendFixed(TestParent.Server resource, WlFixed value) { }
        protected override void SendString(TestParent.Server resource, string text) { }
        protected override void SendArray(TestParent.Server resource, ReadOnlySpan<byte> data) { }
        protected override void SendFd(TestParent.Server resource, WaylandFd fd) => fd.Consume();
        protected override void SendMixed(TestParent.Server resource, int i, uint u, WlFixed f, string s, ReadOnlySpan<byte> a) { }
        protected override void SendMultiFd(TestParent.Server resource, WaylandFd fd1, WaylandFd fd2, string label) { fd1.Consume(); fd2.Consume(); }
        protected override void SendObject(TestParent.Server resource, TestChild.Server? obj) { }
        protected override void SendNewId(TestParent.Server resource, NewId<TestChild.Server, TestChild.ServerListener> id)
        {
            // Deliberately do NOT call id.GetAndConsume() — simulates buggy listener
            OnNewIdHandled?.Invoke();
        }
    }
}
