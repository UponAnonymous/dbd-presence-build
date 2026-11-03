// Covers: FR-TPL-06 (preview card), FR-RPC-13 (application name in the heading), FR-ART-05 (unconfirmed keys warned),
// FR-ART-06 (https artwork never fetched: shown as a labelled tile), UI_SPEC §5 (status copy; colour never the only signal).
using DbdPresence.Core.Game;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Settings;
using DbdPresence.Discord;
using DbdPresence.Presentation.Presence;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class PreviewAndStatusTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

    private static ValidatedActivity Activity(ActivityDraft draft) => ActivityValidator.Validate(draft).Activity;

    [Fact]
    public void NoActivity_IsAnEmptyCard()
    {
        var preview = ActivityPreview.From(null, "DBD Presence", new ArtworkSettings(), s_now);

        Assert.True(preview.IsEmpty);
        Assert.Equal("Nothing shown on Discord", preview.Heading);
    }

    [Theory]
    [InlineData(ActivityKind.Playing, "Playing Dead by Daylight")]
    [InlineData(ActivityKind.Competing, "Competing in Dead by Daylight")]
    [InlineData(ActivityKind.Watching, "Watching Dead by Daylight")]
    [InlineData(ActivityKind.Listening, "Listening to Dead by Daylight")]
    public void Heading_UsesTheActivityTypeAndApplicationName(ActivityKind kind, string expected)
    {
        var preview = ActivityPreview.From(Activity(new ActivityDraft { Kind = kind, Details = "In a lobby" }), "Dead by Daylight", new ArtworkSettings(), s_now);

        Assert.Equal(expected, preview.Heading);
    }

    [Fact]
    public void NameOverride_ReplacesTheApplicationName()
    {
        var preview = ActivityPreview.From(Activity(new ActivityDraft { Details = "Hi", NameOverride = "The Fog" }), "Dead by Daylight", new ArtworkSettings(), s_now);

        Assert.Equal("Playing The Fog", preview.Heading);
    }

    [Theory]
    [InlineData(0, "00:00 elapsed")]
    [InlineData(754, "12:34 elapsed")]
    [InlineData(3723, "1:02:03 elapsed")]
    [InlineData(-30, "00:00 elapsed")]
    public void Elapsed_IsFormattedLikeDiscord(int seconds, string expected) =>
        Assert.Equal(expected, ActivityPreview.ElapsedText(s_now.AddSeconds(-seconds), s_now));

    [Fact]
    public void HttpsArtwork_IsATile_NeverFetched_AndKeysAreCheckedAgainstTheUploadList()
    {
        var artwork = new ArtworkSettings { ConfirmedUploadedKeys = ["app_logo"] };
        var activity = Activity(new ActivityDraft
        {
            Details = "In a lobby",
            LargeImage = "https://example.com/art.png",
            SmallImage = "role_killer",
            SmallText = "Killer",
        });

        var preview = ActivityPreview.From(activity, "DBD Presence", artwork, s_now);

        Assert.Equal(ImagePreview.UserLinkCaption, preview.LargeImage?.Caption);
        Assert.False(preview.LargeImage?.IsUnconfirmed);
        Assert.True(preview.SmallImage?.IsUnconfirmed);
        Assert.Equal("Killer", preview.SmallImage?.HoverText);
    }

    [Fact]
    public void WithNoLargeImage_ThePreviewShowsTheDiscordApplicationIcon_AndNoSmallBadge()
    {
        var activity = Activity(new ActivityDraft { Details = "In a lobby", SmallImage = "app_logo" });

        var preview = ActivityPreview.From(activity, "Dead by Daylight", new ArtworkSettings(), s_now);

        Assert.True(preview.LargeImage?.IsApplicationIcon);
        Assert.Equal(ImagePreview.ApplicationIconCaption, preview.LargeImage?.Caption);
        Assert.Null(preview.SmallImage);
    }

    [Fact]
    public void WithALargeImage_ThePreviewKeepsBothImages()
    {
        var activity = Activity(new ActivityDraft { Details = "In a lobby", LargeImage = "role_killer", SmallImage = "app_logo" });

        var preview = ActivityPreview.From(activity, "Dead by Daylight", new ArtworkSettings(), s_now);

        Assert.False(preview.LargeImage?.IsApplicationIcon);
        Assert.Equal("app_logo", preview.SmallImage?.Reference);
    }

    [Fact]
    public void Buttons_AreListedByLabel()
    {
        var activity = Activity(new ActivityDraft { Details = "Live", Buttons = [new ButtonDraft("Watch", "https://twitch.tv/me")] });

        Assert.Equal(["Watch"], ActivityPreview.From(activity, "x", new ArtworkSettings(), s_now).Buttons);
    }

    [Fact]
    public void Connection_NotConfigured_PointsToTheSetting()
    {
        var line = StatusText.Connection(new RpcHealth(RpcStatus.NotConfigured, null, null, null, 0, null), isConfigured: false, s_now);

        Assert.Equal("Not configured", line.Text);
        Assert.Contains("Settings, Advanced", line.Detail, StringComparison.Ordinal);
        Assert.Equal(StatusTone.Caution, line.Tone);
    }

    [Fact]
    public void Connection_Disconnected_SaysWhenItRetries()
    {
        var health = new RpcHealth(RpcStatus.Disconnected, null, null, "No Discord pipe found", 0, s_now.AddSeconds(3.2));

        var line = StatusText.Connection(health, isConfigured: true, s_now);

        Assert.Equal("Discord not detected — retrying", line.Text);
        Assert.Equal("No Discord pipe found. Next attempt in 4 s.", line.Detail);
        Assert.Equal(StatusTone.Problem, line.Tone);
    }

    [Fact]
    public void Connection_Connected_IsGood()
    {
        var line = StatusText.Connection(new RpcHealth(RpcStatus.Connected, "discord-ipc-0", s_now, null, 1, null), isConfigured: true, s_now);

        Assert.Equal("Connected to Discord", line.Text);
        Assert.Equal(StatusTone.Good, line.Tone);
    }

    [Fact]
    public void Game_Running_ShowsWhenItStarted_AndFlagsApproximateStarts()
    {
        var started = new DateTimeOffset(2026, 10, 23, 17, 5, 0, TimeSpan.Zero);
        var exact = StatusText.Game(new GameLifecycle { State = GameState.Running, StartedUtc = started }, TimeZoneInfo.Utc);
        var approx = StatusText.Game(new GameLifecycle { State = GameState.Running, StartedUtc = started, StartIsApproximate = true }, TimeZoneInfo.Utc);

        Assert.Equal("Dead by Daylight is running", exact.Text);
        Assert.StartsWith("Since ", exact.Detail, StringComparison.Ordinal);
        Assert.Contains("approximate", approx.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Game_NotRunning_IsNeutral() =>
        Assert.Equal(StatusTone.Neutral, StatusText.Game(GameLifecycle.Initial, TimeZoneInfo.Utc).Tone);

    [Theory]
    [InlineData(PresenceState.Running, Reachability.Always)]
    [InlineData(PresenceState.Lobby, Reachability.NeedsDetection)]
    [InlineData(PresenceState.InTrialKiller, Reachability.NeedsDetectionAndRole)]
    public void Reachability_MatchesTheSpec(PresenceState state, Reachability expected) =>
        Assert.Equal(expected, StudioText.ReachabilityOf(state));
}
