// Covers: FR-DET-25 (status line and the "What the app sees" card, in the app only), NFR-PRIV-01 (screen reading is an explicit opt-in, off by default),
// UI_SPEC Home and Privacy. MECHANICS ONLY: the screens are drawn text on flat colour and the recogniser is an exact fake. Nothing here is accuracy.
using DbdPresence.Core.Game;
using DbdPresence.Presentation.Detection;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;
using DbdPresence.Vision.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class DetectionViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbd-presence-study-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static DetectionViewModel Card(LoopHarness h, IStudyProgress? progress = null) =>
        new(h.Settings, new InlineDispatcher(), h.Loop, new DetectionNames(HudHarness.Vocabulary), progress);

    private sealed class FixedProgress(int? value) : IStudyProgress
    {
        public int? MatchesRecorded => value;
    }

    // ---- The setting ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ScreenReading_IsOffByDefault_AndTheCardIsNotShown()
    {
        using var h = new LoopHarness(detectionOn: false);
        using var screen = new ScreenReadingViewModel(h.Settings, new InlineDispatcher(), h.Loop);
        using var card = Card(h);

        Assert.False(screen.IsOn);
        Assert.False(h.Settings.Current.Detection.Enabled);
        Assert.False(card.IsOn);
        Assert.Equal(DetectionText.Off, card.StatusLine);
        Assert.All(card.Rows, r => Assert.Equal(DetectionRow.LearningMark, r.Mark));
    }

    [Fact]
    public void TurningScreenReadingOn_IsTheAcknowledgement_AndTurningItOffClearsBoth()
    {
        using var h = new LoopHarness(detectionOn: false);
        using var screen = new ScreenReadingViewModel(h.Settings, new InlineDispatcher(), h.Loop);
        using var card = Card(h);

        screen.BeginTurnOn();
        screen.Understood = true;
        screen.ConfirmTurnOnCommand.Execute(null);

        Assert.True(h.Settings.Current.Detection.Enabled);
        Assert.True(h.Settings.Current.Detection.RiskAcknowledged);
        Assert.True(card.IsOn);

        screen.TurnOffCommand.Execute(null);

        Assert.False(h.Settings.Current.Detection.Enabled);
        Assert.False(h.Settings.Current.Detection.RiskAcknowledged);
        Assert.False(card.IsOn);
    }

    [Fact]
    public void TheExplanationBesideTheSwitch_IsHonest_AboutRiskStorageAndDiscord()
    {
        var text = DetectionText.SettingExplanation;

        Assert.Contains("Nothing is saved", text, StringComparison.Ordinal);
        Assert.Contains("nothing from it is sent to Discord yet", text, StringComparison.Ordinal);
        Assert.Contains("off until you turn it on", text, StringComparison.Ordinal);
        Assert.Contains("not guaranteed", text, StringComparison.Ordinal);
        Assert.Contains("at your own risk", text, StringComparison.Ordinal);
        Assert.Equal("Shown here only — not sent to Discord until detection has been checked on your games.", DetectionText.NotSentNote);
    }

    // ---- The card -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheCard_ShowsWhatTheEngineConfirmed_WithAConfirmedOrLearningMarkForEachField()
    {
        using var h = new LoopHarness();
        h.StartGame();
        using var card = Card(h);

        Assert.Equal(["Scene", "Role", "Character", "Map", "Mode"], card.Rows.Select(r => r.Label).ToArray());
        Assert.All(card.Rows, r => Assert.Equal(DetectionRow.LearningMark, r.Mark));

        await h.CyclesAsync(4);

        var scene = card.Rows[0];
        Assert.Equal("Main menu", scene.Value);
        Assert.Equal(DetectionRow.ConfirmedMark, scene.Mark);
        Assert.True(scene.IsConfirmed);
        Assert.All(card.Rows.Skip(1), r =>
        {
            Assert.Equal("Not sure yet", r.Value);
            Assert.Equal(DetectionRow.LearningMark, r.Mark);
        });
        Assert.Equal(DetectionText.Reading, card.StatusLine);
        Assert.True(card.IsOn);
    }

    [Fact]
    public async Task ARoleTheUserSetThemselves_IsMarkedAsTheirsNotAsSeen()
    {
        using var h = new LoopHarness();
        h.StartGame();
        using var card = Card(h);
        await h.CyclesAsync(4); // a main menu, so the engine is up and running
        h.Loop.Engine.DeclareRole(Role.Survivor);

        card.Refresh();

        var role = card.Rows[1];
        Assert.Equal("Survivor", role.Value);
        Assert.Equal(DetectionRow.DeclaredMark, role.Mark);
        Assert.False(role.IsConfirmed);
    }

    [Fact]
    public async Task WhenTheGameCloses_TheCardGoesBackToNotSureYet()
    {
        using var h = new LoopHarness();
        h.StartGame();
        using var card = Card(h);
        await h.CyclesAsync(4);
        Assert.True(card.Rows[0].IsConfirmed);

        h.Game.Set(GameLifecycle.Initial);
        await h.CycleAsync();

        Assert.False(card.Rows[0].IsConfirmed);
        Assert.Equal(DetectionText.WaitingForGame, card.StatusLine);
    }

    // ---- Progress line ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheProgressLine_IsShownOnlyWhenTheAppCanKnowTheCount()
    {
        using var h = new LoopHarness();
        using var unknown = Card(h);
        using var nullCount = Card(h, new FixedProgress(null));
        using var known = Card(h, new FixedProgress(4));
        using var enough = Card(h, new FixedProgress(31));

        Assert.Null(unknown.ProgressLine);
        Assert.Null(nullCount.ProgressLine);
        Assert.Equal("Automatic detection: learning (4 of 30 matches recorded)", known.ProgressLine);
        Assert.Equal("Automatic detection: learning (31 of 30 matches recorded)", enough.ProgressLine);
    }

    private string Recording(string name, params string[] labelLines)
    {
        var folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "labels.jsonl"), labelLines);
        File.WriteAllBytes(Path.Combine(folder, "frame-00001.png"), [0]);
        return folder;
    }

    private static string Fact(string trial, string detector = "role") =>
        $$"""{"schemaVersion":1,"type":"trial-fact","trialId":"{{trial}}","detector":"{{detector}}","label":"Survivor","fromFrame":"000001","toFrame":"000009","legible":true,"labeller":"owner","labelledAtUtc":"2026-10-25T09:00:00.000Z","reviewStatus":"labelled"}""";

    private static string Segment(string trial) =>
        $$"""{"schemaVersion":1,"type":"segment","trialId":"{{trial}}","detector":"scene","label":"Lobby","fromFrame":"000001","toFrame":"000002","legible":true,"labeller":"owner","labelledAtUtc":"2026-10-25T09:00:00.000Z","reviewStatus":"labelled"}""";

    [Fact]
    public void TheStudyFolderCount_IsTheDistinctMatchesTheOwnerConfirmed_ReadFromLabelFilesOnly()
    {
        Recording("2026-10-24_19-30-12", Segment("t00"), Fact("t01"), Fact("t01", "map"), Fact("t02"), Segment("t02"));
        Recording("2026-10-24_20-30-12", Fact("t01"), Fact("t02"), Fact("t03", "character"));
        Directory.CreateDirectory(Path.Combine(_folder, "2026-10-24_21-30-12")); // recorded but not labelled: counts as nothing

        Assert.Equal(5, StudyFolderProgress.Count(_folder));
    }

    private static string ReviewerFact(string trial, string status) =>
        Fact(trial).Replace("\"labeller\":\"owner\"", "\"labeller\":\"reviewer\",\"source\":\"reviewer\"", StringComparison.Ordinal).Replace("\"reviewStatus\":\"labelled\"", $"\"reviewStatus\":\"{status}\"", StringComparison.Ordinal);

    [Fact]
    public void TheStudyFolderCount_CountsAReviewersMatchesOnlyAfterTheOwnersSpotCheckPassedThem()
    {
        Recording("2026-10-24_19-30-12", Fact("t01"), ReviewerFact("t02", "labelled"));
        Recording("2026-10-24_20-30-12", ReviewerFact("t01", "disputed"), ReviewerFact("t02", "labelled"));
        Assert.Equal(1, StudyFolderProgress.Count(_folder)); // only the owner's own match

        Recording("2026-10-24_21-30-12", ReviewerFact("t01", "rechecked"), ReviewerFact("t02", "rechecked"), ReviewerFact("t02", "rechecked"));

        Assert.Equal(3, StudyFolderProgress.Count(_folder));
    }

    [Fact]
    public void TheStudyFolderCount_IsUnknownWhenThereIsNothingLabelled_OrAFileIsDamaged_OrTheFolderIsMissing()
    {
        Assert.Null(StudyFolderProgress.Count(Path.Combine(_folder, "nowhere")));

        Directory.CreateDirectory(Path.Combine(_folder, "2026-10-24_21-30-12"));
        Assert.Null(StudyFolderProgress.Count(_folder));

        Recording("2026-10-24_19-30-12", Fact("t01"), "{ this is not json");
        Assert.Null(StudyFolderProgress.Count(_folder));
        Assert.Throws<ArgumentException>(() => StudyFolderProgress.Count(" "));
    }

    [Fact]
    public void TheStudyFolderCount_IsCachedForHalfAMinute_AndNeverOpensAPicture()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var folder = Recording("2026-10-24_19-30-12", Fact("t01"));
        var progress = new StudyFolderProgress(_folder, time);

        Assert.Equal(1, progress.MatchesRecorded);
        File.AppendAllLines(Path.Combine(folder, "labels.jsonl"), [Fact("t02")]);
        Assert.Equal(1, progress.MatchesRecorded);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(2, progress.MatchesRecorded);

        // The pictures in the folder are not valid PNGs: nothing but label files is read.
        Assert.Single(Directory.GetFiles(folder, "frame-*.png"));
    }

    // ---- Home ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Home_ExposesTheCard_AndRefreshesItOnItsClock()
    {
        using var h = new LoopHarness();
        using var coordinatorHarness = new Harness();
        using var card = Card(h, new FixedProgress(2));
        using var home = new HomeViewModel(coordinatorHarness.Controls(), coordinatorHarness.Coordinator, coordinatorHarness.Settings, coordinatorHarness.Dispatcher, coordinatorHarness.Time, card);

        Assert.Same(card, home.Detection);
        using var withoutCard = new HomeViewModel(coordinatorHarness.Controls(), coordinatorHarness.Coordinator, coordinatorHarness.Settings, coordinatorHarness.Dispatcher, coordinatorHarness.Time);
        Assert.Null(withoutCard.Detection);
    }
}
