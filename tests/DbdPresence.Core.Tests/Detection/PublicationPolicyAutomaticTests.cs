// Covers: FR-AUTO-05 (own status and the two outcome detectors publish only at Validated, only for classes on the publishable list, role fit,
// scene rule), FR-AUTO-07 (hook count follows the generators' rules), DETECTION_VALIDATION §7 (never at Experimental, never as an initial guess).
// Mechanics only: scripted observation sequences, not measurements. Nothing here is an accuracy figure.
using DbdPresence.Core.Composition;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Tests.Detection;

public sealed class PublicationPolicyAutomaticTests
{
    private static Dictionary<DetectorId, DetectorSettings> Settings(DetectionTier statusTier, IReadOnlyList<string>? classes, DetectionTier resultTier = DetectionTier.Validated)
    {
        var settings = new Dictionary<DetectorId, DetectorSettings>(EngineHarness.AllAt(DetectionTier.Validated))
        {
            [DetectorId.Status] = EngineHarness.Measured(statusTier) with { PublishableClasses = classes },
            [DetectorId.SurvivorResult] = EngineHarness.Measured(resultTier) with { PublishableClasses = classes },
            [DetectorId.KillerResult] = EngineHarness.Measured(resultTier) with { PublishableClasses = classes },
            [DetectorId.HookCount] = EngineHarness.Measured(statusTier),
        };
        return settings;
    }

    private static EngineHarness Trial(string role, IReadOnlyDictionary<DetectorId, DetectorSettings> settings)
    {
        var h = new EngineHarness(settings: settings);
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, role, 3);
        h.EnterTrial();
        h.SceneFrame(Scene.InTrial);
        return h;
    }

    private static PublicationResult Compose(EngineHarness h, IReadOnlyDictionary<DetectorId, DetectorSettings> settings, bool optIn = false) =>
        PublicationPolicy.Compose(h.Snapshot, settings, new PublicationOptions(optIn), null, h.Now, h.Now.AddHours(-1));

    // ---- own status ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void OwnStatus_PublishesAtValidated_ForAListedClass()
    {
        var settings = Settings(DetectionTier.Validated, [OwnStatuses.Hooked, OwnStatuses.Dying]);
        var h = Trial("Survivor", settings);
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        Assert.Equal(OwnStatus.Hooked, Compose(h, settings).Inputs.OwnStatus);

        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Dying, 3);
        Assert.Equal(OwnStatus.Dying, Compose(h, settings).Inputs.OwnStatus);
    }

    [Theory]
    [InlineData(DetectionTier.Experimental, true)]
    [InlineData(DetectionTier.Experimental, false)]
    [InlineData(DetectionTier.Disabled, true)]
    public void OwnStatus_IsRefused_BelowValidated_EvenWithTheSessionOptIn(DetectionTier tier, bool optIn)
    {
        var settings = Settings(tier, [OwnStatuses.Hooked]);
        var h = Trial("Survivor", settings);
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        var result = Compose(h, settings, optIn);

        Assert.Equal(OwnStatus.None, result.Inputs.OwnStatus);
        Assert.DoesNotContain(FieldKind.OwnStatus, result.BetaFields);
        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.OwnStatus);
    }

    [Fact]
    public void OwnStatus_ExperimentalSaysWhyItNeedsValidated()
    {
        var settings = Settings(DetectionTier.Experimental, [OwnStatuses.Hooked]);
        var h = Trial("Survivor", settings);
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        var result = Compose(h, settings, optIn: true);

        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.OwnStatus && s.Reason.Contains("Validated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OwnStatus_WithoutAPublishableClassList_PublishesNothing(string? listed)
    {
        IReadOnlyList<string>? classes = listed is null ? null : [];
        var settings = Settings(DetectionTier.Validated, classes);
        var h = Trial("Survivor", settings);
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);

        var result = Compose(h, settings);

        Assert.Equal(OwnStatus.None, result.Inputs.OwnStatus);
        Assert.Contains(result.Suppressed, s => s.Reason.Contains("per-class", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnStatus_OnlyTheListedClassPublishes()
    {
        var settings = Settings(DetectionTier.Validated, [OwnStatuses.Hooked]);
        var h = Trial("Survivor", settings);
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Dying, 3);

        Assert.Equal(OwnStatus.None, Compose(h, settings).Inputs.OwnStatus);
    }

    [Theory]
    [InlineData(OwnStatuses.Portrait)]
    [InlineData(OwnStatuses.Dead)]
    public void OwnStatus_PortraitAndDead_AreNeverPublished(string value)
    {
        var settings = Settings(DetectionTier.Validated, [OwnStatuses.Hooked, OwnStatuses.Dying, OwnStatuses.Dead, OwnStatuses.Portrait]);
        var h = Trial("Survivor", settings);
        h.Frames(ObservationKind.OwnStatus, value, 3);

        Assert.Equal(OwnStatus.None, Compose(h, settings).Inputs.OwnStatus);
    }

    [Fact]
    public void OwnStatus_InitialGuessThresholds_NeverPublish()
    {
        var valid = Settings(DetectionTier.Validated, [OwnStatuses.Hooked]);
        var h = Trial("Survivor", valid);
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);
        var guessed = new Dictionary<DetectorId, DetectorSettings>(valid)
        {
            [DetectorId.Status] = new DetectorSettings(DetectionTier.Validated, ThresholdProvenance.InitialGuess, null, "10.2.0", null, [OwnStatuses.Hooked]),
        };

        Assert.Equal(OwnStatus.None, Compose(h, guessed).Inputs.OwnStatus); // the engine would refuse such settings; the policy refuses them too
    }

    [Fact]
    public void OwnStatus_IsHiddenOutsideTheTrialView_AndWhenTheRoleIsNotPublishable()
    {
        var settings = Settings(DetectionTier.Validated, [OwnStatuses.Hooked]);
        var h = Trial("Survivor", settings);
        h.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);
        Assert.Equal(OwnStatus.Hooked, Compose(h, settings).Inputs.OwnStatus);

        h.ConfirmScene(Scene.InMatchMenu); // TAB open: the trial view is not on screen
        Assert.Equal(OwnStatus.None, Compose(h, settings).Inputs.OwnStatus);

        var noRole = new Dictionary<DetectorId, DetectorSettings>(settings) { [DetectorId.Role] = DetectorSettings.Default };
        var h2 = Trial("Survivor", noRole);
        h2.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);
        Assert.Equal(OwnStatus.None, Compose(h2, noRole).Inputs.OwnStatus);
    }

    // ---- outcomes --------------------------------------------------------------------------------------------------------------

    private static EngineHarness Results(string role, IReadOnlyDictionary<DetectorId, DetectorSettings> settings)
    {
        var h = Trial(role, settings);
        h.ConfirmScene(Scene.Results);
        return h;
    }

    [Fact]
    public void SurvivorOutcome_PublishesAtValidated_ForAListedClass()
    {
        var settings = Settings(DetectionTier.Validated, [ResultOutcomes.Escaped]);
        var h = Results("Survivor", settings);
        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Escaped, 3);

        var inputs = Compose(h, settings).Inputs;

        Assert.Equal(PresenceState.Results, inputs.State);
        Assert.Equal(ResultOutcomes.Escaped, inputs.OutcomeId);
    }

    [Fact]
    public void KillerOutcome_PublishesAtValidated_ForAListedClass()
    {
        var settings = Settings(DetectionTier.Validated, [ResultOutcomes.BrutalKiller]);
        var h = Results("Killer", settings);
        h.Frames(ObservationKind.KillerResult, ResultOutcomes.BrutalKiller, 3);

        Assert.Equal(ResultOutcomes.BrutalKiller, Compose(h, settings).Inputs.OutcomeId);
    }

    [Theory]
    [InlineData(DetectionTier.Experimental)]
    [InlineData(DetectionTier.Disabled)]
    public void Outcome_IsRefused_BelowValidated_EvenWithTheSessionOptIn(DetectionTier tier)
    {
        var settings = Settings(DetectionTier.Validated, [ResultOutcomes.Escaped], resultTier: tier);
        var h = Results("Survivor", settings);
        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Escaped, 3);

        var result = Compose(h, settings, optIn: true);

        Assert.Null(result.Inputs.OutcomeId);
        Assert.Empty(result.BetaFields);
    }

    [Fact]
    public void Outcome_OfAnUnlistedClass_IsHeldBack()
    {
        var settings = Settings(DetectionTier.Validated, [ResultOutcomes.Escaped]);
        var h = Results("Survivor", settings);
        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Dead, 3);

        var result = Compose(h, settings);

        Assert.Null(result.Inputs.OutcomeId);
        Assert.Contains(result.Suppressed, s => s.Reason.Contains("per-class", StringComparison.Ordinal));
    }

    [Fact]
    public void Outcome_IsOnlyPublishedOnTheResultScreen()
    {
        var settings = Settings(DetectionTier.Validated, [ResultOutcomes.Escaped]);
        var h = Results("Survivor", settings);
        h.Frames(ObservationKind.SurvivorResult, ResultOutcomes.Escaped, 3);

        h.ConfirmScene(Scene.Lobby);

        Assert.Null(Compose(h, settings).Inputs.OutcomeId);
    }

    [Fact]
    public void Outcome_OfTheOtherRole_IsDropped()
    {
        // The role is confirmed as Killer, so a survivor outcome never gets in; and if the fields disagree later the policy drops it as well.
        var settings = Settings(DetectionTier.Validated, [ResultOutcomes.Escaped, ResultOutcomes.BrutalKiller]);
        var h = Results("Killer", settings);
        Assert.Equal(SubmitResult.RejectedRoleGate, h.Observe(ObservationKind.SurvivorResult, ResultOutcomes.Escaped));

        Assert.Null(Compose(h, settings).Inputs.OutcomeId);
    }

    private static ConfirmedField Confirmed(string value, DateTimeOffset now) => new(FieldState.Confirmed, value, FieldSource.Detected, now, null, null);

    [Fact]
    public void Outcome_OfTheOtherRole_IsDroppedByThePolicyItself_WhenTheSnapshotAlreadyHoldsIt()
    {
        // Round-2 QA review F4: the engine's role gate normally stops this; the policy repeats the check, and this test builds the contradiction by hand.
        var settings = Settings(DetectionTier.Validated, [ResultOutcomes.Escaped, ResultOutcomes.BrutalKiller]);
        var h = Results("Survivor", settings);
        var forged = h.Snapshot with { SurvivorResult = Confirmed(ResultOutcomes.BrutalKiller, h.Now) };

        var result = PublicationPolicy.Compose(forged, settings, new PublicationOptions(false), null, h.Now, h.Now.AddHours(-1));

        Assert.Null(result.Inputs.OutcomeId);
        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.SurvivorResult && s.Reason.Contains("does not fit the role", StringComparison.Ordinal));
    }

    // ---- hook count ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void HookCount_IsNotPublishedForASurvivor_EvenWhenTheSnapshotAlreadyHoldsIt()
    {
        var settings = Settings(DetectionTier.Validated, null);
        var h = Trial("Survivor", settings);
        var forged = h.Snapshot with { HookCount = Confirmed("3", h.Now) };

        var result = PublicationPolicy.Compose(forged, settings, new PublicationOptions(false), null, h.Now, h.Now.AddHours(-1));

        Assert.Null(result.Inputs.HookCount);
    }

    [Fact]
    public void HookCount_PublishesForTheKiller_AtValidated()
    {
        var settings = Settings(DetectionTier.Validated, null);
        var h = Trial("Killer", settings);
        h.Frames(ObservationKind.HookCount, "4", 3);

        Assert.Equal(4, Compose(h, settings).Inputs.HookCount);
    }

    [Fact]
    public void HookCount_FollowsTheTierRules_DisabledAndExperimentalAreHeldBack()
    {
        var disabled = Settings(DetectionTier.Disabled, null);
        var h = Trial("Killer", disabled);
        h.Frames(ObservationKind.HookCount, "4", 3);
        Assert.Null(Compose(h, disabled).Inputs.HookCount);

        var experimental = Settings(DetectionTier.Experimental, null);
        experimental[DetectorId.HookCount] = EngineHarness.Measured(DetectionTier.Experimental);
        var h2 = Trial("Killer", experimental);
        h2.Frames(ObservationKind.HookCount, "4", 3);
        Assert.Null(Compose(h2, experimental).Inputs.HookCount);

        var on = Compose(h2, experimental, optIn: true);
        Assert.Equal(4, on.Inputs.HookCount);
        Assert.Contains(FieldKind.HookCount, on.BetaFields);
    }

    [Fact]
    public void HookCount_IsNeverPublishedForASurvivor()
    {
        var settings = Settings(DetectionTier.Validated, null);
        var h = Trial("Survivor", settings);

        Assert.Equal(SubmitResult.RejectedRoleGate, h.Observe(ObservationKind.HookCount, "2"));
        Assert.Null(Compose(h, settings).Inputs.HookCount);
    }

    [Fact]
    public void EveryDetectorDefaultsToDisabled_SoNothingNewReachesDiscordWithoutAReport()
    {
        var none = new Dictionary<DetectorId, DetectorSettings>();
        var survivor = Trial("Survivor", none);
        survivor.Frames(ObservationKind.OwnStatus, OwnStatuses.Hooked, 3);
        var killer = Trial("Killer", none);
        killer.Frames(ObservationKind.HookCount, "2", 3);

        Assert.Equal(OwnStatus.None, Compose(survivor, none).Inputs.OwnStatus);
        Assert.Null(Compose(killer, none).Inputs.HookCount);
        Assert.Null(Compose(survivor, none).Inputs.OutcomeId);
    }
}
