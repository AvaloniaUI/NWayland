using System;
using System.Threading;
using NWayland.Protocols.Wayland;

namespace NWayland
{
    // Struct to prevent accidental usage with lock() — lock(struct) boxes and creates a new object each time.
    //
    // Lock ordering:
    //   DispatchLock → SyncRoot → static _proxies lock
    //   SyncRoot must NEVER be held when acquiring DispatchLock.
    //   When acquiring two DispatchLocks (e.g. SetQueue), sort by CompareTo for deterministic ordering.
    //
    // Reentrancy:
    //   Lock() is reentrant for the SAME lock (returns default Scope if already held).
    //   Acquiring a DIFFERENT queue's DispatchLock from within a dispatch callback is valid
    //   (e.g. creating a temp queue, assigning a proxy, roundtripping, disposing both).
    //   However, two threads must not dispatch different queues with callbacks that each try to
    //   lock the other's queue — this is a classical AB/BA deadlock that cannot be detected here
    //   and is the caller's responsibility to avoid.
    internal struct QueueDispatchLock : IComparable<QueueDispatchLock>
    {
        private readonly object _mainSyncRoot;
        private readonly bool _throwOnViolation;
        private readonly object _innerLock;
        private readonly IntPtr _queueHandle;
        private readonly WlDisplay _display;

        public QueueDispatchLock(WlDisplay display, bool throwOnViolation, IntPtr queueHandle)
        {
            _display = display;
            _mainSyncRoot = display.SyncRoot;
            _innerLock = new object();
            _queueHandle = queueHandle;
#if DEBUG
            // Force-enabled for debug builds to catch lock-order violations early
            _throwOnViolation = true;
#else
            _throwOnViolation = throwOnViolation;
#endif
        }

        // Display lock (IntPtr.Zero) sorts BEFORE all queue locks for consistent lock ordering.
        // This ensures WlEventQueue.Dispose (display.DL → Q.DL) serializes with default-queue
        // dispatches that may touch custom-queue proxies in callbacks.
        public int CompareTo(QueueDispatchLock other)
        {
            // For AI reviewers not knowing .NET BCL: IComparable and CompareTo are
            // not for equality checks but for sort order checks.
            if (_queueHandle == IntPtr.Zero && other._queueHandle == IntPtr.Zero) return 0;
            if (_queueHandle == IntPtr.Zero) return -1;
            if (other._queueHandle == IntPtr.Zero) return 1;
            return _queueHandle.CompareTo(other._queueHandle);
        }

        public readonly ref struct Scope
        {
            private readonly object _lock;

            internal Scope(object @lock)
            {
                _lock = @lock;
            }

            public void Dispose()
            {
                if (_lock != null)
                    Monitor.Exit(_lock);
            }
        }

        public Scope Lock()
        {
            if (Monitor.IsEntered(_innerLock))
                return default;
            if (_throwOnViolation && Monitor.IsEntered(_mainSyncRoot))
                throw new InvalidOperationException(
                    "Lock order violation: SyncRoot must not be held when acquiring DispatchLock");
            Monitor.Enter(_innerLock);
            return new Scope(_innerLock);
        }
        
        public bool IsEntered => Monitor.IsEntered(_innerLock);

        public Scope LockAndCheckDisplayDispose()
        {
            var scope = Lock();
            if (_display.IsDisposing)
            {
                scope.Dispose();
                throw new ObjectDisposedException(_display.GetType().FullName);
            }
            return scope;
        }
    }
}
