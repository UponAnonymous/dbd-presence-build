// Covers: FR-DET-02 (InMatchMenu scene and the Lobby -> InTrial edge), FR-DET-18 to FR-DET-22 (engine side of the owner's character, queue mode and
// killer name, ARCHITECTURE §13.1). Mechanics only: scripted observation sequences on a fake clock, not measurements.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Tests.Detection;

public sealed class MatchStateEngineHudCatalogueTests
{
    private const string MapA = "coldwind-farm/rotten-fields";
    private const string Lich = "the-lich";
    private const string Dwight = "dwight-fairfield";

    // ---- InMatchMenu ---------------------------------------------------------------------------------------------

    [Fact]
    public void OpeningTab_InsideATrial_KeepsTheSessionItsStartAndTheMap()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.Map, MapA, 3);
        var session = h.Snapshot.Session!;
        Assert.Equal(MapA, h.Snapshot.Map.ConfirmedValue);

        h.ConfirmScene(Scene.InMatchMenu);

        Assert.Equal(Scene.InMatchMenu, h.Snapshot.ConfirmedScene);
        Assert.Equal(GamePhase.InMatch, h.Snapshot.Phase);
        Assert.Equal(session, h.Snapshot.Session);
        Assert.Equal(MapA, h.Snapshot.Map.ConfirmedValue);
        Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);

        h.ConfirmScene(Scene.InTrial);
        Assert.Equal(session, h.Snapshot.Session);
        Assert.Equal(MapA, h.Snapshot.Map.ConfirmedValue);
    }

    [Fact]
    public void TabOpenedAsTheFirstThingSeen_OpensALateSession_AndTheMapIsAcceptedFromIt()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.InMatchMenu);

        Assert.Equal(StartQuality.JoinedLate, h.Snapshot.Session!.Quality);
        h.Frames(ObservationKind.Map, MapA, 3);
        Assert.Equal(MapA, h.Snapshot.Map.ConfirmedValue);
    }

    [Fact]
    public void LeavingTheTrialThroughTheMenu_EndsTheSessionOnlyOnPositiveEvidence()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.ConfirmScene(Scene.InMatchMenu);
        h.ConfirmScene(Scene.MainMenu); // QUIT from the menu

        Assert.Equal(Scene.MainMenu, h.Snapshot.ConfirmedScene);
        Assert.Null(h.Snapshot.Session);
    }

    [Fact]
    public void TheMenuOverASpectatedTrial_IsLegalBothWays()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.ConfirmScene(Scene.Spectating);
        h.ConfirmScene(Scene.InMatchMenu);
        h.ConfirmScene(Scene.Spectating);

        Assert.Equal(Scene.Spectating, h.Snapshot.ConfirmedScene);
        Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
    }

    [Fact]
    public void RoleIsAccepted_InTheMenuOverATrial_AndOnTheScoreboard()
    {
        var menu = new EngineHarness();
        menu.EnterTrial();
        menu.ConfirmScene(Scene.InMatchMenu);
        menu.Frames(ObservationKind.Role, "Killer", 3);
        Assert.Equal(Role.Killer, menu.Snapshot.DetectedRole);

        var results = new EngineHarness();
        results.EnterTrial();
        results.ConfirmScene(Scene.Results);
        results.Frames(ObservationKind.Role, "Survivor", 3);
        Assert.Equal(Role.Survivor, results.Snapshot.DetectedRole);
    }

    // ---- Lobby -> InTrial ----------------------------------------------------------------------------------------

    [Fact]
    public void LobbyThenTrial_WithinTheWindow_IsALegalEdge_AndAnExactStart()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Advance(20); // the loading screen has no cue; the lobby's last support is 20 s before the match start card
        h.Engine.Tick();
        Assert.Equal(Scene.Unknown, h.Snapshot.ConfirmedScene); // the lobby was lost while the loading screen was up
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(Scene.InTrial, h.Snapshot.ConfirmedScene);
        Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
        Assert.Equal(StartQuality.Exact, h.Snapshot.Session!.Quality);
    }

    [Fact]
    public void LobbyThenTrial_WithoutLosingTheLobby_IsALegalEdge_AndAnExactStart()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Advance(5);
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
        Assert.Equal(StartQuality.Exact, h.Snapshot.Session!.Quality);
    }

    [Fact]
    public void WeakTrialCueFramesOnly_AfterALobby_NeverReachInTrial()
    {
        // Review U5 F1 / F5(g): a lobby whose anchors went unread plus text in the HUD rows gives only weak trial readings (the HUD detector scores
        // them 0.65 and 0.75, both below the menu-to-trial floor). They must not confirm the trial, not even with doubled evidence.
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);

        for (var i = 0; i < 7; i++)
        {
            h.SceneFrame(Scene.InTrial, i % 2 == 0 ? 0.75 : 0.65);
            Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);
        }

        Assert.Null(h.Snapshot.Session);
        Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
    }

    [Theory]
    [InlineData(Scene.MainMenu, Scene.InTrial)]
    [InlineData(Scene.Lobby, Scene.Spectating)]
    [InlineData(Scene.Matchmaking, Scene.InTrial)]
    [InlineData(Scene.Other, Scene.Spectating)]
    [InlineData(Scene.Results, Scene.InTrial)]
    public void WeakCuesNeverCarryAMenuSceneIntoATrialOrSpectating(Scene menu, Scene trialLike)
    {
        var h = new EngineHarness();
        h.ConfirmScene(menu);

        for (var i = 0; i < 7; i++)
        {
            h.SceneFrame(trialLike, 0.75);
        }

        Assert.Equal(menu, h.Snapshot.ConfirmedScene);
        Assert.Null(h.Snapshot.Session);
    }

    [Fact]
    public void AStrongTrialCue_CarriesTheLobbyIntoTheTrial_AtTheFloor_ButNotJustBelowIt()
    {
        var options = new EngineOptions();
        var below = new EngineHarness();
        below.ConfirmScene(Scene.Lobby);
        for (var i = 0; i < 3; i++)
        {
            below.SceneFrame(Scene.InTrial, options.TrialEntryFromMenuMinScore - 0.01);
        }

        var at = new EngineHarness();
        at.ConfirmScene(Scene.Lobby);
        for (var i = 0; i < 3; i++)
        {
            at.SceneFrame(Scene.InTrial, options.TrialEntryFromMenuMinScore);
        }

        Assert.Equal(Scene.Lobby, below.Snapshot.ConfirmedScene);
        Assert.Equal(Scene.InTrial, at.Snapshot.ConfirmedScene);
        Assert.Equal(StartQuality.Exact, at.Snapshot.Session!.Quality);
    }

    [Fact]
    public void OneWeakFrameAmongStrongOnes_BlocksTheMenuToTrialEdge_UntilTheStrongOnesAreAgainEnough()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);

        h.SceneFrame(Scene.InTrial, 0.95);
        h.SceneFrame(Scene.InTrial, 0.65);
        h.SceneFrame(Scene.InTrial, 0.95);
        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);

        h.SceneFrame(Scene.InTrial, 0.95);
        h.SceneFrame(Scene.InTrial, 0.95);
        h.SceneFrame(Scene.InTrial, 0.95);
        Assert.Equal(Scene.InTrial, h.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void WeakTrialCues_StillConfirmTheTrial_OnceTheLobbyEvidenceHasLapsed_BecauseLoadingHasNoCue()
    {
        // By design: after the pre-match lobby the loading screen shows nothing the detectors can read, the lobby is lost to Unknown, and the
        // trial HUD (a weak cue) is then the only evidence of the trial. The ordinary scene floor applies from Unknown.
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Advance(20);
        h.Engine.Tick();
        Assert.Equal(Scene.Unknown, h.Snapshot.ConfirmedScene);

        for (var i = 0; i < 3; i++)
        {
            h.SceneFrame(Scene.InTrial, 0.75);
        }

        Assert.Equal(Scene.InTrial, h.Snapshot.ConfirmedScene);
        Assert.Equal(StartQuality.Exact, h.Snapshot.Session!.Quality);
    }

    [Fact]
    public void TheMenuToTrialFloor_MustBeAFraction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineOptions { TrialEntryFromMenuMinScore = 1.5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineOptions { TrialEntryFromMenuMinScore = double.NaN }.Validate());
    }

    [Fact]
    public void ATrialSeenAfterAMenuThatIsNotARunUp_IsALateStart()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.MainMenu);
        h.Advance(20);
        h.Engine.Tick();
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(StartQuality.JoinedLate, h.Snapshot.Session!.Quality);
    }

    [Fact]
    public void LobbyThenTrial_MoreThanSixtySecondsLater_IsALateStart()
    {
        var h = new EngineHarness(new EngineOptions { SceneLostMenu = TimeSpan.FromMinutes(10) });
        h.ConfirmScene(Scene.Lobby);
        h.Advance(90);
        h.Engine.Tick();
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(StartQuality.JoinedLate, h.Snapshot.Session!.Quality);
    }

    // ---- Character -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Scene.Lobby, true)]
    [InlineData(Scene.InMatchMenu, true)]
    [InlineData(Scene.Results, true)]
    [InlineData(Scene.InTrial, false)]
    [InlineData(Scene.MainMenu, false)]
    [InlineData(Scene.Spectating, false)]
    public void Character_IsAcceptedOnlyWhereTheOwnersRowIsPrinted(Scene scene, bool accepted)
    {
        var h = new EngineHarness();
        h.ConfirmScene(scene);

        var result = h.Observe(ObservationKind.Character, Dwight);

        Assert.Equal(accepted ? SubmitResult.Accepted : SubmitResult.RejectedSceneGate, result);
    }

    [Fact]
    public void Character_NeedsKAgreeingReadings_AndIsDetectedNotDeclared()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.InMatchMenu);
        h.Frames(ObservationKind.Character, Lich, 2);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Character.State);

        h.Frames(ObservationKind.Character, Lich, 1);

        Assert.Equal(Lich, h.Snapshot.Character.ConfirmedValue);
        Assert.Equal(FieldSource.Detected, h.Snapshot.Character.Source);
    }

    [Fact]
    public void ADeclaredCharacter_WinsOverADetectedOne()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.InMatchMenu);
        h.Frames(ObservationKind.Character, Lich, 3);
        h.Engine.DeclareCharacter("The Trapper");

        Assert.Equal("The Trapper", h.Snapshot.Character.ConfirmedValue);
        Assert.Equal(FieldSource.UserDeclared, h.Snapshot.Character.Source);
    }

    [Fact]
    public void Character_SurvivesLoadingIntoTheTrial_ButNotTheNextLobbyOrResults()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Character, Dwight, 3);
        h.ConfirmScene(Scene.Loading);
        h.ConfirmScene(Scene.InTrial);
        Assert.Equal(Dwight, h.Snapshot.Character.ConfirmedValue);

        h.ConfirmScene(Scene.Results);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Character.State); // re-read from the scoreboard, never carried over
        h.ConfirmScene(Scene.Lobby);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Character.State);
    }

    [Fact]
    public void Character_GoesStaleWhenCaptureIsPaused()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Character, Dwight, 3);

        h.Engine.SetCapturePaused(true);

        Assert.Equal(FieldState.Stale, h.Snapshot.Character.State);
        Assert.Null(h.Snapshot.Character.ConfirmedValue);
    }

    [Fact]
    public void Character_OutsideTheVocabulary_IsRejectedWhenTheEngineKnowsTheList()
    {
        var known = new HashSet<string> { Dwight, Lich };
        var h = new EngineHarness(new EngineOptions { IsKnownCharacterId = known.Contains });
        h.ConfirmScene(Scene.Lobby);

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Character, "someone-else"));
        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.Character, Dwight));
    }

    // ---- Killer --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Scene.InMatchMenu, true)]
    [InlineData(Scene.Results, true)]
    [InlineData(Scene.Lobby, false)]
    [InlineData(Scene.InTrial, false)]
    public void KillerName_IsAcceptedOnlyOnTabAndTheScoreboard(Scene scene, bool accepted)
    {
        var h = new EngineHarness();
        h.ConfirmScene(scene);

        Assert.Equal(accepted ? SubmitResult.Accepted : SubmitResult.RejectedSceneGate, h.Observe(ObservationKind.Killer, Lich));
    }

    [Fact]
    public void KillerName_IsConfirmedAfterK_AndClearedByTheNextRound()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Results);
        h.Frames(ObservationKind.Killer, Lich, 3);
        Assert.Equal(Lich, h.Snapshot.Killer.ConfirmedValue);

        h.ConfirmScene(Scene.Lobby);

        Assert.Equal(FieldState.Unknown, h.Snapshot.Killer.State);
    }

    // ---- Queue mode ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Scene.Other, true)]
    [InlineData(Scene.Matchmaking, true)]
    [InlineData(Scene.Lobby, true)]
    [InlineData(Scene.InTrial, false)]
    [InlineData(Scene.Results, false)]
    public void QueueMode_IsAcceptedOnlyOnTheMenusThatPrintIt(Scene scene, bool accepted)
    {
        var h = new EngineHarness();
        h.ConfirmScene(scene);

        Assert.Equal(accepted ? SubmitResult.Accepted : SubmitResult.RejectedSceneGate, h.Observe(ObservationKind.QueueMode, QueueModes.Normal));
    }

    [Fact]
    public void QueueMode_ConfirmsAnEventId_AndIsClearedOnTheMainMenu()
    {
        var h = new EngineHarness(new EngineOptions { IsKnownEventModeId = id => id == "event-haunted-by-daylight" });
        h.ConfirmScene(Scene.Matchmaking);
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.QueueMode, "event-unlisted"));
        h.Frames(ObservationKind.QueueMode, "event-haunted-by-daylight", 3);
        Assert.Equal("event-haunted-by-daylight", h.Snapshot.QueueMode.ConfirmedValue);

        h.ConfirmScene(Scene.MainMenu);

        Assert.Equal(FieldState.Unknown, h.Snapshot.QueueMode.State);
    }

    [Fact]
    public void QueueMode_AcceptsNormalEvenWhenTheEventListIsKnown()
    {
        var h = new EngineHarness(new EngineOptions { IsKnownEventModeId = _ => false });
        h.ConfirmScene(Scene.Other);

        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.QueueMode, QueueModes.Normal));
    }

    // ---- Publication ---------------------------------------------------------------------------------------------

    [Fact]
    public void DetectedCharacterAndKiller_NeverReachThePresenceInputs_EvenWhenEveryTierIsValidated()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings);
        h.ConfirmScene(Scene.InMatchMenu);
        h.Frames(ObservationKind.Character, Lich, 3);
        h.Frames(ObservationKind.Killer, Lich, 3);
        Assert.Equal(Lich, h.Snapshot.Character.ConfirmedValue);

        var result = PublicationPolicy.Compose(h.Snapshot, settings, new PublicationOptions(), null, h.Now, null);

        Assert.Equal(PresenceState.InTrial, result.Inputs.State); // the menu over a trial reads as the trial
        Assert.DoesNotContain(Lich, result.Inputs.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs one scripted session on its own fake clock. With <paramref name="withInAppFields"/> the character, queue mode and killer detectors also
    /// report (at the same instants, without moving the clock); without it they stay silent. Every tier is Validated with measured thresholds.
    /// </summary>
    private static (EngineHarness Harness, PublicationResult Result) PublishedSession(bool withInAppFields)
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings);

        // Each in-app field is reported only after the scene it belongs to is confirmed (the lobby resets the character, so it comes after it).
        h.ConfirmScene(Scene.Other);
        for (var i = 0; i < 4; i++)
        {
            if (withInAppFields)
            {
                h.Observe(ObservationKind.QueueMode, QueueModes.Normal, advance: false);
            }

            h.SceneFrame(Scene.Other);
        }

        h.ConfirmScene(Scene.Lobby);
        for (var i = 0; i < 4; i++)
        {
            if (withInAppFields)
            {
                h.Observe(ObservationKind.Character, Dwight, advance: false);
            }

            h.Observe(ObservationKind.Role, "Survivor");
        }

        h.EnterTrial();
        h.Frames(ObservationKind.Map, MapA, 3);
        h.ConfirmScene(Scene.InMatchMenu);
        for (var i = 0; i < 4; i++)
        {
            if (withInAppFields)
            {
                h.Observe(ObservationKind.Killer, Lich, advance: false);
            }

            h.SceneFrame(Scene.InMatchMenu);
        }

        var maps = new FakeMaps();
        return (h, PublicationPolicy.Compose(h.Snapshot, settings, new PublicationOptions(), maps, h.Now, null));
    }

    private sealed class FakeMaps : IMapNameResolver
    {
        public MapNames? Resolve(string mapId) => mapId == MapA ? new MapNames("Rotten Fields", "Coldwind Farm") : null;
    }

    [Fact]
    public void CharacterKillerAndQueueMode_ChangeNothingInThePublishedInputs_EvenWhenEveryTierIsValidatedAndMeasured()
    {
        // Review U5 F5(f): compare the WHOLE PresenceInputs of two otherwise identical sessions, not a string search for one value.
        var (without, withoutResult) = PublishedSession(withInAppFields: false);
        var (with, withResult) = PublishedSession(withInAppFields: true);

        // The in-app fields really were confirmed in the second session (so the comparison is not vacuous) ...
        Assert.Equal(Dwight, with.Snapshot.Character.ConfirmedValue);
        Assert.Equal(Lich, with.Snapshot.Killer.ConfirmedValue);
        Assert.Equal(QueueModes.Normal, with.Snapshot.QueueMode.ConfirmedValue);
        Assert.Equal(FieldState.Unknown, without.Snapshot.Character.State);
        // ... and the published scene, role and map really were published in both.
        Assert.Equal(PresenceState.InTrialSurvivor, withResult.Inputs.State);
        Assert.Equal("Rotten Fields", withResult.Inputs.MapName);

        Assert.Equal(withoutResult.Inputs, withResult.Inputs);
        Assert.Equal(withoutResult.BetaFields, withResult.BetaFields);
        Assert.Equal(withoutResult.Suppressed, withResult.Suppressed);
    }

    [Fact]
    public void EveryNewDetectorIsDisabledByDefault_SoTheMenuOverATrialPublishesNothing()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.InMatchMenu);
        h.Frames(ObservationKind.Character, Lich, 3);

        var result = PublicationPolicy.Compose(h.Snapshot, new Dictionary<DetectorId, DetectorSettings>(), new PublicationOptions(), null, h.Now, null);

        Assert.Equal(PresenceState.Running, result.Inputs.State);
        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.Scene);
    }

    [Fact]
    public void ObservationKinds_MapToTheirDetectorsBothWays()
    {
        foreach (var kind in Enum.GetValues<ObservationKind>())
        {
            Assert.Equal(kind, Observation.KindOf(Observation.DetectorOf(kind)));
        }
    }
}
