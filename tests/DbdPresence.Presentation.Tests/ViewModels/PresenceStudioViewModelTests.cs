// Covers: FR-TPL-01 (per-state templates), FR-TPL-04 (live validation: syntax column, limits), FR-TPL-05 (presets,
// reset per state), FR-TPL-06 (preview with preview values that are never published), FR-TPL-07 (reachability),
// FR-RPC-09 (button validation), FR-ART-01 (image slot validation), FR-VIS-02 (streamer template set).
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class PresenceStudioViewModelTests
{
    private static (PresenceStudioViewModel Studio, FakeSettingsService Settings) Create(AppSettings? settings = null)
    {
        var h = new Harness(settings);
        return (new PresenceStudioViewModel(h.Settings, h.Catalog, h.Dispatcher, h.Time), h.Settings);
    }

    private static PresenceTemplate Saved(FakeSettingsService settings, PresenceState state, bool streamer = false)
    {
        var profile = settings.Current.ActiveProfile;
        return (streamer ? profile.StreamerTemplates : profile.Templates).ByState[state];
    }

    private static void Select(PresenceStudioViewModel studio, PresenceState state) => studio.SelectedState = studio.States.Single(s => s.State == state);

    [Fact]
    public void WithNoLargeImage_ANoteExplainsThatDiscordShowsItsOwnIcon_AndThePreviewShowsThatIcon()
    {
        var (studio, settings) = Create();
        Assert.False(studio.ShowsSmallImageNeedsLargeNote);
        Assert.Equal(string.Empty, studio.SmallImageNeedsLargeNote);

        studio.SmallImageSource = ImageSource.AppLogo;
        studio.LargeImageSource = ImageSource.None;

        Assert.True(studio.ShowsSmallImageNeedsLargeNote);
        Assert.Equal(
            "Discord shows a small badge only on top of a big picture. With no big picture, Discord shows its own app icon instead.",
            studio.SmallImageNeedsLargeNote);
        Assert.True(studio.Preview.LargeImage?.IsApplicationIcon);
        Assert.Null(studio.Preview.SmallImage);
        Assert.Equal(ImageSlot.AppLogo, Saved(settings, PresenceState.Running).SmallImage);
        Assert.Equal(ImageSlot.None, Saved(settings, PresenceState.Running).LargeImage);
    }

    [Fact]
    public void TheNoLargeImageNote_GoesAwayWhenALargeImageIsChosen()
    {
        var (studio, _) = Create();
        studio.LargeImageSource = ImageSource.None;

        studio.LargeImageSource = ImageSource.AppLogo;

        Assert.False(studio.ShowsSmallImageNeedsLargeNote);
        Assert.False(studio.Preview.LargeImage?.IsApplicationIcon);
    }

    [Fact]
    public void Opens_OnRunning_WithItsSavedTemplate()
    {
        var (studio, _) = Create();

        Assert.Equal(PresenceState.Running, studio.SelectedState.State);
        Assert.Equal("Playing Dead by Daylight", studio.Details.Text);
        Assert.Equal(ImageSource.RoleArtwork, studio.LargeImageSource);
        Assert.Equal(13, studio.States.Count);
    }

    [Fact]
    public void EditingDetails_SavesThatStatesTemplate_AndUpdatesThePreview()
    {
        var (studio, settings) = Create();

        studio.Details.Text = "Surviving the Fog";

        Assert.Equal("Surviving the Fog", Saved(settings, PresenceState.Running).Details);
        Assert.Equal("Surviving the Fog", studio.Preview.Details);
        Assert.Equal("17 of 128", studio.Details.Counter);
        Assert.Equal(FieldSeverity.None, studio.Details.Severity);
    }

    [Fact]
    public void SyntaxError_IsShownWithItsColumn()
    {
        var (studio, _) = Create();

        studio.StateLine.Text = "Playing [as {role}";

        Assert.Equal(FieldSeverity.Error, studio.StateLine.Severity);
        Assert.Contains("column", studio.StateLine.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownToken_IsAnError()
    {
        var (studio, _) = Create();

        studio.Details.Text = "Hunting {survivor_names}";

        Assert.Equal(FieldSeverity.Error, studio.Details.Severity);
    }

    [Fact]
    public void OverLongText_IsReportedAsShortened()
    {
        var (studio, _) = Create();

        studio.Details.Text = new string('x', 140);

        Assert.Equal(FieldSeverity.Error, studio.Details.Severity);
        Assert.Contains("shortened", studio.Details.Message, StringComparison.Ordinal);
        Assert.EndsWith("…", studio.Preview.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingState_LoadsThatStatesTemplate_AndExplainsReachability()
    {
        var (studio, _) = Create();

        Select(studio, PresenceState.InTrialKiller);

        Assert.Equal("Hunting[ on {map}]", studio.Details.Text);
        Assert.Equal(Reachability.NeedsDetectionAndRole, studio.SelectedState.Reachability);
        Assert.Contains("Needs screen reading", studio.ReachabilityNote, StringComparison.Ordinal);
        Assert.Equal("Needs screen reading", studio.SelectedState.Badge);
    }

    [Fact]
    public void ThePreview_UsesOnlyRealValues_SoAnUnknownMapIsLeftOut_AndNothingIsSaved()
    {
        var (studio, settings) = Create();
        var before = settings.UpdateCount;

        Select(studio, PresenceState.InTrialSurvivor);

        Assert.Equal("In a trial", studio.Preview.Details);
        Assert.Equal(before, settings.UpdateCount);
    }

    [Fact]
    public void EmptyState_FallsBack_AndSaysWhy()
    {
        var (studio, _) = Create();
        Select(studio, PresenceState.LobbyKiller);

        studio.Details.Text = string.Empty;

        Assert.Equal(FieldSeverity.Warning, studio.Details.Severity);
        Assert.Contains("“In a lobby as Killer” is empty", studio.FallbackExplanation, StringComparison.Ordinal);
        Assert.Contains("your “In a lobby” text is used", studio.FallbackExplanation, StringComparison.Ordinal);
        Assert.Equal("In a lobby", studio.Preview.Details);
    }

    [Fact]
    public void InvalidButton_IsNotSaved_UntilFixed()
    {
        var (studio, settings) = Create();

        studio.Button1Label = "Watch me";
        var writes = settings.UpdateCount;
        studio.Button1Url = "http://twitch.tv/me";
        Assert.NotNull(studio.ButtonsError);
        Assert.Equal(writes, settings.UpdateCount);
        Assert.Empty(Saved(settings, PresenceState.Running).Buttons);

        studio.Button1Url = "https://twitch.tv/me";
        Assert.Null(studio.ButtonsError);
        Assert.Equal([new ButtonTemplate("Watch me", "https://twitch.tv/me")], Saved(settings, PresenceState.Running).Buttons);
    }

    [Fact]
    public void ButtonLabel_OverDiscordsLimit_IsRejected()
    {
        var (studio, _) = Create();

        studio.Button1Url = "https://example.com";
        studio.Button1Label = new string('a', 33);

        Assert.Contains("too long", studio.ButtonsError, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedImage_IsSavedOnlyWhenValid()
    {
        var (studio, settings) = Create();

        studio.LargeImageSource = ImageSource.Fixed;
        var writes = settings.UpdateCount;
        studio.LargeImageValue = "Not A Key!";
        Assert.NotNull(studio.LargeImageError);
        Assert.Equal(writes, settings.UpdateCount);
        Assert.Equal(ImageSource.RoleArtwork, Saved(settings, PresenceState.Running).LargeImage.Source);

        studio.LargeImageValue = "my_banner";
        Assert.Null(studio.LargeImageError);
        Assert.Equal(new ImageSlot(ImageSource.Fixed, "my_banner"), Saved(settings, PresenceState.Running).LargeImage);
        Assert.Equal("my_banner", studio.Preview.LargeImage?.Reference);
        Assert.True(studio.Preview.LargeImage?.IsUnconfirmed);
    }

    [Fact]
    public void StreamerTarget_EditsTheStreamerTemplates_Only()
    {
        var (studio, settings) = Create();
        studio.Target = TemplateTarget.Streamer;

        studio.Details.Text = "Live now";

        Assert.Equal("Live now", Saved(settings, PresenceState.Running, streamer: true).Details);
        Assert.Equal("Playing Dead by Daylight", Saved(settings, PresenceState.Running).Details);
    }

    [Fact]
    public void ResetAllText_RestoresTheWholeEditedSet_OnlyForThatSet()
    {
        var (studio, settings) = Create();
        studio.Details.Text = "My own running text";
        Select(studio, PresenceState.Launching);
        studio.Details.Text = "My own launching text";
        studio.Target = TemplateTarget.Streamer;
        studio.Details.Text = "Live now";

        studio.ResetAllTextCommand.Execute(null);

        Assert.Equal("Playing Dead by Daylight", Saved(settings, PresenceState.Running, streamer: true).Details);
        Assert.Equal("My own running text", Saved(settings, PresenceState.Running).Details);
        Assert.Equal("My own launching text", Saved(settings, PresenceState.Launching).Details);

        studio.Target = TemplateTarget.Normal;
        studio.ResetAllTextCommand.Execute(null);

        Assert.Equal("Playing Dead by Daylight", Saved(settings, PresenceState.Running).Details);
        Assert.Equal("Launching Dead by Daylight", Saved(settings, PresenceState.Launching).Details);
        Assert.Contains(PresenceState.InTrialKiller, settings.Current.ActiveProfile.Templates.ByState.Keys);
    }

    [Fact]
    public void MomentNames_AndBadges_UsePlainWords()
    {
        var (studio, _) = Create();

        Assert.Equal(
            [
                "Game closed", "Game starting", "Playing (no screen reading)", "Main menu", "Looking for a match", "In a lobby", "In a lobby as Survivor",
                "In a lobby as Killer", "Loading", "In a match", "In a match as Survivor", "In a match as Killer", "After the match",
            ],
            studio.States.Select(s => s.Name));
        Assert.Equal(["Always available", "Needs screen reading"], studio.States.Select(s => s.Badge).Distinct());
        Assert.Equal(["My usual style", "Private style"], studio.Targets.Select(t => t.Label));
        Assert.DoesNotContain(studio.States, s => s.Name.Contains("trial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResetState_RestoresOnlyThatState()
    {
        var (studio, settings) = Create();
        studio.Details.Text = "Changed running";
        Select(studio, PresenceState.Launching);
        studio.Details.Text = "Changed launching";

        studio.ResetStateCommand.Execute(null);

        Assert.Equal("Launching Dead by Daylight", Saved(settings, PresenceState.Launching).Details);
        Assert.Equal("Changed running", Saved(settings, PresenceState.Running).Details);
        Assert.Equal("Launching Dead by Daylight", studio.Details.Text);
    }

    [Fact]
    public void ActivityKind_IsSavedOnTheProfile_AndHeadsThePreview()
    {
        var settings = new AppSettings { Discord = new DiscordSettings { UseOwnApplication = true, ApplicationName = "Dead by Daylight" } };
        var (studio, service) = Create(settings);

        studio.ActivityKind = Core.Presence.ActivityKind.Competing;

        Assert.Equal(Core.Presence.ActivityKind.Competing, service.Current.ActiveProfile.ActivityKind);
        Assert.Equal("Competing in Dead by Daylight", studio.Preview.Heading);
    }

    [Fact]
    public void ExternalChanges_ReloadTheEditor()
    {
        var (studio, settings) = Create();

        settings.Update(s => s with
        {
            Profiles = [.. s.Profiles.Select(p => p with { Templates = TemplatePresets.StreamerSafe })],
        });

        Assert.Equal("[As {role}]", studio.StateLine.Text);
    }

    private static readonly string[] s_expectedDetailLabels = ["Time played", "Role", "Survivors out", "Survivors on hooks"];

    [Fact]
    public void TokenPalette_ListsEveryKnownToken_WithItsAvailability()
    {
        var (studio, _) = Create();

        Assert.Contains(studio.Tokens, t => t is { Token: "{map}", Label: "Map" } && t.Description.Contains("sure", StringComparison.Ordinal));
        Assert.DoesNotContain(studio.Tokens, t => t.Token == "{killer}");
        Assert.Equal(
            s_expectedDetailLabels.Order(StringComparer.Ordinal),
            studio.Tokens.Where(t => t.Token is "{duration}" or "{role}" or "{status_dead}" or "{status_hooked}").Select(t => t.Label).Order(StringComparer.Ordinal));
        Assert.Equal("Insert detail: Map", studio.Tokens.Single(t => t.Token == "{map}").AutomationName);
        Assert.DoesNotContain(studio.Tokens, t => t.Description.Contains("manual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MemberListDisplay_DefaultIsARealChoice_AndMapsToTheProfile()
    {
        var (studio, settings) = Create();
        Assert.Equal(MemberListDisplay.DiscordDefault, studio.StatusDisplay);
        Assert.Contains(studio.StatusDisplays, c => c.Value == MemberListDisplay.DiscordDefault);

        studio.StatusDisplay = MemberListDisplay.State;
        Assert.Equal(Core.Presence.StatusDisplay.State, settings.Current.ActiveProfile.StatusDisplay);

        studio.StatusDisplay = MemberListDisplay.DiscordDefault;
        Assert.Null(settings.Current.ActiveProfile.StatusDisplay);
    }

    [Fact]
    public void TheCounter_IsOneNumber_TheStricterOfCharactersAndBytes_AndNeverMentionsBytes()
    {
        var (studio, _) = Create();

        studio.Details.Text = "Playing é";

        // 9 characters but 10 UTF-8 bytes: one number, the larger.
        Assert.Equal("10 of 128", studio.Details.Counter);
        Assert.DoesNotContain("byte", studio.Details.Counter, StringComparison.OrdinalIgnoreCase);
    }
}
