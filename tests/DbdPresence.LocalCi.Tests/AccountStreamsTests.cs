// Covers: red-team F5 and F2 (a started program's input is copied in and closed; its output is read in bounded pieces as bytes or as lines with secrets removed; a failure of the program's own end of a
// pipe is the normal end, a failure on the other side is real and is raised; the pumps of one program stop together and keep the first failure; a capped buffer refuses more than it is allowed; the Job
// Object limits are the documented numbers). docs/owner/free-ci.md.
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class AccountStreamsTests
{
    /// <summary>A stream that records what happens to it and can be made to fail on a read, a write or both.</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly MemoryStream _data;

        public ScriptedStream(byte[]? readable = null)
        {
            _data = new MemoryStream(readable ?? []);
        }

        public Exception? ReadFailure { get; set; }

        public Exception? WriteFailure { get; set; }

        public MemoryStream Written { get; } = new();

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            // Nothing is buffered.
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadFailure is null ? _data.Read(buffer, offset, count) : throw ReadFailure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ReadFailure is null ? new ValueTask<int>(_data.Read(buffer.Span)) : ValueTask.FromException<int>(ReadFailure);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Written.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (WriteFailure is not null)
            {
                return ValueTask.FromException(WriteFailure);
            }

            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // ---- input

    [Fact]
    public async Task TheInput_IsCopiedWholeToThePipe_ThenThePipeIsClosed()
    {
        var pipe = new ScriptedStream();
        var input = new MemoryStream(Enumerable.Range(0, 250_000).Select(i => (byte)(i % 251)).ToArray());

        await AccountStreams.PumpInputAsync(pipe, input, TestContext.Current.CancellationToken);

        Assert.Equal(input.ToArray(), pipe.Written.ToArray());
        Assert.True(pipe.Disposed);
    }

    [Fact]
    public async Task WithNoInput_ThePipeIsStillClosed_SoTheProgramSeesTheEndOfItsInput()
    {
        var pipe = new ScriptedStream();

        await AccountStreams.PumpInputAsync(pipe, null, TestContext.Current.CancellationToken);

        Assert.True(pipe.Disposed);
        Assert.Equal(0, pipe.Written.Length);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    public async Task AProgramThatEndedBeforeItReadAllItsInput_IsNotAFailureOfThePump(string kind)
    {
        var pipe = new ScriptedStream { WriteFailure = kind == "io" ? new IOException("The pipe is broken.") : new ObjectDisposedException("pipe") };

        var ex = await Record.ExceptionAsync(() => AccountStreams.PumpInputAsync(pipe, new MemoryStream([1, 2, 3]), TestContext.Current.CancellationToken));

        Assert.Null(ex);
        Assert.True(pipe.Disposed);
    }

    /// <summary>A truncated input must never pass for a whole one: failing to READ the input is raised.</summary>
    [Fact]
    public async Task AnInputThatCannotBeRead_IsRaised_NotTreatedAsTheEnd()
    {
        var pipe = new ScriptedStream();
        var input = new ScriptedStream { ReadFailure = new IOException("The disk reported an error.") };

        var ex = await Assert.ThrowsAsync<IOException>(() => AccountStreams.PumpInputAsync(pipe, input, TestContext.Current.CancellationToken));

        Assert.Contains("disk", ex.Message, StringComparison.Ordinal);
        Assert.True(pipe.Disposed);
    }

    [Fact]
    public async Task ACancelledInputPump_StillClosesThePipe()
    {
        var pipe = new ScriptedStream();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AccountStreams.PumpInputAsync(pipe, new MemoryStream([1]), cts.Token));

        Assert.True(pipe.Disposed);
    }

    // ---- output

    [Fact]
    public async Task RawOutput_IsCopiedToTheSink_Whole()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 253)).ToArray();
        var sink = new MemoryStream();

        await AccountStreams.PumpOutputAsync(new MemoryStream(bytes), sink, Encoding.UTF8, null, new Redactor(), TestContext.Current.CancellationToken);

        Assert.Equal(bytes, sink.ToArray());
    }

    /// <summary>A sink that fails (the disk is full while an export is written) must stop the command: the failure is raised, not swallowed as an end of output.</summary>
    [Fact]
    public async Task ASinkThatCannotBeWritten_IsRaised()
    {
        var sink = new ScriptedStream { WriteFailure = new IOException("There is not enough space on the disk.") };

        var ex = await Assert.ThrowsAsync<IOException>(() => AccountStreams.PumpOutputAsync(new MemoryStream([1, 2, 3]), sink, Encoding.UTF8, null, new Redactor(), TestContext.Current.CancellationToken));

        Assert.Contains("space", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    public async Task APipeThatIsClosedUnderTheReader_IsTheEndOfTheOutput_ForBytesAndForLines(string kind)
    {
        Exception Failure() => kind == "io" ? new IOException("The pipe has been ended.") : new ObjectDisposedException("pipe");
        var lines = new List<string>();

        var raw = await Record.ExceptionAsync(() => AccountStreams.PumpOutputAsync(new ScriptedStream { ReadFailure = Failure() }, new MemoryStream(), Encoding.UTF8, null, new Redactor(), TestContext.Current.CancellationToken));
        var text = await Record.ExceptionAsync(() => AccountStreams.PumpOutputAsync(new ScriptedStream { ReadFailure = Failure() }, null, Encoding.UTF8, lines.Add, new Redactor(), TestContext.Current.CancellationToken));

        Assert.Null(raw);
        Assert.Null(text);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task OutputLines_AreBounded_AndHaveRegisteredSecretsRemoved()
    {
        var redactor = new Redactor();
        redactor.Register("JIT-VALUE-0123456789");
        var lines = new List<string>();
        var text = "using JIT-VALUE-0123456789 now\r\n" + new string('q', 9000) + "\r\nend";

        await AccountStreams.PumpOutputAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), null, Encoding.UTF8, lines.Add, redactor, TestContext.Current.CancellationToken);

        Assert.Equal(3, lines.Count);
        Assert.Equal("using [hidden] now", lines[0]);
        Assert.Contains("more characters were not kept", lines[1], StringComparison.Ordinal);
        Assert.True(lines[1].Length < 4200);
        Assert.Equal("end", lines[2]);
    }

    [Fact]
    public async Task OutputLines_CanBeIgnored_WhenNobodyListens()
    {
        var ex = await Record.ExceptionAsync(() => AccountStreams.PumpOutputAsync(new MemoryStream(Encoding.UTF8.GetBytes("a\nb\n")), null, Encoding.UTF8, null, new Redactor(), TestContext.Current.CancellationToken));

        Assert.Null(ex);
    }

    // ---- the set of pumps of one program

    [Fact]
    public async Task APumpThatFails_CancelsTheOthers_AndItsFailureIsKept()
    {
        using var pumps = new PumpSet(CancellationToken.None);
        var otherStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pumps.Start(async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                otherStopped.SetResult();
                throw;
            }
        });
        pumps.Start(_ => throw new IOException("the export file cannot be written"));

        await otherStopped.Task.OrFail();
        Assert.True(await pumps.WhenAllAsync(TimeSpan.FromSeconds(30), TimeProvider.System).OrFail());

        Assert.True(pumps.Token.IsCancellationRequested);
        var failure = Assert.IsType<IOException>(pumps.Failure);
        Assert.Contains("export file", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelFromTheCaller_StopsThePumps_WithoutCountingAsAFailure()
    {
        using var cts = new CancellationTokenSource();
        using var pumps = new PumpSet(cts.Token);
        pumps.Start(token => Task.Delay(Timeout.Infinite, token));

        await cts.CancelAsync();
        await pumps.WhenAllAsync(TimeSpan.FromSeconds(30), TimeProvider.System).OrFail();

        Assert.Null(pumps.Failure);
        Assert.True(pumps.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task OnlyTheFirstFailureIsKept()
    {
        using var pumps = new PumpSet(CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pumps.Start(_ => throw new IOException("first"));
        pumps.Start(async _ =>
        {
            await gate.Task;
            throw new IOException("second");
        });
        await Task.Run(
            async () =>
            {
                while (pumps.Failure is null)
                {
                    await Task.Yield();
                }
            },
            TestContext.Current.CancellationToken).OrFail();
        gate.SetResult();
        await pumps.WhenAllAsync(TimeSpan.FromSeconds(30), TimeProvider.System).OrFail();

        Assert.Equal("first", pumps.Failure!.Message);
    }

    [Fact]
    public async Task WaitingForThePumps_EndsAtTheLimit_WhenOneIsStillBlocked()
    {
        var time = new FakeTimeProvider(TestTime.New().GetUtcNow());
        using var pumps = new PumpSet(CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pumps.Start(async _ => await release.Task);

        var waiting = pumps.WhenAllAsync(TimeSpan.FromSeconds(10), time);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(await waiting.OrFail());
        release.SetResult();
        Assert.True(await pumps.WhenAllAsync(TimeSpan.FromSeconds(10), time).OrFail());
    }

    [Fact]
    public async Task WithNoPumps_WaitingEndsAtOnce()
    {
        using var pumps = new PumpSet(CancellationToken.None);

        Assert.True(await pumps.WhenAllAsync(TimeSpan.FromSeconds(10), TimeProvider.System).OrFail());
    }

    // ---- the capped buffer

    [Fact]
    public void ACappedBuffer_KeepsUpToItsCapacity_AndRefusesMore()
    {
        using var buffer = new CappedMemoryStream(5);

        buffer.Write([1, 2, 3], 0, 3);
        buffer.Write([4, 5]);

        Assert.Equal([1, 2, 3, 4, 5], buffer.ToArray());
        Assert.Equal(5, buffer.Length);
        var ex = Assert.Throws<IOException>(() => buffer.Write([6]));
        Assert.Contains("more than 5 bytes", ex.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4, 5], buffer.ToArray());
    }

    [Fact]
    public async Task ACappedBuffer_RefusesAWriteThatWouldPassTheCap_InOneStep_AsyncToo()
    {
        await using var buffer = new CappedMemoryStream(4);

        await buffer.WriteAsync(new byte[4], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(async () => await buffer.WriteAsync(new byte[1], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => buffer.WriteAsync(new byte[1], 0, 1, TestContext.Current.CancellationToken));
        Assert.False(buffer.CanRead);
        Assert.False(buffer.CanSeek);
        Assert.True(buffer.CanWrite);
        Assert.Throws<NotSupportedException>(() => buffer.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => buffer.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => buffer.SetLength(0));
    }

    [Fact]
    public void ACappedBufferNeedsARoomyCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CappedMemoryStream(0));
    }

    // ---- the line collector

    [Fact]
    public void TheCollector_KeepsTheLastLines_AndTheirTextInOrder()
    {
        var lines = new LineCollector(3);

        foreach (var line in new[] { "one", "two", "three", "four", "five" })
        {
            lines.Add(line);
        }

        Assert.Equal(["three", "four", "five"], lines.Lines);
        Assert.Equal("three\nfour\nfive", lines.Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineCollector(0));
    }

    [Fact]
    public async Task TheCollector_CanBeFilledFromManyThreads()
    {
        var lines = new LineCollector(1000);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(
            () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    lines.Add($"{t}-{i}");
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(800, lines.Lines.Count);
        Assert.Equal(800, lines.Lines.Distinct().Count());
    }
}
