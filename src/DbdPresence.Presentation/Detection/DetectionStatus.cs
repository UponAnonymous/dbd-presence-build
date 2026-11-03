using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;

namespace DbdPresence.Presentation.Detection;

/// <summary>What the live screen-reading loop is doing, in the words the app uses.</summary>
public enum DetectionRunState
{
    /// <summary>The user has not turned screen reading on (the default). Nothing is captured.</summary>
    Off,

    /// <summary>The user turned it on, but the signed remote configuration is missing, expired or switches detection or capture off. Nothing is captured.</summary>
    SwitchedOffRemotely,

    /// <summary>Screen reading is on and the game is not running.</summary>
    WaitingForGame,

    /// <summary>The game window is being read.</summary>
    Reading,

    /// <summary>The game runs but its window cannot be read now (minimised, protected, or Windows delivers no picture).</summary>
    WindowUnavailable,

    /// <summary>The game window is read, but Windows text recognition cannot run (for example the English language pack is missing), so no text is read.</summary>
    TextRecognitionUnavailable,

    /// <summary>An unexpected failure; the loop tries again on its next cycle and the app logs the reason.</summary>
    Problem,
}

/// <summary>
/// A snapshot of the live loop, for the app's own status line and the "What the app sees" card. It holds no screen text, no picture and no player name:
/// <see cref="Match"/> is the engine's confirmed state (closed-list ids), and <see cref="Detail"/> is a sentence written by the app.
/// </summary>
/// <param name="Run">What the loop is doing.</param>
/// <param name="Detail">One plain sentence.</param>
/// <param name="Capture">The capture state of the last cycle, or null when none ran.</param>
/// <param name="Match">The engine's snapshot while the loop runs against a running game, otherwise null.</param>
/// <param name="Cycles">How many cycles ran since the app started.</param>
/// <param name="SkippedCycles">How many cycles were skipped because the previous one was still running (the re-entrancy guard).</param>
/// <param name="OcrCalls">How many text recognitions ran.</param>
/// <param name="RefusedDetectors">Detectors whose requested tier the profile gate refused (each line says why; no screen text).</param>
public sealed record DetectionStatus(
    DetectionRunState Run,
    string Detail,
    CaptureState? Capture,
    MatchSnapshot? Match,
    long Cycles,
    long SkippedCycles,
    long OcrCalls,
    IReadOnlyList<string> RefusedDetectors)
{
    /// <summary>Gets the status of a loop that has not started or is off.</summary>
    public static DetectionStatus Off { get; } = new(DetectionRunState.Off, DetectionText.Off, null, null, 0, 0, 0, []);
}

/// <summary>The words of the screen-reading status line and card. Kept in one place so the app, the tests and the docs say the same thing.</summary>
public static class DetectionText
{
    /// <summary>The setting is off.</summary>
    public const string Off = "Screen reading is off.";

    /// <summary>The signed configuration does not allow it.</summary>
    public const string SwitchedOffRemotely = "Screen reading is not allowed right now: the project's signed configuration is missing, out of date or switches it off, so nothing is read.";

    /// <summary>Waiting for the game.</summary>
    public const string WaitingForGame = "Waiting for Dead by Daylight to start.";

    /// <summary>Reading.</summary>
    public const string Reading = "Reading the game window. Nothing is saved.";

    /// <summary>The window cannot be read now.</summary>
    public const string WindowUnavailable = "The game window cannot be read right now";

    /// <summary>Text recognition does not work (the English pack is missing, or Windows failed).</summary>
    public const string TextRecognitionBroken = "Windows text recognition is not working right now, so nothing can be read from the game window.";

    /// <summary>Unexpected failure.</summary>
    public const string Problem = "Screen reading hit an unexpected problem and will try again. The reason is in the log.";

    /// <summary>The honest line under the card: what is shown and what is not sent.</summary>
    public const string NotSentNote = "Shown here only — not sent to Discord until detection has been checked on your games.";

    /// <summary>What the setting does and the risk, shown next to the switch (SAFETY_AND_PRIVACY §1).</summary>
    public const string SettingExplanation =
        "Reads pictures of the game window (only that window, only while you play) to show here what the app sees. Nothing is saved and nothing from it is sent to Discord yet. " +
        "It is off until you turn it on. Behaviour Interactive has not approved third-party tools; the app never touches the game itself, but acceptance by its anti-cheat is not guaranteed. Use it at your own risk.";

    /// <summary>How many recorded matches the detection study works towards (DETECTION_VALIDATION §7: at least 30 trials for the Experimental tier).</summary>
    public const int MatchGoal = 30;

    /// <summary>Gets the progress line, for example "Automatic detection: learning (4 of 30 matches recorded)".</summary>
    public static string Progress(int matches, int goal) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Automatic detection: learning ({matches} of {goal} matches recorded)");
}

/// <summary>Source of the loop's status for the UI.</summary>
public interface IDetectionStatusSource
{
    /// <summary>Raised (on any thread) after <see cref="Status"/> changed.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Gets the latest status.</summary>
    DetectionStatus Status { get; }
}

/// <summary>
/// What the presence coordinator may use from detection. The coordinator still goes through <see cref="PublicationPolicy"/>, which publishes nothing from
/// a Disabled detector, so a feed can only ever add what a validated detector may publish.
/// </summary>
public interface IDetectionFeed
{
    /// <summary>Raised (on any thread) after <see cref="Snapshot"/> or the run state changed.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the engine's snapshot, or null while the loop is off or no game runs.</summary>
    MatchSnapshot? Snapshot { get; }

    /// <summary>Gets the per-detector settings in force (after the profile gate refused what it must).</summary>
    IReadOnlyDictionary<DetectorId, DetectorSettings> DetectorSettings { get; }

    /// <summary>Gets the per-session publication switches.</summary>
    PublicationOptions Options { get; }

    /// <summary>Gets the resolver from map ids to display names, or null when maps may never be published.</summary>
    IMapNameResolver? Maps { get; }
}
