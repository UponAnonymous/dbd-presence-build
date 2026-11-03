using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>
/// The names of the programs that mean "Dead by Daylight is running" (FR-CI-03): the three this program knows, and the names the owner adds in the file <c>game-names.txt</c> in the data folder (QA round 3,
/// minor: another build of the game, such as one from another store, may have a program with another name). The product's own list in its settings is not read: that file lives in the owner's profile,
/// where any program of the owner can change it, and this program runs with administrator rights. The data folder is closed to ordinary users, so what is written there is the owner's own word.
/// One name per line, with or without <c>.exe</c>, <c>#</c> starts a comment; a line that is not a plain program name is ignored and said in the log once.
/// </summary>
public sealed partial class GameNames
{
    /// <summary>The most extra names that count.</summary>
    public const int MostExtraNames = 20;

    /// <summary>The largest the file may be; a bigger file is not read.</summary>
    public const int MostBytes = 8 * 1024;

    private readonly string _path;
    private readonly ICiLog _log;
    private readonly HashSet<string> _complainedAbout = new(StringComparer.Ordinal);

    public GameNames(string path, ICiLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _log = log;
    }

    /// <summary>The names the game can have now: the built-in ones, then the owner's.</summary>
    public IReadOnlyList<string> Current()
    {
        var extra = Read();
        return [.. LocalCiDefaults.GameProcessNames, .. extra.Where(e => !LocalCiDefaults.GameProcessNames.Contains(e, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>The valid names in a text, without <c>.exe</c>, without duplicates (names are compared without regard to case), at most <see cref="MostExtraNames"/>; the lines that are no program name come back in <paramref name="ignored"/>.</summary>
    public static IReadOnlyList<string> Parse(string? text, out IReadOnlyList<string> ignored)
    {
        var names = new List<string>();
        var bad = new List<string>();
        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                line = line[..^4];
            }

            if (!PlainName().IsMatch(line))
            {
                bad.Add(raw.Trim().Length > 40 ? raw.Trim()[..40] + "..." : raw.Trim());
                continue;
            }

            if (names.Count < MostExtraNames && !names.Contains(line, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(line);
            }
        }

        ignored = bad;
        return names;
    }

    private List<string> Read()
    {
        try
        {
            if (Directory.Exists(_path))
            {
                Complain($"{_path} is a folder, not a file, so the game names the owner meant to add there do not count.");
                return [];
            }

            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                return [];
            }

            if (info.Length > MostBytes)
            {
                Complain($"The file {_path} is larger than {MostBytes / 1024} KB, so it was not read; the game names it holds do not count.");
                return [];
            }

            var names = Parse(File.ReadAllText(_path), out var ignored);
            foreach (var line in ignored)
            {
                Complain($"The file {_path} has a line that is not a plain program name, so it was ignored: {line}");
            }

            return [.. names];
        }
        catch (IOException ex)
        {
            Complain($"The file {_path} could not be read, so the game names in it do not count: {ex.Message}");
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            Complain($"The file {_path} could not be read, so the game names in it do not count: {ex.Message}");
            return [];
        }
    }

    private void Complain(string message)
    {
        if (_complainedAbout.Add(message))
        {
            _log.Warn(message);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}
