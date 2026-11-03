using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>
/// The words for ending the programs of the dedicated account (uninstall only): <c>taskkill /F /T /FI "USERNAME eq PC\dbd-ci-wsl"</c>. The filter names one account and this function builds it for
/// that account and no other, so it can never be turned on the owner's own programs or on the game (which runs as the owner).
/// </summary>
public static partial class TaskkillArguments
{
    /// <summary>The exit code <c>taskkill</c> gives when no program matches the filter (nothing is left).</summary>
    public const int NothingFound = 128;

    /// <summary>The arguments for the account <paramref name="user"/> on the computer <paramref name="machine"/>.</summary>
    /// <exception cref="ArgumentException">The user is not exactly the dedicated account, or the computer name holds anything but letters, digits, hyphens, underscores and dots.</exception>
    public static IReadOnlyList<string> ForTheAccount(string machine, string user)
    {
        if (!LocalCiDefaults.IsWslAccountName(user))
        {
            throw new ArgumentException($"Programs are only ended for the account {LocalCiDefaults.WslAccountName}, never for '{user}'.", nameof(user));
        }

        if (string.IsNullOrEmpty(machine) || !MachineName().IsMatch(machine))
        {
            throw new ArgumentException("The computer name is empty or has a character that is not allowed in a filter.", nameof(machine));
        }

        return ["/F", "/T", "/FI", $"USERNAME eq {machine}\\{LocalCiDefaults.WslAccountName}"];
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MachineName();
}
