# NWayland.Server

A managed Wayland server runtime for .NET. Provides a synchronous, epoll-based event loop with no background threads or async state machines.

## Quick Start

```csharp
await using var server = new WaylandServer();

// Accept a client from a .NET socket (thread-safe)
var client = server.AddClient(acceptedSocket);

// Advertise globals
client.AddGlobal("wl_compositor", 6);
client.AddGlobal("wl_shm", 1);

// Event loop — blocks on epoll, returns one event at a time
while (true)
{
    var evt = server.NextEvent();

    switch (evt)
    {
        case WaylandServerSyncEvent sync:
            sync.Complete(nextSerial++);
            break;

        case WaylandServerRegistryBindEvent bind:
            var resource = bind.Accept<WlCompositor.Server>(myListener);
            break;

        case WaylandServerRequestEvent request:
            request.Dispatch();
            break;

        case WaylandClientDisconnectEvent disconnect:
            Console.WriteLine("Client disconnected");
            break;

        case WaylandCustomEvent custom:
            // Handle cross-thread work posted via server.Post()
            ((Action)custom.State!)();
            break;
    }
}
```

## Threading Model

**Single-dispatch-context rule:** All server APIs must be called from a single dispatch context — the same logical flow that processes events returned by `NextEvent()`. This includes resource event methods (e.g., `surface.Frame(callback)`), `TryFlush()`, `AddGlobal()`, and resource disposal.

It is perfectly fine to run `NextEvent()` on a background thread via `Task.Run` and call dispatch-context APIs from the main thread (or vice versa) — **as long as they are not called concurrently with `NextEvent()`**. The dispatch lock detects misuse: calling a dispatch-context API from a different thread while `NextEvent()` is running throws `InvalidOperationException`.

**Thread-safe exceptions** (can be called from any thread, including concurrently with `NextEvent()`):
- `AddClient()` — enqueues a new client connection
- `Post()` — posts a custom event to wake the event loop
- `DisposeAsync()` — signals shutdown and waits for cleanup

To run work on the dispatch thread from another thread, use `Post()`:

```csharp
// From a worker thread:
server.Post(() => resource.SomeEvent(42));
```

**Typical pattern with `Task.Run`:**

```csharp
// NextEvent blocks, so wrap each call in Task.Run to yield
// back to the SynchronizationContext between events.
while (true)
{
    var evt = await Task.Run(server.NextEvent);

    // All dispatch-context APIs are safe here — NextEvent has returned,
    // so the dispatch lock is not held.
    switch (evt)
    {
        case WaylandServerSyncEvent sync:
            sync.Complete(serial++);
            client.TryFlush();
            break;
        case WaylandServerRequestEvent req:
            req.Dispatch();
            client.TryFlush();
            break;
        // ...
    }
}

// From other async methods — use Post() for dispatch-context work:
server.Post(() => resource.SomeEvent(42));
```

## Disposal

`WaylandServer` implements `IAsyncDisposable`. If `NextEvent()` is not running, cleanup is synchronous. If it is, cleanup is deferred to its exit:

```csharp
// From another thread — wakes NextEvent, which throws ObjectDisposedException
await server.DisposeAsync();
```

## Options

```csharp
var server = new WaylandServer(new WaylandServerOptions
{
    // Silently drop events sent to disposed resources instead of throwing
    DisposedServerProxyCallIsNoOp = true
});
```
