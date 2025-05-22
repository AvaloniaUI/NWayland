using System;

namespace NWayland;

[AttributeUsage(AttributeTargets.Assembly)]
public class NWaylandObjectNameMappingAttribute : Attribute
{
    public NWaylandObjectNameMappingAttribute(string waylandNameIdentifier, Type type)
    {
        
    }
}