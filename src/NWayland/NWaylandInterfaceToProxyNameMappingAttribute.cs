using System;

namespace NWayland;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class NWaylandInterfaceToProxyNameMappingAttribute : Attribute
{
    public NWaylandInterfaceToProxyNameMappingAttribute(string waylandNameIdentifier, Type type)
    {
        
    }
}