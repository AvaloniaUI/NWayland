using Avalonia;

namespace SubcompositorHost;

class Program
{
    public static WaylandCompositor? Compositor { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Accept optional socket path from command line
        string? socketPath = args.Length > 0 ? args[0] : null;

        Compositor = new WaylandCompositor(socketPath);
        Compositor.Start();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Compositor.Dispose();
            Compositor = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
