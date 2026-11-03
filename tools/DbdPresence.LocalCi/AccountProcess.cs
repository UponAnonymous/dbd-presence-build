using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// One program to start as the dedicated Windows account (local CI round 2, red-team F2): its full path, its arguments, where its standard input comes from and where its output goes. It never
/// holds a secret (the JIT configuration of a runner travels in the input stream, never in the arguments).
/// </summary>
public sealed class AccountCommand
{
    public AccountCommand(string fileName, IReadOnlyList<string> arguments, Encoding outputEncoding, Stream? standardInput = null, Stream? rawOutput = null, Action<string>? onLine = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(outputEncoding);
        FileName = fileName;
        Arguments = arguments;
        OutputEncoding = outputEncoding;
        StandardInput = standardInput;
        RawOutput = rawOutput;
        OnLine = onLine;
    }

    /// <summary>The program's full path (checked against the list of approved programs before it is started).</summary>
    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    /// <summary>How the lines of text the program prints are decoded (UTF-16 for wsl.exe's own messages, UTF-8 for what runs inside Linux).</summary>
    public Encoding OutputEncoding { get; }

    /// <summary>Copied to the program's standard input, which is then closed. Not disposed here.</summary>
    public Stream? StandardInput { get; }

    /// <summary>When set, the program's standard output is copied to it as bytes (an export) instead of being read as lines. Not disposed here.</summary>
    public Stream? RawOutput { get; }

    /// <summary>Gets each line of text the program prints (standard error always; standard output unless <see cref="RawOutput"/> is set), cut to 4 KB and with registered secrets removed.</summary>
    public Action<string>? OnLine { get; }

    public override string ToString() => $"{Path.GetFileName(FileName)} with {Arguments.Count} arguments";
}

/// <summary>Runs an <see cref="AccountCommand"/> as the dedicated account and returns its exit code.</summary>
public interface IAccountProcess
{
    /// <summary>
    /// Starts the program as the account (never under this program's own administrator token), holds it and everything it starts in a Job Object with limits, runs it to its end or until the token is
    /// cancelled, ends whatever is left, and returns the exit code. Throws when the program could not be started or held; the program never runs outside its Job Object.
    /// </summary>
    Task<int> RunAsync(AccountCommand command, CancellationToken cancellationToken);
}

/// <summary>Keeps the last lines a program printed (for the message of a failure), safe to fill from several threads.</summary>
public sealed class LineCollector
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly int _maximum;

    public LineCollector(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        _maximum = maximum;
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _maximum)
            {
                _ = _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>The kept lines, one per line of text.</summary>
    public string Text => string.Join('\n', Lines);
}
