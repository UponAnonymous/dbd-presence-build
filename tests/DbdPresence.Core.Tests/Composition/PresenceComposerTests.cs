// Covers: FR-TPL-01/03 (state templates, fallback chain), FR-RPC-06 (not running), FR-VIS-01..04 (visibility,
// streamer, pause, post-match only), FR-ART-04/05 (artwork mapping, unconfirmed keys), FR-DET-08 (approximate timers),
// NFR-ACC-03 (only confirmed inputs are ever published), FR-RPC-12 (Behaviour artwork labelling), FR-RPC-13 (display name).
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Settings;

namespace DbdPresence.Core.Tests.Composition;

public sealed class PresenceComposerTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_gameStart = s_now.AddMinutes(-42);

    private static readonly PresenceProfile s_profile = new()
    {
        Artwork = new ArtworkSettings { ConfirmedUploadedKeys = ["app_logo", "role_survivor", "role_killer"] },
    };

    private static PresenceInputs Inputs(PresenceState state, Role role = Role.None) =>
        new() { State = state, Now = s_now, GameStartedUtc = s_gameStart, DetectedRole = role };

    private static CompositionResult Compose(PresenceInputs inputs, PresenceProfile? profile = null, PrivacyState? privacy = null) =>
        PresenceComposer.Compose(inputs, profile ?? s_profile, privacy ?? new PrivacyState());

    [Fact]
    public void NotRunning_DefaultBehaviour_ClearsActivity()
    {
        var result = Compose(Inputs(PresenceState.NotRunning));

        Assert.Null(result.Activity);
        Assert.Contains(result.Notes, n => n.Contains("cleared", StringComparison.Ordinal));
    }

    [Fact]
    public void NotRunning_ShowTemplate_PublishesNotRunningTemplateWithoutTimer()
    {
        var result = Compose(Inputs(PresenceState.NotRunning), s_profile with { WhenNotRunning = NotRunningBehavior.ShowTemplate });

        Assert.Equal("Taking a break from the Fog", result.Activity!.Details);
        Assert.Null(result.Activity.StartTimestamp);
    }

    [Fact]
    public void Running_WithDetectedRole_ShowsRoleTextArtworkAndSessionTimer()
    {
        var result = Compose(Inputs(PresenceState.Running, Role.Killer));

        var activity = result.Activity!;
        Assert.Equal(PresenceState.Running, result.TemplateState);
        Assert.Equal("Playing Dead by Daylight", activity.Details);
        Assert.Equal("As Killer", activity.State);
        Assert.Equal("role_killer", activity.LargeImage);
        Assert.Equal("Killer", activity.LargeText);
        Assert.Equal(s_gameStart, activity.StartTimestamp);
    }

    [Fact]
    public void Running_WithoutRole_DropsOptionalSegmentsAndUsesFallbackArt()
    {
        var activity = Compose(Inputs(PresenceState.Running)).Activity!;

        Assert.Equal("Playing Dead by Daylight", activity.Details);
        Assert.Null(activity.State);
        Assert.Equal("app_logo", activity.LargeImage);
        Assert.Null(activity.LargeText);
    }

    [Fact]
    public void HiddenRole_IsNotLeakedThroughTextOrArtwork()
    {
        var profile = s_profile with { Visibility = new VisibilitySettings { ShowRole = false } };

        var activity = Compose(Inputs(PresenceState.Running, Role.Killer), profile).Activity!;

        Assert.Null(activity.State);
        Assert.Equal("app_logo", activity.LargeImage);
    }

    [Fact]
    public void EmptyRoleSpecificTemplate_FallsBackToGenericState()
    {
        var templates = TemplatePresets.Detailed with
        {
            ByState = new Dictionary<PresenceState, PresenceTemplate>(TemplatePresets.Detailed.ByState) { [PresenceState.LobbyKiller] = new() },
        };

        var result = Compose(Inputs(PresenceState.LobbyKiller, Role.Killer), s_profile with { Templates = templates });

        Assert.Equal(PresenceState.Lobby, result.TemplateState);
        Assert.Contains(result.Notes, n => n.Contains("LobbyKiller template is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void InTrial_MapOnlyAppearsWhenConfirmed()
    {
        var withoutMap = Compose(Inputs(PresenceState.InTrialKiller, Role.Killer)).Activity!;
        var withMap = Compose(Inputs(PresenceState.InTrialKiller, Role.Killer) with { MapName = "Coal Tower" }).Activity!;

        Assert.Equal("Hunting", withoutMap.Details);
        Assert.Equal("Hunting on Coal Tower", withMap.Details);
    }

    [Fact]
    public void LiveMatchStats_HiddenByDefault_ShownWhenEnabled()
    {
        var inputs = Inputs(PresenceState.InTrialSurvivor) with { GeneratorsRemaining = 3 };
        var enabled = s_profile with { Visibility = new VisibilitySettings { ShowLiveMatchStats = true } };

        var hidden = Compose(inputs);
        var shown = Compose(inputs, enabled);

        Assert.Null(hidden.Activity!.State);
        Assert.Contains(hidden.Notes, n => n.Contains("Live match stats hidden", StringComparison.Ordinal));
        Assert.Equal("3 gens left", shown.Activity!.State);
    }

    [Fact]
    public void PostMatchDetailsOnly_WithholdsMapDuringTrialButNotOnResults()
    {
        var profile = s_profile with { Visibility = new VisibilitySettings { PostMatchDetailsOnly = true } };

        var inTrial = Compose(Inputs(PresenceState.InTrial) with { MapName = "Coal Tower" }, profile);
        var results = Compose(Inputs(PresenceState.Results) with { MapName = "Coal Tower", Result = "Escaped" }, profile);

        Assert.Equal("In a trial", inTrial.Activity!.Details);
        Assert.Contains(inTrial.Notes, n => n.Contains("withheld until the trial ends", StringComparison.Ordinal));
        Assert.Equal("Escaped", results.Activity!.State);
    }

    [Fact]
    public void Paused_DefaultClears_GenericModeDropsMatchDetails()
    {
        var inputs = Inputs(PresenceState.InTrialKiller, Role.Killer) with { MapName = "Coal Tower", MatchStartedUtc = s_now.AddMinutes(-5) };

        var cleared = Compose(inputs, privacy: new PrivacyState(Paused: true));
        var generic = Compose(inputs, s_profile with { WhenPaused = PauseBehavior.Generic }, new PrivacyState(Paused: true));

        Assert.Null(cleared.Activity);
        Assert.Equal(PresenceState.Running, generic.TemplateState);
        Assert.DoesNotContain("Coal Tower", generic.Activity!.Details ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void HideFullActivity_AlwaysClears()
    {
        Assert.Null(Compose(Inputs(PresenceState.Running), privacy: new PrivacyState(HideFullActivity: true)).Activity);
    }

    [Fact]
    public void StreamerMode_UsesStreamerTemplatesWithoutTimer()
    {
        var result = Compose(Inputs(PresenceState.Running, Role.Survivor), privacy: new PrivacyState(StreamerMode: true));

        Assert.Equal("As Survivor", result.Activity!.State);
        Assert.Null(result.Activity.StartTimestamp);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ApproximateMatchStart_TimerOnlyWhenAllowed(bool allowApproximate, bool expectTimer)
    {
        var inputs = Inputs(PresenceState.InTrial) with { MatchStartedUtc = s_now.AddMinutes(-7), MatchStartApproximate = true };
        var profile = s_profile with { Visibility = new VisibilitySettings { AllowApproximateDurations = allowApproximate } };

        var activity = Compose(inputs, profile).Activity!;

        Assert.Equal(expectTimer, activity.StartTimestamp is not null);
    }

    [Fact]
    public void UnconfirmedAssetKey_ProducesWarning_OrFallbackWhenReplacing()
    {
        var unconfirmed = s_profile with { Artwork = new ArtworkSettings { ConfirmedUploadedKeys = ["app_logo"] } };
        var replacing = unconfirmed with { Artwork = unconfirmed.Artwork with { ReplaceUnconfirmedKeys = true } };

        var warned = Compose(Inputs(PresenceState.Running, Role.Killer), unconfirmed);
        var replaced = Compose(Inputs(PresenceState.Running, Role.Killer), replacing);

        Assert.Equal("role_killer", warned.Activity!.LargeImage);
        Assert.Contains(warned.Notes, n => n.Contains("not confirmed as uploaded", StringComparison.Ordinal));
        Assert.Equal("app_logo", replaced.Activity!.LargeImage);
    }

    [Fact]
    public void HttpsArtwork_IsNotTreatedAsAnAssetKey()
    {
        var profile = s_profile with
        {
            Artwork = s_profile.Artwork with { RoleKiller = "https://cdn.example.com/killer.png", ReplaceUnconfirmedKeys = true },
        };

        Assert.Equal("https://cdn.example.com/killer.png", Compose(Inputs(PresenceState.Running, Role.Killer), profile).Activity!.LargeImage);
    }

    [Fact]
    public void UnusableTemplates_ClearInsteadOfPublishingSomethingWrong()
    {
        var broken = new TemplateSet
        {
            ByState = new Dictionary<PresenceState, PresenceTemplate>
            {
                [PresenceState.Running] = new() { Details = "On {map}" },
                [PresenceState.Lobby] = new() { Details = "{unknown_token" },
            },
        };

        var result = Compose(Inputs(PresenceState.Lobby), s_profile with { Templates = broken });

        Assert.Null(result.Activity);
        Assert.Contains(result.Notes, n => n.Contains("No usable template", StringComparison.Ordinal));
    }

    [Fact]
    public void Duration_IsMinuteResolution()
    {
        Assert.Equal("0 min", PresenceComposer.FormatDuration(TimeSpan.FromSeconds(59)));
        Assert.Equal("42 min", PresenceComposer.FormatDuration(TimeSpan.FromMinutes(42.9)));
        Assert.Equal("1 h 5 min", PresenceComposer.FormatDuration(TimeSpan.FromMinutes(65)));
        Assert.Equal("0 min", PresenceComposer.FormatDuration(TimeSpan.FromMinutes(-3)));
    }

    private static PresenceProfile WithLargeText(string largeText, PresenceProfile profile)
    {
        var running = profile.Templates.ByState[PresenceState.Running] with { LargeText = largeText };
        return profile with
        {
            Templates = profile.Templates with
            {
                ByState = new Dictionary<PresenceState, PresenceTemplate>(profile.Templates.ByState) { [PresenceState.Running] = running },
            },
        };
    }

    [Fact]
    public void DisplayName_IsSentAsTheNameOverride_AndTheProfilesOwnOverrideWins()
    {
        var plain = Compose(Inputs(PresenceState.Running)).Activity!;
        var named = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile, new PrivacyState(), "Fog Watcher").Activity!;
        var both = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile with { NameOverride = "Profile Name" }, new PrivacyState(), "Fog Watcher").Activity!;
        var blank = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile, new PrivacyState(), "   ").Activity!;

        Assert.Null(plain.NameOverride);
        Assert.Equal("Fog Watcher", named.NameOverride);
        Assert.Equal("Profile Name", both.NameOverride);
        Assert.Null(blank.NameOverride);
    }

    [Fact]
    public void OwnArtwork_GetsNoBehaviourLabel()
    {
        var activity = Compose(Inputs(PresenceState.Running), WithLargeText("Hello fog", s_profile)).Activity!;

        Assert.Equal("Hello fog", activity.LargeText);
    }

    [Fact]
    public void BehaviourArtwork_AddsTheLabelToTheLargeImageText()
    {
        var profile = WithLargeText("Hello fog", s_profile with { Artwork = s_profile.Artwork with { IsBehaviourArtwork = true } });

        var activity = Compose(Inputs(PresenceState.Running), profile).Activity!;

        Assert.Equal("Hello fog · Unofficial · © Behaviour Interactive", activity.LargeText);
        Assert.Equal(PresenceComposer.BehaviourLabel, "Unofficial · © Behaviour Interactive");
    }

    [Fact]
    public void BehaviourArtwork_WithNoOtherHoverText_ShowsJustTheLabel()
    {
        var profile = WithLargeText(string.Empty, s_profile with { Artwork = s_profile.Artwork with { IsBehaviourArtwork = true } });

        var activity = Compose(Inputs(PresenceState.Running), profile).Activity!;

        Assert.Equal(PresenceComposer.BehaviourLabel, activity.LargeText);
    }

    [Fact]
    public void BehaviourArtwork_LongUserText_IsShortenedNotTheLabel()
    {
        var profile = WithLargeText(new string('w', 300), s_profile with { Artwork = s_profile.Artwork with { IsBehaviourArtwork = true } });

        var result = Compose(Inputs(PresenceState.Running), profile);

        Assert.EndsWith(PresenceComposer.BehaviourLabel, result.Activity!.LargeText, StringComparison.Ordinal);
        Assert.True(TextFit.Fits(result.Activity.LargeText!, 128, 128));
        Assert.DoesNotContain(result.Issues, i => i.Field == ActivityField.LargeText);
    }

    [Fact]
    public void BehaviourArtwork_LabelIsNotRepeated()
    {
        var profile = WithLargeText("Cool · Unofficial · © Behaviour Interactive", s_profile with { Artwork = s_profile.Artwork with { IsBehaviourArtwork = true } });

        var activity = Compose(Inputs(PresenceState.Running), profile).Activity!;

        Assert.Equal("Cool · Unofficial · © Behaviour Interactive", activity.LargeText);
    }

    [Fact]
    public void BehaviourArtwork_FlagIsNotPartOfTheSavedProfile()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ArtworkSettings { IsBehaviourArtwork = true });

        Assert.DoesNotContain("IsBehaviourArtwork", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposedText_OnlyContainsValuesFromInputsOrTemplates()
    {
        // Property-style guard for NFR-ACC-03: every map/result/count string that appears must come from the inputs.
        var random = new Random(7);
        string[] maps = ["Coal Tower", "Dead Dawg Saloon", "The Game"];
        var profile = s_profile with { Visibility = new VisibilitySettings { ShowLiveMatchStats = true } };
        foreach (var state in Enum.GetValues<PresenceState>())
        {
            for (var i = 0; i < 50; i++)
            {
                var map = random.Next(2) == 0 ? null : maps[random.Next(maps.Length)];
                var gens = random.Next(2) == 0 ? (int?)null : random.Next(0, 6);
                var result = Compose(Inputs(state) with { MapName = map, GeneratorsRemaining = gens }, profile);
                var text = $"{result.Activity?.Details} {result.Activity?.State}";
                foreach (var other in maps.Where(m => m != map))
                {
                    Assert.DoesNotContain(other, text, StringComparison.Ordinal);
                }

                if (gens is null)
                {
                    Assert.DoesNotContain("gens left", text, StringComparison.Ordinal);
                }
            }
        }
    }
}
