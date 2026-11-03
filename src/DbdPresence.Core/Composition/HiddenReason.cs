namespace DbdPresence.Core.Composition;

/// <summary>
/// Why a part of the status is missing or different (docs/design/whole-app.md §3.4). A code, not a sentence: the Presentation layer owns the
/// words (<c>PlainReasons</c>), so the composer stays free of user-facing copy and the same reason reads the same on every page.
/// </summary>
public enum HiddenReasonCode
{
    /// <summary>Access for this account was withdrawn (a signed revocation); nothing is shown.</summary>
    AccessWithdrawn,

    /// <summary>The user chose to hide the activity completely.</summary>
    HiddenCompletely,

    /// <summary>Paused; Discord shows nothing.</summary>
    Paused,

    /// <summary>Paused; Discord shows only that the user is playing.</summary>
    PausedGeneric,

    /// <summary>The game is not running and the user shows nothing then.</summary>
    GameNotRunning,

    /// <summary>The Private style is on, so details are kept off Discord.</summary>
    PrivateStyle,

    /// <summary>A moment's text was empty or needed an unconfirmed value, so the next fallback text is used. Shown only in Advanced.</summary>
    TextFallback,

    /// <summary>A detail is missing because the app has not confirmed it. <see cref="HiddenReason.Subject"/> names the detail.</summary>
    DetailNotConfirmed,

    /// <summary>The user hides the map.</summary>
    MapHiddenByUser,

    /// <summary>The map is held back until the match ends (post-match details only).</summary>
    MapAfterMatch,

    /// <summary>Live match details are off (the default).</summary>
    LiveStatsOff,

    /// <summary>The user hides the timer.</summary>
    TimerHiddenByUser,

    /// <summary>The match timer is hidden because the app did not see the match start.</summary>
    TimerStartNotSeen,

    /// <summary>The user hides their role.</summary>
    RoleHiddenByUser,

    /// <summary>The user hides their character.</summary>
    CharacterHiddenByUser,

    /// <summary>The user hides the game mode.</summary>
    ModeHiddenByUser,

    /// <summary>The user hides where they are; the plain "playing" text is used.</summary>
    SceneHiddenByUser,

    /// <summary>The user hides the match result.</summary>
    ResultHiddenByUser,

    /// <summary>The role picture needs the role, which is not confirmed.</summary>
    RolePictureNeedsRole,

    /// <summary>A picture name may not exist in the user's own Discord app. <see cref="HiddenReason.Subject"/> is the picture name.</summary>
    PictureMayNotExist,

    /// <summary>
    /// The automatic small picture is showing the role icon although a hook, dying or result state is confirmed, because the pack has no picture for
    /// it. <see cref="HiddenReason.Subject"/> names the moment ("On the hook", "Escaped").
    /// </summary>
    StatusPictureMissing,

    /// <summary>Nothing could be composed for this moment, so Discord shows nothing.</summary>
    NothingToShow,
}

/// <summary>One reason, with the detail or picture it is about when there is one.</summary>
/// <param name="Code">The reason.</param>
/// <param name="Subject">The detail ("Map", "Character") or picture name the reason is about; null when it applies to the whole status.</param>
public sealed record HiddenReason(HiddenReasonCode Code, string? Subject = null);
