using System;

namespace NWayland.Interop.Metadata;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class NWaylandInterfaceToProxyNameMappingAttribute : Attribute
{
    public NWaylandInterfaceToProxyNameMappingAttribute(string waylandNameIdentifier, Type type)
    {
        
    }
}