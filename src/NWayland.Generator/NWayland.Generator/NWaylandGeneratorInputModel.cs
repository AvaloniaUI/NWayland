namespace NWayland.Generator;

public record NwgExternalTypeMapping(string Name, string FullTypeName);
public record NwgArrayTypeMapping(string Protocol, string Interface, string Member, string Argument, string TypeName);
public record NwgSourceText(string Path, string Source, string Namespace);


public record class NwgInputModel(
    EquatableArray<NwgSourceText> Sources,
    EquatableArray<NwgArrayTypeMapping> ArrayMappings,
    EquatableArray<NwgExternalTypeMapping> ExternalTypeMappings);
