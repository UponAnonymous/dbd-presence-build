using System.Globalization;

namespace DbdPresence.LocalCi;

/// <summary>Writes <c>localci-yyyy-MM-dd.log</c> in the data folder's log folder, keeps 14 days, never writes a registered secret, and writes every message as one clean line.</summary>
public sealed class FileCiLog : ICiLog
{
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Redactor _redactor;
    private readonly TextWriter? _echo;
    private readonly object _gate = new();
    private string? _rotatedForDate;

    public FileCiLog(string directory, TimeProvider time, Redactor redactor, TextWriter? echo)
    {
        _directory = directory;
        _time = time;
        _redactor = redactor;
        _echo = echo;
    }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Failure(string message) => Write("ERROR", message);

    public static string FileNameFor(DateTimeOffset day) => $"localci-{day.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log";

    private void Write(string level, string message)
    {
        var now = _time.GetUtcNow();
        var line = $"{now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z {level} {LogText.Clean(_redactor.Redact(message))}";
        lock (_gate)
        {
            _echo?.WriteLine(line);
            try
            {
                // The log folder is inside the data folder; this never creates the data folder itself (only setup does, with its closed permissions).
                if (!Directory.Exists(Path.GetDirectoryName(_directory)))
                {
                    return;
                }

                DataFolder.CreateChild(_directory);
                RotateOncePerDay(now);
                File.AppendAllText(Path.Combine(_directory, FileNameFor(now)), line + Environment.NewLine);
            }
            catch (IOException ex)
            {
                // The log file cannot be written: say so on the console instead of losing the line without a trace.
                Console.Error.WriteLine($"Cannot write the log file: {_redactor.Describe(ex)}");
                Console.Error.WriteLine(line);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Cannot write the log file: {_redactor.Describe(ex)}");
                Console.Error.WriteLine(line);
            }
        }
    }

    private void RotateOncePerDay(DateTimeOffset now)
    {
        var today = FileNameFor(now);
        if (string.Equals(_rotatedForDate, today, StringComparison.Ordinal))
        {
            return;
        }

        _rotatedForDate = today;
        var oldest = now.UtcDateTime.Date.AddDays(-(LocalCiDefaults.LogRetentionDays - 1));
        foreach (var file in Directory.EnumerateFiles(_directory, "localci-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file)["localci-".Length..];
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                && day < oldest)
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>A log that keeps its lines (tests, and the console parts of setup).</summary>
public sealed class MemoryCiLog : ICiLog
{
    private readonly Redactor _redactor;
    private readonly List<string> _lines = [];

    public MemoryCiLog(Redactor redactor)
    {
        _redactor = redactor;
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public void Info(string message) => Add("INFO", message);

    public void Warn(string message) => Add("WARN", message);

    public void Failure(string message) => Add("ERROR", message);

    private void Add(string level, string message)
    {
        lock (_lines)
        {
            _lines.Add($"{level} {LogText.Clean(_redactor.Redact(message))}");
        }
    }
}
