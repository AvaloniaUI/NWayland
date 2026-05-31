using System;
using System.Linq;
using NWayland.Tests.Protocols.TestSerialization;
using Xunit;

namespace NWayland.Tests;

/// <summary>
/// Verifies the generator's NWaylandVisibility knob: test-serialization.xml is generated public
/// (the default), test-internal.xml is generated internal (NWaylandVisibility="internal"), and
/// internal bindings get no assembly-level proxy-map attribute.
/// </summary>
public class GeneratorVisibilityTests
{
    [Fact]
    public void DefaultProtocol_IsPublic()
    {
        Assert.True(typeof(TestParent).IsPublic);
    }

    [Fact]
    public void InternalProtocol_IsNotPublic()
    {
        // Same assembly, so the internal type is referenceable here.
        var t = typeof(NWayland.Tests.Protocols.TestInternal.TestInternalThing);
        Assert.False(t.IsPublic);
        Assert.True(t.IsNotPublic);
        // Nested Server type is effectively internal via its outer type.
        Assert.False(typeof(NWayland.Tests.Protocols.TestInternal.TestInternalThing.Server).IsVisible);
    }

    [Fact]
    public void MappingAttribute_EmittedForPublicButNotInternal()
    {
        // The attribute stores nothing at runtime, but its ctor args are readable via metadata.
        var mapped = typeof(TestParent).Assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.Name == "NWaylandInterfaceToProxyNameMappingAttribute")
            .Select(a => (string)a.ConstructorArguments[0].Value!)
            .ToList();

        Assert.Contains("test_parent", mapped);            // public -> mapped
        Assert.DoesNotContain("test_internal_thing", mapped); // internal -> skipped
    }
}
