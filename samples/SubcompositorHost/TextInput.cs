using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.TextInputUnstableV3;
using NWayland.Server;

namespace SubcompositorHost;

/// <summary>
/// State for a zwp_text_input_v3 instance. Tracks enable/disable and content type.
/// </summary>
public sealed class TextInputState
{
    public ZwpTextInputV3.Server Resource { get; }
    public ClientState ClientState { get; }

    public bool IsEnabled { get; private set; }
    public WlSurface.Server? FocusedSurface { get; private set; }

    // Content type state
    public uint ContentHint { get; private set; }
    public uint ContentPurpose { get; private set; }

    // Cursor rectangle (for IME positioning)
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public int CursorWidth { get; private set; }
    public int CursorHeight { get; private set; }

    // Serial for done events
    private uint _serial;

    public TextInputState(ZwpTextInputV3.Server resource, ClientState clientState)
    {
        Resource = resource;
        ClientState = clientState;
    }

    public void Enable() => IsEnabled = true;
    public void Disable() => IsEnabled = false;

    public void SetContentType(uint hint, uint purpose)
    {
        ContentHint = hint;
        ContentPurpose = purpose;
    }

    public void SetCursorRectangle(int x, int y, int width, int height)
    {
        CursorX = x;
        CursorY = y;
        CursorWidth = width;
        CursorHeight = height;
    }

    /// <summary>
    /// Send enter event when surface gains keyboard focus.
    /// </summary>
    public void Enter(WlSurface.Server surface)
    {
        FocusedSurface = surface;
        Resource.Enter(surface);
        Resource.Done(++_serial);
    }

    /// <summary>
    /// Send leave event when surface loses keyboard focus.
    /// </summary>
    public void Leave(WlSurface.Server surface)
    {
        Resource.Leave(surface);
        Resource.Done(++_serial);
        FocusedSurface = null;
    }

    /// <summary>
    /// Send committed text to the client.
    /// </summary>
    public void CommitString(string text)
    {
        if (!IsEnabled) return;
        Resource.CommitString(text);
        Resource.Done(++_serial);
    }

    /// <summary>
    /// Send preedit (composition) text to the client.
    /// </summary>
    public void PreeditString(string text, int cursorBegin, int cursorEnd)
    {
        if (!IsEnabled) return;
        Resource.PreeditString(text, cursorBegin, cursorEnd);
        Resource.Done(++_serial);
    }
}

/// <summary>
/// Listener for zwp_text_input_manager_v3 requests.
/// </summary>
public sealed class TextInputManagerListener : ZwpTextInputManagerV3.ServerListener
{
    private readonly ClientState _client;
    public TextInputManagerListener(ClientState client) => _client = client;

    protected override void GetTextInput(ZwpTextInputManagerV3.Server resource,
        NewId<ZwpTextInputV3.Server, ZwpTextInputV3.ServerListener> @id, WlSeat.Server? @seat)
    {
        var textInputListener = new TextInputListener();
        var textInput = id.GetAndConsume(textInputListener);
        var state = new TextInputState(textInput, _client);
        textInputListener.Init(state);
        _client.TextInput = state;
    }

    protected override void Destroy(ZwpTextInputManagerV3.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Listener for zwp_text_input_v3 requests.
/// </summary>
public sealed class TextInputListener : ZwpTextInputV3.ServerListener
{
    private TextInputState _state = null!;

    public void Init(TextInputState state) => _state = state;

    protected override void Enable(ZwpTextInputV3.Server resource)
    {
        _state.Enable();
    }

    protected override void Disable(ZwpTextInputV3.Server resource)
    {
        _state.Disable();
    }

    protected override void SetSurroundingText(ZwpTextInputV3.Server resource,
        string @text, int @cursor, int @anchor)
    {
        // Tracked by client, not actively used in this sample compositor
    }

    protected override void SetTextChangeCause(ZwpTextInputV3.Server resource, ZwpTextInputV3.ChangeCauseEnum @cause)
    {
        // Not actively used
    }

    protected override void SetContentType(ZwpTextInputV3.Server resource,
        ZwpTextInputV3.ContentHintEnum @hint, ZwpTextInputV3.ContentPurposeEnum @purpose)
    {
        _state.SetContentType((uint)hint, (uint)purpose);
    }

    protected override void SetCursorRectangle(ZwpTextInputV3.Server resource,
        int @x, int @y, int @width, int @height)
    {
        _state.SetCursorRectangle(x, y, width, height);
    }

    protected override void ShowInputPanel(ZwpTextInputV3.Server resource)
    {
    }

    protected override void HideInputPanel(ZwpTextInputV3.Server resource)
    {
    }

    protected override void Commit(ZwpTextInputV3.Server resource)
    {
        // Client committed its pending state — acknowledged
    }

    protected override void SetAvailableActions(ZwpTextInputV3.Server resource, ReadOnlySpan<byte> @actions)
    {
    }

    protected override void Destroy(ZwpTextInputV3.Server resource)
    {
        _state.ClientState.TextInput = null;
        resource.Dispose();
    }
}

/// <summary>
/// Input event for text committed via Avalonia text input, delivered via text-input-v3.
/// </summary>
public sealed class TextInputCommitEvent : InputEvent
{
    private readonly TextInputState _textInput;
    private readonly string _text;

    public TextInputCommitEvent(TextInputState textInput, string text)
    {
        _textInput = textInput;
        _text = text;
    }

    public override void Deliver(WaylandCompositor compositor)
    {
        _textInput.CommitString(_text);
    }
}
