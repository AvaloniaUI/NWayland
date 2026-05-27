using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NWayland.Protocols.Wayland;

namespace SubcompositorHost;

public partial class ToplevelHostWindow : Window
{
    private readonly XdgToplevelState _toplevel;
    private readonly WaylandCompositor _compositor;
    private readonly SurfaceControl _surfaceControl;
    public SurfaceControl SurfaceControl => _surfaceControl;
    private bool _pointerInside;
    private bool _keyboardFocused;
    private bool _closingFromServer;

    // Timestamp base for input events
    private static readonly long _startTicks = Environment.TickCount64;
    private static uint GetTimestamp() => (uint)(Environment.TickCount64 - _startTicks);

    public ToplevelHostWindow(XdgToplevelState toplevel, WaylandCompositor compositor)
    {
        _toplevel = toplevel;
        _compositor = compositor;

        AvaloniaXamlLoader.Load(this);

        Title = toplevel.Title ?? "Wayland Toplevel";
        var titleText = this.FindControl<TextBlock>("TitleText");
        var statusText = this.FindControl<TextBlock>("StatusText");
        var contentArea = this.FindControl<Border>("ContentArea");

        if (titleText != null)
            titleText.Text = toplevel.Title ?? "Untitled";
        if (statusText != null)
            statusText.Text = $"App: {toplevel.AppId}";

        _surfaceControl = new SurfaceControl(toplevel, compositor);
        if (contentArea != null)
            contentArea.Child = _surfaceControl;

        // Set up input event handlers
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerMoved += OnPointerMoved;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        TextInput += OnTextInput;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;

        Closing += OnWindowClosing;
        Activated += OnWindowActivated;
        
        // Send configure when the content area resizes
        _surfaceControl.SizeChanged += OnSurfaceControlSizeChanged;
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        // Send xdg_toplevel.close to the client instead of force-closing
        _compositor.Server.Post(new ToplevelCloseEvent(_toplevel));
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingFromServer) return; // Allow close when triggered by server
        
        // Prevent the window from closing — send close to the client instead
        // The client will destroy the toplevel, which will close this window
        e.Cancel = true;
        _compositor.Server.Post(new ToplevelCloseEvent(_toplevel));
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        
        // Send configure with activated state so GTK renders as active
        _compositor.Server.Post(new ToplevelConfigureEvent(_toplevel, 0, 0, activated: true));

        if (clientState.KeyboardResource != null && !_keyboardFocused)
        {
            _keyboardFocused = true;
            _compositor.Server.Post(new KeyboardEnterEvent(
                clientState.KeyboardResource,
                _toplevel.XdgSurface.Surface.Resource));
            // Send initial modifiers (all zeros) — required after keyboard enter
            _compositor.Server.Post(new KeyboardModifiersEvent(
                clientState.KeyboardResource,
                0, 0, 0, 0));
        }
    }

    /// <summary>
    /// Stop the render timer and allow the window to close (called from server thread via Dispatcher).
    /// </summary>
    public void StopRenderTimer()
    {
        _closingFromServer = true;
        _surfaceControl.Stop();
    }

    private void OnSurfaceControlSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // When the Avalonia window resizes, send a configure event to the client
        var width = (int)e.NewSize.Width;
        var height = (int)e.NewSize.Height;
        if (width > 0 && height > 0)
        {
            _compositor.Server.Post(new ToplevelConfigureEvent(_toplevel, width, height));
        }
    }

    // ── Pointer events ──

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.PointerResource == null) return;

        _pointerInside = true;
        var pos = GetSurfacePosition(e);
        _compositor.Server.Post(new PointerEnterEvent(
            clientState.PointerResource,
            _toplevel.XdgSurface.Surface.Resource,
            pos.X, pos.Y));
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.PointerResource == null) return;

        _pointerInside = false;
        _compositor.Server.Post(new PointerLeaveEvent(
            clientState.PointerResource,
            _toplevel.XdgSurface.Surface.Resource));
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.PointerResource == null || !_pointerInside) return;

        var pos = GetSurfacePosition(e);
        _compositor.Server.Post(new PointerMotionEvent(
            clientState.PointerResource,
            GetTimestamp(),
            pos.X, pos.Y));
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.PointerResource == null) return;

        uint button = MapPointerButton(e.GetCurrentPoint(this).Properties);
        _compositor.Server.Post(new PointerButtonEvent(
            clientState.PointerResource,
            GetTimestamp(),
            button, WlPointer.ButtonStateEnum.Pressed));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.PointerResource == null) return;

        uint button = MapPointerButton(e.GetCurrentPoint(this).Properties);
        _compositor.Server.Post(new PointerButtonEvent(
            clientState.PointerResource,
            GetTimestamp(),
            button, WlPointer.ButtonStateEnum.Released));
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.PointerResource == null) return;

        if (e.Delta.Y != 0)
        {
            _compositor.Server.Post(new PointerAxisEvent(
                clientState.PointerResource,
                GetTimestamp(),
                WlPointer.AxisEnum.VerticalScroll,
                -e.Delta.Y * 15)); // 15 pixels per scroll unit
        }

        if (e.Delta.X != 0)
        {
            _compositor.Server.Post(new PointerAxisEvent(
                clientState.PointerResource,
                GetTimestamp(),
                WlPointer.AxisEnum.HorizontalScroll,
                e.Delta.X * 15));
        }
    }

    // ── Keyboard events ──

    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.KeyboardResource == null) return;

        if (!_keyboardFocused)
        {
            _keyboardFocused = true;
            _compositor.Server.Post(new KeyboardEnterEvent(
                clientState.KeyboardResource,
                _toplevel.XdgSurface.Surface.Resource));
            // Send initial modifiers after enter
            _compositor.Server.Post(new KeyboardModifiersEvent(
                clientState.KeyboardResource,
                0, 0, 0, 0));
        }

        // Also enter text-input if enabled
        if (clientState.TextInput is { IsEnabled: true })
        {
            _compositor.Server.Post(new TextInputEnterEvent(
                clientState.TextInput,
                _toplevel.XdgSurface.Surface.Resource));
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.KeyboardResource == null) return;

        _keyboardFocused = false;
        _compositor.Server.Post(new KeyboardLeaveEvent(
            clientState.KeyboardResource,
            _toplevel.XdgSurface.Surface.Resource));

        if (clientState.TextInput?.FocusedSurface != null)
        {
            _compositor.Server.Post(new TextInputLeaveEvent(
                clientState.TextInput,
                _toplevel.XdgSurface.Surface.Resource));
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.KeyboardResource == null) return;

        var linuxKey = AvaloniaKeyToLinux(e.Key);
        if (linuxKey == 0) return;

        _compositor.Server.Post(new KeyboardKeyEvent(
            clientState.KeyboardResource,
            GetTimestamp(),
            linuxKey, WlKeyboard.KeyStateEnum.Pressed));

        var mods = GetModifiers(e.KeyModifiers);
        _compositor.Server.Post(new KeyboardModifiersEvent(
            clientState.KeyboardResource,
            mods, 0, 0, 0));
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.KeyboardResource == null) return;

        var linuxKey = AvaloniaKeyToLinux(e.Key);
        if (linuxKey == 0) return;

        _compositor.Server.Post(new KeyboardKeyEvent(
            clientState.KeyboardResource,
            GetTimestamp(),
            linuxKey, WlKeyboard.KeyStateEnum.Released));

        var mods = GetModifiers(e.KeyModifiers);
        _compositor.Server.Post(new KeyboardModifiersEvent(
            clientState.KeyboardResource,
            mods, 0, 0, 0));
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        var clientState = _toplevel.XdgSurface.Surface.ClientState;
        if (clientState.TextInput is not { IsEnabled: true }) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        _compositor.Server.Post(new TextInputCommitEvent(clientState.TextInput, e.Text));
    }

    // ── Helpers ──

    private Point GetSurfacePosition(PointerEventArgs e)
    {
        var pos = e.GetPosition(_surfaceControl);
        var xdgSurface = _toplevel.XdgSurface;

        // Add geometry offset so the coordinates are relative to the surface, not the geometry
        if (xdgSurface.HasGeometry)
        {
            pos = new Point(pos.X + xdgSurface.GeometryX, pos.Y + xdgSurface.GeometryY);
        }

        return pos;
    }

    private static uint MapPointerButton(PointerPointProperties properties)
    {
        // Linux input event codes for mouse buttons (BTN_LEFT=0x110, BTN_RIGHT=0x111, BTN_MIDDLE=0x112)
        if (properties.IsLeftButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            || properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased)
            return 0x110; // BTN_LEFT

        if (properties.IsRightButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
            || properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
            return 0x111; // BTN_RIGHT

        if (properties.IsMiddleButtonPressed || properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed
            || properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            return 0x112; // BTN_MIDDLE

        return 0x110; // default to left
    }

    private static uint GetModifiers(KeyModifiers mods)
    {
        uint result = 0;
        if (mods.HasFlag(KeyModifiers.Shift)) result |= 1;    // MOD_SHIFT
        if (mods.HasFlag(KeyModifiers.Control)) result |= 4;  // MOD_CONTROL
        if (mods.HasFlag(KeyModifiers.Alt)) result |= 8;      // MOD_ALT
        if (mods.HasFlag(KeyModifiers.Meta)) result |= 64;    // MOD_LOGO
        return result;
    }

    /// <summary>
    /// Map Avalonia Key to Linux input keycode (evdev).
    /// This is a simplified mapping covering common keys.
    /// </summary>
    private static uint AvaloniaKeyToLinux(Key key) => key switch
    {
        Key.Escape => 1,
        Key.D1 => 2, Key.D2 => 3, Key.D3 => 4, Key.D4 => 5,
        Key.D5 => 6, Key.D6 => 7, Key.D7 => 8, Key.D8 => 9,
        Key.D9 => 10, Key.D0 => 11,
        Key.OemMinus => 12, Key.OemPlus => 13,
        Key.Back => 14,
        Key.Tab => 15,
        Key.Q => 16, Key.W => 17, Key.E => 18, Key.R => 19,
        Key.T => 20, Key.Y => 21, Key.U => 22, Key.I => 23,
        Key.O => 24, Key.P => 25,
        Key.OemOpenBrackets => 26, Key.OemCloseBrackets => 27,
        Key.Return or Key.Enter => 28,
        Key.LeftCtrl => 29,
        Key.A => 30, Key.S => 31, Key.D => 32, Key.F => 33,
        Key.G => 34, Key.H => 35, Key.J => 36, Key.K => 37,
        Key.L => 38,
        Key.OemSemicolon => 39, Key.OemQuotes => 40,
        Key.OemTilde => 41,
        Key.LeftShift => 42,
        Key.OemBackslash => 43,
        Key.Z => 44, Key.X => 45, Key.C => 46, Key.V => 47,
        Key.B => 48, Key.N => 49, Key.M => 50,
        Key.OemComma => 51, Key.OemPeriod => 52,
        Key.Oem2 => 53, // forward slash
        Key.RightShift => 54,
        Key.Multiply => 55,
        Key.LeftAlt => 56,
        Key.Space => 57,
        Key.CapsLock => 58,
        Key.F1 => 59, Key.F2 => 60, Key.F3 => 61, Key.F4 => 62,
        Key.F5 => 63, Key.F6 => 64, Key.F7 => 65, Key.F8 => 66,
        Key.F9 => 67, Key.F10 => 68,
        Key.NumLock => 69, Key.Scroll => 70,
        Key.NumPad7 => 71, Key.NumPad8 => 72, Key.NumPad9 => 73,
        Key.Subtract => 74,
        Key.NumPad4 => 75, Key.NumPad5 => 76, Key.NumPad6 => 77,
        Key.Add => 78,
        Key.NumPad1 => 79, Key.NumPad2 => 80, Key.NumPad3 => 81,
        Key.NumPad0 => 82, Key.Decimal => 83,
        Key.F11 => 87, Key.F12 => 88,
        Key.RightCtrl => 97,
        Key.RightAlt => 100,
        Key.Home => 102, Key.Up => 103, Key.PageUp => 104,
        Key.Left => 105, Key.Right => 106,
        Key.End => 107, Key.Down => 108, Key.PageDown => 109,
        Key.Insert => 110, Key.Delete => 111,
        Key.LWin => 125, Key.RWin => 126,
        _ => 0
    };
}

/// <summary>
/// Event to send xdg_toplevel.close to the client.
/// </summary>
public sealed class ToplevelCloseEvent : InputEvent
{
    private readonly XdgToplevelState _toplevel;
    public ToplevelCloseEvent(XdgToplevelState toplevel) => _toplevel = toplevel;

    public override void Deliver(WaylandCompositor compositor)
    {
        _toplevel.SendClose();
    }
}

/// <summary>
/// Event to send xdg_toplevel.configure + xdg_surface.configure when the host window resizes.
/// </summary>
public sealed class ToplevelConfigureEvent : InputEvent
{
    private readonly XdgToplevelState _toplevel;
    private readonly int _width, _height;
    private readonly bool _activated;

    public ToplevelConfigureEvent(XdgToplevelState toplevel, int width, int height, bool activated = true)
    {
        _toplevel = toplevel;
        _width = width;
        _height = height;
        _activated = activated;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        if (_activated)
        {
            // activated = 4 as uint32 LE
            ReadOnlySpan<byte> states = stackalloc byte[] { 4, 0, 0, 0 };
            _toplevel.SendConfigure(_width, _height, states);
        }
        else
        {
            _toplevel.SendConfigure(_width, _height, ReadOnlySpan<byte>.Empty);
        }
        _toplevel.XdgSurface.SendConfigure(compositor);
    }
}

/// <summary>
/// Event to enter text-input for a surface.
/// </summary>
public sealed class TextInputEnterEvent : InputEvent
{
    private readonly TextInputState _textInput;
    private readonly WlSurface.Server _surface;

    public TextInputEnterEvent(TextInputState textInput, WlSurface.Server surface)
    {
        _textInput = textInput;
        _surface = surface;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _textInput.Enter(_surface);
    }
}

/// <summary>
/// Event to leave text-input for a surface.
/// </summary>
public sealed class TextInputLeaveEvent : InputEvent
{
    private readonly TextInputState _textInput;
    private readonly WlSurface.Server _surface;

    public TextInputLeaveEvent(TextInputState textInput, WlSurface.Server surface)
    {
        _textInput = textInput;
        _surface = surface;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _textInput.Leave(_surface);
    }
}
