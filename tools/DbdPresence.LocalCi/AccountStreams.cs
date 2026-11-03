using System.Collections.Concurrent;
using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// The three byte streams of a program started as the dedicated account, as pure stream code (local CI round 2, red-team F5): input is copied in and closed, output is read in bounded pieces. A failure
/// of the program's own end of a pipe (it ended early, the pipe was closed) is the normal end; a failure on the other side (the input file cannot be read, the output file cannot be written) is
/// real and is raised, because treating it as an end would pass on a truncated input or keep a truncated export.
/// </summary>
public static class AccountStreams
{
    private const int PieceBytes = 81920;

    /// <summary>
    /// Copies <paramref name="input"/> to the program's input <paramref name="pipe"/>, then closes the pipe so the program sees the end of its input. A pipe that is broken or already closed means the program
    /// ended before it read everything; its exit code says why, so that is not raised. Reading <paramref name="input"/> failing is.
    /// </summary>
    public static async Task PumpInputAsync(Stream pipe, Stream? input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        try
        {
            if (input is not null)
            {
                var buffer = new byte[PieceBytes];
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    try
                    {
                        await pipe.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // hygiene: allow-swallow the program ended before it read all of its input; its exit code says why
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        // hygiene: allow-swallow the pipe was closed because the program ended; its exit code says why
                        return;
                    }
                }
            }
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the program's output <paramref name="pipe"/> to its end. With <paramref name="raw"/> the bytes are copied to it; otherwise they are read as lines of <paramref name="encoding"/> (at most 4 KB of a
    /// line kept) and given to <paramref name="onLine"/> with registered secrets removed. A pipe that is closed under the reader ends the reading.
    /// </summary>
    public static async Task PumpOutputAsync(Stream pipe, Stream? raw, Encoding encoding, Action<string>? onLine, Redactor redactor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(redactor);
        if (raw is not null)
        {
            var buffer = new byte[PieceBytes];
            while (true)
            {
                int read;
                try
                {
                    read = await pipe.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // hygiene: allow-swallow the program's end of the pipe is closed, which is the end of its output
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // hygiene: allow-swallow the pipe was closed because the program ended, which is the end of its output
                    return;
                }

                if (read == 0)
                {
                    return;
                }

                await raw.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            await BoundedLineReader.ReadAsync(pipe, encoding, line => onLine?.Invoke(redactor.Redact(line)), BoundedLineReader.DefaultMaximumLineLength, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // hygiene: allow-swallow the program's end of the pipe is closed, which is the end of its output
        }
        catch (ObjectDisposedException)
        {
            // hygiene: allow-swallow the pipe was closed because the program ended, which is the end of its output
        }
    }
}

/// <summary>A stream that keeps at most a fixed number of bytes and refuses more (a listing from wsl.exe is a few lines; anything huge is not one).</summary>
public sealed class CappedMemoryStream : Stream
{
    private readonly MemoryStream _inner = new();
    private readonly int _capacity;

    public CappedMemoryStream(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    /// <summary>The bytes written so far.</summary>
    public byte[] ToArray() => _inner.ToArray();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_inner.Length + buffer.Length > _capacity)
        {
            throw new IOException($"The program printed more than {_capacity} bytes, which is not what was asked for.");
        }

        _inner.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override void Flush()
    {
        // Everything is kept in memory as it is written.
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>The limits of the Job Object that holds a program started as the dedicated account (local CI round 2, red-team F5), as plain numbers from winnt.h so they can be tested without Windows.</summary>
public static class AccountJobLimits
{
    /// <summary>JOB_OBJECT_LIMIT_ACTIVE_PROCESS (winnt.h 0x8).</summary>
    public const uint LimitActiveProcess = 0x8;

    /// <summary>JOB_OBJECT_LIMIT_JOB_MEMORY (winnt.h 0x200).</summary>
    public const uint LimitJobMemory = 0x200;

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (winnt.h 0x2000): everything in the job ends when the last handle to it closes.</summary>
    public const uint LimitKillOnJobClose = 0x2000;

    /// <summary>All three limits are set on every job.</summary>
    public const uint LimitFlags = LimitActiveProcess | LimitJobMemory | LimitKillOnJobClose;

    /// <summary>The most programs the job may hold at once: wsl.exe and a few helpers. A program that starts more than this is not wsl.exe doing its work.</summary>
    public const uint ActiveProcesses = 16;

    /// <summary>The most memory, in bytes, all programs of the job may commit together (wsl.exe uses a small part of it; the Linux machine's own memory is not in this job).</summary>
    public const long MemoryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// JOB_OBJECT_UILIMIT_HANDLES 0x1, READCLIPBOARD 0x2, WRITECLIPBOARD 0x4, SYSTEMPARAMETERS 0x8, DISPLAYSETTINGS 0x10, GLOBALATOMS 0x20, DESKTOP 0x40, EXITWINDOWS 0x80: all eight. They are set only
    /// when the program is not already in another Job Object (nested jobs cannot have them); the private window station is the isolation in that case.
    /// </summary>
    public const uint UiLimitAll = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80;
}

/// <summary>
/// The pumps of one started program (its input, its output, its error output) run together. When one of them fails for a real reason (the input file cannot be read, the output file cannot be written) the
/// others are cancelled through <see cref="Token"/> so the owner of the set can end the program, and the first failure is kept for it to raise: a failure inside a pump never goes unseen and never
/// leaves the program running.
/// </summary>
public sealed class PumpSet : IDisposable
{
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly CancellationTokenSource _stop;
    private readonly List<Task> _tasks = [];

    /// <param name="cancellationToken">The caller's token; it stops the pumps too.</param>
    public PumpSet(CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>Cancelled when the caller cancels or any pump failed.</summary>
    public CancellationToken Token => _stop.Token;

    /// <summary>The first real failure of any pump, or null.</summary>
    public Exception? Failure => _failures.TryPeek(out var failure) ? failure : null;

    public void Start(Func<CancellationToken, Task> pump)
    {
        ArgumentNullException.ThrowIfNull(pump);
        _tasks.Add(RunAsync(pump));
    }

    /// <summary>Waits until every pump has ended or <paramref name="limit"/> has passed; true when all ended. A pump that is still blocked is released when its pipe is closed.</summary>
    public async Task<bool> WhenAllAsync(TimeSpan limit, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var all = Task.WhenAll(_tasks);
        using var timer = new CancellationTokenSource();
        var finished = await Task.WhenAny(all, Task.Delay(limit, time, timer.Token)).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        return ReferenceEquals(finished, all);
    }

    public void Dispose() => _stop.Dispose();

    private async Task RunAsync(Func<CancellationToken, Task> pump)
    {
        try
        {
            await pump(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // hygiene: allow-swallow the program is being stopped (the caller cancelled or another pump failed); whoever stopped it reports why
        }
#pragma warning disable CA1031 // A pump's failure is kept for the owner of the set to raise after it has ended the program; nothing is hidden.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _failures.Enqueue(ex);
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }
}
