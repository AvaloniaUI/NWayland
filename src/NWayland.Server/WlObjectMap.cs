using System;
using System.Collections.Generic;
using NWayland.Server;

namespace NWayland.Server;

/// <summary>
/// Per-client object map. Maps uint32 object IDs to WlResource instances.
/// Client-allocated IDs: [1, 0xfeffffff]. Server-allocated IDs: [0xff000000, 0xffffffff].
/// Not thread-safe — only accessed from the client's processing context.
/// </summary>
internal sealed class WlObjectMap
{
    private const uint ClientIdMax = 0xfeffffff;
    internal const uint ServerIdBase = 0xff000000;
    private const int InitialServerCapacity = 16;

    private readonly Dictionary<uint, WlResource> _clientObjects = new();
    private WlResource?[] _serverObjects = new WlResource?[InitialServerCapacity];
    private readonly Stack<uint> _freeServerIds = new();
    private uint _serverNextId = ServerIdBase;

    /// <summary>
    /// Look up a resource by its object ID.
    /// </summary>
    public WlResource? Get(uint id)
    {
        if (id == 0)
            return null;

        if (id >= ServerIdBase)
        {
            uint index = id - ServerIdBase;
            if (index >= (uint)_serverObjects.Length)
                return null;
            return _serverObjects[index];
        }

        return _clientObjects.GetValueOrDefault(id);
    }

    /// <summary>
    /// Insert a resource at a client-allocated ID (the client chose this ID in a new_id request arg).
    /// Note: the protocol spec requires dense ID packing, but we do not enforce it.
    /// Enforcement is impractical because of delete_id races: a client may create
    /// wl_callback N, the server destroys it and sends delete_id(N), but the client
    /// has already sent a request using ID N+1 before receiving delete_id — making
    /// the sequence appear non-dense from the server's perspective.
    /// </summary>
    public void InsertClientId(uint id, WlResource resource)
    {
        if (id == 0 || id > ClientIdMax)
            throw new ArgumentOutOfRangeException(nameof(id), $"Client ID {id} out of range [1, {ClientIdMax}]");

        if (_clientObjects.ContainsKey(id))
            throw new InvalidOperationException($"Object ID {id} is already in use");

        _clientObjects[id] = resource;
    }

    /// <summary>
    /// Allocate the next server-side ID. The caller must insert
    /// the resource via <see cref="InsertServerIdAt"/> after creation.
    /// Reuses freed IDs first to keep the ID space tightly packed.
    /// </summary>
    public uint AllocateNextServerId()
    {
        if (_freeServerIds.Count > 0)
            return _freeServerIds.Pop();

        // uint overflow: after 0xffffffff, _serverNextId wraps to 0
        if (_serverNextId < ServerIdBase)
            throw new InvalidOperationException("Server-side object ID space exhausted");

        return _serverNextId++;
    }

    /// <summary>
    /// Insert a resource at a previously allocated server ID.
    /// </summary>
    public void InsertServerIdAt(uint id, WlResource resource)
    {
        if (id < ServerIdBase)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Server ID {id} is below ServerIdBase {ServerIdBase}");

        uint index = id - ServerIdBase;
        EnsureCapacity(ref _serverObjects, (int)index + 1);

        if (_serverObjects[index] != null)
            throw new InvalidOperationException($"Server object ID {id} is already in use");

        _serverObjects[index] = resource;
    }

    /// <summary>
    /// Remove a resource by its object ID. Returns the removed resource, or null.
    /// </summary>
    public WlResource? Remove(uint id)
    {
        if (id == 0)
            return null;

        if (id >= ServerIdBase)
        {
            uint index = id - ServerIdBase;
            if (index >= (uint)_serverObjects.Length)
                return null;
            var resource = _serverObjects[index];
            _serverObjects[index] = null;
            if (resource != null)
                _freeServerIds.Push(id);
            return resource;
        }

        _clientObjects.Remove(id, out var res);
        return res;
    }

    /// <summary>
    /// Iterate all live resources and invoke an action. Used for cleanup on disconnect.
    /// </summary>
    public void ForEach(Action<uint, WlResource> action)
    {
        foreach (var (id, resource) in _clientObjects)
            action(id, resource);

        for (int i = 0; i < _serverObjects.Length; i++)
        {
            var r = _serverObjects[i];
            if (r != null)
                action(ServerIdBase + (uint)i, r);
        }
    }

    private static void EnsureCapacity(ref WlResource?[] array, int requiredLength)
    {
        if (requiredLength <= array.Length)
            return;
        // Server object IDs use 24 bits (0xff000000 base + index), hard protocol limit
        const int MaxCapacity = 1 << 24;
        int newLen = array.Length;
        while (newLen < requiredLength)
        {
            newLen *= 2;
            if (newLen > MaxCapacity || newLen < 0)
                throw new InvalidOperationException(
                    $"Object map capacity {requiredLength} exceeds protocol maximum {MaxCapacity}");
        }
        Array.Resize(ref array, newLen);
    }
}
