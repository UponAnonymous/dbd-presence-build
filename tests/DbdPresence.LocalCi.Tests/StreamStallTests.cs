// Covers: QA M5 (a read that waits for ever on a dead connection is given up after a limit; the limit is on the time between pieces of data, not on the whole answer; the caller's own cancel stays a
// cancel and is not mistaken for a stall). docs/owner/free-ci.md.

namespace DbdPresence.LocalCi.Tests;

public sealed class StreamStallTests
{
    private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(60);

    /// <summary>A stream whose next read completes only when the test says so, or when the token is cancelled.</summary>
    private sealed class HandOperatedStream : Stream
    {
        private readonly TaskCompletionSource<int> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Deliver(int bytes) => _next.TrySetResult(bytes);

        public override void Flush()
        {
            // A read-only stream has nothing to flush.
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await _next.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AReadThatCompletesInTime_ReturnsItsBytes()
    {
        var time = TestTime.New();
        var stream = new HandOperatedStream();
        var reading = StreamStall.ReadAsync(stream, new byte[10], s_limit, time, CancellationToken.None).AsTask();

        time.Advance(TimeSpan.FromSeconds(59));
        stream.Deliver(7);

        Assert.Equal(7, await reading.OrFail());
    }

    [Fact]
    public async Task AReadThatWaitsLongerThanTheLimit_IsAStall()
    {
        var time = TestTime.New();
        var stream = new HandOperatedStream();
        var reading = StreamStall.ReadAsync(stream, new byte[10], s_limit, time, CancellationToken.None).AsTask();

        time.Advance(s_limit);

        var ex = await Assert.ThrowsAsync<StreamStalledException>(() => reading.OrFail());
        Assert.Contains("60 seconds", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Fact]
    public async Task TheCallersOwnCancel_StaysACancel_NotAStall()
    {
        var time = TestTime.New();
        var stream = new HandOperatedStream();
        using var cts = new CancellationTokenSource();
        var reading = StreamStall.ReadAsync(stream, new byte[10], s_limit, time, cts.Token).AsTask();

        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => reading.OrFail());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<StreamStalledException>(ex);
    }

    [Fact]
    public async Task EachReadGetsItsOwnLimit_SoALongAnswerThatKeepsComingIsNeverCutOff()
    {
        var time = TestTime.New();
        var first = new HandOperatedStream();
        var firstRead = StreamStall.ReadAsync(first, new byte[10], s_limit, time, CancellationToken.None).AsTask();
        time.Advance(TimeSpan.FromSeconds(50));
        first.Deliver(3);
        Assert.Equal(3, await firstRead.OrFail());

        var second = new HandOperatedStream();
        var secondRead = StreamStall.ReadAsync(second, new byte[10], s_limit, time, CancellationToken.None).AsTask();
        time.Advance(TimeSpan.FromSeconds(50));
        second.Deliver(4);

        Assert.Equal(4, await secondRead.OrFail());
    }

    [Fact]
    public async Task BadArguments_AreRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await StreamStall.ReadAsync(null!, new byte[1], s_limit, TestTime.New(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await StreamStall.ReadAsync(Stream.Null, new byte[1], s_limit, null!, CancellationToken.None));
    }
}
