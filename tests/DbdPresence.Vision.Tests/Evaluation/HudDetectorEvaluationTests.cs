// Covers: FR-DET-16 and FR-DET-24 (every HUD detector runs through the evaluator, also from PNG frames on disk), C5.
// MECHANICS ONLY: the frames are drawn text and flat colour; every report below is marked synthetic and can never earn a tier. None of these numbers
// is accuracy; accuracy on the game is UNKNOWN until the owner's held-out sessions are evaluated (PENDING-ENV).
using System.Globalization;
using DbdPresence.Core.Detection;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Evaluation;

public sealed class HudDetectorEvaluationTests : IDisposable
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConfirmationRule s_rule = new(3, 4, TimeSpan.FromSeconds(1.5), 0.6, TimeSpan.FromSeconds(30));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbd-presence-hud-eval-" + Guid.NewGuid().ToString("N"));

    public HudDetectorEvaluationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static HudEvaluationAdapter Adapter() =>
        new(HudProfile.Catalogue, HudHarness.Lists, new BitmapFontOcr(), HudHarness.Context, HudScenes.Owner);

    private static EvaluationUnit Unit(string id, string session, string? truth, ImageBuffer image, int frames = 5) =>
        new(id, session, truth, Enumerable.Range(0, frames).Select(i => new EvaluationFrame(s_t0.AddSeconds(i), image)).ToList());

    private static EvaluationOptions Options(string detector) => new("synthetic-hud", detector, 1, "synthetic 16:9", DataOrigin.Synthetic, s_rule);

    private static void AssertMechanicsOnly(EvaluationReport report)
    {
        Assert.False(report.Gates.Applicable);
        Assert.False(report.Gates.ExperimentalMeasuredGatesMet);
        Assert.False(report.Gates.ValidatedMeasuredGatesMet);
        Assert.Contains("mechanics only", report.Gates.Notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheSceneDetector_IsEvaluated_OnEveryCataloguedScreenAtThreeSizes_WithConstructedTruth()
    {
        var adapter = Adapter();
        var units = new List<EvaluationUnit>();
        var n = 0;
        foreach (var width in HudScenes.Widths)
        {
            (string Truth, ImageBuffer Image)[] screens =
            [
                ("MainMenu", HudScenes.MainMenu(width)),
                ("Other", HudScenes.ModeList(width, eventList: true)),
                ("Lobby", HudScenes.Lobby(width)),
                ("Matchmaking", HudScenes.Searching(width, "HAUNTED BY DAYLIGHT")),
                ("Lobby", HudScenes.PreMatchSurvivor(width, "DWIGHT FAIRFIELD")),
                ("InTrial", HudScenes.MatchStart(width, "MOUNT ORMOND RESORT", "ORMOND")),
                ("InTrial", HudScenes.TrialHud(width)),
                ("Spectating", HudScenes.TrialHud(width, spectatorBar: true)),
                ("Results", HudScenes.MatchResult(width)),
                ("Results", HudScenes.Scoreboard(width, HudScenes.SurvivorGame(), 1)),
                ("InMatchMenu", HudScenes.MatchDetails(width, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT")),
            ];
            units.AddRange(screens.Select(s => Unit($"u{n++}", $"s{width}", s.Truth, s.Image)));
        }

        var report = DetectionEvaluator.Run(units, adapter.ClassifierFor(ObservationKind.Scene), Options("scene"));

        Assert.Equal(33, report.Units);
        Assert.Equal(new Proportion(33, 33), report.Precision);
        Assert.Equal(new Proportion(33, 33), report.Coverage);
        Assert.Equal(0, report.Abstained);
        Assert.Equal(3, report.Sessions);
        AssertMechanicsOnly(report);
    }

    [Fact]
    public void TheSettingsMenu_IsEvaluatedWithThePriorSceneSetPerUnit()
    {
        var adapter = Adapter();
        var settings = HudScenes.Settings(1920);

        var inTrial = DetectionEvaluator.Run([Unit("a", "s", "InMatchMenu", settings)], adapter.ClassifierFor(ObservationKind.Scene, SceneKind.InTrial), Options("scene"));
        var fromMenu = DetectionEvaluator.Run([Unit("b", "s", "Other", settings)], adapter.ClassifierFor(ObservationKind.Scene, SceneKind.MainMenu), Options("scene"));
        var wrongPrior = DetectionEvaluator.Run([Unit("c", "s", "InMatchMenu", settings)], adapter.ClassifierFor(ObservationKind.Scene, SceneKind.MainMenu), Options("scene"));

        Assert.Equal(new Proportion(1, 1), inTrial.Precision);
        Assert.Equal(new Proportion(1, 1), fromMenu.Precision);
        Assert.Equal(new Proportion(0, 1), wrongPrior.Precision); // the evaluator does catch a wrong reading
    }

    [Fact]
    public void QueueMapRoleCharacterAndKiller_AreEvaluatedThroughTheSameEvaluator()
    {
        var adapter = Adapter();
        var rows = HudScenes.SurvivorGame();
        var tab = HudScenes.MatchDetails(1920, rows, 1, "ORMOND - MOUNT ORMOND RESORT");
        var killerTab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame("THE NIGHTMARE", 4), 4, "ORMOND - MOUNT ORMOND RESORT", survivorView: false);

        var queue = DetectionEvaluator.Run(
            [
                Unit("q1", "s1", QueueModes.Normal, HudScenes.ModeList(1920, eventList: false)),
                Unit("q2", "s1", "event-haunted-by-daylight", HudScenes.ModeList(1920, eventList: true)),
                Unit("q3", "s2", "event-haunted-by-daylight", HudScenes.Searching(2560, "HAUNTED BY DAYLIGHT")),
            ],
            adapter.ClassifierFor(ObservationKind.QueueMode),
            Options("queue-mode"));
        var map = DetectionEvaluator.Run(
            [
                Unit("m1", "s1", "ormond/mount-ormond-resort", tab),
                Unit("m2", "s2", "the-decimated-borgo/the-shattered-square", HudScenes.MatchStart(1280, "THE SHATTERED SQUARE", "THE DECIMATED BORGO")),
            ],
            adapter.ClassifierFor(ObservationKind.Map),
            Options("map"));
        var role = DetectionEvaluator.Run([Unit("r1", "s1", "Survivor", tab), Unit("r2", "s2", "Killer", killerTab), Unit("r3", "s3", "Killer", HudScenes.PreMatchKiller(1280))], adapter.ClassifierFor(ObservationKind.Role), Options("role"));
        var character = DetectionEvaluator.Run([Unit("c1", "s1", "dwight-fairfield", tab), Unit("c2", "s2", "the-nightmare", killerTab)], adapter.ClassifierFor(ObservationKind.Character), Options("character"));
        var killer = DetectionEvaluator.Run([Unit("k1", "s1", "the-lich", tab), Unit("k2", "s2", "the-nightmare", killerTab)], adapter.ClassifierFor(ObservationKind.Killer), Options("killer"));

        foreach (var report in new[] { queue, map, role, character, killer })
        {
            Assert.Equal(report.Units, report.Correct);
            Assert.Equal(0, report.Abstained);
            AssertMechanicsOnly(report);
        }
    }

    [Fact]
    public void AnUnreadableOrWronglyLabelledUnit_ShowsUpAsAbstentionOrError_NeverAsAHit()
    {
        var adapter = Adapter();
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT");

        var report = DetectionEvaluator.Run(
            [
                Unit("good", "s1", "dwight-fairfield", tab),
                Unit("wrong-label", "s1", "meg-thomas", tab),
                Unit("blank", "s2", "dwight-fairfield", HudScenes.Blank(1920)),
            ],
            adapter.ClassifierFor(ObservationKind.Character),
            Options("character"));

        Assert.Equal(3, report.LegibleUnits);
        Assert.Equal(new Proportion(1, 2), report.Precision);
        Assert.Equal(1, report.Abstained);
        Assert.Equal(1, report.Correct);
    }

    [Fact]
    public void TextRecognitionRunsOncePerFrame_NotOncePerEvaluatedKind()
    {
        var ocr = new BitmapFontOcr();
        var adapter = new HudEvaluationAdapter(HudProfile.Catalogue, HudHarness.Lists, ocr, HudHarness.Context, HudScenes.Owner);
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT");
        var frame = new EvaluationFrame(s_t0, tab);

        foreach (var kind in new[] { ObservationKind.Scene, ObservationKind.Map, ObservationKind.Role, ObservationKind.Character, ObservationKind.Killer })
        {
            adapter.ClassifierFor(kind)(frame);
        }

        var once = ocr.Calls;
        Assert.Equal(once, adapter.OcrCalls);
        adapter.ClassifierFor(ObservationKind.Scene)(frame);
        Assert.Equal(once, ocr.Calls);
    }

    [Fact]
    public void TheEvaluation_IsDeterministic_ByteForByte()
    {
        string Run()
        {
            var adapter = Adapter();
            var report = DetectionEvaluator.Run(
                [Unit("a", "s1", "MainMenu", HudScenes.MainMenu(1920)), Unit("b", "s2", "Lobby", HudScenes.Lobby(1280))],
                adapter.ClassifierFor(ObservationKind.Scene),
                Options("scene"));
            return EvaluationReportWriter.ToMarkdown(report);
        }

        Assert.Equal(Run(), Run());
    }

    // ---- From PNG frames on disk (what the study tool writes) -----------------------------------------------------

    [Fact]
    public void AStudyStyleSession_OfPngFrames_IsLoadedDecodedAndEvaluated_ForSceneAndCharacter()
    {
        const int Width = 1280;
        var rows = HudScenes.SurvivorGame();
        var screens = new (string Scene, ImageBuffer Image)[]
        {
            ("MainMenu", HudScenes.MainMenu(Width)),
            ("Lobby", HudScenes.Lobby(Width)),
            ("InMatchMenu", HudScenes.MatchDetails(Width, rows, 1, "ORMOND - MOUNT ORMOND RESORT")),
            ("Results", HudScenes.Scoreboard(Width, rows, 1)),
        };
        var directory = Path.Combine(_root, "sessions", "2026-10-24-1930");
        Directory.CreateDirectory(Path.Combine(directory, "frames"));
        File.WriteAllText(
            Path.Combine(directory, "session.json"),
            """{"schemaVersion":1,"sessionId":"2026-10-24-1930","startedAtUtc":"2026-10-24T19:30:12Z","gameVersion":"synthetic","resolution":{"width":1280,"height":720},"windowMode":"Borderless","dpiScale":1.0,"hudScale":1.0,"menuScale":1.0,"language":"en","colourBlindMode":"none","declaredMode":"Standard","split":"tuning"}""");

        var frames = new List<string>();
        var labels = new List<string>();
        var number = 1;
        foreach (var (scene, image) in screens)
        {
            var first = number;
            for (var i = 0; i < 4; i++, number++)
            {
                var id = number.ToString("D6", CultureInfo.InvariantCulture);
                var bgra = image.ToPixelBuffer().Bgra.ToArray();
                File.WriteAllBytes(Path.Combine(directory, "frames", id + ".png"), PngEncoder.Encode(image.Width, image.Height, bgra));
                frames.Add(string.Create(CultureInfo.InvariantCulture, $$"""{"schemaVersion":1,"frameId":"{{id}}","timestampUtc":"2026-10-24T19:30:{{number:D2}}Z","file":"frames/{{id}}.png"}"""));
            }

            var from = first.ToString("D6", CultureInfo.InvariantCulture);
            var to = (number - 1).ToString("D6", CultureInfo.InvariantCulture);
            labels.Add(Label("t", "scene", scene, from, to, "segment"));
            if (scene is "InMatchMenu" or "Results")
            {
                labels.Add(Label("t", "character", "dwight-fairfield", from, to, "trial-fact"));
            }
        }

        File.WriteAllLines(Path.Combine(directory, "frames.jsonl"), frames);
        File.WriteAllLines(Path.Combine(directory, "labels.jsonl"), labels);

        var session = DatasetLoader.LoadDataset(_root).Single();
        var decoder = new ExtensionFrameDecoder();
        var adapter = Adapter();
        var scene1 = DetectionEvaluator.Run(DatasetLoader.BuildUnits(session, "scene", decoder), adapter.ClassifierFor(ObservationKind.Scene), Options("scene"));
        var character1 = DetectionEvaluator.Run(DatasetLoader.BuildUnits(session, "character", decoder), adapter.ClassifierFor(ObservationKind.Character), Options("character"));

        Assert.Equal(new Proportion(4, 4), scene1.Precision);
        Assert.Equal(new Proportion(2, 2), character1.Precision);
        AssertMechanicsOnly(scene1);
    }

    private static string Label(string trial, string detector, string label, string from, string to, string type) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"schemaVersion":1,"type":"{{type}}","trialId":"{{trial}}","detector":"{{detector}}","label":"{{label}}","fromFrame":"{{from}}","toFrame":"{{to}}","legible":true,"labeller":"test","labelledAtUtc":"2026-10-24T20:00:00Z","reviewStatus":"labelled"}""");
}
