using System.Net.Sockets;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using NWayland.Interop;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgShell;
using NWayland.Protocols.TextInputUnstableV3;
using NWayland.Server;

namespace SubcompositorHost;

/// <summary>
/// Core Wayland compositor. Runs the server event loop on a background thread,
/// manages client connections, advertises globals, and dispatches protocol requests.
/// </summary>
public sealed class WaylandCompositor : IDisposable
{
    private readonly WaylandServer _server;
    private readonly string _socketPath;
    private Socket? _listenSocket;
    private Thread? _thread;
    private Thread? _acceptThread;
    private volatile bool _disposed;

    // State tracked per client
    private readonly Dictionary<WaylandClient, ClientState> _clientStates = new();

    public WaylandCompositor(string? socketPath = null)
    {
        _server = new WaylandServer();

        if (socketPath != null)
        {
            _socketPath = socketPath;
        }
        else
        {
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                             ?? throw new InvalidOperationException("XDG_RUNTIME_DIR not set");
            _socketPath = Path.Combine(runtimeDir, $"wayland-subcompositor-{Environment.ProcessId}");
        }
    }

    public WaylandServer Server => _server;
    public string SocketPath => _socketPath;

    public void Start()
    {
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listenSocket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listenSocket.Listen(4);
        _listenSocket.Blocking = true;

        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", _socketPath);

        _thread = new Thread(RunLoop) { Name = "WaylandCompositor", IsBackground = true };
        _thread.Start();

        _acceptThread = new Thread(AcceptLoop) { Name = "WaylandAccept", IsBackground = true };
        _acceptThread.Start();

        Console.WriteLine($"Wayland compositor listening on {_socketPath}");
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            try
            {
                var clientSocket = _listenSocket!.Accept();
                int fd = (int)clientSocket.SafeHandle.DangerousGetHandle();
                clientSocket.SafeHandle.SetHandleAsInvalid();
                // Don't dispose the .NET Socket — SetHandleAsInvalid() detached fd ownership,
                // and Dispose() can block on graceful shutdown attempts.

                // Post the raw fd to the event loop thread so AddClient + SetupClientGlobals
                // run atomically before any client messages are parsed.
                _server.Post(new NewClientFdEvent(fd));
            }
            catch (SocketException) when (_disposed)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Console.Error.WriteLine($"Accept error: {ex}");
            }
        }
    }

    private void RunLoop()
    {
        while (!_disposed)
        {
            try
            {
                WaylandServerEvent evt;
                try
                {
                    evt = _server.NextEvent();
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    break;
                }

                HandleEvent(evt);
            }
            catch (ObjectDisposedException)
            {
                // Client resource disposed during event handling — continue
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Console.Error.WriteLine($"Compositor error: {ex.Message}");
            }
        }
    }

    private void HandleEvent(WaylandServerEvent evt)
    {
        switch (evt)
        {
            case WaylandCustomEvent custom:
                HandleCustomEvent(custom);
                break;

            case WaylandClientDisconnectEvent disconnect:
                HandleClientDisconnect(disconnect);
                break;

            case WaylandServerSyncEvent sync:
                sync.Complete(NextSerial());
                break;

            case WaylandServerRegistryBindEvent bind:
                HandleRegistryBind(bind);
                break;

            case WaylandServerRequestEvent request:
                try
                {
                    request.Dispatch();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Request dispatch error: {ex}");
                }
                finally
                {
                    request.Dispose();
                }
                break;
        }
    }

    private void SetupClientGlobals(WaylandClient client)
    {
        var state = new ClientState(client);
        _clientStates[client] = state;

        client.AddGlobal("wl_compositor", 6);
        client.AddGlobal("wl_subcompositor", 1);
        client.AddGlobal("wl_shm", 1);
        client.AddGlobal("wl_output", 1);
        client.AddGlobal("wl_seat", 1);
        client.AddGlobal("wl_data_device_manager", 1);
        client.AddGlobal("xdg_wm_base", 1);
        client.AddGlobal("zwp_text_input_manager_v3", 1);
    }

    private void HandleRegistryBind(WaylandServerRegistryBindEvent bind)
    {
        var state = _clientStates[bind.Client!];

        switch (bind.Global.Interface)
        {
            case "wl_compositor":
                bind.Accept<WlCompositor.Server>(state.CompositorListener);
                break;
            case "wl_subcompositor":
                bind.Accept<WlSubcompositor.Server>(state.SubcompositorListener);
                break;
            case "wl_shm":
                var shm = bind.Accept<WlShm.Server>(state.ShmListener);
                // Advertise supported formats
                shm.Format(WlShm.FormatEnum.Argb8888);
                shm.Format(WlShm.FormatEnum.Xrgb8888);
                break;
            case "wl_output":
                var output = bind.Accept<WlOutput.Server>(state.OutputListener);
                // Send static output info (v1: geometry + mode, no done event)
                output.Geometry(0, 0, 300, 200, (int)WlOutput.SubpixelEnum.Unknown,
                    "SubcompositorHost", "Virtual", (int)WlOutput.TransformEnum.Normal);
                output.Mode(WlOutput.ModeEnum.Current, 1920, 1080, 60000);
                break;
            case "wl_seat":
                var seat = bind.Accept<WlSeat.Server>(state.SeatListener);
                state.SeatResource = seat;
                // Advertise pointer + keyboard capabilities
                seat.Capabilities(WlSeat.CapabilityEnum.Pointer | WlSeat.CapabilityEnum.Keyboard);
                break;
            case "wl_data_device_manager":
                bind.Accept<WlDataDeviceManager.Server>(state.DataDeviceManagerListener);
                break;
            case "xdg_wm_base":
                bind.Accept<XdgWmBase.Server>(state.XdgWmBaseListener);
                break;
            case "zwp_text_input_manager_v3":
                bind.Accept<ZwpTextInputManagerV3.Server>(state.TextInputManagerListener);
                break;
        }
    }

    private void HandleCustomEvent(WaylandCustomEvent custom)
    {
        switch (custom.State)
        {
            case NewClientFdEvent newFd:
                try
                {
                    var client = _server.AddClient(newFd.Fd);
                    SetupClientGlobals(client);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"AddClient failed: {ex}");
                }
                break;
            case InputEvent input:
                HandleInputEvent(input);
                break;
            case BitmapsConsumed consumed:
                HandleBitmapsConsumed(consumed);
                break;
        }
    }

    private void HandleInputEvent(InputEvent input)
    {
        try
        {
            input.Deliver(this);
        }
        catch (ObjectDisposedException)
        {
            // Resource was destroyed (client disconnect race) — ignore
        }
    }

    private void HandleBitmapsConsumed(BitmapsConsumed consumed)
    {
        foreach (var surface in consumed.Surfaces)
            surface.OnBitmapConsumed(this);
    }

    private void HandleClientDisconnect(WaylandClientDisconnectEvent disconnect)
    {
        if (disconnect.Client != null && _clientStates.Remove(disconnect.Client, out var state))
        {
            state.Dispose(this);
        }
    }

    // Serial counter for events
    private uint _serial;
    public uint NextSerial() => ++_serial;

    // Surface registry (all surfaces across all clients, for lookup)
    private readonly Dictionary<WlSurface.Server, SurfaceState> _surfaces = new();

    public void RegisterSurface(WlSurface.Server resource, SurfaceState state)
        => _surfaces[resource] = state;

    public void UnregisterSurface(WlSurface.Server resource)
        => _surfaces.Remove(resource);

    public SurfaceState? GetSurface(WlSurface.Server resource)
        => _surfaces.GetValueOrDefault(resource);

    public SurfaceState? GetSurface(WlResource resource)
        => resource is WlSurface.Server s ? _surfaces.GetValueOrDefault(s) : null;

    // Toplevel registry
    private readonly List<XdgToplevelState> _toplevels = new();

    public void RegisterToplevel(XdgToplevelState toplevel) => _toplevels.Add(toplevel);

    public void UnregisterToplevel(XdgToplevelState toplevel)
    {
        _toplevels.Remove(toplevel);
        Dispatcher.UIThread.Post(() =>
        {
            toplevel.Window?.StopRenderTimer();
            toplevel.Window?.Close();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Close the listen socket to unblock the accept thread
        _listenSocket?.Dispose();
        _acceptThread?.Join(2000);

        _server.Post(); // wake up the loop
        _thread?.Join(2000);
        _server.DisposeAsync().AsTask().Wait(2000);

        if (File.Exists(_socketPath))
            File.Delete(_socketPath);
    }
}

/// <summary>
/// Tracks per-client state: listener instances, seat resources, surfaces.
/// </summary>
public sealed class ClientState : IDisposable
{
    public WaylandClient Client { get; }

    // Listeners (created once per client, reused for all binds)
    public CompositorListener CompositorListener { get; }
    public SubcompositorListener SubcompositorListener { get; }
    public ShmListener ShmListener { get; }
    public OutputListener OutputListener { get; }
    public SeatListener SeatListener { get; }
    public DataDeviceManagerListener DataDeviceManagerListener { get; }
    public XdgWmBaseListener XdgWmBaseListener { get; }
    public TextInputManagerListener TextInputManagerListener { get; }

    // Seat resources
    public WlSeat.Server? SeatResource { get; set; }
    public WlKeyboard.Server? KeyboardResource { get; set; }
    public WlPointer.Server? PointerResource { get; set; }

    // Surfaces owned by this client
    public List<SurfaceState> Surfaces { get; } = new();

    // Text input state
    public TextInputState? TextInput { get; set; }

    public ClientState(WaylandClient client)
    {
        Client = client;
        CompositorListener = new CompositorListener(this);
        SubcompositorListener = new SubcompositorListener(this);
        ShmListener = new ShmListener(this);
        OutputListener = new OutputListener();
        SeatListener = new SeatListener(this);
        DataDeviceManagerListener = new DataDeviceManagerListener(this);
        XdgWmBaseListener = new XdgWmBaseListener(this);
        TextInputManagerListener = new TextInputManagerListener(this);
    }

    public void Dispose(WaylandCompositor compositor)
    {
        foreach (var surface in Surfaces.ToArray())
        {
            surface.Destroy(compositor);
        }
    }

    void IDisposable.Dispose() { }
}

/// <summary>
/// Custom event posted from the UI thread to deliver input to a Wayland client.
/// </summary>
public abstract class InputEvent
{
    public abstract void Deliver(WaylandCompositor compositor);
}

/// <summary>
/// Signal from UI thread that specific surface bitmaps have been rendered (consumed).
/// Server thread clears pending flags and fires deferred frame callbacks.
/// </summary>
public sealed class BitmapsConsumed
{
    public List<SurfaceState> Surfaces { get; }
    public BitmapsConsumed(List<SurfaceState> surfaces) => Surfaces = surfaces;
}

/// <summary>
/// Signal that a new client fd has been accepted and needs AddClient + globals set up.
/// </summary>
public sealed class NewClientFdEvent
{
    public int Fd { get; }
    public NewClientFdEvent(int fd) => Fd = fd;
}
