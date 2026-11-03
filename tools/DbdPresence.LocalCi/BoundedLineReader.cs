using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// Reads the text lines of a byte stream (what a started program prints) without ever holding more than a fixed number of characters of one line (local CI round 2, red-team F5). The
/// ordinary line readers of the framework buffer a whole line, so a program that prints 300 MB without a line break made the loop use gigabytes; this one decodes the stream in small
/// pieces, keeps the first <see cref="DefaultMaximumLineLength"/> characters of every line, counts the rest and says how many were dropped.
/// </summary>
public static class BoundedLineReader
{
    /// <summary>Characters kept of one line (about 4 KB for ordinary text).</summary>
    public const int DefaultMaximumLineLength = 4096;

    private const int PieceBytes = 16 * 1024;

    /// <summary>
    /// Reads <paramref name="stream"/> to its end and hands each line to <paramref name="onLine"/>. A line ends at a line feed, a carriage return or both; the last line need not end. A line longer
    /// than <paramref name="maximumLineLength"/> is cut there and followed by a note that says how many characters were not kept. Memory use does not depend on the size of the stream.
    /// </summary>
    /// <param name="stream">The program's output.</param>
    /// <param name="encoding">How the bytes are text (UTF-8, or UTF-16 for wsl.exe's own messages).</param>
    /// <param name="onLine">Called once for every line.</param>
    /// <param name="maximumLineLength">The most characters kept of one line.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    public static async Task ReadAsync(Stream stream, Encoding encoding, Action<string> onLine, int maximumLineLength = DefaultMaximumLineLength, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(onLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLineLength, 1);

        var decoder = encoding.GetDecoder();
        var bytes = new byte[PieceBytes];
        var chars = new char[encoding.GetMaxCharCount(PieceBytes) + 2];
        var line = new StringBuilder(Math.Min(maximumLineLength, 256));
        long dropped = 0;
        var started = false;
        var skipLineFeed = false;

        void Emit()
        {
            if (dropped > 0)
            {
                line.Append(" [").Append(dropped).Append(" more characters were not kept]");
            }

            onLine(line.ToString());
            line.Clear();
            dropped = 0;
            started = false;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            var last = read == 0;
            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: last);
            for (var i = 0; i < count; i++)
            {
                var c = chars[i];
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (c == '\n')
                    {
                        continue;
                    }
                }

                if (c == '\r' || c == '\n')
                {
                    Emit();
                    skipLineFeed = c == '\r';
                    continue;
                }

                started = true;
                if (line.Length < maximumLineLength)
                {
                    line.Append(c);
                }
                else
                {
                    dropped++;
                }
            }

            if (last)
            {
                if (started)
                {
                    Emit();
                }

                return;
            }
        }
    }
}
