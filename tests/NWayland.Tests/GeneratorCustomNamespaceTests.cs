using Custom.External.Bindings.TestCustomNs;
using NWayland;
using Xunit;

namespace NWayland.Tests;

/// <summary>
/// Regression guard: a protocol generated under a root namespace that is NOT nested under
/// NWayland must still compile. Root-namespace types (WlProxy base type, IWlTargetQueue, WlFixed)
/// are referenced unqualified, so the generated file has to emit `using NWayland;`.
/// The mere fact that this test assembly compiles with test-customns.xml proves the fix; the
/// assertions below pin the expectations explicitly.
/// </summary>
public class GeneratorCustomNamespaceTests
{
    [Fact]
    public void CustomNamespaceType_IsGenerated_AndInheritsWlProxy()
    {
        var t = typeof(TestCustomNsThing);
        Assert.Equal("Custom.External.Bindings.TestCustomNs", t.Namespace);
        Assert.Equal(typeof(WlProxy), t.BaseType);
    }
}
