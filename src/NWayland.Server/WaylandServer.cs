using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NWayland.Interop;
using NWayland.Protocols.Wayland;
using static NWayland.Server.Interop.LinuxInterop;

namespace NWayland.Server;

/// <summary>
/// Lightweight RAII handle that holds the server's state lock. The lock is
/// acquired (and the dispatch-thread guard checked) by
/// <see cref="WaylandServer.AcquireDispatchLock"/>; disposing this struct
/// releases the lock via <see cref="Monitor.Exit"/>.
/// </summary>
internal ref struct DispatchLock
{
    private object? _lock;

    internal DispatchLock(object lockObj)
    {
        _lock = lockObj;
    }

    public void Dispose()
    {
        if (_lock != null)
        {
            Monitor.Exit(_lock);
            _lock = null;
        }
    }
}

/// <summary>
/// A Wayland server that manages client connections and dispatches events.
/// Uses a synchronous epoll-based event loop.
/// </summary>
/// <remarks>
/// <para><b>Threading model:</b> <see cref="NextEvent"/> defines the dispatch context.
/// All per-client operations — resource event methods, <see cref="WaylandClient.TryFlush"/>,
/// <see cref="WaylandClient.AddGlobal"/>, and object map mutations — must be called from
/// the same context that calls <see cref="NextEvent"/> (or before the first
/// <see cref="NextEvent"/> call). <see cref="AddClient(int,Action{int}?)"/>,
/// <see cref="AddClient(Socket)"/>, and <see cref="Post"/> are the thread-safe entry
/// points that enqueue work and wake <see cref="NextEvent"/>.</para>
/// <para><b>Locking:</b> A single <c>_stateLock</c> mutex protects all shared state
/// and enforces the dispatch-thread guard. Any non-whitelisted public API acquires
/// the lock via <see cref="AcquireDispatchLock"/> and verifies that <see cref="NextEvent"/>
/// is either not running or running on the calling thread. <c>Monitor</c> reentrancy
/// allows nested calls (e.g. <c>PostError</c> → <c>Invoke</c> → inner lock).</para>
/// <para><b>Globals:</b> Global registration is intentionally per-client, not display-wide.
/// This allows compositors to advertise different capability sets to different clients
/// (e.g., privileged vs. sandboxed). The application is responsible for calling
/// <see cref="WaylandClient.AddGlobal"/> on each client as needed.</para>
/// </remarks>
public sealed partial class WaylandServer : IAsyncDisposable
{
    private readonly WaylandEventPoll _poll;

    private readonly object _stateLock = new();
    private readonly Queue<WaylandClient> _pendingClients = new();
    private readonly Queue<WaylandClient> _deadClients = new();
    private readonly Queue<object?> _customEvents = new();
    private int _nextEventThreadId;
    private volatile bool _disposed;
    private bool _cleanedUp;
    private bool _cleaningUp;
    private TaskCompletionSource? _disposeTcs;

    private readonly List<WaylandClient> _clients = new();
    private readonly Dictionary<int, WaylandClient> _fdToClient = new();
    private int _roundRobinIndex;
    private WaylandClient? _currentClient;
    private readonly EpollResult[] _epollResults = new EpollResult[64];
    private readonly ConcurrentStack<List<WlServerArgument>> _argsPool = new();

    internal WaylandServerOptions Options { get; }

    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Returns a snapshot of all clients known to this server, including both
    /// registered clients and those still pending registration in the dispatch loop.
    /// Safe to call after disposal — acquires a dispose-safe dispatch lock so
    /// user code can enumerate clients for cleanup.
    /// </summary>
    public IReadOnlyList<WaylandClient> GetClients()
    {
        using (AcquireDispatchLock(allowDisposed: true))
        {
            var result = new List<WaylandClient>(_clients.Count + _pendingClients.Count);
            result.AddRange(_clients);
            result.AddRange(_pendingClients);
            return result;
        }
    }

    public WaylandServer(WaylandServerOptions? options = null)
    {
        Options = options ?? new WaylandServerOptions();
        _poll = new WaylandEventPoll();
    }

    internal List<WlServerArgument> RentArgsList()
    {
        if (_argsPool.TryPop(out var list))
        {
            list.Clear();
            return list;
        }
        return new List<WlServerArgument>();
    }

    internal void ReturnArgsList(List<WlServerArgument> list)
    {
        list.Clear();
        _argsPool.Push(list);
    }

    /// <summary>
    /// Acquire the dispatch lock. Verifies that <see cref="NextEvent"/> is not
    /// running on a different thread. The returned <see cref="DispatchLock"/>
    /// must be disposed to release the lock.
    /// </summary>
    internal DispatchLock AcquireDispatchLock(bool allowDisposed = false)
    {
        Monitor.Enter(_stateLock);
        if (_disposed && !_cleaningUp && !allowDisposed)
        {
            Monitor.Exit(_stateLock);
            throw new ObjectDisposedException(nameof(WaylandServer));
        }
        var tid = _nextEventThreadId;
        if (tid != 0 && tid != Environment.CurrentManagedThreadId)
        {
            Monitor.Exit(_stateLock);
            throw new InvalidOperationException(
                "Cannot access dispatch-thread state while NextEvent is running on another thread.");
        }
        return new DispatchLock(_stateLock);
    }

    /// <summary>
    /// Add a client from a raw socket file descriptor.
    /// Thread-safe — enqueues the client for the dispatch thread.
    /// </summary>
    /// <remarks>
    /// FD ownership is only transferred on success. If this method throws
    /// (e.g. server is disposed), the caller retains ownership of the FD.
    /// </remarks>
    public WaylandClient AddClient(int fd, Action<int>? releaseCallback = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var socket = new WaylandServerSocket(fd, releaseCallback);
        return EnqueueClient(socket);
    }

    /// <summary>
    /// Add a client from an existing .NET <see cref="Socket"/>.
    /// Thread-safe — enqueues the client for the dispatch thread.
    /// </summary>
    /// <remarks>
    /// Socket ownership is only transferred on success. If this method throws
    /// (e.g. server is disposed), the caller retains ownership of the Socket.
    /// </remarks>
    public WaylandClient AddClient(Socket socket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var serverSocket = new WaylandServerSocket(socket);
        return EnqueueClient(serverSocket);
    }

    /// <summary>
    /// Post a custom event that will be returned by the next <see cref="NextEvent"/> call
    /// as a <see cref="WaylandCustomEvent"/>. Thread-safe — can be called from any thread
    /// to wake up the dispatch loop.
    /// </summary>
    /// <param name="state">Opaque state object to pass to the event consumer.</param>
    public void Post(object? state = null)
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WaylandServer));
            _customEvents.Enqueue(state);
        }
        _poll.Wake();
    }

    /// <summary>
    /// Wait for the next event from any connected client. Blocks until an event
    /// is available. Only one in-flight call is allowed at a time (not thread-safe).
    /// Round-robins across clients so no single client starves others.
    /// </summary>
    /// <remarks>
    /// This method drives epoll directly — there are no background threads, Tasks,
    /// or async state machines. The calling thread blocks in epoll_wait when no
    /// client data is available.
    /// </remarks>
    public WaylandServerEvent NextEvent()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WaylandServer));
            if (_nextEventThreadId != 0)
                throw new InvalidOperationException("NextEvent is already running on another thread.");
            _nextEventThreadId = Environment.CurrentManagedThreadId;
        }

        try
        {
            return NextEventCore();
        }
        finally
        {
            TaskCompletionSource? tcs;
            lock (_stateLock)
            {
                _nextEventThreadId = 0;
                tcs = _disposeTcs;
            }
            if (_disposed)
            {
                DoCleanup();
                tcs?.TrySetResult();
            }
        }
    }

    private WaylandClient EnqueueClient(WaylandServerSocket socket)
    {
        var client = new WaylandClient(this, socket);
        var parser = new WaylandMessageParser(client);
        parser.Readable = true;
        client.Parser = parser;

        bool disposed;
        lock (_stateLock)
        {
            disposed = _disposed;
            if (!disposed)
                _pendingClients.Enqueue(client);
        }

        if (disposed)
        {
            parser.Dispose();
            socket.Dispose();
            throw new ObjectDisposedException(nameof(WaylandServer));
        }

        _poll.Wake();
        return client;
    }

    /// <summary>
    /// Enqueue a client for disconnect-event delivery on the next
    /// <see cref="NextEvent"/> call. Called by <see cref="WaylandClient.PostError"/>.
    /// Thread-safe (uses <c>_stateLock</c>).
    /// </summary>
    internal void EnqueueDisconnect(WaylandClient client)
    {
        lock (_stateLock)
            _deadClients.Enqueue(client);
        _poll.Wake();
    }

    /// <summary>
    /// Idempotent cleanup of a single client: remove from epoll, collections,
    /// and dispose. Safe to call even if the client was already cleaned up.
    /// Must be called from the dispatch thread (inside <see cref="NextEventCore"/>).
    /// </summary>
    internal void CleanupClient(WaylandClient client)
    {
        int fd = client.Socket.Fd;
        if (_fdToClient.TryGetValue(fd, out var mapped) && mapped == client)
        {
            try { _poll.RemoveFd(fd); } catch { }
            _fdToClient.Remove(fd);
        }
        _clients.Remove(client);
        client.Parser?.Dispose();
        client.Dispose();

        _roundRobinIndex = _clients.Count > 0
            ? _roundRobinIndex % _clients.Count : 0;
        if (_currentClient == client)
            _currentClient = null;
    }

    /// <summary>
    /// Dispose the server asynchronously. If <see cref="NextEvent"/> is not running,
    /// cleanup happens synchronously and a completed <see cref="ValueTask"/> is returned.
    /// If <see cref="NextEvent"/> is running, cleanup is deferred to its exit and the
    /// returned task completes when cleanup finishes.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;

            if (_nextEventThreadId == 0)
            {
                DoCleanup();
                return ValueTask.CompletedTask;
            }

            _disposeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _poll.Wake();
            return new ValueTask(_disposeTcs.Task);
        }
    }

    /// <summary>
    /// Perform actual cleanup — dispose all clients (registered and pending).
    /// Guarded by <c>_cleanedUp</c> to prevent double cleanup.
    /// </summary>
    private void DoCleanup()
    {
        lock (_stateLock)
        {
            if (_cleanedUp)
                return;
            _cleanedUp = true;
            _cleaningUp = true;
        }

        // Drain pending clients that were never registered
        while (true)
        {
            WaylandClient? pending;
            lock (_stateLock)
            {
                if (_pendingClients.Count == 0)
                    break;
                pending = _pendingClients.Dequeue();
            }
            pending.Parser?.Dispose();
            pending.Dispose();
        }

        // Clean up registered clients (dispatch-thread-only data)
        foreach (var client in _clients)
        {
            try { _poll.RemoveFd(client.Socket.Fd); } catch { }
            client.Parser?.Dispose();
            client.Dispose();
        }
        _clients.Clear();
        _fdToClient.Clear();
        _poll.Dispose();

        lock (_stateLock)
            _cleaningUp = false;
    }
}
