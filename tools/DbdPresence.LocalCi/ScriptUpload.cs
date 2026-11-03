using System.Globalization;
using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// Gets a bash script into a Linux machine through standard input (local CI round 2, red-team F2). <c>CreateProcessWithLogonW</c> accepts a command line of fewer than 1,024 characters, which the
/// setup and isolation scripts exceed, so the command line only carries a short loader. The loader reads the script's byte count from the first line of standard input, reads exactly that many
/// bytes as the script and runs it with the rest of standard input still unread: the script itself then reads its data (the runner package, the runner's configuration) from the same stream.
/// </summary>
public static class ScriptUpload
{
    /// <summary>The longest script this accepts (the real ones are a few KB).</summary>
    public const int MaximumScriptBytes = 64 * 1024;

    /// <summary>
    /// The loader, one argument of <c>bash -c</c>. <c>read</c> takes the count without reading further (it reads a pipe one byte at a time up to the line feed), GNU <c>head -c</c> reads exactly that
    /// many bytes from a pipe and no more, and <c>exec</c> hands the same standard input to the script's own shell.
    /// </summary>
    public const string Loader = "IFS= read -r n && s=$(head -c \"$n\") && exec bash -c \"$s\"";

    /// <summary>The standard input for the loader: the count, a line feed, the script, then everything <paramref name="data"/> has (nothing when it is null). <paramref name="data"/> is read, never disposed.</summary>
    public static Stream Frame(string script, Stream? data)
    {
        ArgumentException.ThrowIfNullOrEmpty(script);
        var bytes = Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (bytes.Length > MaximumScriptBytes)
        {
            throw new ArgumentException($"The script is {bytes.Length} bytes; at most {MaximumScriptBytes} are accepted.", nameof(script));
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            throw new ArgumentException("A script cannot hold a NUL character.", nameof(script));
        }

        var count = Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n");
        var head = new MemoryStream([.. count, .. bytes], writable: false);
        return data is null ? head : new ConcatenatedStream(head, data);
    }
}

/// <summary>Two streams read one after the other as one. It does not dispose either of them.</summary>
public sealed class ConcatenatedStream : Stream
{
    private readonly Stream _first;
    private readonly Stream _second;
    private bool _firstDone;

    public ConcatenatedStream(Stream first, Stream second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        _first = first;
        _second = second;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (!_firstDone)
        {
            var read = _first.Read(buffer);
            if (read > 0)
            {
                return read;
            }

            _firstDone = true;
        }

        return _second.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (!_firstDone)
        {
            var read = await _first.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                return read;
            }

            _firstDone = true;
        }

        return await _second.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
        // A read-only stream has nothing to flush.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
