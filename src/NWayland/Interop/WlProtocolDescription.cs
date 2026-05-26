using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;

namespace NWayland.Interop;

public unsafe class WlInterfaceDescription
{
    public string Name { get; private set; }
    public int Version { get; private set; }
    public IReadOnlyList<WlMessageDescription> Methods { get; private set; }
    public IReadOnlyList<WlMessageDescription> Events { get; private set; }

    public static Builder Create(string name, int version) => new Builder(name, version);

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
            return _nativeCache = native;
        }
    }
    
    public class Builder(string Name, int Version)
    {
        private List<WlMessageDescription> _methods = new();
        private List<WlMessageDescription> _events = new();

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
        
        public WlInterfaceDescription Build()
        {
            return new WlInterfaceDescription
            {
                Name = Name,
                Version = Version,
                Methods = _methods.ToList(),
                Events = _events.ToList()
            };
        }
    }
}

public class WlMessageDescription
{
    public string Name { get; private set; }
    public string Signature { get; private set; }
    public bool IsDestructor { get; private set; }
    public int SinceVersion { get; private set; }
    public IReadOnlyList<WlMessageArgumentDescription> Arguments { get; private set; }

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
    
    public static WlMessageArgumentDescription Object(WlProxyTypeDescriptor proxyType) =>
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