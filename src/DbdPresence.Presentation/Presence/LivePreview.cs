using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Packs.Local;

namespace DbdPresence.Presentation.Presence;

/// <summary>
/// What Home and Discord status show about the status Discord has now (D29): the card built from the activity requested from Discord, the plain
/// reasons, the empty-card sentence when there is nothing, and the delivery caption. Built in one place so both pages always agree.
/// </summary>
/// <param name="Preview">The activity card.</param>
/// <param name="Reasons">The plain reasons, at most three shown.</param>
/// <param name="EmptyText">The sentence that replaces the card when nothing is on Discord; null when the card is shown.</param>
/// <param name="Hints">Fix buttons for the preview (Resume while paused, pictures that cannot be previewed).</param>
/// <param name="DeliveryNote">Set when the activity is composed but Discord is not connected yet.</param>
public sealed record LivePreview(ActivityPreview Preview, ReasonList Reasons, string? EmptyText, IReadOnlyList<PreviewHint> Hints, string? DeliveryNote)
{
    /// <summary>The caption under the card on every page.</summary>
    public const string Approximate = "Approximate preview. Discord may look a little different.";

    /// <summary>The caption above the card while Discord is not connected.</summary>
    public const string NotConnectedNote = "Discord isn't connected yet. This is what it will show.";

    /// <summary>
    /// True when <paramref name="other"/> would look the same on screen. The pages refresh once a second, and only a visible change should replace the
    /// model (a record with lists compares lists by reference).
    /// </summary>
    /// <param name="other">The other preview.</param>
    public bool SameAs(LivePreview other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Preview with { Buttons = other.Preview.Buttons } == other.Preview
            && Preview.Buttons.SequenceEqual(other.Preview.Buttons)
            && EmptyText == other.EmptyText
            && DeliveryNote == other.DeliveryNote
            && Reasons.All.SequenceEqual(other.Reasons.All)
            && Hints.Select(h => h.Id + h.Message).SequenceEqual(other.Hints.Select(h => h.Id + h.Message));
    }

    /// <summary>Gets a value indicating whether the empty-card sentence replaces the card.</summary>
    public bool IsEmpty => EmptyText is not null;

    /// <summary>Builds the live preview from the coordinator's snapshot (the payload actually requested from Discord; while paused, what is shown on resume).</summary>
    /// <param name="coordinator">The presence coordinator.</param>
    /// <param name="settings">The current settings.</param>
    /// <param name="now">The current time.</param>
    /// <param name="resume">Resumes presence (the Resume button of the paused hint).</param>
    public static LivePreview From(PresenceCoordinator coordinator, AppSettings settings, DateTimeOffset now, Action resume)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resume);
        var snapshot = coordinator.Snapshot;
        var match = coordinator.Packs is { } packs ? (Func<string, PackReferenceMatch?>)(r => packs.Match(r)) : null;

        // The card shows the very activity requested from Discord. While paused that is nothing (or a generic activity), so the card
        // shows what will appear on resume and the hints say that Discord is paused.
        var shown = snapshot.Paused && snapshot.ActivityIfNotPaused is { } resumed ? resumed : snapshot.Published;
        var preview = ActivityPreview.From(shown, coordinator.Application.Name, snapshot.Artwork ?? settings.ActiveProfile.Artwork, now, match);
        var hints = PreviewHints.Build(
            new PreviewHintInputs(
                snapshot.Paused,
                settings.ActiveProfile.WhenPaused == Core.Settings.PauseBehavior.Generic,
                snapshot.State == PresenceState.NotRunning,
                snapshot.Published is not null || snapshot.ActivityIfNotPaused is not null,
                preview),
            resume);

        var reasons = snapshot.Composition.Reasons;
        var empty = shown is null ? PlainReasons.EmptyCard(reasons) ?? "Nothing is on Discord right now." : null;

        // The empty card already says the game is not running; the hint would repeat it.
        if (empty is not null)
        {
            hints = [.. hints.Where(h => h.Id != "game-closed")];
        }

        var delivery = snapshot.Published is not null && snapshot.Connection.Status != Discord.RpcStatus.Connected ? NotConnectedNote : null;
        return new LivePreview(preview, PlainReasons.For(reasons), empty, hints, delivery);
    }
}
