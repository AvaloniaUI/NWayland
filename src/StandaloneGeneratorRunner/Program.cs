using NWayland.Generator;

namespace StandaloneGeneratorRunner;

class Program
{
    static void Main(string[] args)
    {
        while (!File.Exists("NWayland.sln"))
            Directory.SetCurrentDirectory("..");

        NwgSourceText CreateText(string path, string? ns = null) =>
            new NwgSourceText(path, File.ReadAllText(path), ns ?? "NWayland.Protocols.Wayland");
            
        
        WaylandProtocolGenerator.GenerateModel(new NwgInputModel(new NwgSourceText[]
        {
            CreateText("external/wayland/protocol/wayland.xml")
        }.ToEquatableArray(), Array.Empty<NwgArrayTypeMapping>().ToEquatableArray(), Array.Empty<NwgExternalTypeMapping>().ToEquatableArray()),
        (name, src) =>
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("=========================");
            Console.WriteLine(name+".cs:");
            Console.WriteLine("=========================");
            Console.WriteLine();
            Console.WriteLine(src);
            Console.WriteLine();
        });
        
        Console.WriteLine("Hello, World!");
    }
}