using System.Text;
using System.Text.RegularExpressions;

namespace DbdPresence.Core.Diagnostics;

/// <summary>
/// Describes an exception for the log without the user's identity (NFR-PRIV-02, security review L2). An exception message can hold a path such as
/// <c>C:\Users\Alice\AppData\...</c>, the computer's name or the user's name; this keeps the type names, the message and the first stack frames, with the user
/// profile path, the user name, the machine name, any <c>\Users\&lt;name&gt;</c> or <c>/home/&lt;name&gt;</c> segment and any network host replaced by a fixed mask.
/// Pure: the identity values are passed in (see <see cref="ForCurrentUser"/> for the live ones), so it is tested without a PC.
/// </summary>
public sealed partial class LogRedaction
{
    /// <summary>The most stack lines kept.</summary>
    public const int MaxStackLines = 12;

    /// <summary>The deepest inner-exception chain described.</summary>
    public const int MaxDepth = 5;

    private const int MinNameLength = 3;

    private readonly string[] _literals;
    private readonly string[] _replacements;

    /// <summary>Initializes a new instance of the <see cref="LogRedaction"/> class.</summary>
    /// <param name="userProfilePath">The user's profile folder (replaced with <c>%USERPROFILE%</c>), or null.</param>
    /// <param name="userName">The user's account name (replaced with <c>&lt;user&gt;</c>), or null.</param>
    /// <param name="machineName">The computer's name (replaced with <c>&lt;machine&gt;</c>), or null.</param>
    public LogRedaction(string? userProfilePath, string? userName, string? machineName)
    {
        var literals = new List<string>();
        var replacements = new List<string>();
        if (!string.IsNullOrWhiteSpace(userProfilePath) && userProfilePath.Length >= MinNameLength)
        {
            literals.Add(userProfilePath.TrimEnd('\\', '/'));
            replacements.Add("%USERPROFILE%");
            literals.Add(userProfilePath.TrimEnd('\\', '/').Replace('\\', '/'));
            replacements.Add("%USERPROFILE%");
        }

        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= MinNameLength)
        {
            literals.Add(userName);
            replacements.Add("<user>");
        }

        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= MinNameLength)
        {
            literals.Add(machineName);
            replacements.Add("<machine>");
        }

        // Longest first, so a profile path is replaced as a whole before the user name inside it.
        var order = Enumerable.Range(0, literals.Count).OrderByDescending(i => literals[i].Length).ToArray();
        _literals = [.. order.Select(i => literals[i])];
        _replacements = [.. order.Select(i => replacements[i])];
    }

    /// <summary>Gets the redaction for the user running the app, created on first use.</summary>
    public static LogRedaction Current { get; } = ForCurrentUser();

    /// <summary>Creates the redaction for the user running the app.</summary>
    /// <returns>The redaction.</returns>
    public static LogRedaction ForCurrentUser() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    /// <summary>Removes the identity from a piece of text.</summary>
    /// <param name="text">Any text, for example an exception message.</param>
    /// <returns>The text with the user's profile path, user name, machine name and network hosts replaced.</returns>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        for (var i = 0; i < _literals.Length; i++)
        {
            result = result.Replace(_literals[i], _replacements[i], StringComparison.OrdinalIgnoreCase);
        }

        result = UsersSegment().Replace(result, "$1<user>");
        return NetworkHost().Replace(result, @"\\<host>\");
    }

    /// <summary>Describes an exception chain: type names and redacted messages, then the first redacted stack lines of the outermost one.</summary>
    /// <param name="error">The exception.</param>
    /// <returns>Text for one log property.</returns>
    public string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var text = new StringBuilder();
        var current = error;
        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            _ = text.Append(depth == 0 ? string.Empty : " <- ").Append(current.GetType().Name).Append(": ").Append(Redact(current.Message));
            if (current is AggregateException { InnerExceptions.Count: > 1 } many)
            {
                _ = text.Append(" [").Append(string.Join("; ", many.InnerExceptions.Take(MaxDepth).Select(e => e.GetType().Name + ": " + Redact(e.Message)))).Append(']');
            }

            current = current.InnerException;
        }

        if (error.StackTrace is { Length: > 0 } stack)
        {
            var lines = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(MaxStackLines).Select(l => Redact(l.TrimEnd('\r')));
            _ = text.Append('\n').AppendJoin('\n', lines);
        }

        return text.ToString();
    }

    [GeneratedRegex(@"(?i)([\\/]+(?:users|home)[\\/]+)[^\\/\s""'<>:*?|]+", RegexOptions.CultureInvariant)]
    private static partial Regex UsersSegment();

    [GeneratedRegex(@"\\\\[^\\/\s""'<>:*?|]+\\", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkHost();
}
