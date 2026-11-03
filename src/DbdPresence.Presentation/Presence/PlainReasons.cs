using DbdPresence.Core.Composition;

namespace DbdPresence.Presentation.Presence;

/// <summary>The reasons shown under a preview: at most <see cref="PlainReasons.MaxShown"/> sentences, and how many there are in all.</summary>
/// <param name="Shown">The most important sentences, at most three.</param>
/// <param name="All">Every sentence, most important first.</param>
public sealed record ReasonList(IReadOnlyList<string> Shown, IReadOnlyList<string> All)
{
    /// <summary>Gets the empty list.</summary>
    public static ReasonList Empty { get; } = new([], []);

    /// <summary>Gets a value indicating whether there are more sentences than are shown.</summary>
    public bool HasMore => All.Count > Shown.Count;

    /// <summary>Gets the label of the button that reveals the rest, for example "Show all (5)".</summary>
    public string ShowAllLabel => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Show all ({All.Count})");
}

/// <summary>
/// The copy table that turns the composer's <see cref="HiddenReason"/> codes into plain sentences (whole-app design §3.4, §8): no "template", "token" or
/// "situation", one idea per sentence. One table serves Home and Discord status, so the same reason always reads the same.
/// </summary>
public static class PlainReasons
{
    /// <summary>The most sentences shown before "Show all".</summary>
    public const int MaxShown = 3;

    /// <summary>The sentence for a reason, or null when the reason is not shown to people in the main screens (it only matters in Advanced).</summary>
    /// <param name="reason">The reason.</param>
    public static string? Describe(HiddenReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var subject = reason.Subject ?? "This detail";
        return reason.Code switch
        {
            HiddenReasonCode.AccessWithdrawn => "Access for this account was withdrawn, so nothing is shown.",
            HiddenReasonCode.HiddenCompletely => "You chose to hide your activity completely.",
            HiddenReasonCode.Paused => "Paused, so Discord shows nothing until you resume.",
            HiddenReasonCode.PausedGeneric => "Paused, so only “Playing Dead by Daylight” is shown.",
            HiddenReasonCode.PrivateStyle => "Private style: details are kept off Discord.",
            HiddenReasonCode.DetailNotConfirmed => $"{subject}: the app is still learning to read it.",
            HiddenReasonCode.MapHiddenByUser => "Map: hidden by you.",
            HiddenReasonCode.MapAfterMatch => "Map: shown when the match ends.",
            HiddenReasonCode.LiveStatsOff => "Live match details are off.",
            HiddenReasonCode.TimerHiddenByUser => "Match timer: hidden by you.",
            HiddenReasonCode.TimerStartNotSeen => "Match timer: hidden because the app didn't see the match start.",
            HiddenReasonCode.RoleHiddenByUser => "Role: hidden by you.",
            HiddenReasonCode.CharacterHiddenByUser => "Character: hidden by you.",
            HiddenReasonCode.ModeHiddenByUser => "Game mode: hidden by you.",
            HiddenReasonCode.SceneHiddenByUser => "Where you are: hidden by you.",
            HiddenReasonCode.ResultHiddenByUser => "Match result: hidden by you.",
            HiddenReasonCode.RolePictureNeedsRole => "Role picture: shown once the app knows your role.",
            HiddenReasonCode.PictureMayNotExist => $"Picture “{subject}” may not exist in your own Discord app.",
            HiddenReasonCode.StatusPictureMissing => $"{subject}: your pack has no picture for this, so your role icon is shown.",
            HiddenReasonCode.NothingToShow => "Nothing to show for this moment, so Discord shows nothing.",
            _ => null,
        };
    }

    /// <summary>
    /// The sentence that fills the empty card when nothing is on Discord (design §3.4), or null when a normal card is shown. Only the reasons that
    /// clear the whole status count.
    /// </summary>
    /// <param name="reasons">The composer's reasons.</param>
    public static string? EmptyCard(IEnumerable<HiddenReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        foreach (var reason in reasons)
        {
            switch (reason.Code)
            {
                case HiddenReasonCode.AccessWithdrawn:
                    return "Nothing on Discord: access for this account was withdrawn.";
                case HiddenReasonCode.HiddenCompletely:
                    return "Nothing on Discord: you chose to hide your activity completely.";
                case HiddenReasonCode.GameNotRunning:
                    return "Nothing on Discord right now. Your status appears when you start the game.";
                case HiddenReasonCode.Paused:
                    return "Nothing on Discord while you're paused.";
                case HiddenReasonCode.NothingToShow:
                    return "Nothing to show for this moment, so Discord shows nothing.";
                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>Builds the list shown under a preview: described reasons, most important first, without repeats.</summary>
    /// <param name="reasons">The composer's reasons.</param>
    public static ReasonList For(IEnumerable<HiddenReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        var all = reasons
            .Select((r, i) => (Reason: r, Index: i))
            .OrderBy(x => (int)x.Reason.Code)
            .ThenBy(x => x.Index)
            .Select(x => Describe(x.Reason))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return all.Count == 0 ? ReasonList.Empty : new ReasonList([.. all.Take(MaxShown)], all);
    }
}
