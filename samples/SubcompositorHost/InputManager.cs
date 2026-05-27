using System.Runtime.InteropServices;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Server;

namespace SubcompositorHost;

/// <summary>
/// Listener for wl_seat. Handles get_pointer and get_keyboard requests.
/// Sends capabilities on bind.
/// </summary>
public sealed class SeatListener : WlSeat.ServerListener
{
    private readonly ClientState _client;
    public SeatListener(ClientState client) => _client = client;

    protected override void GetPointer(WlSeat.Server resource, NewId<WlPointer.Server, WlPointer.ServerListener> @id)
    {
        var pointer = id.GetAndConsume(new PointerListener());
        _client.PointerResource = pointer;
    }

    protected override void GetKeyboard(WlSeat.Server resource, NewId<WlKeyboard.Server, WlKeyboard.ServerListener> @id)
    {
        var keyboard = id.GetAndConsume(new KeyboardListener());
        _client.KeyboardResource = keyboard;

        // Send keymap with NO_KEYMAP format
        // Wire format requires an fd even for no-keymap; use /dev/null
        var devNullFd = Open("/dev/null", 0 /* O_RDONLY */, 0);
        if (devNullFd >= 0)
        {
            keyboard.Keymap(0 /* WL_KEYBOARD_KEYMAP_FORMAT_NO_KEYMAP */, devNullFd, 0);
            // fd is consumed by the protocol send
        }
    }

    protected override void GetTouch(WlSeat.Server resource, NewId<WlTouch.Server, WlTouch.ServerListener> @id)
    {
        id.GetAndConsume();
    }

    protected override void Release(WlSeat.Server resource)
    {
        resource.Dispose();
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, int mode);
}

/// <summary>
/// Listener for wl_keyboard requests (release only in v1).
/// </summary>
public sealed class KeyboardListener : WlKeyboard.ServerListener
{
    protected override void Release(WlKeyboard.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Listener for wl_pointer requests (release only in newer versions).
/// </summary>
public sealed class PointerListener : WlPointer.ServerListener
{
    protected override void SetCursor(WlPointer.Server resource, uint @serial, WlSurface.Server? @surface, int @hotspotX, int @hotspotY)
    {
    }

    protected override void Release(WlPointer.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Listener for wl_output requests (release only in v3+).
/// </summary>
public sealed class OutputListener : WlOutput.ServerListener
{
    protected override void Release(WlOutput.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Stub listener for wl_data_device_manager. Creates data devices but never sends enter events.
/// </summary>
public sealed class DataDeviceManagerListener : WlDataDeviceManager.ServerListener
{
    private readonly ClientState _client;
    public DataDeviceManagerListener(ClientState client) => _client = client;

    protected override void CreateDataSource(WlDataDeviceManager.Server resource, NewId<WlDataSource.Server, WlDataSource.ServerListener> @id)
    {
        id.GetAndConsume(new DataSourceListener());
    }

    protected override void GetDataDevice(WlDataDeviceManager.Server resource,
        NewId<WlDataDevice.Server, WlDataDevice.ServerListener> @id, WlSeat.Server? @seat)
    {
        id.GetAndConsume(new DataDeviceListener());
        // Don't send enter events — clipboard support deferred
    }

    protected override void Release(WlDataDeviceManager.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Stub listener for wl_data_source.
/// </summary>
public sealed class DataSourceListener : WlDataSource.ServerListener
{
    protected override void Offer(WlDataSource.Server resource, string @mimeType)
    {
    }

    protected override void SetActions(WlDataSource.Server resource, WlDataDeviceManager.DndActionEnum @dndActions)
    {
    }

    protected override void Destroy(WlDataSource.Server resource) => resource.Dispose();
}

/// <summary>
/// Stub listener for wl_data_device.
/// </summary>
public sealed class DataDeviceListener : WlDataDevice.ServerListener
{
    protected override void StartDrag(WlDataDevice.Server resource, WlDataSource.Server? @source, WlSurface.Server? @origin, WlSurface.Server? @icon, uint @serial)
    {
    }

    protected override void SetSelection(WlDataDevice.Server resource, WlDataSource.Server? @source, uint @serial)
    {
    }

    protected override void Release(WlDataDevice.Server resource)
    {
        resource.Dispose();
    }
}

// ── Input event types posted from UI thread to server thread ──

public sealed class KeyboardEnterEvent : InputEvent
{
    private readonly WlKeyboard.Server _keyboard;
    private readonly WlSurface.Server _surface;

    public KeyboardEnterEvent(WlKeyboard.Server keyboard, WlSurface.Server surface)
    {
        _keyboard = keyboard;
        _surface = surface;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _keyboard.Enter(compositor.NextSerial(), _surface, ReadOnlySpan<byte>.Empty);
    }
}

public sealed class KeyboardLeaveEvent : InputEvent
{
    private readonly WlKeyboard.Server _keyboard;
    private readonly WlSurface.Server _surface;

    public KeyboardLeaveEvent(WlKeyboard.Server keyboard, WlSurface.Server surface)
    {
        _keyboard = keyboard;
        _surface = surface;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _keyboard.Leave(compositor.NextSerial(), _surface);
    }
}

public sealed class KeyboardKeyEvent : InputEvent
{
    private readonly WlKeyboard.Server _keyboard;
    private readonly uint _time;
    private readonly uint _key;
    private readonly WlKeyboard.KeyStateEnum _state;

    public KeyboardKeyEvent(WlKeyboard.Server keyboard, uint time,
        uint key, WlKeyboard.KeyStateEnum state)
    {
        _keyboard = keyboard;
        _time = time;
        _key = key;
        _state = state;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _keyboard.Key(compositor.NextSerial(), _time, _key, _state);
    }
}

public sealed class KeyboardModifiersEvent : InputEvent
{
    private readonly WlKeyboard.Server _keyboard;
    private readonly uint _modsDepressed;
    private readonly uint _modsLatched;
    private readonly uint _modsLocked;
    private readonly uint _group;

    public KeyboardModifiersEvent(WlKeyboard.Server keyboard,
        uint modsDepressed, uint modsLatched, uint modsLocked, uint group)
    {
        _keyboard = keyboard;
        _modsDepressed = modsDepressed;
        _modsLatched = modsLatched;
        _modsLocked = modsLocked;
        _group = group;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _keyboard.Modifiers(compositor.NextSerial(), _modsDepressed, _modsLatched, _modsLocked, _group);
    }
}

public sealed class PointerEnterEvent : InputEvent
{
    private readonly WlPointer.Server _pointer;
    private readonly WlSurface.Server _surface;
    private readonly double _x, _y;

    public PointerEnterEvent(WlPointer.Server pointer, WlSurface.Server surface,
        double x, double y)
    {
        _pointer = pointer;
        _surface = surface;
        _x = x;
        _y = y;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _pointer.Enter(compositor.NextSerial(), _surface,
            new NWayland.WlFixed(_x), new NWayland.WlFixed(_y));
    }
}

public sealed class PointerLeaveEvent : InputEvent
{
    private readonly WlPointer.Server _pointer;
    private readonly WlSurface.Server _surface;

    public PointerLeaveEvent(WlPointer.Server pointer, WlSurface.Server surface)
    {
        _pointer = pointer;
        _surface = surface;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _pointer.Leave(compositor.NextSerial(), _surface);
    }
}

public sealed class PointerMotionEvent : InputEvent
{
    private readonly WlPointer.Server _pointer;
    private readonly uint _time;
    private readonly double _x, _y;

    public PointerMotionEvent(WlPointer.Server pointer, uint time, double x, double y)
    {
        _pointer = pointer;
        _time = time;
        _x = x;
        _y = y;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _pointer.Motion(_time, new NWayland.WlFixed(_x), new NWayland.WlFixed(_y));
    }
}

public sealed class PointerButtonEvent : InputEvent
{
    private readonly WlPointer.Server _pointer;
    private readonly uint _time;
    private readonly uint _button;
    private readonly WlPointer.ButtonStateEnum _state;

    public PointerButtonEvent(WlPointer.Server pointer, uint time,
        uint button, WlPointer.ButtonStateEnum state)
    {
        _pointer = pointer;
        _time = time;
        _button = button;
        _state = state;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _pointer.Button(compositor.NextSerial(), _time, _button, _state);
    }
}

public sealed class PointerAxisEvent : InputEvent
{
    private readonly WlPointer.Server _pointer;
    private readonly uint _time;
    private readonly WlPointer.AxisEnum _axis;
    private readonly double _value;

    public PointerAxisEvent(WlPointer.Server pointer, uint time,
        WlPointer.AxisEnum axis, double value)
    {
        _pointer = pointer;
        _time = time;
        _axis = axis;
        _value = value;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _pointer.Axis(_time, _axis, new NWayland.WlFixed(_value));
    }
}
