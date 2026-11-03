// Covers: FR-AUTO-03 (own-row status icon: NCC against templates the owner's captures taught this PC, owner-row gate, silent in anonymous mode, Dead never
// read as Hooked), FR-AUTO-04 (outcomes from MATCH RESULT against the closed list, split by role), FR-AUTO-07 (the killer hook counter).
// MECHANICS ONLY: the frames are flat colour rectangles and drawn digits and words at the profile's regions, read by a fake recogniser. The "icons" are
// invented patterns, not game artwork, and nothing here measures how well anything reads the real game (UNKNOWN, PENDING-ENV). No accuracy claim.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class OwnStatusAndOutcomeDetectorTests
{
    private static readonly (byte R, byte G, byte B) s_ink = (235, 235, 235);
    private static readonly (byte R, byte G, byte B) s_mid = (140, 140, 150);

    /// <summary>Draws an invented pattern per class inside the icon cell of row 0. Different classes have clearly different shapes.</summary>
    private static void Icon(HudCanvas canvas, string cls, bool dim = false)
    {
        var cell = HudScenes.R(HudRegionIds.HudIcon(0));
        void Part(double x, double y, double w, double h, (byte R, byte G, byte B) colour)
        {
            var c = dim ? ((byte)(colour.R / 2), (byte)(colour.G / 2), (byte)(colour.B / 2)) : colour;
            canvas.Rect(new NormalizedRect(cell.X + (cell.Width * x), cell.Y + (cell.Height * y), cell.Width * w, cell.Height * h), c);
        }

        switch (cls)
        {
            case OwnStatuses.Portrait:
                Part(0.10, 0.10, 0.80, 0.40, s_ink);
                Part(0.30, 0.60, 0.40, 0.30, s_mid);
                break;
            case OwnStatuses.Hooked:
                Part(0.40, 0.05, 0.20, 0.90, s_ink);
                Part(0.40, 0.70, 0.50, 0.20, s_ink);
                break;
            case OwnStatuses.Dying:
                Part(0.05, 0.40, 0.90, 0.20, s_ink);
                Part(0.20, 0.15, 0.20, 0.70, s_mid);
                break;
            case OwnStatuses.Dead:
                Part(0.10, 0.10, 0.25, 0.80, s_ink);
                Part(0.65, 0.10, 0.25, 0.80, s_ink);
                break;
            default:
                break;
        }
    }

    private static ImageBuffer Hud(int width, string? cls, bool dim = false, string? owner = HudScenes.Owner) =>
        HudScenes.TrialHud(width, owner: owner, decorate: canvas =>
        {
            if (cls is not null)
            {
                Icon(canvas, cls, dim);
            }
        });

    private static OwnStatusTemplates Learned(params string[] classes) => new(classes.ToDictionary(
        c => c,
        c => (IReadOnlyList<ImageBuffer>)[HudScenes.Profile.Region(HudRegionIds.HudIcon(0)) is { } r ? Hud(1920, c).Crop(r.Rect) : throw new InvalidOperationException()],
        StringComparer.Ordinal));

    private static readonly string[] s_all = [OwnStatuses.Portrait, OwnStatuses.Hooked, OwnStatuses.Dying, OwnStatuses.Dead];

    // ---- own status ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OwnStatuses.Portrait, 1920)]
    [InlineData(OwnStatuses.Hooked, 1920)]
    [InlineData(OwnStatuses.Dying, 1920)]
    [InlineData(OwnStatuses.Dead, 1920)]
    [InlineData(OwnStatuses.Hooked, 2560)]
    [InlineData(OwnStatuses.Dying, 1280)]
    [InlineData(OwnStatuses.Portrait, 2560)]
    public async Task EachClass_IsReadAsItself_AtThreeFrameSizes_AfterLearning(string cls, int width)
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));

        var frame = await h.Frame(Hud(width, cls), role: GameRole.Survivor);

        var status = HudHarness.Of(frame, ObservationKind.OwnStatus);
        Assert.NotNull(status);
        Assert.Equal(cls, status.Value);
        Assert.Equal(DetectorId.Status, status.Detector);
        Assert.InRange(status.Score, HudProfile.Catalogue.Thresholds.StatusMinScore, 1.0);
    }

    [Fact]
    public async Task TheDeadIcon_IsNeverReadAsHooked_AndAHookedIconIsNeverReadAsDead()
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));

        var dead = HudHarness.Of(await h.Frame(Hud(1920, OwnStatuses.Dead), role: GameRole.Survivor), ObservationKind.OwnStatus);
        var hooked = HudHarness.Of(await h.Frame(Hud(1920, OwnStatuses.Hooked), role: GameRole.Survivor), ObservationKind.OwnStatus);

        Assert.Equal(OwnStatuses.Dead, dead!.Value);
        Assert.Equal(OwnStatuses.Hooked, hooked!.Value);
    }

    [Fact]
    public async Task ADimmerPicture_StillMatches_BecauseCorrelationIgnoresBrightness()
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));

        var status = HudHarness.Of(await h.Frame(Hud(1920, OwnStatuses.Hooked, dim: true), role: GameRole.Survivor), ObservationKind.OwnStatus);

        Assert.Equal(OwnStatuses.Hooked, status!.Value);
    }

    [Fact]
    public async Task AnUnknownPicture_IsUnreadable_NeverAGuess()
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));

        var blank = HudHarness.Of(await h.Frame(Hud(1920, null), role: GameRole.Survivor), ObservationKind.OwnStatus);
        var noisy = HudHarness.Of(await h.Frame(HudScenes.TrialHud(1920, decorate: c =>
        {
            var cell = HudScenes.R(HudRegionIds.HudIcon(0));
            for (var i = 0; i < 6; i++)
            {
                c.Rect(new NormalizedRect(cell.X + (cell.Width * ((i * 37 % 10) / 10.0)), cell.Y + (cell.Height * ((i * 53 % 10) / 10.0)), cell.Width * 0.12, cell.Height * 0.12), s_ink);
            }
        }), role: GameRole.Survivor), ObservationKind.OwnStatus);

        Assert.NotNull(blank);
        Assert.Null(blank.Value);
        Assert.NotNull(noisy);
        Assert.Null(noisy.Value);
    }

    [Fact]
    public async Task InAnonymousMode_WithNoOwnerName_TheDetectorGivesNoObservationAtAll()
    {
        var h = new HudHarness(ownerName: null, statusTemplates: Learned(s_all));

        var frame = await h.Frame(Hud(1920, OwnStatuses.Hooked, owner: null), role: GameRole.Survivor);

        Assert.Null(HudHarness.Of(frame, ObservationKind.OwnStatus));
    }

    [Fact]
    public async Task WhenTheFirstRowIsNotTheOwners_TheDetectorGivesNoObservation()
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));

        var frame = await h.Frame(Hud(1920, OwnStatuses.Hooked, owner: "SOMEONEELSE"), role: GameRole.Survivor);

        Assert.Null(HudHarness.Of(frame, ObservationKind.OwnStatus));
    }

    [Theory]
    [InlineData(GameRole.None)]
    [InlineData(GameRole.Killer)]
    public async Task WithoutAConfirmedSurvivorRole_TheDetectorGivesNoObservation(GameRole role)
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));

        var frame = await h.Frame(Hud(1920, OwnStatuses.Hooked), role: role);

        Assert.Null(HudHarness.Of(frame, ObservationKind.OwnStatus));
    }

    [Fact]
    public async Task OutsideTheTrialHud_TheDetectorGivesNoObservation()
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));

        var frame = await h.Frame(HudScenes.Lobby(1920), role: GameRole.Survivor);

        Assert.Null(HudHarness.Of(frame, ObservationKind.OwnStatus));
    }

    [Theory]
    [InlineData(OwnStatuses.Dead)]
    [InlineData(OwnStatuses.Portrait)]
    [InlineData(OwnStatuses.Dying)]
    public async Task AnIncompleteSetOfPictures_FailsClosed_AndSoDoesNoPictureAtAll(string missing)
    {
        var h = new HudHarness(statusTemplates: Learned([.. s_all.Where(c => c != missing)]));
        var none = new HudHarness();

        var partial = await h.Frame(Hud(1920, OwnStatuses.Hooked), role: GameRole.Survivor);
        var empty = await none.Frame(Hud(1920, OwnStatuses.Hooked), role: GameRole.Survivor);

        Assert.Null(HudHarness.Of(partial, ObservationKind.OwnStatus));
        Assert.Null(HudHarness.Of(empty, ObservationKind.OwnStatus));
    }

    [Fact]
    public void TheTemplates_RefuseUnknownClasses_FlatPictures_AndOversizedOnes_AndRoundTripThroughJson()
    {
        var good = Hud(1920, OwnStatuses.Hooked).Crop(HudScenes.R(HudRegionIds.HudIcon(0)));
        var flat = ImageBuffer.CreateGray(16, 16, 90);

        Assert.Throws<ArgumentException>(() => new OwnStatusTemplates(new Dictionary<string, IReadOnlyList<ImageBuffer>> { ["Banana"] = [good] }));
        Assert.Throws<ArgumentException>(() => new OwnStatusTemplates(new Dictionary<string, IReadOnlyList<ImageBuffer>> { [OwnStatuses.Hooked] = [flat] }));
        Assert.Throws<ArgumentException>(() => new OwnStatusTemplates(new Dictionary<string, IReadOnlyList<ImageBuffer>> { [OwnStatuses.Hooked] = [ImageBuffer.FromFunction(300, 20, (x, _) => (byte)(x % 255))] }));

        var set = Learned(s_all);
        var parsed = OwnStatusTemplates.TryParse(set.ToJson(), out var problem);

        Assert.Null(problem);
        Assert.NotNull(parsed);
        Assert.True(parsed.IsComplete);
        Assert.Equal(set.ToJson(), parsed.ToJson());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"templates\":[]}")]
    [InlineData("{\"version\":1,\"templates\":[{\"class\":\"Hooked\",\"width\":4,\"height\":4,\"gray\":\"AAAA\"}]}")]
    [InlineData("{\"version\":1,\"templates\":[{\"class\":\"Banana\",\"width\":4,\"height\":4,\"gray\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}]}")]
    public void AFileThatIsNotUsable_IsRefusedWithAReason_NeverHalfLoaded(string json)
    {
        var parsed = OwnStatusTemplates.TryParse(json, out var problem);

        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public async Task TheStatusDetector_StaysDisabledForDiscord_AndTheCatalogueProfileCannotRaiseIt()
    {
        var h = new HudHarness(statusTemplates: Learned(s_all));
        var requested = Enum.GetValues<DetectorId>().ToDictionary(id => id, _ => new DetectorSettings(DetectionTier.Validated, ThresholdProvenance.Measured, 0.05, "1", HudProfile.CatalogueId));

        var gate = h.Pipeline.GateSettings(requested);

        Assert.All(h.Pipeline.Detectors, d => Assert.Equal(DetectionTier.Disabled, d.DefaultTier));
        foreach (var id in new[] { DetectorId.Status, DetectorId.SurvivorResult, DetectorId.KillerResult, DetectorId.HookCount })
        {
            Assert.Equal(DetectionTier.Disabled, gate.Effective[id].Tier);
            Assert.Contains(gate.Refused, r => r.StartsWith(id.ToString(), StringComparison.Ordinal));
        }

        await Task.CompletedTask;
    }

    // ---- hook counter --------------------------------------------------------------------------------------------------------

    private static ImageBuffer KillerHud(int width, string? counter) =>
        HudScenes.TrialHud(width, survivorView: false, decorate: canvas =>
        {
            if (counter is not null)
            {
                canvas.TextIn(HudScenes.R(HudRegionIds.HookCounter), counter);
            }
        });

    [Theory]
    [InlineData("0", 1920)]
    [InlineData("3", 1920)]
    [InlineData("12", 2560)]
    [InlineData("7", 1280)]
    public async Task TheHookCounter_IsReadAsANumber_ForTheKiller(string text, int width)
    {
        var h = new HudHarness();

        var hooks = HudHarness.Of(await h.Frame(KillerHud(width, text), role: GameRole.Killer), ObservationKind.HookCount);

        Assert.NotNull(hooks);
        Assert.Equal(text, hooks.Value);
        Assert.Equal(DetectorId.HookCount, hooks.Detector);
        Assert.Equal(HudProfile.Catalogue.Thresholds.WeakCueScore, hooks.Score);
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("123")]
    [InlineData("1 2")]
    public async Task ATextThatIsNotANumber_IsUnreadable(string text)
    {
        var h = new HudHarness();

        var hooks = HudHarness.Of(await h.Frame(KillerHud(1920, text), role: GameRole.Killer), ObservationKind.HookCount);

        Assert.NotNull(hooks);
        Assert.Null(hooks.Value);
    }

    [Theory]
    [InlineData(GameRole.None)]
    [InlineData(GameRole.Survivor)]
    public async Task TheHookCounter_IsNotReadForAnyoneButTheKiller(GameRole role)
    {
        var h = new HudHarness();

        var frame = await h.Frame(KillerHud(1920, "3"), role: role);

        Assert.Null(HudHarness.Of(frame, ObservationKind.HookCount));
    }

    [Fact]
    public async Task TheHookCounterRegion_IsReadOnlyOnTheKillersTrialHud_AndIsAnInitialGuessOutsideTheCatalogue()
    {
        var region = HudProfile.Catalogue.Region(HudRegionIds.HookCounter);
        Assert.NotNull(region);
        Assert.True(region.Detail);
        Assert.True(region.ReadsText);
        Assert.Equal(ThresholdProvenance.InitialGuess, HudProfile.Catalogue.Provenance);

        var survivor = new HudHarness();
        await survivor.Frame(KillerHud(1920, "3"), role: GameRole.Survivor);
        var killer = new HudHarness();
        await killer.Frame(KillerHud(1920, "3"), role: GameRole.Killer);

        Assert.DoesNotContain(HudRegionIds.HookCounter, survivor.Pipeline.CachedRegionIds);
        Assert.Contains(HudRegionIds.HookCounter, killer.Pipeline.CachedRegionIds);
    }

    // ---- outcomes -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ESCAPED", ResultOutcomes.Escaped, true)]
    [InlineData("SACRIFICED", ResultOutcomes.Sacrificed, true)]
    [InlineData("DEAD", ResultOutcomes.Dead, true)]
    [InlineData("THE ENTITY HUNGERS...", ResultOutcomes.EntityHungers, false)]
    [InlineData("BRUTAL KILLER", ResultOutcomes.BrutalKiller, false)]
    [InlineData("RUTHLESS KILLER", ResultOutcomes.RuthlessKiller, false)]
    [InlineData("MERCILESS KILLER", ResultOutcomes.MercilessKiller, false)]
    public async Task EachListedOutcome_IsReportedByTheDetectorOfItsKind_AndByNoOther(string drawn, string id, bool survivor)
    {
        var h = new HudHarness();

        var frame = await h.Frame(HudScenes.MatchResult(1920, outcome: drawn));

        var mine = HudHarness.Of(frame, survivor ? ObservationKind.SurvivorResult : ObservationKind.KillerResult);
        var other = HudHarness.Of(frame, survivor ? ObservationKind.KillerResult : ObservationKind.SurvivorResult);
        Assert.NotNull(mine);
        Assert.Equal(id, mine.Value);
        Assert.Null(other);
        Assert.InRange(mine.Score, 0.6, 1.0);
    }

    [Fact]
    public async Task TheLabelWithoutAListedOutcome_ReportsNothing()
    {
        var h = new HudHarness();

        var frame = await h.Frame(HudScenes.MatchResult(1920, outcome: "SOMETHING ELSE ENTIRELY"));

        Assert.Null(HudHarness.Of(frame, ObservationKind.SurvivorResult));
        Assert.Null(HudHarness.Of(frame, ObservationKind.KillerResult));
    }

    [Fact]
    public async Task AnOutcomeOfTheOtherRole_IsNotReported_WhenTheRoleIsKnown()
    {
        var h = new HudHarness();

        var asKiller = await h.Frame(HudScenes.MatchResult(1920, outcome: "ESCAPED"), role: GameRole.Killer);
        var asSurvivor = await h.Frame(HudScenes.MatchResult(1920, outcome: "BRUTAL KILLER"), role: GameRole.Survivor);

        Assert.Null(HudHarness.Of(asKiller, ObservationKind.SurvivorResult));
        Assert.Null(HudHarness.Of(asSurvivor, ObservationKind.KillerResult));
    }

    [Fact]
    public async Task Outcomes_AreOnlyReadOnTheResultScreen()
    {
        var h = new HudHarness();

        var frame = await h.Frame(HudScenes.Lobby(1920));

        Assert.Null(HudHarness.Of(frame, ObservationKind.SurvivorResult));
        Assert.Null(HudHarness.Of(frame, ObservationKind.KillerResult));
    }

    [Fact]
    public void TheOutcomeIdsOfTheEngine_AreTheIdsOfTheClosedVocabulary()
    {
        var ids = HudHarness.Vocabulary.Entries(DbdPresence.Vision.Text.VocabularyKind.ResultOutcome).Select(e => e.Id).Order(StringComparer.Ordinal).ToArray();
        string[] expected =
        [
            ResultOutcomes.Escaped, ResultOutcomes.Sacrificed, ResultOutcomes.Dead, ResultOutcomes.EntityHungers,
            ResultOutcomes.BrutalKiller, ResultOutcomes.RuthlessKiller, ResultOutcomes.MercilessKiller,
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), ids);
        Assert.All(ids, id => Assert.NotNull(ResultOutcomes.KindOf(id)));
    }
}
