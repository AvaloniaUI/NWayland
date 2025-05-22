namespace NWayland.Generator;

record NwgExternalTypeMapping(string Name, string FullTypeName);
record NwgArrayTypeMapping(string Protocol, string Interface, string Member, string Argument, string TypeName);
record NwgSourceText(string Path, string Source, string Namespace);


record class NwgInputModel(
    EquatableArray<NwgSourceText> Sources,
    EquatableArray<NwgArrayTypeMapping> ArrayMappings,
    EquatableArray<NwgExternalTypeMapping> ExternalTypeMappings);
