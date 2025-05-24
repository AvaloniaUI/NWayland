using System;
using NWayland.Interop;
using NWayland.Protocols.Wayland;
namespace NWayland;

public class WlEventQueue : IDisposable
{
    private IntPtr _handle;
    private readonly WlDisplay _display;
    private bool _disposed;

    internal IntPtr Handle => _disposed ? throw new ObjectDisposedException(nameof(WlEventQueue)) : _handle;
    
    internal WlEventQueue(WlDisplay display)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        
        _handle = LibWayland.wl_display_create_queue(display.Handle);
        if (_handle == IntPtr.Zero)
            throw new NWaylandException("Failed to create Wayland event queue");
    }

    /// <summary>
    /// Dispatch events in the given event queue.
    /// </summary>
    /// <returns>The number of dispatched events on success or -1 on failure.</returns>
    /// <remarks>
    /// If the given event queue is empty, this function blocks until there are
    /// events to be read from the display file descriptor. Events are read and queued on
    /// the appropriate event queues. Finally, events on the given event queue are
    /// dispatched. On failure, -1 is returned and <c>errno</c> is set appropriately.
    ///
    /// In a multi-threaded environment, do not manually wait using <c>poll()</c> (or
    /// equivalent) before calling this function, as doing so might cause a deadlock.
    /// If external reliance on <c>poll()</c> (or equivalent) is required, see
    /// <see cref="PrepareRead"/> for how to do so.
    ///
    /// This function is thread safe as long as it dispatches the right queue on the
    /// right thread. It is also compatible with the multi-thread event reading
    /// preparation API (see <see cref="PrepareRead"/>), and uses the
    /// equivalent functionality internally. It is not allowed to call this function
    /// while the thread is being prepared for reading events, and doing so will
    /// cause a deadlock.
    ///
    /// It can be used as a helper function to ease the procedure of reading and
    /// dispatching events.
    ///
    /// Since Wayland 1.5, the display has an extra queue for its own events (e.g., <c>delete_id</c>).
    /// This queue is dispatched always, no matter what queue is passed as an argument to this function.
    /// That means this function can return a non-zero value even when it hasn't dispatched any event for the given queue.
    /// </remarks>
    /// <seealso cref="WlDisplay.Dispatch"/>
    /// <seealso cref="WlDisplay.DispatchPending"/>
    /// <seealso cref="WlEventQueue.DispatchPending"/>
    /// <seealso cref="PrepareRead"/>
    public int DispatchPending() => LibWayland.wl_display_dispatch_queue_pending(_display.Handle, Handle);

    /// <summary>
    /// Dispatch events in an event queue
    ///
    /// Dispatch events on the given event queue.
    ///
    /// If the given event queue is empty, this function blocks until there are
    /// events to be read from the display fd. Events are read and queued on
    /// the appropriate event queues. Finally, events on given event queue are
    /// dispatched. On failure -1 is returned and errno set appropriately.
    /// </summary>
    /// <returns>The number of dispatched events on success or -1 on failure</returns>
    public int Dispatch() => LibWayland.wl_display_dispatch_queue(_display.Handle, Handle);

    /// <summary>
    /// Prepare to read events from the display's file descriptor to this queue.
    /// </summary>
    /// <returns>0 on success or -1 if event queue was not empty</returns>
    /// <remarks>
    /// This function must be called before reading from the file descriptor using 
    /// <see cref="WlDisplay.ReadEvents"/>. Calling this method announces the calling thread's intention 
    /// to read and ensures that until the thread is ready to read and calls <see cref="WlDisplay.ReadEvents"/>, 
    /// no other thread will read from the file descriptor. This only succeeds if the event queue is empty, 
    /// and if not -1 is returned and errno set to EAGAIN.
    /// 
    /// If a thread successfully calls this method, it must either call <see cref="WlDisplay.ReadEvents"/> 
    /// when it's ready or cancel the read intention by calling <see cref="WlDisplay.CancelRead"/>.
    /// 
    /// Use this function before polling on the display fd or integrate the fd into a toolkit event 
    /// loop in a race-free way. A correct usage would be (with most error checking left out):
    /// 
    /// <code>
    /// while (queue.PrepareRead() != 0)
    ///     queue.DispatchPending();
    /// display.Flush();
    /// 
    /// ret = Poll(fds, nfds, -1);
    /// if (HasError(ret))
    ///     display.CancelRead();
    /// else
    ///     display.ReadEvents();
    /// 
    /// queue.DispatchPending();
    /// </code>
    /// 
    /// This method doesn't acquire exclusive access to the display's fd. It only registers that 
    /// the thread calling this function has intention to read from fd. When all registered readers call
    /// <see cref="WlDisplay.ReadEvents"/>, only one (at random) eventually reads and queues the events 
    /// and the others are sleeping meanwhile. This way we avoid races and still can read from more threads.
    /// </remarks>
    public int PrepareRead() => LibWayland.wl_display_prepare_read_queue(_display.Handle, Handle);

    /// <summary>
    /// Block until all pending requests are processed by the server on the specified event queue.
    /// </summary>
    /// <returns>The number of dispatched events on success or -1 on failure.</returns>
    /// <remarks>
    /// This function blocks until the server has processed all currently issued
    /// requests by sending a request to the display server and waiting for a
    /// reply before returning.
    ///
    /// This function uses <see cref="WlEventQueue.Dispatch"/> internally. It is not allowed
    /// to call this function while the thread is being prepared for reading events,
    /// and doing so will cause a deadlock.
    ///
    /// <note>This function may dispatch other events being received on the given queue.</note>
    /// </remarks>
    /// <seealso cref="WlDisplay.Roundtrip"/>
    public int Roundtrip() => LibWayland.wl_display_roundtrip_queue(_display.Handle, Handle);

    /// <summary>
    /// Destroy the given event queue. Any pending event on that queue is discarded.
    /// </summary>
    /// <remarks>
    /// The <seealso cref="WlDisplay"/> object used to create the queue should not be destroyed
    /// until all event queues created with it are destroyed with this function.
    /// </remarks>
    public void Dispose()
    {
        lock (_display.SyncRoot)
        {
            if (!_disposed)
            {
                if (_handle != IntPtr.Zero)
                {
                    LibWayland.wl_event_queue_destroy(_handle);
                    _handle = IntPtr.Zero;
                }

                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }

    ~WlEventQueue()
    {
        Dispose();
    }
}
