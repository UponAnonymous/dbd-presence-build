using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Discord;

namespace DbdPresence.Presentation.Presence;

/// <summary>Everything the UI shows about what is (or would be) on Discord right now.</summary>
/// <param name="Game">The confirmed game lifecycle.</param>
/// <param name="State">The presence state the composer used as its starting point.</param>
/// <param name="Composition">The composer's decision, including notes for "Why is this hidden?".</param>
/// <param name="Published">The activity requested from Discord (the composition, or the connection test activity).</param>
/// <param name="Connection">Discord connection health.</param>
/// <param name="Paused">True while presence is paused (runtime only).</param>
/// <param name="StreamerMode">True while streamer mode is on.</param>
/// <param name="IsConfigured">True when there is an application ID to connect with.</param>
/// <param name="ConnectionTest">The running or finished connection test, if any.</param>
/// <param name="ComposedUtc">When this snapshot was composed.</param>
/// <param name="ActivityIfNotPaused">While paused: the activity the composer would build with only the pause removed; null when not paused or when it would show nothing.</param>
/// <param name="Artwork">The artwork the composer used, with the image packs applied (the profile's own references when no packs are wired).</param>
/// <param name="Inputs">The confirmed inputs the composer used. The style cards on Discord status compose their own live mini previews from the very same inputs.</param>
public sealed record PresenceSnapshot(
    GameLifecycle Game,
    PresenceState State,
    CompositionResult Composition,
    Core.Presence.ValidatedActivity? Published,
    RpcHealth Connection,
    bool Paused,
    bool StreamerMode,
    bool IsConfigured,
    ConnectionTest? ConnectionTest,
    DateTimeOffset ComposedUtc,
    Core.Settings.ArtworkSettings? Artwork = null,
    Core.Presence.ValidatedActivity? ActivityIfNotPaused = null,
    PresenceInputs? Inputs = null);

/// <summary>A connection test: a temporary activity, then a check that Discord acknowledged it.</summary>
/// <param name="StartedUtc">When the test activity was requested.</param>
/// <param name="EndsUtc">When the test activity is withdrawn.</param>
/// <param name="AcknowledgedUtc">When Discord acknowledged an update after the test started, if it has.</param>
public sealed record ConnectionTest(DateTimeOffset StartedUtc, DateTimeOffset EndsUtc, DateTimeOffset? AcknowledgedUtc)
{
    /// <summary>Gets a value indicating whether Discord accepted the test activity.</summary>
    public bool Succeeded => AcknowledgedUtc is not null;
}

/// <summary>The outcome of asking for a connection test.</summary>
public enum ConnectionTestStart
{
    /// <summary>The test activity was requested.</summary>
    Started,

    /// <summary>Your own application is selected but has no valid Application ID.</summary>
    NotConfigured,

    /// <summary>Presence is paused or hidden; the test does not override that.</summary>
    ActivityHidden,

    /// <summary>The app is locked (D32): nothing is sent to Discord, so there is nothing to test.</summary>
    Locked,
}
