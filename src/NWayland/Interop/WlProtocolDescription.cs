using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

/// <summary>
/// A protocol error code defined by an interface's <c>error</c> enum, used to give
/// protocol errors a human-readable name and summary (libwayland exposes only the
/// numeric code, interface and object id — the compositor's free-form message is
/// not retrievable).
/// </summary>
public readonly record struct WlProtocolErrorDescription(uint Code, string Name, string? Summary);

public unsafe class WlInterfaceDescription
{
    // Always assigned via the Builder before the instance is observed; null! silences CS8618.
    public string Name { get; private set; } = null!;
    public int Version { get; private set; }
    public IReadOnlyList<WlMessageDescription> Methods { get; private set; } = null!;
    public IReadOnlyList<WlMessageDescription> Events { get; private set; } = null!;
    private IReadOnlyDictionary<uint, WlProtocolErrorDescription> _errors =
        new Dictionary<uint, WlProtocolErrorDescription>();

    public static Builder Create(string name, int version) => new Builder(name, version);

    /// <summary>
    /// Looks up the <c>error</c>-enum entry for a protocol error code raised by an object
    /// of this interface. Returns false if this interface defines no such code.
    /// </summary>
    public bool TryGetError(uint code, out WlProtocolErrorDescription error)
        => _errors.TryGetValue(code, out error);

    // Maps each allocated native wl_interface* back to its managed description so the
    // interface pointer returned by wl_display_get_protocol_error can be resolved.
    private static readonly ConcurrentDictionary<IntPtr, WlInterfaceDescription> _nativeRegistry = new();

    internal static WlInterfaceDescription? ResolveNative(IntPtr native)
        => native != IntPtr.Zero && _nativeRegistry.TryGetValue(native, out var desc) ? desc : null;

    // Maps interface name -> description. Needed because some proxies (notably wl_display)
    // are created by libwayland with its own wl_interface, so the pointer returned by
    // wl_display_get_protocol_error won't be in _nativeRegistry, but the name still resolves.
    private static readonly ConcurrentDictionary<string, WlInterfaceDescription> _nameRegistry = new();

    internal static WlInterfaceDescription? ResolveByName(string name)
        => _nameRegistry.TryGetValue(name, out var desc) ? desc : null;

    private volatile WlInterface* _nativeCache;
    private object _nativeCacheBuilderLock = new();
    internal WlInterface* GetNative()
    {
        if (_nativeCache != null)
            // check -> lock -> recheck -> alloc caching pattern
            // ReSharper disable once InconsistentlySynchronizedField
            return _nativeCache;
        lock (_nativeCacheBuilderLock)
        {
            if (_nativeCache != null)
                return _nativeCache;
            var native = (WlInterface*)Marshal.AllocHGlobal(Unsafe.SizeOf<WlInterface>());
            *native = new WlInterface(Name, Version, Methods.Select(x => x.GetNative()).ToArray(),
                Events.Select(x => x.GetNative()).ToArray());
            _nativeRegistry[(IntPtr)native] = this;
            return _nativeCache = native;
        }
    }

    public class Builder(string Name, int Version)
    {
        private List<WlMessageDescription> _methods = new();
        private List<WlMessageDescription> _events = new();
        private Dictionary<uint, WlProtocolErrorDescription> _errors = new();

        public Builder AddMethod(WlMessageDescription method)
        {
            _methods.Add(method);
            return this;
        }

        public Builder AddEvent(WlMessageDescription ev)
        {
            _events.Add(ev);
            return this;
        }

        public Builder AddError(uint code, string name, string? summary)
        {
            _errors[code] = new WlProtocolErrorDescription(code, name, summary);
            return this;
        }

        public WlInterfaceDescription Build()
        {
            var desc = new WlInterfaceDescription
            {
                Name = Name,
                Version = Version,
                Methods = _methods.ToList(),
                Events = _events.ToList(),
                _errors = _errors
            };
            // Index by name so protocol errors can be resolved even for libwayland-created
            // proxies (e.g. wl_display). Last writer wins if a name is defined twice.
            if (_errors.Count > 0)
                _nameRegistry[Name] = desc;
            return desc;
        }
    }
}

public class WlMessageDescription
{
    // Always assigned via the Builder before the instance is observed; null! silences CS8618.
    public string Name { get; private set; } = null!;
    public string Signature { get; private set; } = null!;
    public bool IsDestructor { get; private set; }
    public int SinceVersion { get; private set; }
    public IReadOnlyList<WlMessageArgumentDescription> Arguments { get; private set; } = null!;

    public static Builder Create(string name) => new Builder(name);

    private WlMessage? _nativeCache;
    internal unsafe WlMessage GetNative()
    {
        if (_nativeCache != null)
            return _nativeCache.Value;

        var typesArr = Arguments.Count > 0 ? new WlInterface*[Arguments.Count] : null;
        for (var c = 0; c < Arguments.Count; c++)
        {
            var arg = Arguments[c];
            if (arg.Code is WaylandArgumentCodes.NewId or WaylandArgumentCodes.Object)
            {
                var ntype = IntPtr.Zero;
                if (arg.ProxyType != null)
                    ntype = (IntPtr)arg.ProxyType.Interface.GetNative();

                typesArr![c] = (WlInterface*)ntype;
            }
            else
                typesArr![c] = null;
        }
        
        _nativeCache = new WlMessage(Name, Signature, typesArr);
        return _nativeCache.Value;
    }
    
    public class Builder(string Name)
    {
        private List<WlMessageArgumentDescription> _args = new();
        private bool _isDestructor;
        private int _sinceVersion;

        public Builder Add(WlMessageArgumentDescription argument)
        {
            _args.Add(argument);
            return this;
        }

        public Builder IsDestructor()
        {
            _isDestructor = true;
            return this;
        }

        public Builder SinceVersion(int version)
        {
            _sinceVersion = version;
            return this;
        }
        
        public WlMessageDescription Build()
        {
            return new WlMessageDescription
            {
                Name = Name,
                Arguments = _args.ToList(),
                IsDestructor = _isDestructor,
                SinceVersion = _sinceVersion,
                Signature = string.Join("", _args.Select(x => (x.AllowNull ? "?" : "") + (char)x.Code))
            };
        }
    }
}

public delegate WlProxy WlProxyFactory(WlProxyCreationContext context);

public delegate NWayland.Server.WlResource WlResourceFactory(NWayland.Server.WlResourceCreationContext context);

public record WlProxyTypeDescriptor(
    WlInterfaceDescription Interface,
    Type ProxyType,
    WlProxyFactory Factory,
    bool Frozen = false,
    Type? ServerResourceType = null,
    WlResourceFactory? ServerFactory = null);

public interface IWlProxyTypeDescriptorProvider
{
    static abstract WlProxyTypeDescriptor ProxyType { get; } 
}

public record class WlMessageArgumentDescription
{
    private WlMessageArgumentDescription(WaylandArgumentCodes code, WlProxyTypeDescriptor? proxyType = null, bool allowNull = false)
    {
        Code = code;
        ProxyType = proxyType;
        AllowNull = allowNull;
    }

    public static readonly WlMessageArgumentDescription Int32 = new(WaylandArgumentCodes.Int32);
    public static readonly WlMessageArgumentDescription UInt32 = new(WaylandArgumentCodes.UInt32);
    public static readonly WlMessageArgumentDescription Fixed = new(WaylandArgumentCodes.Fixed);
    public static readonly WlMessageArgumentDescription String = new(WaylandArgumentCodes.String); 
    public static readonly WlMessageArgumentDescription Array = new(WaylandArgumentCodes.Array);
    public static readonly WlMessageArgumentDescription Fd = new(WaylandArgumentCodes.Fd);
    public static readonly WlMessageArgumentDescription FileDescriptor = new(WaylandArgumentCodes.Fd);
    public WaylandArgumentCodes Code { get; init; }
    public WlProxyTypeDescriptor? ProxyType { get; init; }
    public bool AllowNull { get; init; }

    public static WlMessageArgumentDescription NewId(WlProxyTypeDescriptor? proxyType) =>
        new(WaylandArgumentCodes.NewId, proxyType);
    
    // Nullable like NewId: an object arg may omit its interface (e.g. wl_display.error.object_id),
    // in which case the generator passes null.
    public static WlMessageArgumentDescription Object(WlProxyTypeDescriptor? proxyType) =>
        new(WaylandArgumentCodes.Object, proxyType);

    public WlMessageArgumentDescription AsNullable()
    {
        return new WlMessageArgumentDescription(Code, ProxyType, true);
    }
}

public enum WaylandArgumentCodes : byte
{
    Int32 = (byte)'i',
    UInt32 = (byte)'u',
    Fixed = (byte)'f',
    String = (byte)'s',
    Object = (byte)'o',
    NewId = (byte)'n',
    Array = (byte)'a',
    Fd = (byte)'h'
}