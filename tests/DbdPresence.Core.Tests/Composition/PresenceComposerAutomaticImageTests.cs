// Covers: FR-AUTO-01 (the Automatic small image: one case per row of PLAN_AUTO_ICONS §3, the role-fit rule, the fall back to the role icon),
// FR-AUTO-06 (hook and dying icons follow "Show live match stats" and "Post-match details only"), FR-VIS-03 (pause clears them),
// FR-AUTO-07 (the {hook_count} token follows the live-stats switch and the killer role), FR-AUTO-02 (nine reserved slugs).
using DbdPresence.Core.Composition;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;

namespace DbdPresence.Core.Tests.Composition;

public sealed class PresenceComposerAutomaticImageTests
{
    private const string SurvivorIcon = "https://cdn.example/role-survivor.png";
    private const string KillerIcon = "https://cdn.example/role-killer.png";

    private static readonly DateTimeOffset s_now = new(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> s_pictures = AutomaticIcons.AllSlugs.ToDictionary(s => s, s => $"https://cdn.example/{s}.png", StringComparer.Ordinal);

    private static PresenceProfile Profile(bool liveStats = true, bool postMatchOnly = false, IReadOnlyDictionary<string, string>? pictures = null) => new()
    {
        Visibility = new VisibilitySettings { ShowLiveMatchStats = liveStats, PostMatchDetailsOnly = postMatchOnly },
        Artwork = new ArtworkSettings { RoleSurvivor = SurvivorIcon, RoleKiller = KillerIcon, StatusImages = pictures ?? s_pictures },
    };

    private static PresenceInputs Inputs(PresenceState state, Role role, OwnStatus status = OwnStatus.None, string? outcome = null) =>
        new() { State = state, Now = s_now, GameStartedUtc = s_now.AddMinutes(-5), DetectedRole = role, OwnStatus = status, OutcomeId = outcome };

    private static CompositionResult Compose(PresenceInputs inputs, PresenceProfile? profile = null, PrivacyState? privacy = null) =>
        PresenceComposer.Compose(inputs, profile ?? Profile(), privacy ?? new PrivacyState());

    private static string? Small(PresenceInputs inputs, PresenceProfile? profile = null, PrivacyState? privacy = null) => Compose(inputs, profile, privacy).Activity!.SmallImage;

    [Fact]
    public void TheDetailedPreset_UsesAutomaticForTheSurvivorTrialAndTheResults_AndNotForTheLargeImage()
    {
        var templates = TemplatePresets.Detailed.ByState;

        Assert.Equal(ImageSource.Automatic, templates[PresenceState.InTrialSurvivor].SmallImage.Source);
        Assert.Equal(ImageSource.Automatic, templates[PresenceState.Results].SmallImage.Source);
        Assert.DoesNotContain(templates.Values, t => t.LargeImage.Source == ImageSource.Automatic);
    }

    // ---- rows 1 and 2: results ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ResultOutcomes.Escaped, "result-escaped")]
    [InlineData(ResultOutcomes.Sacrificed, "result-sacrificed")]
    [InlineData(ResultOutcomes.Dead, "result-dead")]
    public void Row1_SurvivorOutcome_ShowsTheResultIcon(string outcome, string slug)
    {
        var small = Small(Inputs(PresenceState.Results, Role.Survivor, outcome: outcome));

        Assert.Equal($"https://cdn.example/{slug}.png", small);
    }

    [Theory]
    [InlineData(ResultOutcomes.EntityHungers, "result-entity-hungers")]
    [InlineData(ResultOutcomes.BrutalKiller, "result-brutal")]
    [InlineData(ResultOutcomes.RuthlessKiller, "result-ruthless")]
    [InlineData(ResultOutcomes.MercilessKiller, "result-merciless")]
    public void Row2_KillerOutcome_ShowsTheResultIcon(string outcome, string slug)
    {
        var small = Small(Inputs(PresenceState.Results, Role.Killer, outcome: outcome));

        Assert.Equal($"https://cdn.example/{slug}.png", small);
    }

    [Theory]
    [InlineData(Role.Killer, ResultOutcomes.Escaped)]
    [InlineData(Role.Survivor, ResultOutcomes.MercilessKiller)]
    public void Outcome_ThatContradictsTheRole_IsDroppedAndTheRoleIconShows(Role role, string outcome)
    {
        var small = Small(Inputs(PresenceState.Results, role, outcome: outcome));

        Assert.Equal(role == Role.Killer ? KillerIcon : SurvivorIcon, small);
    }

    [Fact]
    public void Outcome_WithTheResultHiddenByTheUser_ShowsTheRoleIcon()
    {
        var profile = Profile() with { Visibility = new VisibilitySettings { ShowResult = false } };

        Assert.Equal(SurvivorIcon, Small(Inputs(PresenceState.Results, Role.Survivor, outcome: ResultOutcomes.Escaped), profile));
    }

    [Fact]
    public void Outcome_AfterTheResultsScreen_IsNotShown()
    {
        Assert.Null(Small(Inputs(PresenceState.Lobby, Role.Survivor, outcome: ResultOutcomes.Escaped)));
    }

    [Fact]
    public void AnOutcome_CarriedIntoAnotherState_ShowsTheRoleIcon_NotTheResultIcon()
    {
        // Round-2 QA review F4: the result icon needs the Results state itself, not only an outcome id in the inputs (an automatic slot used in the trial).
        var inputs = Inputs(PresenceState.InTrialSurvivor, Role.Survivor, outcome: ResultOutcomes.Escaped);

        Assert.Equal(SurvivorIcon, Small(inputs));
    }

    [Fact]
    public void AStatusPictureThatIsOnlyWhitespace_IsTreatedAsMissing_AndTheRoleIconShowsWithANote()
    {
        var pictures = new Dictionary<string, string>(s_pictures, StringComparer.Ordinal) { ["status-hooked"] = "   " };
        var result = Compose(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Hooked), Profile(pictures: pictures));

        Assert.Equal(SurvivorIcon, result.Activity!.SmallImage);
        Assert.Contains(result.Notes, n => n.Contains("no picture for it", StringComparison.Ordinal));
    }

    // ---- rows 3 and 4: own status ---------------------------------------------------------------------------------------------

    [Fact]
    public void Row3_Hooked_ShowsTheHookIcon_WhenLiveStatsAreOn()
    {
        Assert.Equal("https://cdn.example/status-hooked.png", Small(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Hooked)));
    }

    [Fact]
    public void Row4_Dying_ShowsTheDyingIcon_WhenLiveStatsAreOn()
    {
        Assert.Equal("https://cdn.example/status-dying.png", Small(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Dying)));
    }

    [Fact]
    public void HookedAndDying_FollowTheLiveStatsSwitch_AndExplainWhy()
    {
        var result = Compose(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Hooked), Profile(liveStats: false));

        Assert.Equal(SurvivorIcon, result.Activity!.SmallImage);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.LiveStatsOff);
    }

    [Fact]
    public void HookedAndDying_WaitForTheEndOfTheTrial_WhenPostMatchDetailsOnly()
    {
        var small = Small(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Dying), Profile(postMatchOnly: true));

        Assert.Equal(SurvivorIcon, small);
    }

    [Fact]
    public void Status_IsIgnored_ForAKiller_AndOutsideTheSurvivorsTrial()
    {
        Assert.Equal(KillerIcon, Small(Inputs(PresenceState.InTrialKiller, Role.Killer, OwnStatus.Hooked), WithAutomaticOn(PresenceState.InTrialKiller)));
        Assert.Equal(SurvivorIcon, Small(Inputs(PresenceState.InTrial, Role.Survivor, OwnStatus.Hooked), WithAutomaticOn(PresenceState.InTrial)));
    }

    // ---- row 5: the role icon -------------------------------------------------------------------------------------------------

    [Fact]
    public void Row5_NothingConfirmed_ShowsTheRoleIcon()
    {
        Assert.Equal(SurvivorIcon, Small(Inputs(PresenceState.InTrialSurvivor, Role.Survivor)));
        Assert.Equal(KillerIcon, Small(Inputs(PresenceState.Results, Role.Killer)));
    }

    [Fact]
    public void Row5_AMissingPicture_FallsBackToTheRoleIcon_AndSaysSo()
    {
        var pictures = s_pictures.Where(p => p.Key != AutomaticIcons.StatusHooked).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var result = Compose(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Hooked), Profile(pictures: pictures));

        Assert.Equal(SurvivorIcon, result.Activity!.SmallImage);
        Assert.Contains(result.Reasons, r => r.Code == HiddenReasonCode.StatusPictureMissing);
    }

    [Fact]
    public void Row5_AnEmptyPack_ShowsOnlyTheRoleIcon()
    {
        var empty = new Dictionary<string, string>();

        Assert.Equal(SurvivorIcon, Small(Inputs(PresenceState.Results, Role.Survivor, outcome: ResultOutcomes.Escaped), Profile(pictures: empty)));
    }

    [Fact]
    public void Row5_WithTheRoleHiddenByTheUser_NeitherTheRoleNorTheStatusIconShows()
    {
        var profile = Profile() with { Visibility = new VisibilitySettings { ShowRole = false, ShowLiveMatchStats = true } };

        var inputs = Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Hooked);

        Assert.Null(Small(inputs, profile));
    }

    // ---- row 6: no role -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Row6_NoRoleKnown_ShowsNoSmallImage_AndNeverFallsBack()
    {
        var result = Compose(Inputs(PresenceState.Results, Role.None, outcome: ResultOutcomes.Escaped));

        Assert.Null(result.Activity!.SmallImage);
    }

    // ---- pause, Private, large image ------------------------------------------------------------------------------------------

    [Fact]
    public void Pause_ClearsTheStatusAndTheOutcome()
    {
        var profile = Profile() with { WhenPaused = PauseBehavior.Generic };

        var hooked = Compose(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Hooked), profile, new PrivacyState(Paused: true));
        var escaped = Compose(Inputs(PresenceState.Results, Role.Survivor, outcome: ResultOutcomes.Escaped), profile, new PrivacyState(Paused: true));

        Assert.DoesNotContain(s_pictures.Values, v => v == hooked.Activity!.SmallImage);
        Assert.DoesNotContain(s_pictures.Values, v => v == escaped.Activity!.SmallImage);
    }

    [Fact]
    public void PrivateStyle_NeverShowsAStatusOrResultIcon_OnlyTheRoleIcon()
    {
        var automatic = new TemplateSet
        {
            ByState = new Dictionary<PresenceState, PresenceTemplate>
            {
                [PresenceState.Running] = new() { Details = "Playing Dead by Daylight", SmallImage = ImageSlot.Automatic },
                [PresenceState.Results] = new() { Details = "Viewing results", SmallImage = ImageSlot.Automatic },
            },
        };
        var profile = Profile() with { StreamerTemplates = automatic };
        var inputs = Inputs(PresenceState.Results, Role.Survivor, outcome: ResultOutcomes.Escaped);

        var small = Small(inputs, profile, new PrivacyState(StreamerMode: true));

        Assert.Equal(SurvivorIcon, small);
    }

    [Fact]
    public void AutomaticOnTheLargeImage_ShowsTheRoleIcon_NeverAStatusIcon()
    {
        var template = new PresenceTemplate { Details = "Viewing results", LargeImage = ImageSlot.Automatic };
        var profile = Profile() with { Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Results] = template } } };

        var large = Compose(Inputs(PresenceState.Results, Role.Survivor, outcome: ResultOutcomes.Escaped), profile).Activity!.LargeImage;

        Assert.Equal(SurvivorIcon, large);
    }

    [Fact]
    public void ABehaviourPack_LabelsTheHoverText_WhenAStatusIconShows()
    {
        var profile = Profile();
        profile = profile with { Artwork = profile.Artwork with { IsBehaviourArtwork = true } };

        var activity = Compose(Inputs(PresenceState.InTrialSurvivor, Role.Survivor, OwnStatus.Hooked), profile).Activity!;

        Assert.Contains(PresenceComposer.BehaviourLabel, activity.LargeText ?? activity.SmallText ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- the {hook_count} token -----------------------------------------------------------------------------------------------

    private static PresenceProfile WithHookText(bool liveStats = true)
    {
        var template = new PresenceTemplate { Details = "Hunting", State = "[{hook_count} hooks]" };
        var templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.InTrialKiller] = template, [PresenceState.InTrialSurvivor] = template } };
        return Profile(liveStats) with { Templates = templates };
    }

    [Fact]
    public void HookCount_ShowsForTheKiller_WhenLiveStatsAreOn()
    {
        var inputs = Inputs(PresenceState.InTrialKiller, Role.Killer) with { HookCount = 3 };

        Assert.Equal("3 hooks", Compose(inputs, WithHookText()).Activity!.State);
    }

    [Fact]
    public void HookCount_IsLeftOut_WhenLiveStatsAreOff_OrForASurvivor_OrNotConfirmed()
    {
        var killer = Inputs(PresenceState.InTrialKiller, Role.Killer) with { HookCount = 3 };
        var off = Compose(killer, WithHookText(liveStats: false));
        Assert.Null(off.Activity!.State);
        Assert.Contains(off.Reasons, r => r.Code == HiddenReasonCode.LiveStatsOff);

        var survivor = Inputs(PresenceState.InTrialSurvivor, Role.Survivor) with { HookCount = 3 };
        Assert.Null(Compose(survivor, WithHookText()).Activity!.State);

        Assert.Null(Compose(Inputs(PresenceState.InTrialKiller, Role.Killer), WithHookText()).Activity!.State);
    }

    [Fact]
    public void HookCount_IsKeptOutOfPrivateStyle()
    {
        var killer = Inputs(PresenceState.InTrialKiller, Role.Killer) with { HookCount = 3 };

        var privateResult = Compose(killer, WithHookText(), new PrivacyState(StreamerMode: true));

        Assert.DoesNotContain("hooks", privateResult.Activity?.State ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void HookCount_IsAKnownToken_AndTheOldHooksTokenPointsToIt()
    {
        var known = DbdPresence.Core.Templates.TemplateParser.Parse("{hook_count} hooks");
        var old = DbdPresence.Core.Templates.TemplateParser.Parse("{hooks} hooks");

        Assert.Empty(known.Errors);
        Assert.Contains(DbdPresence.Core.Templates.TemplateTokens.HookCount, known.Tokens);
        Assert.Contains(old.Errors, e => e.Message.Contains("{hook_count}", StringComparison.Ordinal));
    }

    // ---- slugs ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheNineSlugs_AreDistinct_ValidPackSlugs_AndEveryOutcomeAndStatusMapsToOne()
    {
        Assert.Equal(9, AutomaticIcons.AllSlugs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(AutomaticIcons.AllSlugs, slug => Assert.Matches("^[a-z0-9-]{1,64}$", slug));
        Assert.Equal(AutomaticIcons.StatusHooked, AutomaticIcons.SlugOf(OwnStatus.Hooked));
        Assert.Equal(AutomaticIcons.StatusDying, AutomaticIcons.SlugOf(OwnStatus.Dying));
        Assert.Null(AutomaticIcons.SlugOf(OwnStatus.None));
        foreach (var outcome in new[]
        {
            ResultOutcomes.Escaped, ResultOutcomes.Sacrificed, ResultOutcomes.Dead, ResultOutcomes.EntityHungers,
            ResultOutcomes.BrutalKiller, ResultOutcomes.RuthlessKiller, ResultOutcomes.MercilessKiller,
        })
        {
            Assert.True(AutomaticIcons.IsReserved(AutomaticIcons.SlugOfOutcome(outcome)));
        }

        Assert.Null(AutomaticIcons.SlugOfOutcome("outcome-unknown"));
        Assert.False(AutomaticIcons.IsReserved("role-killer"));
    }

    private static PresenceProfile WithAutomaticOn(PresenceState state)
    {
        var template = new PresenceTemplate { Details = "In a trial", SmallImage = ImageSlot.Automatic };
        return Profile() with { Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [state] = template } } };
    }
}
