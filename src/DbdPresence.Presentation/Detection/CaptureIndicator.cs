namespace DbdPresence.Presentation.Detection;

/// <summary>
/// The words the tray tooltip adds while the app looks at the game window (SAFETY_AND_PRIVACY section 5). Since 2026-11-02 the app asks Windows to leave out
/// its yellow capture border, so the tray is the place outside the window that always says when the game is being read or recorded.
/// </summary>
public static class CaptureIndicator
{
    /// <summary>The words while the owner's study recorder saves pictures of the game window.</summary>
    public const string Recording = "Recording the game window";

    /// <summary>The words while screen reading looks at the game window.</summary>
    public const string Reading = "Reading the screen";

    /// <summary>Gets the words for the current state, or null when the game window is not being looked at.</summary>
    /// <param name="run">What the screen-reading loop is doing.</param>
    /// <param name="recording">Whether the study recorder is waiting for the game or recording.</param>
    /// <returns>The words, or null.</returns>
    public static string? Words(DetectionRunState run, bool recording) =>
        recording ? Recording : run is DetectionRunState.Reading or DetectionRunState.WindowUnavailable or DetectionRunState.TextRecognitionUnavailable ? Reading : null;

    /// <summary>Adds the words to a tooltip, keeping it within Windows' 127 characters by shortening the summary, never the indicator.</summary>
    /// <param name="summary">The tooltip without the indicator.</param>
    /// <param name="words">The indicator words, or null.</param>
    /// <returns>The tooltip.</returns>
    public static string Tooltip(string summary, string? words)
    {
        ArgumentNullException.ThrowIfNull(summary);
        const int Max = 127;
        var suffix = words is null ? string.Empty : $" · {words}";
        var room = Max - suffix.Length;
        return (summary.Length > room ? summary[..(room - 1)] + "…" : summary) + suffix;
    }
}
