using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// Learns the owner's in-game player name from the profile name shown at the top right of every menu screen, so the owner's row can be found
/// in TAB match details, the scoreboard and the lobby. A reading counts only after <see cref="RequiredReadings"/> identical readings in a row
/// (compared after OCR folding), and a different stable reading replaces it. The name lives in memory only: it is never persisted, logged,
/// published or copied into evidence strings (SAFETY_AND_PRIVACY: other players' names are never persisted or logged, and the owner's own is used only to
/// compare). The owner may instead supply the name as a setting; that always wins.
/// </summary>
public sealed class OwnerNameTracker
{
    /// <summary>How many identical readings in a row establish the name (an initial guess).</summary>
    public const int RequiredReadings = 3;

    private string? _candidateKey;
    private string? _candidateText;
    private int _count;

    /// <summary>Gets the established name, or null while it is not known.</summary>
    public string? Current { get; private set; }

    /// <summary>Feeds one reading of the profile-name region (null when nothing name-like was read; that breaks a run).</summary>
    /// <returns>The established name, or null.</returns>
    public string? Observe(string? reading)
    {
        var key = TextNormalizer.Skeleton(reading);
        if (reading is null || key.Length == 0)
        {
            _candidateKey = null;
            _candidateText = null;
            _count = 0;
            return Current;
        }

        if (string.Equals(key, _candidateKey, StringComparison.Ordinal))
        {
            _count++;
        }
        else
        {
            _candidateKey = key;
            _candidateText = reading;
            _count = 1;
        }

        if (_count >= RequiredReadings)
        {
            Current = _candidateText;
        }

        return Current;
    }

    /// <summary>Forgets the name (the game closed).</summary>
    public void Reset()
    {
        Current = null;
        _candidateKey = null;
        _candidateText = null;
        _count = 0;
    }
}
