namespace DbdPresence.Core.Detection;

/// <summary>
/// The power prompts seen during one match, tied to the killer the match's scoreboard named. Prompts are the bottom-centre text of the killer HUD
/// (for example <c>VILE DARKNESS</c>); only text that recurred during the match and is not a player or character name is ever passed on.
/// </summary>
/// <param name="KillerId">The killer id the owner's own scoreboard row named (the owner's role, the owner's row and the killer row all agreed).</param>
/// <param name="Prompts">The recurring prompt texts, upper case, letters and spaces only, at most a handful.</param>
/// <param name="ObservedAtUtc">When the scoreboard confirmed the killer.</param>
public sealed record PromptAssociation(string KillerId, IReadOnlyList<string> Prompts, DateTimeOffset ObservedAtUtc);

/// <summary>
/// The owner played a killer (the scoreboard row was the owner's red killer row) but the character on it could not be matched to the closed list, so
/// the prompts of this match cannot be tied to a killer yet. A character-name sink (the roster workflow) records the name itself; this report carries only
/// the prompts, so the two can be joined by the owner's app when the name is added to the list.
/// </summary>
/// <param name="Prompts">The recurring prompt texts of the match.</param>
/// <param name="ObservedAtUtc">When the scoreboard was read.</param>
public sealed record UnmatchedKillerPrompts(IReadOnlyList<string> Prompts, DateTimeOffset ObservedAtUtc);

/// <summary>
/// Receives what the power-prompt learning path observed (FR-DET-22 follow-up: killer identity without opening TAB). It is a seam owned by the
/// owner's app: the detection side calls it, the app decides whether and where to keep the data (on this PC only). Implementations must not throw
/// and must not block; the vision pipeline calls them on its loop thread. Nothing passed here is a player name: the texts are filtered against the
/// names on screen, the owner's name and the character lists before they get here, and a text is passed only after it recurred within the match.
/// </summary>
public interface IPromptObservationSink
{
    /// <summary>A match ended with the killer confirmed by the scoreboard; the prompts seen in it can be associated with that killer.</summary>
    /// <param name="association">The killer and the prompts.</param>
    void PromptsAssociated(PromptAssociation association);

    /// <summary>A match ended with the owner as a killer the closed list does not name.</summary>
    /// <param name="report">The prompts of the match.</param>
    void KillerNotRecognised(UnmatchedKillerPrompts report);
}

/// <summary>A sink that drops everything (the default when the app keeps nothing).</summary>
public sealed class NullPromptObservationSink : IPromptObservationSink
{
    /// <summary>Gets the shared instance.</summary>
    public static NullPromptObservationSink Instance { get; } = new();

    private NullPromptObservationSink()
    {
    }

    /// <inheritdoc />
    public void PromptsAssociated(PromptAssociation association)
    {
    }

    /// <inheritdoc />
    public void KillerNotRecognised(UnmatchedKillerPrompts report)
    {
    }
}
