// Covers: red-team F5 (a program's output is read as bytes in bounded pieces: at most 4 KB of a line is kept, the rest is counted, memory does not grow with the size of the output, lines end at
// line feed, carriage return or both, UTF-8 and UTF-16 text are decoded across any split of the bytes), FR-CI-06 (the log never holds more than a bounded line). docs/owner/free-ci.md.
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class BoundedLineReaderTests
{
    /// <summary>Hands out the bytes a few at a time, so every split of a line ending, a multi-byte character or a surrogate pair is met.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _piece;
        private int _position;

        public TrickleStream(byte[] bytes, int piece)
        {
            _bytes = bytes;
            _piece = piece;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _bytes.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
            // A read-only stream has nothing to flush.
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(_piece, count), _bytes.Length - _position);
            Array.Copy(_bytes, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream of the same byte, a fixed number of times, made on the fly (nothing of it is ever held).</summary>
    private sealed class RepeatingStream : Stream
    {
        private readonly byte _value;
        private long _left;

        public RepeatingStream(byte value, long count)
        {
            _value = value;
            _left = count;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            // A read-only stream has nothing to flush.
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, _left);
            Array.Fill(buffer, _value, offset, take);
            _left -= take;
            return take;
        }

        // Completes at once on the calling thread, so the whole read runs on one thread and its allocations can be counted for that thread alone (other tests run at the same time).
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = (int)Math.Min(buffer.Length, _left);
            buffer.Span[..take].Fill(_value);
            _left -= take;
            return new ValueTask<int>(take);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ReadAsync(Stream stream, Encoding? encoding = null, int maximum = BoundedLineReader.DefaultMaximumLineLength)
    {
        var lines = new List<string>();
        await BoundedLineReader.ReadAsync(stream, encoding ?? Encoding.UTF8, lines.Add, maximum, TestContext.Current.CancellationToken);
        return lines;
    }

    [Theory]
    [InlineData("a\nb\nc\n", new[] { "a", "b", "c" })]
    [InlineData("a\r\nb\r\nc\r\n", new[] { "a", "b", "c" })]
    [InlineData("a\rb\rc\r", new[] { "a", "b", "c" })]
    [InlineData("a\nb\r\nc\rd", new[] { "a", "b", "c", "d" })]
    [InlineData("no ending", new[] { "no ending" })]
    [InlineData("a\n\nb", new[] { "a", "", "b" })]
    [InlineData("\n", new[] { "" })]
    [InlineData("", new string[0])]
    [InlineData("a\r\n\r\nb", new[] { "a", "", "b" })]
    public async Task LinesEndAtLineFeedCarriageReturnOrBoth_AndTheLastLineNeedNotEnd(string text, string[] expected)
    {
        Assert.Equal(expected, await ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text))));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task TheSameLinesComeOut_WhateverWayTheBytesArriveInPieces(int piece)
    {
        const string Text = "first\r\nsecond\nthird\rfourth é ü 日本語 😀\r\n\r\nlast";
        var bytes = Encoding.UTF8.GetBytes(Text);

        var lines = await ReadAsync(new TrickleStream(bytes, piece));

        Assert.Equal(["first", "second", "third", "fourth é ü 日本語 😀", string.Empty, "last"], lines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Utf16Text_LikeWslExesOwnMessages_IsDecodedAcrossAnySplit(int piece)
    {
        var bytes = Encoding.Unicode.GetBytes("Ubuntu\r\ndbd-ci-job-20261029120000\r\n😀 end\r\n");

        var lines = await ReadAsync(new TrickleStream(bytes, piece), Encoding.Unicode);

        Assert.Equal(["Ubuntu", "dbd-ci-job-20261029120000", "😀 end"], lines);
    }

    [Fact]
    public async Task ALineLongerThanTheLimit_IsCutThere_AndSaysHowManyCharactersWereNotKept()
    {
        var text = new string('x', 10_000) + "\nshort\n";

        var lines = await ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)));

        Assert.Equal(2, lines.Count);
        Assert.StartsWith(new string('x', 4096), lines[0], StringComparison.Ordinal);
        Assert.Equal(new string('x', 4096) + " [5904 more characters were not kept]", lines[0]);
        Assert.Equal("short", lines[1]);
    }

    [Theory]
    [InlineData(4095, false)]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    public async Task TheLimitIsExact_AtItNothingIsDropped_OneOverItIsCounted(int length, bool dropped)
    {
        var lines = await ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(new string('y', length) + "\n")));

        var line = Assert.Single(lines);
        Assert.Equal(dropped, line.Contains("more characters were not kept", StringComparison.Ordinal));
        if (dropped)
        {
            Assert.EndsWith(" [1 more characters were not kept]", line, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(length, line.Length);
        }
    }

    [Fact]
    public async Task AnyOtherLimitWorks_AndTheNoteIsPerLine()
    {
        var lines = await ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes("abcdefgh\nabc\n")), maximum: 5);

        Assert.Equal(["abcde [3 more characters were not kept]", "abc"], lines);
    }

    /// <summary>The finding: 300 MB without a line break made the loop use gigabytes. 64 MB here is enough to show that nothing grows with the size.</summary>
    [Fact]
    public async Task AHugeLineWithoutABreak_IsKeptToTheLimit_AndItsSizeIsNotHeld()
    {
        const long Size = 64L * 1024 * 1024;
        var before = GC.GetAllocatedBytesForCurrentThread();

        var lines = await ReadAsync(new RepeatingStream((byte)'z', Size));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var line = Assert.Single(lines);
        Assert.StartsWith(new string('z', 4096), line, StringComparison.Ordinal);
        Assert.EndsWith($" [{Size - 4096} more characters were not kept]", line, StringComparison.Ordinal);
        Assert.True(allocated < 8L * 1024 * 1024, $"reading 64 MB allocated {allocated} bytes; the reader must not grow with the size of the output");
    }

    [Fact]
    public async Task ANulCharacterInTheTextIsKept_ItIsTheCallersJobToCleanIt()
    {
        var lines = await ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes("a\0b\n")));

        Assert.Equal(["a\0b"], lines);
    }

    [Fact]
    public async Task ACancelledToken_StopsTheReading()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedLineReader.ReadAsync(new RepeatingStream((byte)'a', 100), Encoding.UTF8, _ => { }, 10, cts.Token));
    }

    [Fact]
    public async Task BadArguments_AreRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => BoundedLineReader.ReadAsync(null!, Encoding.UTF8, _ => { }, 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => BoundedLineReader.ReadAsync(Stream.Null, null!, _ => { }, 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => BoundedLineReader.ReadAsync(Stream.Null, Encoding.UTF8, null!, 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedLineReader.ReadAsync(Stream.Null, Encoding.UTF8, _ => { }, 0, TestContext.Current.CancellationToken));
    }
}
