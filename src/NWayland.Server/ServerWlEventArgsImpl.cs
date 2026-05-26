using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using NWayland.Interop;
using NWayland.Server.Interop;

namespace NWayland.Server;

/// <summary>
/// Server-side <see cref="IWlEventArgsImpl"/>. Fully managed — reads arguments
/// from a pre-parsed message body and FD dequeue delegate. Uses a pooled
/// <see cref="List{WlServerArgument}"/> to avoid per-message array allocations.
/// </summary>
internal sealed class ServerWlEventArgsImpl : IWlEventArgsImpl
{
    private readonly WlResource _resource;
    private readonly WlMessageDescription _method;
    private readonly WaylandClient _client;
    private readonly List<WlServerArgument> _args;
    // Tracks which FD arguments have been consumed. Uses bit-per-argument-index;
    // the source generator rejects messages with ≥64 arguments (see SigGen.cs).
    private ulong _consumedFds;
    // Tracks which new_id arguments have been consumed via GetAndConsume.
    private ulong _consumedNewIds;
    private bool _dispatched;
    private bool _disposed;

    public object Sender => _resource;
    public uint Opcode { get; }
    public WlMessageDescription Message => _method;

    internal ServerWlEventArgsImpl(
        WlResource resource,
        WlMessageDescription method,
        uint opcode,
        ReadOnlySpan<byte> body,
        WaylandClient client,
        Func<int> dequeueFd)
    {
        _resource = resource;
        _method = method;
        _client = client;
        Opcode = opcode;
        _args = client.Server.RentArgsList();

        ParseArgs(body, dequeueFd);
    }

    private void ParseArgs(ReadOnlySpan<byte> body, Func<int> dequeueFd)
    {
        int offset = 0;
        try
        {
            for (int i = 0; i < _method.Arguments.Count; i++)
            {
                var arg = _method.Arguments[i];
                switch (arg.Code)
                {
                    case WaylandArgumentCodes.Int32:
                        _args.Add(new WlServerArgument
                        {
                            Type = WlServerArgumentType.Int32,
                            Int = ReadInt32(body, offset)
                        });
                        offset += 4;
                        break;

                    case WaylandArgumentCodes.UInt32:
                        _args.Add(new WlServerArgument
                        {
                            Type = WlServerArgumentType.UInt32,
                            Int = ReadInt32(body, offset)
                        });
                        offset += 4;
                        break;

                    case WaylandArgumentCodes.Fixed:
                        _args.Add(new WlServerArgument
                        {
                            Type = WlServerArgumentType.Fixed,
                            Int = ReadInt32(body, offset)
                        });
                        offset += 4;
                        break;

                    case WaylandArgumentCodes.Object:
                    {
                        int raw = ReadInt32(body, offset);
                        offset += 4;
                        uint objId = unchecked((uint)raw);
                        if (objId == 0)
                        {
                            if (!arg.AllowNull)
                                throw new WaylandConnectionException(
                                    $"Non-nullable object argument {i} is null");
                        }
                        else
                        {
                            var obj = _client.ObjectMap.Get(objId);
                            if (obj == null)
                                throw new WaylandConnectionException(
                                    $"Object argument references unknown ID {objId}");
                            if (arg.ProxyType != null && obj.Interface.Name != arg.ProxyType.Interface.Name)
                                throw new WaylandConnectionException(
                                    $"Object {objId} has interface '{obj.Interface.Name}' " +
                                    $"but expected '{arg.ProxyType.Interface.Name}'");
                        }
                        _args.Add(new WlServerArgument
                        {
                            Type = WlServerArgumentType.Object,
                            Int = raw
                        });
                        break;
                    }

                    case WaylandArgumentCodes.NewId:
                    {
                        var maxObjects = _client.Server.Options.MaxClientObjects;
                        if (arg.ProxyType != null)
                        {
                            int raw = ReadInt32(body, offset);
                            offset += 4;
                            uint newId = unchecked((uint)raw);

                            if (newId == 0)
                                throw new WaylandServerProtocolErrorException(_resource, 0,
                                    "Client sent object ID 0 for new_id argument");

                            if (newId >= WlObjectMap.ServerIdBase)
                                throw new WaylandServerProtocolErrorException(_resource, 0,
                                    $"Client-chosen object ID {newId} is in the server-reserved range");
                            
                            if (maxObjects > 0 && newId > maxObjects)
                                throw new WaylandServerProtocolErrorException(_resource, 0,
                                    $"Client object ID {newId} exceeds limit of {maxObjects}");

                            WlResource? resource = null;
                            if (arg.ProxyType.ServerFactory != null)
                            {
                                if (_client.ObjectMap.Get(newId) != null)
                                    throw new WaylandServerProtocolErrorException(_resource, 0,
                                        $"Client-chosen object ID {newId} is already in use");

                                resource = arg.ProxyType.ServerFactory(new WlResourceCreationContext
                                {
                                    Impl = new WaylandResourceImpl(_client, newId, _resource.Version,
                                        arg.ProxyType.Interface, null, _client.ObjectMap.InsertClientId)
                                });
                            }
                            _args.Add(new WlServerArgument
                            {
                                Type = WlServerArgumentType.NewId,
                                Int = raw,
                                Object = resource
                            });
                        }
                        else
                        {
                            // Untyped new_id (e.g. wl_registry.bind):
                            // The generator injects string + uint args before
                            // the NewId arg, so those were already parsed at
                            // indices i-2 (interface name) and i-1 (version).
                            int raw = ReadInt32(body, offset);
                            offset += 4;
                            uint newId = unchecked((uint)raw);

                            if (newId == 0)
                                throw new WaylandServerProtocolErrorException(_resource, 0,
                                    "Client sent object ID 0 for new_id argument");

                            if (newId >= WlObjectMap.ServerIdBase)
                                throw new WaylandServerProtocolErrorException(_resource, 0,
                                    $"Client-chosen object ID {newId} is in the server-reserved range");
                            
                            if (maxObjects > 0 && newId > maxObjects)
                                throw new WaylandServerProtocolErrorException(_resource, 0,
                                    $"Client object ID {newId} exceeds limit of {maxObjects}");

                            var iface = (string?)_args[i - 2].Object ?? "";
                            var version = unchecked((uint)_args[i - 1].Int);

                            _args.Add(new WlServerArgument
                            {
                                Type = WlServerArgumentType.UntypedNewId,
                                Int = raw,
                                Object = new WlUntypedNewId(iface, version, newId)
                            });
                        }
                        break;
                    }

                    case WaylandArgumentCodes.Fd:
                        _args.Add(new WlServerArgument
                        {
                            Type = WlServerArgumentType.Fd,
                            Int = dequeueFd()
                        });
                        break;

                    case WaylandArgumentCodes.String:
                    {
                        uint len = ReadUInt32(body, offset);
                        offset += 4;
                        string? s = null;
                        if (len == 0)
                        {
                            if (!arg.AllowNull)
                                throw new WaylandConnectionException(
                                    $"Non-nullable string argument {i} is null");
                        }
                        else
                        {
                            if (body[offset + (int)len - 1] != 0)
                                throw new WaylandConnectionException(
                                    $"String argument {i} missing NUL terminator");
                            int strLen = (int)len - 1;
                            var strBytes = body.Slice(offset, strLen);
                            if (strBytes.IndexOf((byte)0) >= 0)
                                throw new WaylandConnectionException(
                                    $"String argument {i} contains interior NUL byte");
                            s = Encoding.UTF8.GetString(strBytes);
                        }
                        offset += ((int)len + 3) & ~3;
                        _args.Add(new WlServerArgument
                        {
                            Type = WlServerArgumentType.String,
                            Object = s
                        });
                        break;
                    }

                    case WaylandArgumentCodes.Array:
                    {
                        uint len = ReadUInt32(body, offset);
                        offset += 4;
                        byte[] arr = len == 0
                            ? Array.Empty<byte>()
                            : body.Slice(offset, (int)len).ToArray();
                        offset += ((int)len + 3) & ~3;
                        _args.Add(new WlServerArgument
                        {
                            Type = WlServerArgumentType.Array,
                            Object = arr
                        });
                        break;
                    }
                }
            }
        }
        catch
        {
            CloseUnconsumedFds();
            throw;
        }
    }

    private void CloseUnconsumedFds()
    {
        for (int i = 0; i < _args.Count; i++)
        {
            if (_args[i].Type == WlServerArgumentType.Fd)
                CloseFd(i);
        }
    }

    internal void MarkDispatched() => _dispatched = true;

    internal WlTracedArgument[] ToTracedArgs()
    {
        var traced = new WlTracedArgument[_args.Count];
        for (int i = 0; i < _args.Count; i++)
        {
            var a = _args[i];
            traced[i] = a.Type switch
            {
                WlServerArgumentType.Int32 => new WlTracedArgument { Int32 = a.Int },
                WlServerArgumentType.UInt32 => new WlTracedArgument { UInt32 = unchecked((uint)a.Int) },
                WlServerArgumentType.Fixed => new WlTracedArgument
                {
                    Fixed = Unsafe.As<int, WlFixed>(ref Unsafe.AsRef(in a.Int))
                },
                WlServerArgumentType.Fd => new WlTracedArgument { Int32 = a.Int },
                WlServerArgumentType.Object => new WlTracedArgument
                {
                    UInt32 = unchecked((uint)a.Int)
                },
                WlServerArgumentType.NewId => new WlTracedArgument
                {
                    UInt32 = unchecked((uint)a.Int), Object = a.Object
                },
                WlServerArgumentType.UntypedNewId => new WlTracedArgument
                {
                    UInt32 = unchecked((uint)a.Int), Object = a.Object
                },
                _ => new WlTracedArgument { Object = a.Object }
            };
        }
        return traced;
    }

    public int GetInt32(int num) => _args[num].Int;
    public uint GetUInt32(int num) => unchecked((uint)_args[num].Int);

    public int GetFd(int num)
    {
        if (_args[num].Type != WlServerArgumentType.Fd)
            throw new InvalidOperationException($"Argument {num} is not an FD");
        var bit = 1ul << num;
        if ((_consumedFds & bit) != 0)
            throw new InvalidOperationException("FD already consumed");
        _consumedFds |= bit;
        return _args[num].Int;
    }

    public void CloseFd(int num)
    {
        if (_args[num].Type != WlServerArgumentType.Fd)
            return;
        var bit = 1ul << num;
        if ((_consumedFds & bit) != 0)
            return;
        _consumedFds |= bit;
        LinuxInterop.close(_args[num].Int);
    }

    public WlFixed GetWlFixed(int num)
    {
        var i = _args[num].Int;
        return Unsafe.As<int, WlFixed>(ref i);
    }

    public string? GetString(int num) => (string?)_args[num].Object;
    public byte[]? GetArrayBytes(int num) => (byte[]?)_args[num].Object;

    public T? GetProxy<T>(int num) where T : WlProxy
        => throw new InvalidOperationException("GetProxy<T> is not available on server side");

    public T GetNewIdProxy<T>(int num, IWlEventsListener? listener) where T : WlProxy
        => throw new InvalidOperationException("GetNewIdProxy is not available on server side");

    public WlResource GetServerNewId(int num)
    {
        _consumedNewIds |= 1UL << num;
        return (WlResource?)_args[num].Object
            ?? throw new InvalidOperationException(
                $"No server resource for new_id argument {num}");
    }

    public WlResource? GetServerResource(int num)
    {
        uint objectId = unchecked((uint)_args[num].Int);
        if (objectId == 0)
            return null;
        return _client.ObjectMap.Get(objectId);
    }

    public WlUntypedNewId GetUntypedNewId(int num)
    {
        if (_args[num].Object is not WlUntypedNewId untypedNewId)
            throw new InvalidOperationException($"Argument {num} is not an untyped new_id");
        return untypedNewId;
    }

    /// <summary>
    /// Destroy any eagerly-created new_id resources (for events that will
    /// never be dispatched, e.g. no listener attached).
    /// </summary>
    internal void DestroyEagerResources()
    {
        for (int i = 0; i < _args.Count; i++)
        {
            if (_args[i].Type == WlServerArgumentType.NewId && _args[i].Object is WlResource res)
            {
                _client.DestroyResource(res);
                var arg = _args[i];
                arg.Object = null;
                _args[i] = arg;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_dispatched)
        {
            DestroyEagerResources();
        }
        else
        {
            DestroyUnconsumedNewIds();
        }
        CloseUnconsumedFds();
        _client.Server.ReturnArgsList(_args);
    }

    /// <summary>
    /// Destroy new_id resources that were eagerly created during parsing
    /// but never consumed via <c>GetAndConsume()</c>. This covers the case
    /// where a listener ignores a new_id argument or throws before consuming it.
    /// Logged via the server tracer to help identify buggy listeners.
    /// </summary>
    private void DestroyUnconsumedNewIds()
    {
        for (int i = 0; i < _args.Count; i++)
        {
            if (_args[i].Type == WlServerArgumentType.NewId
                && _args[i].Object is WlResource res
                && (_consumedNewIds & (1UL << i)) == 0)
            {
                _client.Server.Options.Tracer?.TraceUnconsumedNewId(
                    _resource, _method, res);
                _client.DestroyResource(res);
                var arg = _args[i];
                arg.Object = null;
                _args[i] = arg;
            }
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> span, int offset)
    {
        if (offset + 4 > span.Length)
            throw new WaylandConnectionException("Body truncated");
        return Unsafe.ReadUnaligned<int>(ref Unsafe.AsRef(in span[offset]));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset)
    {
        if (offset + 4 > span.Length)
            throw new WaylandConnectionException("Body truncated");
        return Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in span[offset]));
    }
}
