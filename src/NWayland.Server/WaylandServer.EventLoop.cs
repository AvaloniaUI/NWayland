using System;
using NWayland.Interop;
using static NWayland.Server.Interop.LinuxInterop;

namespace NWayland.Server;

public sealed partial class WaylandServer
{
    private WaylandServerEvent NextEventCore()
    {
        while (true)
        {
            // 1. Drain pending clients from the shared queue into dispatch-thread state
            DrainPendingClients();

            // 2. Check for dispose request
            if (_disposed)
                throw new ObjectDisposedException(nameof(WaylandServer));

            // 3. Drain dead clients (PostError'd from application code)
            var deadEvt = DrainDeadClients();
            if (deadEvt != null)
                return deadEvt;

            // 4. Custom events always take precedence over protocol events
            lock (_stateLock)
            {
                if (_customEvents.Count > 0)
                    return new WaylandCustomEvent(_customEvents.Dequeue());
            }

            // 5. Continue parsing from current client's buffer
            if (_currentClient != null)
            {
                var evt = TryDrainAndParse(_currentClient);
                if (evt != null)
                    return evt;
                FinishCurrentClient();
                // Re-check custom events before moving to next client
                continue;
            }

            // 6. Round-robin: find a client with buffered data or readable socket
            var ready = FindReadyClient();
            if (ready != null)
            {
                _currentClient = ready;
                continue;
            }

            // 7. No ready client — flush all pending writes, then block on epoll
            FlushAllClients();
            PollAndDispatchReadiness();
        }
    }

    /// <summary>
    /// Dequeue all pending clients from the shared queue and register them
    /// with epoll + dispatch-thread-only collections.
    /// </summary>
    private void DrainPendingClients()
    {
        while (true)
        {
            WaylandClient? client;
            lock (_stateLock)
            {
                if (_pendingClients.Count == 0)
                    break;
                client = _pendingClients.Dequeue();
            }

            try
            {
                _poll.AddFd(client.Socket.Fd, EPOLLIN);
            }
            catch
            {
                // Dispose parser + socket directly (not client.Dispose()) to avoid
                // AcquireDispatchLock — the client was never registered and has no
                // state worth protecting. Managed objects (wl_display resource, etc.)
                // will be GC'd.
                client.Parser?.Dispose();
                client.Socket.Dispose();
                continue;
            }

            _clients.Add(client);
            _fdToClient[client.Socket.Fd] = client;
        }
    }

    /// <summary>
    /// Drain clients enqueued by <see cref="WaylandClient.PostError"/>.
    /// Returns a disconnect event for the first dead client found, or null.
    /// Skips clients already cleaned up by <see cref="DisconnectClient"/>.
    /// </summary>
    private WaylandServerEvent? DrainDeadClients()
    {
        while (true)
        {
            WaylandClient? dead;
            lock (_stateLock)
            {
                if (_deadClients.Count == 0)
                    return null;
                dead = _deadClients.Dequeue();
            }

            // Already cleaned up by DisconnectClient (e.g. from HandleProtocolError path)?
            if (!_fdToClient.TryGetValue(dead.Socket.Fd, out var mapped) || mapped != dead)
                continue;

            CleanupClient(dead);
            return new WaylandClientDisconnectEvent(dead);
        }
    }

    /// <summary>
    /// Non-blocking drain reads from socket into ring buffer, then try to parse.
    /// Returns an event if one is ready, or null if buffer is exhausted / client disconnected.
    /// </summary>
    private WaylandServerEvent? TryDrainAndParse(WaylandClient client)
    {
        var parser = client.Parser!;
        var socket = client.Socket;

        // If parser is already disposed (e.g. PostError was called), skip directly to disconnect
        if (parser.IsDisposed)
            return DisconnectClient(client, parser);

        // Non-blocking drain loop
        while (parser.Readable && parser.HasBufferRoom)
        {
            var (dataBuf1, dataBuf2) = parser.DataBuffer.GetWriteBuffers();
            var (fdBuf1, fdBuf2) = parser.FdBuffer.GetWriteBuffers();

            (int bytesRead, int fdsRead) result;
            try
            {
                result = socket.TryReadNonBlocking(dataBuf1, dataBuf2, fdBuf1, fdBuf2);
            }
            catch (WaylandConnectionException)
            {
                parser.Dispose();
                break;
            }

            if (result.bytesRead < 0)
            {
                parser.Readable = false;
                break;
            }

            if (result.bytesRead == 0)
            {
                // Client sent EOF. We deliberately do not drain remaining
                // buffered messages — for a sudden client disconnect the final
                // batch of requests is best-effort. If we ever reuse the
                // socket/parser for a client-side library implementation (unlikely,
                // since EGL/Vulkan drivers require libwayland) this could be
                // revisited to match libwayland-server's behavior of processing
                // buffered data before disconnecting.
                parser.Dispose();
                break;
            }

            parser.DataBuffer.Written(result.bytesRead);
            parser.FdBuffer.Written(result.fdsRead);
        }

        return TryParseFromBuffer(client);
    }

    /// <summary>
    /// Try to parse and process one event from the client's ring buffer.
    /// Handles internal events (get_registry, destructors) by looping internally.
    /// </summary>
    private WaylandServerEvent? TryParseFromBuffer(WaylandClient client)
    {
        var parser = client.Parser!;

        // Parser already disposed (PostError or I/O error)
        if (parser.IsDisposed)
            return DisconnectClient(client, parser);

        while (true)
        {
            ParsedRequest? parsed;
            try
            {
                parsed = parser.TryParseOneEvent();
            }
            catch (WaylandConnectionException ex)
            {
                HandleProtocolError(client, parser, ex);
                return DisconnectClient(client, parser);
            }

            if (parsed != null)
            {
                WaylandServerEvent? evt;
                try
                {
                    evt = ProcessParsedRequest(client, parsed.Value);
                }
                catch (WaylandConnectionException ex)
                {
                    HandleProtocolError(client, parser, ex);
                    return DisconnectClient(client, parser);
                }
                catch
                {
                    // Non-protocol exception (bug) — ensure args are disposed to prevent FD leaks
                    parsed.Value.Args.Dispose();
                    throw;
                }

                if (evt != null)
                    return evt;

                // Internal request (e.g. get_registry) — loop to parse next
                continue;
            }

            // No complete message in buffer
            if (parser.IsDisposed)
                return DisconnectClient(client, parser);

            // FD flooding check — only when we cannot form a complete message.
            // This is a per-message limit, not per-read: a valid message can
            // legitimately carry up to MaxFdsPerMessage FDs.
            if (parser.PendingFdCount > WaylandMessageParser.MaxPendingFds)
            {
                client.PostError(null, 1,
                    $"Too many pending FDs ({parser.PendingFdCount}) without a complete message");
                return DisconnectClient(client, parser);
            }

            return null; // Buffer exhausted
        }
    }

    private void FinishCurrentClient()
    {
        var client = _currentClient!;
        try
        {
            if (!client.TryFlush())
            {
                if (!client.PendingWrite)
                {
                    client.PendingWrite = true;
                    // Drop EPOLLIN to avoid spinning — we can't process requests
                    // until the send buffer drains (back-pressure).
                    _poll.ModFd(client.Socket.Fd, EPOLLOUT);
                }
            }
        }
        catch
        {
            // Socket error during flush — mark for disconnect on next read attempt
            client.Parser!.Dispose();
        }
        _currentClient = null;
    }

    private WaylandClient? FindReadyClient()
    {
        int count = _clients.Count;
        for (int i = 0; i < count; i++)
        {
            int idx = (_roundRobinIndex + i) % count;
            var client = _clients[idx];
            var parser = client.Parser!;

            // Skip clients with pending writes (back-pressure): if the client's
            // send buffer is full, stop processing their requests to avoid
            // accumulating unbounded outgoing data.
            if (client.PendingWrite)
                continue;

            if (parser.DataBuffer.Count > 0 || parser.Readable)
            {
                _roundRobinIndex = (idx + 1) % count;
                return client;
            }
        }

        return null;
    }

    private void FlushAllClients()
    {
        foreach (var c in _clients)
        {
            try
            {
                if (!c.TryFlush())
                {
                    if (!c.PendingWrite)
                    {
                        c.PendingWrite = true;
                        // Drop EPOLLIN — back-pressure prevents processing requests
                        _poll.ModFd(c.Socket.Fd, EPOLLOUT);
                    }
                }
                else if (c.PendingWrite)
                {
                    c.PendingWrite = false;
                    _poll.ModFd(c.Socket.Fd, EPOLLIN);
                }
            }
            catch { /* ignored — client may be mid-disconnect */ }
        }
    }

    /// <summary>
    /// Block on epoll_wait and update client readiness flags.
    /// </summary>
    private void PollAndDispatchReadiness()
    {
        int n = _poll.Wait(_epollResults, -1);

        for (int i = 0; i < n; i++)
        {
            var result = _epollResults[i];

            if (!_fdToClient.TryGetValue(result.Fd, out var client))
                continue;

            if (result.IsReadable)
                client.Parser!.Readable = true;

            if (result.IsWritable && client.PendingWrite)
            {
                try
                {
                    if (client.TryFlush())
                    {
                        client.PendingWrite = false;
                        _poll.ModFd(client.Socket.Fd, EPOLLIN);
                    }
                }
                catch { /* ignored */ }
            }

            if (result.IsError)
            {
                // Clear PendingWrite so FindReadyClient can select this client
                // for the normal disconnect path (TryDrainAndParse → parser.IsDisposed).
                // Without this, a client that crashes during back-pressure would
                // be skipped forever by FindReadyClient, causing a CPU spin.
                client.PendingWrite = false;
                client.Parser!.Readable = true;
                client.Parser.Dispose();
            }
        }
    }
}
