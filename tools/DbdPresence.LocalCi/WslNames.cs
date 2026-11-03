using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>
/// The names that go into a wsl.exe command line, checked before they do. A distribution name or Linux user name that starts with a dash would be read as an option, and one with a quote or
/// a space would split an argument; nothing this program makes looks like either, so anything that does is refused.
/// </summary>
public static partial class WslNames
{
    /// <summary>The longest distribution name accepted.</summary>
    public const int MaximumDistributionLength = 64;

    public static string RequireDistribution(string name)
    {
        if (string.IsNullOrEmpty(name) || !Distribution().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a Linux machine name this program may pass to WSL (letters, digits, dot, hyphen and underscore; not starting with a symbol; at most {MaximumDistributionLength} characters).", nameof(name));
        }

        return name;
    }

    public static string RequireLinuxUser(string user)
    {
        if (string.IsNullOrEmpty(user) || !LinuxUser().IsMatch(user))
        {
            throw new ArgumentException($"'{user}' is not a Linux user name this program may pass to WSL.", nameof(user));
        }

        return user;
    }

    /// <summary>A full path without a quote, a control character or a trailing separator.</summary>
    public static string RequireInstallDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.Any(c => char.IsControl(c) || c is '"' or '|' or '<' or '>' or '*' or '?'))
        {
            throw new ArgumentException($"'{path}' is not a folder this program may pass to WSL.", nameof(path));
        }

        return path;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Distribution();

    [GeneratedRegex(@"^[a-z_][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxUser();
}
