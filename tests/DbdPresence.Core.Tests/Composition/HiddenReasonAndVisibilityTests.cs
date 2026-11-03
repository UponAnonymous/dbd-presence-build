// Covers: FR-VIS-01 (ShowMode, ShowScene and ShowResult switches, all on by default), FR-DET-12 as amended by D30 (role, character and game mode come only
// from confirmed detection; the old declared details are ignored), docs/design/whole-app.md §3.4 (the composer reports why something is missing as
// codes) and §11 (mode token that honours ShowMode).
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Core.Templates;

namespace DbdPresence.Core.Tests.Composition;

public sealed class HiddenReasonAndVisibilityTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);

    private static readonly PresenceProfile s_profile = new()
    {
        Artwork = new ArtworkSettings { ConfirmedUploadedKeys = ["app_logo", "role_survivor", "role_killer"] },
    };

    private static PresenceInputs Inputs(PresenceState state) => new() { State = state, Now = s_now, GameStartedUtc = s_now.AddMinutes(-30) };

    private static PresenceProfile WithTemplate(PresenceState state, PresenceTemplate template, VisibilitySettings? visibility = null) => s_profile with
    {
        Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Running] = new() { Details = "Playing Dead by Daylight" }, [state] = template } },
        Visibility = visibility ?? new VisibilitySettings(),
    };

    [Fact]
    public void NewVisibilitySwitches_AreOnByDefault()
    {
        var visibility = new VisibilitySettings();

        Assert.True(visibility.ShowMode);
        Assert.True(visibility.ShowScene);
        Assert.True(visibility.ShowResult);
    }

    [Fact]
    public void ModeToken_ComesFromTheConfirmedMode_AndIsLeftOutWhenNoneIsConfirmed()
    {
        var profile = WithTemplate(PresenceState.Running, new PresenceTemplate { Details = "Playing Dead by Daylight", State = "[Mode: {mode}]" });

        var withMode = PresenceComposer.Compose(Inputs(PresenceState.Running) with { ModeName = "2v8" }, profile, new PrivacyState());
        var without = PresenceComposer.Compose(Inputs(PresenceState.Running), profile, new PrivacyState());

        Assert.Equal("Mode: 2v8", withMode.Activity!.State);
        Assert.Null(without.Activity!.State);
        Assert.Contains(without.Reasons, r => r is { Code: HiddenReasonCode.DetailNotConfirmed, Subject: "Game mode" });
    }

    [Fact]
    public void ShowModeOff_HidesTheModeAndSaysSo_NotThatItIsStillLearning()
    {
        var profile = WithTemplate(
            PresenceState.Running,
            new PresenceTemplate { Details = "Playing Dead by Daylight", State = "[Mode: {mode}]" },
            new VisibilitySettings { ShowMode = false });

        var result = PresenceComposer.Compose(Inputs(PresenceState.Running) with { ModeName = "1v4" }, profile, new PrivacyState());

        Assert.Null(result.Activity!.State);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.ModeHiddenByUser);
        Assert.DoesNotContain(result.Reasons, r => r.Code == HiddenReasonCode.DetailNotConfirmed);
    }

    [Fact]
    public void Character_ComesOnlyFromDetection()
    {
        var profile = WithTemplate(PresenceState.Running, new PresenceTemplate { Details = "Playing Dead by Daylight", State = "[As {character}]" });

        var detected = PresenceComposer.Compose(Inputs(PresenceState.Running) with { CharacterName = "Meg Thomas" }, profile, new PrivacyState());
        var none = PresenceComposer.Compose(Inputs(PresenceState.Running), profile, new PrivacyState());

        Assert.Equal("As Meg Thomas", detected.Activity!.State);
        Assert.Null(none.Activity!.State);
    }

    [Fact]
    public void OldDeclaredDetails_AreIgnored()
    {
#pragma warning disable CS0618 // The point of the test: an older settings file may still carry declared details.
        var profile = s_profile with { Manual = new ManualDetails { Role = Role.Killer, Character = "The Trapper", Mode = GameMode.Custom, RoleSource = RoleSource.Manual } };
#pragma warning restore CS0618

        var result = PresenceComposer.Compose(Inputs(PresenceState.Running), profile, new PrivacyState());

        Assert.Null(result.Activity!.State);
        Assert.Equal("app_logo", result.Activity.LargeImage);
    }

    [Fact]
    public void ShowSceneOff_FallsBackToThePlainPlayingTextForEveryDetectionMoment()
    {
        var profile = s_profile with { Visibility = new VisibilitySettings { ShowScene = false } };

        var lobby = PresenceComposer.Compose(Inputs(PresenceState.LobbySurvivor) with { DetectedRole = Role.Survivor }, profile, new PrivacyState());
        var inMatch = PresenceComposer.Compose(Inputs(PresenceState.InTrialKiller) with { DetectedRole = Role.Killer }, profile, new PrivacyState());

        Assert.Equal("Playing Dead by Daylight", lobby.Activity!.Details);
        Assert.Equal("Playing Dead by Daylight", inMatch.Activity!.Details);
        Assert.Equal(PresenceState.Running, lobby.TemplateState);
        Assert.Contains(lobby.Reasons, r => r.Code == HiddenReasonCode.SceneHiddenByUser);
    }

    [Fact]
    public void ShowSceneOff_DoesNotAffectTheGameClosedOrStartingMoments()
    {
        var profile = s_profile with
        {
            Visibility = new VisibilitySettings { ShowScene = false },
            WhenNotRunning = NotRunningBehavior.ShowTemplate,
        };

        var closed = PresenceComposer.Compose(Inputs(PresenceState.NotRunning), profile, new PrivacyState());
        var starting = PresenceComposer.Compose(Inputs(PresenceState.Launching), profile, new PrivacyState());

        Assert.Equal("Taking a break from the Fog", closed.Activity!.Details);
        Assert.Equal("Launching Dead by Daylight", starting.Activity!.Details);
        Assert.DoesNotContain(closed.Reasons, r => r.Code == HiddenReasonCode.SceneHiddenByUser);
    }

    [Fact]
    public void ShowResultOff_DropsTheResultFromTheText()
    {
        var inputs = Inputs(PresenceState.Results) with { Result = "Escaped" };

        var shown = PresenceComposer.Compose(inputs, s_profile, new PrivacyState());
        var hidden = PresenceComposer.Compose(inputs, s_profile with { Visibility = new VisibilitySettings { ShowResult = false } }, new PrivacyState());

        Assert.Equal("Escaped", shown.Activity!.State);
        Assert.Null(hidden.Activity!.State);
        Assert.Contains(hidden.Reasons, r => r.Code == HiddenReasonCode.ResultHiddenByUser);
    }

    [Theory]
    [InlineData(true, false, false, false, HiddenReasonCode.AccessWithdrawn)]
    [InlineData(false, true, false, false, HiddenReasonCode.HiddenCompletely)]
    [InlineData(false, false, true, false, HiddenReasonCode.Paused)]
    public void ClearingReasons_AreReportedAsCodes(bool suspended, bool hide, bool paused, bool streamer, HiddenReasonCode expected)
    {
        var result = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile, new PrivacyState(paused, streamer, hide, suspended));

        Assert.Null(result.Activity);
        Assert.Contains(result.Reasons, r => r.Code == expected);
    }

    [Fact]
    public void GameNotRunning_IsAReasonWhenTheUserShowsNothingThen()
    {
        var result = PresenceComposer.Compose(Inputs(PresenceState.NotRunning), s_profile, new PrivacyState());

        Assert.Null(result.Activity);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.GameNotRunning);
    }

    [Fact]
    public void GenericPause_AndPrivateStyle_AreReasons()
    {
        var paused = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile with { WhenPaused = PauseBehavior.Generic }, new PrivacyState(Paused: true));
        var privateStyle = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile, new PrivacyState(StreamerMode: true));

        Assert.Contains(paused.Reasons, r => r.Code == HiddenReasonCode.PausedGeneric);
        Assert.Contains(privateStyle.Reasons, r => r.Code == HiddenReasonCode.PrivateStyle);
    }

    [Fact]
    public void MapAndStatsAndTimer_ReasonsNameWhatTheUserTurnedOff()
    {
        var inputs = Inputs(PresenceState.InTrialSurvivor) with { DetectedRole = Role.Survivor, MapName = "Coal Tower", GeneratorsRemaining = 4, MatchStartedUtc = s_now.AddMinutes(-3) };
        var profile = s_profile with { Visibility = new VisibilitySettings { ShowMap = false, ShowTimers = false } };

        var result = PresenceComposer.Compose(inputs, profile, new PrivacyState());

        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.MapHiddenByUser);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.LiveStatsOff);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.TimerHiddenByUser);
    }

    [Fact]
    public void MapHeldBackUntilTheMatchEnds_AndApproximateTimer_AreSeparateReasons()
    {
        var inputs = Inputs(PresenceState.InTrialSurvivor) with
        {
            DetectedRole = Role.Survivor,
            MapName = "Coal Tower",
            MatchStartedUtc = s_now.AddMinutes(-3),
            MatchStartApproximate = true,
        };
        var profile = s_profile with { Visibility = new VisibilitySettings { PostMatchDetailsOnly = true } };

        var result = PresenceComposer.Compose(inputs, profile, new PrivacyState());

        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.MapAfterMatch);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.TimerStartNotSeen);
    }

    [Fact]
    public void RolePicture_WithoutARole_SaysItNeedsTheRole()
    {
        var result = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile, new PrivacyState());

        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.RolePictureNeedsRole);
    }

    [Fact]
    public void UnconfirmedPictureName_IsAReasonWithTheName()
    {
        var profile = s_profile with { Artwork = new ArtworkSettings { ConfirmedUploadedKeys = [] } };

        var result = PresenceComposer.Compose(Inputs(PresenceState.Running), profile, new PrivacyState());

        Assert.Contains(result.Reasons, r => r is { Code: HiddenReasonCode.PictureMayNotExist, Subject: "app_logo" });
    }

    [Fact]
    public void TextFallback_AndNothingToShow_AreReported()
    {
        var empty = s_profile with { Templates = new TemplateSet() };
        var onlyRunning = s_profile with
        {
            Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Running] = new() { Details = "Playing Dead by Daylight" } } },
        };
        var fallback = PresenceComposer.Compose(Inputs(PresenceState.Lobby), onlyRunning, new PrivacyState());
        var nothing = PresenceComposer.Compose(Inputs(PresenceState.Lobby), empty, new PrivacyState());

        Assert.Contains(fallback.Reasons, r => r.Code == HiddenReasonCode.TextFallback);
        Assert.Contains(nothing.Reasons, r => r.Code == HiddenReasonCode.NothingToShow);
    }

    [Fact]
    public void Reasons_AreNotRepeated()
    {
        var result = PresenceComposer.Compose(Inputs(PresenceState.Running), s_profile, new PrivacyState());

        Assert.Equal(result.Reasons.Count, result.Reasons.Distinct().Count());
    }

    [Fact]
    public void KnownTokens_NoLongerMentionDeclaringByHand()
    {
        Assert.DoesNotContain("manually", TemplateTokens.Known[TemplateTokens.Character], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("set by you", TemplateTokens.Known[TemplateTokens.Role], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manually", TemplateTokens.Known[TemplateTokens.Mode], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivateStyle_RefusesMapAndCharacterArtAndTokens_WhateverTheTemplateSays()
    {
        var template = new PresenceTemplate
        {
            Details = "In a match[ on {map}]",
            State = "[{character}][ · {generators} gens]",
            LargeImage = ImageSlot.Map,
            LargeText = "[{map}][ · {realm}]",
            SmallImage = ImageSlot.Character,
            SmallText = "[{character}]",
        };
        var profile = s_profile with
        {
            Artwork = new ArtworkSettings { CharacterImage = "https://cdn.example.org/char.png", Maps = new Dictionary<string, string> { ["coal"] = "https://cdn.example.org/map.png" }, Fallback = "app_logo" },
            Visibility = new VisibilitySettings { ShowLiveMatchStats = true },
            StreamerTemplates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Running] = template, [PresenceState.InTrial] = template } },
        };
        var inputs = Inputs(PresenceState.InTrial) with
        {
            MapName = "Coal Tower",
            MapId = "coal",
            RealmName = "Coal",
            CharacterName = "Meg Thomas",
            GeneratorsRemaining = 3,
            StatusDead = 1,
        };

        var result = PresenceComposer.Compose(inputs, profile, new PrivacyState(StreamerMode: true));

        var activity = result.Activity!;
        Assert.Equal("In a match", activity.Details);
        Assert.Null(activity.State);
        Assert.Null(activity.LargeText);
        Assert.Null(activity.SmallText);
        Assert.Equal("app_logo", activity.LargeImage); // the map picture is refused; the plain fallback is used
        Assert.Null(activity.SmallImage);
        Assert.DoesNotContain(result.Reasons, r => r.Code is HiddenReasonCode.MapHiddenByUser or HiddenReasonCode.CharacterHiddenByUser or HiddenReasonCode.LiveStatsOff);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.PrivateStyle);
    }
}
