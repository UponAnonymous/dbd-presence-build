// Covers: FR-DET-22 follow-up (killer identity from power prompts), SAFETY_AND_PRIVACY section 2.2 (nothing about the killer reaches Discord from here),
// DETECTION_VALIDATION section 7. Mechanics only: scripted observation sequences on a fake clock, not measurements.
using DbdPresence.Core.Detection;

namespace DbdPresence.Core.Tests.Detection;

public sealed class MatchStateEngineKillerPromptTests
{
    private const string Lich = "the-lich";

    [Theory]
    [InlineData(Scene.InTrial, true)]
    [InlineData(Scene.Lobby, false)]
    [InlineData(Scene.Results, false)]
    [InlineData(Scene.InMatchMenu, false)]
    [InlineData(Scene.Other, false)]
    public void APromptKiller_IsAcceptedOnlyInATrial(Scene scene, bool accepted)
    {
        var h = new EngineHarness();
        h.ConfirmScene(scene);

        Assert.Equal(accepted ? SubmitResult.Accepted : SubmitResult.RejectedSceneGate, h.Observe(ObservationKind.KillerPrompt, Lich));
    }

    [Fact]
    public void APromptKiller_IsConfirmedAfterK_AndIsAFieldOfItsOwn()
    {
        var h = new EngineHarness();
        h.EnterTrial();

        h.Frames(ObservationKind.KillerPrompt, Lich, 3);

        Assert.Equal(Lich, h.Snapshot.KillerFromPrompt.ConfirmedValue);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Killer.State);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Character.State);
    }

    [Fact]
    public void AnUnreadableObservation_ConfirmsNothing()
    {
        var h = new EngineHarness();
        h.EnterTrial();

        h.Frames(ObservationKind.KillerPrompt, null, 6);

        Assert.Equal(FieldState.Unknown, h.Snapshot.KillerFromPrompt.State);
    }

    [Fact]
    public void AnIdOutsideTheClosedList_IsRefused()
    {
        var h = new EngineHarness(new EngineOptions { IsKnownKillerId = id => id == Lich });
        h.EnterTrial();

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.KillerPrompt, "someone-else"));
        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.KillerPrompt, Lich));
    }

    [Fact]
    public void TheNextRound_ClearsIt()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.KillerPrompt, Lich, 3);
        Assert.Equal(Lich, h.Snapshot.KillerFromPrompt.ConfirmedValue);

        h.ConfirmScene(Scene.Results);
        h.ConfirmScene(Scene.Lobby);

        Assert.Equal(FieldState.Unknown, h.Snapshot.KillerFromPrompt.State);
    }

    [Fact]
    public void ThePromptKiller_ChangesNothingInThePublishedInputs_EvenWhenEveryTierIsValidatedAndMeasured()
    {
        var withoutPrompt = Published(withPrompt: false);
        var withPrompt = Published(withPrompt: true);

        Assert.Equal(Lich, withPrompt.Snapshot.KillerFromPrompt.ConfirmedValue); // not vacuous
        Assert.Equal(Scene.InTrial, withPrompt.Snapshot.ConfirmedScene);
        Assert.Equal(withoutPrompt.Result.Inputs, withPrompt.Result.Inputs);
        Assert.Equal(withoutPrompt.Result.BetaFields, withPrompt.Result.BetaFields);
        Assert.Equal(withoutPrompt.Result.Suppressed, withPrompt.Result.Suppressed);
    }

    [Fact]
    public void TheDetector_HasItsOwnHealthEntry_AndDefaultsToDisabledSettings()
    {
        var h = new EngineHarness();

        Assert.True(h.Snapshot.Health.ContainsKey(DetectorId.KillerPrompt));
        Assert.Equal(DetectionTier.Disabled, DetectorSettings.Default.Tier);
    }

    private static (EngineHarness Harness, MatchSnapshot Snapshot, PublicationResult Result) Published(bool withPrompt)
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings);
        h.EnterTrial();
        for (var i = 0; i < 4; i++)
        {
            if (withPrompt)
            {
                h.Observe(ObservationKind.KillerPrompt, Lich, advance: false);
            }

            h.Observe(ObservationKind.Role, "Killer");
        }

        var result = PublicationPolicy.Compose(h.Snapshot, settings, new PublicationOptions(), null, h.Now, null);
        return (h, h.Snapshot, result);
    }
}
