// Covers: FR-AUTO-03 (own-row status: confirmation, hysteresis, TTL, session scope), FR-AUTO-04 (outcomes: span, attach, role fit),
// FR-AUTO-07 (killer hook counter), ARCHITECTURE §4.3/§4.4 (session scoping, T_result, TTLs).
// Mechanics only: scripted observation sequences on a fake clock, not measurements. No accuracy claim.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Tests.Detection;

public sealed class MatchStateEngineAutomaticTests
{
    private static EngineHarness Trial(string role)
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, role, 3);
        h.EnterTrial();
        h.SceneFrame(Scene.InTrial);
        return h;
    }

    // ---- own status (FR-AUTO-03) -----------------------------------------------------------------------------------------------

    [Fact]
    public void OwnStatus_ConfirmsAfterThreeAgreeingFramesSpanningTheDwell()
    {
        var h = Trial("Survivor");

        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 2);
        Assert.NotEqual(FieldState.Confirmed, h.Snapshot.OwnStatus.State);

        h.Observe(ObservationKind.OwnStatus, OwnStatuses.Hooked);
        Assert.Equal(OwnStatuses.Hooked, h.Snapshot.OwnStatus.ConfirmedValue);
    }

    [Fact]
    public void OwnStatus_ThreeFramesInsideTheDwell_DoNotConfirm()
    {
        var h = Trial("Survivor");

        for (var i = 0; i < 3; i++)
        {
            h.Observe(ObservationKind.OwnStatus, OwnStatuses.Hooked, advance: false);
            h.Advance(0.4);
        }

        Assert.Null(h.Snapshot.OwnStatus.ConfirmedValue); // 0.8 s is shorter than the 1.5 s dwell
    }

    [Fact]
    public void OwnStatus_AlternatingFrames_NeverConfirmAndNeverFlap()
    {
        var h = Trial("Survivor");

        for (var i = 0; i < 12; i++)
        {
            h.Observe(ObservationKind.OwnStatus, i % 2 == 0 ? OwnStatuses.Hooked : OwnStatuses.Portrait);
            Assert.Null(h.Snapshot.OwnStatus.ConfirmedValue);
        }
    }

    [Fact]
    public void OwnStatus_ChangingAConfirmedValueNeedsKNewAgreeingFrames()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Portrait, 2);
        Assert.Equal(OwnStatuses.Hooked, h.Snapshot.OwnStatus.ConfirmedValue); // two frames are not enough to leave the hook

        h.Observe(ObservationKind.OwnStatus, OwnStatuses.Portrait);
        Assert.Equal(OwnStatuses.Portrait, h.Snapshot.OwnStatus.ConfirmedValue);
    }

    [Fact]
    public void OwnStatus_UnreadableFrames_NeitherConfirmNorRefute()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        h.Frames(ObservationKind.OwnStatus, null, 3);

        Assert.Equal(OwnStatuses.Hooked, h.Snapshot.OwnStatus.ConfirmedValue);
    }

    [Fact]
    public void OwnStatus_GoesStaleAfterItsTtl_WhenTheRowIsNoLongerRead()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);
        Assert.Equal(FieldState.Confirmed, h.Snapshot.OwnStatus.State);

        HoldScene(h, Scene.InTrial, 6); // the scene stays, but the row is not read for longer than the 5 s TTL
        h.Engine.Tick();

        Assert.Equal(FieldState.Stale, h.Snapshot.OwnStatus.State);
        Assert.Null(h.Snapshot.OwnStatus.ConfirmedValue);
    }

    [Fact]
    public void OwnStatus_StaysFresh_WhileReadingsKeepArriving()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 12);

        Assert.Equal(OwnStatuses.Hooked, h.Snapshot.OwnStatus.ConfirmedValue);
    }

    [Fact]
    public void OwnStatus_ReconfirmsAfterGoingStale()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);
        HoldScene(h, Scene.InTrial, 6);
        h.Engine.Tick();

        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        Assert.Equal(OwnStatuses.Hooked, h.Snapshot.OwnStatus.ConfirmedValue);
    }

    [Fact]
    public void OwnStatus_IsResetAtSessionEnd_AndNeverReachesTheNextTrial()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        h.ConfirmScene(Scene.Results);
        Assert.Null(h.Snapshot.OwnStatus.ConfirmedValue);

        h.ConfirmScene(Scene.Lobby);
        h.EnterTrial();
        h.SceneFrame(Scene.InTrial);
        Assert.Equal(FieldState.Unknown, h.Snapshot.OwnStatus.State);
    }

    [Fact]
    public void OwnStatus_IsHiddenAtOnce_WhenCapturePauses()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Dying, 3);

        h.Engine.SetCapturePaused(true);

        Assert.Equal(FieldState.Stale, h.Snapshot.OwnStatus.State);
    }

    [Fact]
    public void OwnStatus_IsHiddenAtOnce_WhenSpectating()
    {
        var h = Trial("Survivor");
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Dying, 3);

        h.ConfirmScene(Scene.Spectating);

        Assert.Equal(FieldState.Stale, h.Snapshot.OwnStatus.State);
    }

    [Fact]
    public void OwnStatus_IsRefusedOutsideTheSurvivorsTrial()
    {
        var killer = Trial("Killer");
        Assert.Equal(SubmitResult.RejectedRoleGate, killer.Observe(ObservationKind.OwnStatus, OwnStatuses.Hooked));

        var lobby = new EngineHarness();
        lobby.ConfirmScene(Scene.Lobby);
        Assert.Equal(SubmitResult.RejectedSceneGate, lobby.Observe(ObservationKind.OwnStatus, OwnStatuses.Hooked));

        var noRole = new EngineHarness();
        noRole.EnterTrial();
        Assert.Equal(SubmitResult.RejectedRoleGate, noRole.Observe(ObservationKind.OwnStatus, OwnStatuses.Hooked));
    }

    [Fact]
    public void OwnStatus_RefusesValuesOutsideTheClosedList()
    {
        var h = Trial("Survivor");

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.OwnStatus, "Banana"));
        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.OwnStatus, OwnStatuses.Dead));
    }

    // ---- outcomes (FR-AUTO-04) ------------------------------------------------------------------------------------------------

    /// <summary>Lets time pass with the scene still on screen, so only the field under test can expire.</summary>
    private static void HoldScene(EngineHarness h, Scene scene, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            h.SceneFrame(scene);
        }
    }

    private static EngineHarness ResultScreen(string role)
    {
        var h = Trial(role);
        h.ConfirmScene(Scene.Results);
        return h;
    }

    [Fact]
    public void SurvivorOutcome_ConfirmsAfterThreeFramesSpanningTwoSeconds_AndAttachesToTheSummary()
    {
        var h = ResultScreen("Survivor");

        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Escaped, 2);
        Assert.Null(h.Snapshot.SurvivorResult.ConfirmedValue);

        h.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Escaped);

        Assert.Equal(ResultOutcomes.Escaped, h.Snapshot.SurvivorResult.ConfirmedValue);
        Assert.Equal(ResultOutcomes.Escaped, h.Snapshot.LastSession!.OutcomeId);
    }

    [Fact]
    public void Outcome_ThreeFramesInsideTwoSeconds_DoNotConfirm()
    {
        var h = ResultScreen("Survivor");

        for (var i = 0; i < 3; i++)
        {
            h.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Sacrificed, advance: false);
            h.Advance(0.9);
        }

        Assert.Null(h.Snapshot.SurvivorResult.ConfirmedValue); // 1.8 s is shorter than the 2 s span
    }

    [Fact]
    public void Outcome_FlickeringBetweenTwoOutcomes_NeverConfirms()
    {
        var h = ResultScreen("Survivor");

        for (var i = 0; i < 10; i++)
        {
            h.Observe(ObservationKind.SurvivorResult, i % 2 == 0 ? ResultOutcomes.Escaped : ResultOutcomes.Dead);
        }

        Assert.Null(h.Snapshot.SurvivorResult.ConfirmedValue);
        Assert.Null(h.Snapshot.LastSession!.OutcomeId);
    }

    [Fact]
    public void KillerOutcome_ConfirmsOnTheKillersResultScreen()
    {
        var h = ResultScreen("Killer");

        h.Frames(ObservationKind.KillerResult, ResultOutcomes.MercilessKiller, 3);

        Assert.Equal(ResultOutcomes.MercilessKiller, h.Snapshot.KillerResult.ConfirmedValue);
    }

    [Fact]
    public void Outcome_ThatContradictsTheConfirmedRole_IsDropped()
    {
        var killer = ResultScreen("Killer");
        Assert.Equal(SubmitResult.RejectedRoleGate, killer.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Escaped));

        var survivor = ResultScreen("Survivor");
        Assert.Equal(SubmitResult.RejectedRoleGate, survivor.Observe(ObservationKind.KillerResult, ResultOutcomes.BrutalKiller));
    }

    [Fact]
    public void Outcome_WithAValueOfTheOtherKind_IsInvalid()
    {
        var h = ResultScreen("Survivor");

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.SurvivorResult, ResultOutcomes.BrutalKiller));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.KillerResult, ResultOutcomes.Escaped));
    }

    [Fact]
    public void Outcome_WithoutASessionToAttachTo_IsDropped()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Results); // the app started on the result screen: no trial was seen

        Assert.Equal(SubmitResult.RejectedNoSession, h.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Escaped));
        Assert.Null(h.Snapshot.SurvivorResult.ConfirmedValue);
    }

    [Fact]
    public void Outcome_AttachesLate_WithinTResult_AndNotAfter()
    {
        var late = ResultScreen("Survivor");
        HoldScene(late, Scene.Results, 14);
        Assert.Equal(SubmitResult.Accepted, late.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Escaped));

        var tooLate = ResultScreen("Survivor");
        HoldScene(tooLate, Scene.Results, 25);
        Assert.Equal(SubmitResult.RejectedNoSession, tooLate.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Escaped));
    }

    [Fact]
    public void Outcome_IsRefusedOutsideTheResultScreen()
    {
        var h = Trial("Survivor");

        Assert.Equal(SubmitResult.RejectedSceneGate, h.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Escaped));
    }

    [Fact]
    public void Outcome_LastsOnlyUntilTheSceneChanges()
    {
        var h = ResultScreen("Survivor");
        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Escaped, 3);
        Assert.NotNull(h.Snapshot.SurvivorResult.ConfirmedValue);

        h.ConfirmScene(Scene.Lobby);

        Assert.Null(h.Snapshot.SurvivorResult.ConfirmedValue);
    }

    [Fact]
    public void Outcome_NeverReachesTheNextMatch()
    {
        var h = ResultScreen("Survivor");
        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Sacrificed, 3);

        h.ConfirmScene(Scene.Lobby);
        h.EnterTrial();
        h.ConfirmScene(Scene.Results);

        Assert.Null(h.Snapshot.SurvivorResult.ConfirmedValue);
    }

    [Fact]
    public void Outcome_GoesStale_WhenCapturePauses()
    {
        var h = ResultScreen("Survivor");
        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Escaped, 3);

        h.Engine.SetCapturePaused(true);

        Assert.Null(h.Snapshot.SurvivorResult.ConfirmedValue);
    }

    // ---- killer hook counter (FR-AUTO-07) -------------------------------------------------------------------------------------

    [Fact]
    public void HookCount_ConfirmsForTheKiller_AndFollowsTheReading()
    {
        var h = Trial("Killer");

        h.Frames(ObservationKind.HookCount, "2", 3);
        Assert.Equal(2, h.Snapshot.ConfirmedHookCount);

        h.Frames(ObservationKind.HookCount, "3", 3);
        Assert.Equal(3, h.Snapshot.ConfirmedHookCount);
    }

    [Fact]
    public void HookCount_IsRefusedForASurvivor_OutsideATrial_AndWithoutAStandardMode()
    {
        Assert.Equal(SubmitResult.RejectedRoleGate, Trial("Survivor").Observe(ObservationKind.HookCount, "1"));

        var lobby = new EngineHarness();
        lobby.ConfirmScene(Scene.Lobby);
        Assert.Equal(SubmitResult.RejectedSceneGate, lobby.Observe(ObservationKind.HookCount, "1"));

        var noMode = new EngineHarness(mode: GameMode.Unknown);
        noMode.ConfirmScene(Scene.Lobby);
        noMode.Frames(ObservationKind.Role, "Killer", 3);
        noMode.EnterTrial();
        Assert.Equal(SubmitResult.RejectedModeGate, noMode.Observe(ObservationKind.HookCount, "1"));
        Assert.Equal(FieldState.Disabled, noMode.Snapshot.HookCount.State);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("-1")]
    [InlineData("two")]
    public void HookCount_RefusesValuesOutsideTheRange(string value)
    {
        var h = Trial("Killer");

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.HookCount, value));
    }

    [Fact]
    public void HookCount_GoesStaleAfterItsTtl_AndIsResetAtSessionEnd()
    {
        var h = Trial("Killer");
        h.Frames(ObservationKind.HookCount, "1", 3);
        HoldScene(h, Scene.InTrial, 31);
        h.Engine.Tick();
        Assert.Equal(FieldState.Stale, h.Snapshot.HookCount.State);

        var ended = Trial("Killer");
        ended.Frames(ObservationKind.HookCount, "1", 3);
        ended.ConfirmScene(Scene.Results);
        Assert.Equal(FieldState.Unknown, ended.Snapshot.HookCount.State);
    }

    [Fact]
    public void Observations_OfEveryKind_MapBackToTheirDetector()
    {
        Assert.Equal(DetectorId.Status, Observation.DetectorOf(ObservationKind.OwnStatus));
        Assert.Equal(ObservationKind.SurvivorResult, Observation.KindOf(DetectorId.SurvivorResult));
        Assert.Equal(ObservationKind.KillerResult, Observation.KindOf(DetectorId.KillerResult));
        Assert.Equal(ObservationKind.HookCount, Observation.KindOf(DetectorId.HookCount));
    }
}
