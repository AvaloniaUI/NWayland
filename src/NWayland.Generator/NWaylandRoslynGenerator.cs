using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;


namespace NWayland.Generator;

/// <summary>
/// A sample source generator that creates a custom report based on class properties. The target class should be annotated with the 'Generators.ReportAttribute' attribute.
/// When using the source code as a baseline, an incremental source generator is preferable because it reduces the performance overhead.
/// </summary>
[Generator]
public class NWaylandRoslynGenerator : IIncrementalGenerator
{

    record class ReferencedAssemblyAttribute(string Asm, string Attr, EquatableArray<string> Args)
    {
        public override string ToString()
        {
            return $"{Asm} / {Attr}: " + string.Join(", ", Args);
        }
    }
/*
    static string FullTypeName(INamedTypeSymbol typeSymbol)
    {
        
    }*/
    
    static EquatableArray<NwgExternalTypeMapping> BuildAttributesModel(Compilation c, CancellationToken t)
    {
        var rv = new List<NwgExternalTypeMapping>();
        foreach (var asm in c.References)
        {
            t.ThrowIfCancellationRequested();
            var sym = c.GetAssemblyOrModuleSymbol(asm);
            ImmutableArray<AttributeData> attrs;
            if (sym is IAssemblySymbol asmSymbol) 
                attrs = asmSymbol.GetAttributes();
            else if (sym is IModuleSymbol modSymbol)
                attrs = modSymbol.GetAttributes();
            else 
                continue;
            foreach (var attr in attrs)
            {
                if (attr.AttributeClass?.ToString() == "NWayland.Interop.Metadata.NWaylandInterfaceToProxyNameMappingAttribute")
                    rv.Add(new NwgExternalTypeMapping(attr.ConstructorArguments[0].Value!.ToString(),
                        attr.ConstructorArguments[1].Value!.ToString()));
            }
        }

        return new(rv.ToEquatableArray());
    }
    
    
    record NwgIntermediateInputModel(
        EquatableArray<NwgSourceText> Sources,
        string ArrayMappings,
        EquatableArray<NwgExternalTypeMapping> ExternalTypeMappings);

    
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var arrayHintsString = context.AnalyzerConfigOptionsProvider.Select((g, _) =>
        {
            g.GlobalOptions.TryGetValue("build_property.NWayland_ArrayHints", out var value);
            return value ?? "";
        });

        var allAttrs = context.CompilationProvider.Select(BuildAttributesModel);

        var sources = context.AdditionalTextsProvider.Combine(context.AnalyzerConfigOptionsProvider)
            .Select((x, t) =>
            {
                var fileOptions = x.Right.GetOptions(x.Left);
                fileOptions.TryGetValue("build_metadata.AdditionalFiles.NWaylandNamespace", out var ns);
                // Per-file visibility wins; fall back to the NWayland_DefaultVisibility property.
                if (!fileOptions.TryGetValue("build_metadata.AdditionalFiles.NWaylandVisibility", out var vis)
                    || string.IsNullOrWhiteSpace(vis))
                    x.Right.GlobalOptions.TryGetValue("build_property.NWayland_DefaultVisibility", out vis);
                var isInternal = string.Equals(vis, "internal", StringComparison.OrdinalIgnoreCase);
                return (text: x.Left, ns: ns, isInternal);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ns))
            .Select((x, t) => (path: x.text.Path, text: x.text.GetText(), ns: x.ns, x.isInternal))
            .Where(x => x.text != null)
            .Select((x, _) => new NwgSourceText(x.path, x.text!.ToString(), x.ns!, x.isInternal)).Collect();

        var combo = allAttrs.Combine(sources)
            .Combine(arrayHintsString)
            .Select((x, _) =>
                new NwgIntermediateInputModel(x.Left.Right, x.Right, x.Left.Left));

        context.RegisterSourceOutput(combo, (productionContext, model) =>
        {
            if (model.Sources.IsEmpty)
                return;
            
            try
            {
                var arrayMappings = model.ArrayMappings.Split('|').Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x =>
                    {
                        var arr = x.Split(':');
                        if (arr.Length != 5)
                            throw new NwGeneratorException("Unable to parse type mapping " + x);
                        return new NwgArrayTypeMapping(arr[0], arr[1], arr[2], arr[3], arr[4]);
                    }).ToEquatableArray();

                WaylandProtocolGenerator.GenerateModel(
                    new NwgInputModel(model.Sources, arrayMappings, model.ExternalTypeMappings),
                    productionContext.AddSource);
            }
            catch (Exception e)
            {
                var error = e.ToString();
#pragma warning disable RS1035
                //System.IO.File.WriteAllText("/tmp/full.txt", error);
#pragma warning restore RS1035
                if (e is NwGeneratorException)
                    error = e.Message;

                var location = Location.None;
                if (e is NwGeneratorWithFileException withFile)
                    location = Location.Create(withFile.File, new TextSpan(), new LinePositionSpan());
                
                productionContext.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(
                    id: "NW0000",
                    title: error,
                    messageFormat: error,
                    category: "Usage",
                    defaultSeverity: DiagnosticSeverity.Error,
                    isEnabledByDefault: true,
                    error), location));
            }
        });
    }
}