using System.Runtime.InteropServices;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Server;

namespace SubcompositorHost;

/// <summary>
/// Manages a wl_shm_pool backed by mmap'd shared memory.
/// </summary>
public sealed class ShmPoolState : IDisposable
{
    private IntPtr _data;
    private int _size;
    private int _fd;
    private bool _disposed;

    public ShmPoolState(int fd, int size)
    {
        _fd = fd;
        _size = size;
        _data = Mmap(IntPtr.Zero, (nuint)size, 0x01 /* PROT_READ */, 0x01 /* MAP_SHARED */, fd, 0);
        if (_data == new IntPtr(-1))
            throw new InvalidOperationException($"mmap failed: errno {Marshal.GetLastPInvokeError()}");
    }

    public IntPtr Data => _data;
    public int Size => _size;
    public bool IsDisposed => _disposed;

    public void Resize(int newSize)
    {
        if (newSize <= _size) return;

        var newData = Mremap(_data, (nuint)_size, (nuint)newSize, 1 /* MREMAP_MAYMOVE */);
        if (newData == new IntPtr(-1))
            throw new InvalidOperationException($"mremap failed: errno {Marshal.GetLastPInvokeError()}");

        _data = newData;
        _size = newSize;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_data != IntPtr.Zero && _data != new IntPtr(-1))
        {
            Munmap(_data, (nuint)_size);
            _data = IntPtr.Zero;
        }

        if (_fd >= 0)
        {
            Close(_fd);
            _fd = -1;
        }
    }

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static extern IntPtr Mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static extern int Munmap(IntPtr addr, nuint length);

    [DllImport("libc", EntryPoint = "mremap", SetLastError = true)]
    private static extern IntPtr Mremap(IntPtr oldAddr, nuint oldSize, nuint newSize, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}

/// <summary>
/// Represents a wl_buffer backed by a region of a wl_shm_pool.
/// </summary>
public sealed class ShmBufferState
{
    private static readonly Dictionary<WlBuffer.Server, ShmBufferState> _buffers = new();

    public ShmPoolState Pool { get; }
    public int Offset { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public WlShm.FormatEnum Format { get; }
    public WlBuffer.Server Resource { get; }
    private bool _released;

    public ShmBufferState(ShmPoolState pool, WlBuffer.Server resource,
        int offset, int width, int height, int stride, WlShm.FormatEnum format)
    {
        Pool = pool;
        Resource = resource;
        Offset = offset;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        _buffers[resource] = this;
    }

    public IntPtr GetData()
    {
        if (Pool.IsDisposed || Offset + Height * Stride > Pool.Size)
            return IntPtr.Zero;
        return Pool.Data + Offset;
    }

    public void Release()
    {
        if (!_released)
        {
            _released = true;
            try { Resource.Release(); } catch { }
        }
    }

    public void Reattach()
    {
        _released = false;
    }

    public void Destroy()
    {
        _buffers.Remove(Resource);
    }

    public static ShmBufferState? Get(WlBuffer.Server resource)
        => _buffers.GetValueOrDefault(resource);
}

/// <summary>
/// Listener for wl_shm requests.
/// </summary>
public sealed class ShmListener : WlShm.ServerListener
{
    private readonly ClientState _client;
    public ShmListener(ClientState client) => _client = client;

    protected override void CreatePool(WlShm.Server resource, NewId<WlShmPool.Server, WlShmPool.ServerListener> @id, WaylandFd @fd, int @size)
    {
        var poolState = new ShmPoolState(fd.Consume(), size);
        id.GetAndConsume(new ShmPoolListener(poolState, _client));
    }

    protected override void Release(WlShm.Server resource)
    {
        resource.Dispose();
    }
}

/// <summary>
/// Listener for wl_shm_pool requests.
/// </summary>
public sealed class ShmPoolListener : WlShmPool.ServerListener
{
    private readonly ShmPoolState _pool;
    private readonly ClientState _client;

    public ShmPoolListener(ShmPoolState pool, ClientState client)
    {
        _pool = pool;
        _client = client;
    }

    protected override void CreateBuffer(WlShmPool.Server resource, NewId<WlBuffer.Server, WlBuffer.ServerListener> @id,
        int @offset, int @width, int @height, int @stride, WlShm.FormatEnum @format)
    {
        var bufferListener = new BufferListener();
        var bufferServer = id.GetAndConsume(bufferListener);
        var bufferState = new ShmBufferState(_pool, bufferServer, offset, width, height, stride,
            format);
        bufferListener.Init(bufferState);
    }

    protected override void Resize(WlShmPool.Server resource, int @size)
    {
        _pool.Resize(size);
    }

    protected override void Destroy(WlShmPool.Server resource)
    {
        _pool.Dispose();
        resource.Dispose();
    }
}

/// <summary>
/// Listener for wl_buffer requests.
/// </summary>
public sealed class BufferListener : WlBuffer.ServerListener
{
    private ShmBufferState _state = null!;

    public void Init(ShmBufferState state) => _state = state;

    protected override void Destroy(WlBuffer.Server resource)
    {
        _state.Destroy();
        resource.Dispose();
    }
}
