using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NWayland.Interop;
using NWayland.Server;
using Xunit;

namespace NWayland.Tests;

/// <summary>
/// Tests for <see cref="WaylandOutgoingBuffer"/> boundary logic:
/// FD batching, partial writes, compaction, and coalescing.
/// </summary>
public class OutgoingBufferTests : IDisposable
{
    private readonly WaylandOutgoingBuffer _buf = new();

    private static readonly WlMessageDescription NoArgMethod =
        new WlMessageDescription.Builder("test_noarg").Build();

    private static readonly WlMessageDescription IntArgMethod =
        new WlMessageDescription.Builder("test_int")
            .Add(WlMessageArgumentDescription.UInt32).Build();

    private static readonly WlMessageDescription FdArgMethod =
        new WlMessageDescription.Builder("test_fd")
            .Add(WlMessageArgumentDescription.Fd).Build();

    private static readonly WlMessageDescription TwoFdMethod =
        new WlMessageDescription.Builder("test_2fd")
            .Add(WlMessageArgumentDescription.Fd)
            .Add(WlMessageArgumentDescription.Fd).Build();

    private static readonly WlMessageDescription StringArgMethod =
        new WlMessageDescription.Builder("test_str")
            .Add(WlMessageArgumentDescription.String).Build();

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    public void Dispose()
    {
        _buf.CloseUnsentFds();
    }

    private void WriteNoArgEvent(uint objectId, uint opcode = 0)
    {
        var call = WaylandCallBuilder.Create(null!, opcode);
        _buf.SerializeEvent(objectId, opcode, NoArgMethod, ref call);
        call.Dispose();
    }

    private void WriteUIntEvent(uint objectId, uint value, uint opcode = 0)
    {
        var call = WaylandCallBuilder.Create(null!, opcode);
        call.Arg(value);
        _buf.SerializeEvent(objectId, opcode, IntArgMethod, ref call);
        call.Dispose();
    }

    private void WriteFdEvent(uint objectId, int fd, uint opcode = 0)
    {
        var call = WaylandCallBuilder.Create(null!, opcode);
        call.Arg(fd);
        _buf.SerializeEvent(objectId, opcode, FdArgMethod, ref call);
        call.Dispose();
    }

    private void WriteTwoFdEvent(uint objectId, int fd1, int fd2, uint opcode = 0)
    {
        var call = WaylandCallBuilder.Create(null!, opcode);
        call.Arg(fd1);
        call.Arg(fd2);
        _buf.SerializeEvent(objectId, opcode, TwoFdMethod, ref call);
        call.Dispose();
    }

    private void WriteStringEvent(uint objectId, string s, uint opcode = 0)
    {
        var call = WaylandCallBuilder.Create(null!, opcode);
        call.Arg(s);
        _buf.SerializeEvent(objectId, opcode, StringArgMethod, ref call);
        call.Dispose();
    }

    private static (int read, int write) CreatePipe()
    {
        var fds = new int[2];
        if (pipe(fds) < 0)
            throw new InvalidOperationException("pipe() failed");
        return (fds[0], fds[1]);
    }

    [Fact]
    public void NoArgEvent_WritesHeaderOnly()
    {
        WriteNoArgEvent(42);
        Assert.Equal(8, _buf.BytesUsed);
        Assert.Equal(0, _buf.FdsUsed);
    }

    [Fact]
    public void UIntEvent_WritesHeaderPlusArg()
    {
        WriteUIntEvent(1, 0xDEADBEEF);
        Assert.Equal(12, _buf.BytesUsed);
        Assert.Equal(0, _buf.FdsUsed);
    }

    [Fact]
    public void FdEvent_TracksFdInAncillary()
    {
        var (r, w) = CreatePipe();
        try
        {
            WriteFdEvent(1, r);
            // FD arg contributes to ancillary data only, not wire bytes
            Assert.Equal(8, _buf.BytesUsed);
            Assert.Equal(1, _buf.FdsUsed);
        }
        finally
        {
            close(w);
        }
    }

    [Fact]
    public void StringEvent_PadsTo4ByteAlignment()
    {
        // "hi" = 2 bytes + 1 null = 3, padded to 4
        WriteStringEvent(1, "hi");
        // Header 8 + length prefix 4 + padded string 4 = 16
        Assert.Equal(16, _buf.BytesUsed);
    }

    [Fact]
    public void Clear_ResetsAll()
    {
        WriteNoArgEvent(1);
        WriteNoArgEvent(2);
        _buf.Clear();
        Assert.Equal(0, _buf.BytesUsed);
        Assert.Equal(0, _buf.FdsUsed);
    }

    [Fact]
    public void FlushNoFds_SendsAllInOneSend()
    {
        WriteNoArgEvent(1);
        WriteNoArgEvent(2);
        WriteNoArgEvent(3);

        var mock = new RecordingFlushTarget();
        Assert.True(_buf.TryFlushToSocket(mock));
        Assert.Equal(0, _buf.BytesUsed);
        Assert.Single(mock.Sends);
        Assert.Equal(24, mock.Sends[0].Bytes);
        Assert.Equal(0, mock.Sends[0].Fds);
    }

    [Fact]
    public void FlushWithFds_IncludesFdsInSend()
    {
        var (r, w) = CreatePipe();
        try
        {
            WriteFdEvent(1, r);

            var mock = new RecordingFlushTarget();
            Assert.True(_buf.TryFlushToSocket(mock));
            Assert.Equal(0, _buf.BytesUsed);
            Assert.Single(mock.Sends);
            Assert.Equal(8, mock.Sends[0].Bytes);
            Assert.Equal(1, mock.Sends[0].Fds);
        }
        finally
        {
            close(w);
        }
    }

    [Fact]
    public void Eagain_ReturnsFalseAndRetainsData()
    {
        WriteNoArgEvent(1);
        WriteNoArgEvent(2);

        var mock = new RecordingFlushTarget(eagainAfter: 0);
        Assert.False(_buf.TryFlushToSocket(mock));
        Assert.Equal(16, _buf.BytesUsed);
    }

    [Fact]
    public void PartialWrite_CompactsBytesCorrectly()
    {
        WriteNoArgEvent(1); // 8
        WriteNoArgEvent(2); // 8
        WriteNoArgEvent(3); // 8 (total 24)

        var mock = new RecordingFlushTarget(eagainAfter: 1, partialBytes: 8);
        Assert.False(_buf.TryFlushToSocket(mock));
        Assert.Equal(16, _buf.BytesUsed);
    }

    [Fact]
    public void FdlessEventsCoalesceWithFdEvent()
    {
        var (r, w) = CreatePipe();
        try
        {
            WriteNoArgEvent(1);   // 8 bytes, no FD
            WriteFdEvent(2, r);   // 8 bytes, 1 FD

            var mock = new RecordingFlushTarget();
            Assert.True(_buf.TryFlushToSocket(mock));
            Assert.Single(mock.Sends);
            Assert.Equal(16, mock.Sends[0].Bytes);
            Assert.Equal(1, mock.Sends[0].Fds);
        }
        finally
        {
            close(w);
        }
    }

    [Fact]
    public void MultipleFdEvents_BatchedTogether()
    {
        var (r1, w1) = CreatePipe();
        var (r2, w2) = CreatePipe();
        try
        {
            WriteFdEvent(1, r1);
            WriteFdEvent(2, r2);

            var mock = new RecordingFlushTarget();
            Assert.True(_buf.TryFlushToSocket(mock));
            Assert.Single(mock.Sends);
            Assert.Equal(16, mock.Sends[0].Bytes);
            Assert.Equal(2, mock.Sends[0].Fds);
        }
        finally
        {
            close(w1);
            close(w2);
        }
    }

    [Fact]
    public void MaxFdsPerMessage_SplitsIntoBatches()
    {
        var pipes = new List<(int r, int w)>();
        try
        {
            for (int i = 0; i < 30; i++)
            {
                var p = CreatePipe();
                pipes.Add(p);
                WriteFdEvent((uint)(i + 1), p.read);
            }

            var mock = new RecordingFlushTarget();
            Assert.True(_buf.TryFlushToSocket(mock));
            Assert.True(mock.Sends.Count >= 2,
                $"Expected >= 2 sends for 30 FDs, got {mock.Sends.Count}");
            Assert.True(mock.Sends[0].Fds <= WaylandServerSocket.MaxFdsPerMessage);

            int totalFds = 0;
            foreach (var s in mock.Sends)
                totalFds += s.Fds;
            Assert.Equal(30, totalFds);
        }
        finally
        {
            foreach (var (_, w) in pipes)
                close(w);
        }
    }

    [Fact]
    public void PartialWriteWithFds_CompactsAndRetainsBoundaries()
    {
        var (r1, w1) = CreatePipe();
        var (r2, w2) = CreatePipe();
        try
        {
            WriteFdEvent(1, r1);   // 8 bytes, 1 FD
            WriteNoArgEvent(2);    // 8 bytes
            WriteFdEvent(3, r2);   // 8 bytes, 1 FD

            // Partial: only 4 of 24 bytes, then EAGAIN
            var mock = new RecordingFlushTarget(eagainAfter: 1, partialBytes: 4);
            Assert.False(_buf.TryFlushToSocket(mock));
            Assert.True(_buf.BytesUsed > 0);
        }
        finally
        {
            close(w1);
            close(w2);
        }
    }

    [Fact]
    public void ResumeAfterEagain_FlushesRemainder()
    {
        WriteNoArgEvent(1);
        WriteNoArgEvent(2);
        WriteNoArgEvent(3);

        var mock1 = new RecordingFlushTarget(eagainAfter: 0);
        Assert.False(_buf.TryFlushToSocket(mock1));
        Assert.Equal(24, _buf.BytesUsed);

        var mock2 = new RecordingFlushTarget();
        Assert.True(_buf.TryFlushToSocket(mock2));
        Assert.Equal(0, _buf.BytesUsed);
        Assert.Equal(24, mock2.Sends[0].Bytes);
    }

    [Fact]
    public void TwoFdEvent_BothFdsInSameSend()
    {
        var (r1, w1) = CreatePipe();
        var (r2, w2) = CreatePipe();
        try
        {
            WriteTwoFdEvent(1, r1, r2);

            var mock = new RecordingFlushTarget();
            Assert.True(_buf.TryFlushToSocket(mock));
            Assert.Single(mock.Sends);
            Assert.Equal(2, mock.Sends[0].Fds);
        }
        finally
        {
            close(w1);
            close(w2);
        }
    }

    [Fact]
    public void MixedEvents_FdLessBeforeAndAfterFdEvent()
    {
        var (r, w) = CreatePipe();
        try
        {
            WriteUIntEvent(1, 100);  // 12 bytes
            WriteFdEvent(2, r);      // 8 bytes, 1 FD
            WriteUIntEvent(3, 200);  // 12 bytes

            var mock = new RecordingFlushTarget();
            Assert.True(_buf.TryFlushToSocket(mock));
            Assert.Single(mock.Sends);
            Assert.Equal(32, mock.Sends[0].Bytes);
            Assert.Equal(1, mock.Sends[0].Fds);
        }
        finally
        {
            close(w);
        }
    }

    [Fact]
    public void GrowthBeyondInitialCapacity()
    {
        for (int i = 0; i < 600; i++)
            WriteNoArgEvent((uint)i);

        Assert.Equal(4800, _buf.BytesUsed);

        var mock = new RecordingFlushTarget();
        Assert.True(_buf.TryFlushToSocket(mock));
        Assert.Equal(0, _buf.BytesUsed);
    }

    [Fact]
    public void CloseUnsentFds_ClearsState()
    {
        var (r1, w1) = CreatePipe();
        var (r2, w2) = CreatePipe();

        WriteFdEvent(1, r1);
        WriteFdEvent(2, r2);

        _buf.CloseUnsentFds();
        Assert.Equal(0, _buf.FdsUsed);

        close(w1);
        close(w2);
    }

    [Fact]
    public void PartialWrite_ThenFullFlush_SendsCorrectTotal()
    {
        WriteNoArgEvent(1); // 8
        WriteNoArgEvent(2); // 8
        WriteNoArgEvent(3); // 8

        // Send 8 bytes, then EAGAIN
        var mock1 = new RecordingFlushTarget(eagainAfter: 1, partialBytes: 8);
        Assert.False(_buf.TryFlushToSocket(mock1));
        Assert.Equal(16, _buf.BytesUsed);

        // Now flush the rest
        var mock2 = new RecordingFlushTarget();
        Assert.True(_buf.TryFlushToSocket(mock2));
        Assert.Equal(0, _buf.BytesUsed);

        // Total across both mocks
        int total = mock1.Sends[0].Bytes + mock2.Sends[0].Bytes;
        Assert.Equal(24, total);
    }

    [Fact]
    public void FdBatchSplit_EachBatchHasBytesAndFds()
    {
        var pipes = new List<(int r, int w)>();
        try
        {
            // Write exactly 28 FD events, then 2 more — triggers a batch split
            for (int i = 0; i < 30; i++)
            {
                var p = CreatePipe();
                pipes.Add(p);
                WriteFdEvent((uint)(i + 1), p.read);
            }

            var mock = new RecordingFlushTarget();
            Assert.True(_buf.TryFlushToSocket(mock));

            // Each send must have > 0 bytes (sendmsg requires at least 1 byte with FDs)
            foreach (var s in mock.Sends)
            {
                Assert.True(s.Bytes > 0, "Each sendmsg batch must include at least 1 byte of data");
            }
        }
        finally
        {
            foreach (var (_, w) in pipes)
                close(w);
        }
    }

    /// <summary>
    /// Recording mock implementing <see cref="IFlushTarget"/>.
    /// Simulates EAGAIN and partial writes.
    /// </summary>
    private sealed class RecordingFlushTarget : IWaylandSocketWriter
    {
        private readonly int _eagainAfter;
        private readonly int _partialBytes;
        private int _sendCount;

        public List<SendRecord> Sends { get; } = new();

        /// <param name="eagainAfter">Return -1 (EAGAIN) after this many successful sends. -1 = never.</param>
        /// <param name="partialBytes">If > 0, cap each successful send to this many bytes.</param>
        public RecordingFlushTarget(int eagainAfter = -1, int partialBytes = 0)
        {
            _eagainAfter = eagainAfter;
            _partialBytes = partialBytes;
        }

        public int TryWriteNonBlocking(ReadOnlyMemory<byte> buffer, ReadOnlyMemory<int> fds)
        {
            if (_eagainAfter >= 0 && _sendCount >= _eagainAfter)
                return -1;

            int bytes = buffer.Length;
            if (_partialBytes > 0 && bytes > _partialBytes)
                bytes = _partialBytes;

            Sends.Add(new SendRecord(bytes, fds.Length));
            _sendCount++;
            return bytes;
        }

        public readonly record struct SendRecord(int Bytes, int Fds);
    }
}
