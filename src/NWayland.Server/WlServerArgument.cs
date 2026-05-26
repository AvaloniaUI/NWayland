namespace NWayland.Server;

internal enum WlServerArgumentType : byte
{
    Int32,
    UInt32,
    Fixed,
    Fd,
    Object,
    NewId,
    String,
    Array,
    UntypedNewId,
}

internal struct WlServerArgument
{
    public WlServerArgumentType Type;
    public int Int;
    public object? Object;
}
