// Covers: FR-DET-16 and FR-DET-24 (the command-line evaluator), DETECTION_VALIDATION section 5 (held-out manifest committed beforehand, one-shot rule, tuning
// sessions refused), section 6 (report), PLAN_V2 D25 (the owner's name is never written).
// MECHANICS ONLY: the recordings are drawn text on flat colour and the recogniser is a fake that reads them back exactly. Every number asserted here
// follows from how the recordings were built. None of it is accuracy; accuracy on the game is UNKNOWN until the owner's held-out sessions are evaluated.
using DbdPresence.Core.Capture;
using DbdPresence.Eval;
using DbdPresence.Study;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Tests.Support;
using GameRole = DbdPresence.Core.Game.Role;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class EvalEvaluateTests : IDisposable
{
    private static readonly string[] s_scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];
    private readonly EvalWorkspace _w = new();

    public EvalEvaluateTests()
    {
        // Stands in for Windows text recognition so that runs are classified as measured runs; it is the exact fake of the other tests.
        _w.Recognizer = _ => new BitmapFontOcr();
    }

    public void Dispose() => _w.Dispose();

    private string Out => Path.Combine(_w.Root, "report.md");

    private string AddLabelled(string name, string split)
    {
        var folder = _w.AddRecording(name, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folder, 4);
        _w.LabelAndRecheck(folder, s_scenes, split);
        return folder;
    }

    private (string File, string Hash) MakeManifest(string name = "manifest.txt")
    {
        var file = Path.Combine(_w.Root, name);
        Assert.Equal(0, _w.Run("manifest", "--out", file));
        var text = File.ReadAllText(file);
        var hash = text.Split('\n').Single(l => l.StartsWith("manifest-sha256:", StringComparison.Ordinal))["manifest-sha256:".Length..].Trim();
        return (file, hash);
    }

    private int Evaluate(params string[] extra) => _w.Run(["evaluate", "--out", Out, "--owner-name", HudScenes.Owner, .. extra]);

    // ---- A tuning run ------------------------------------------------------------------------------------------------

    [Fact]
    public void ATuningRun_ReportsEveryLabelledDetector_AndIsMarkedAsNotAccuracy()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");

        var code = Evaluate("--split", "tuning");

        Assert.Equal(0, code);
        var report = File.ReadAllText(Out);
        Assert.Contains("TUNING RUN", report, StringComparison.Ordinal);
        Assert.Contains("TUNING SPLIT", report, StringComparison.Ordinal);
        Assert.Contains("# Detection evaluation: scene v1", report, StringComparison.Ordinal);
        Assert.Contains("# Detection evaluation: role v1", report, StringComparison.Ordinal);
        Assert.Contains("# Detection evaluation: map v1", report, StringComparison.Ordinal);
        Assert.Contains("# Detection evaluation: character v1", report, StringComparison.Ordinal);
        Assert.Contains("# Detection evaluation: killer v1", report, StringComparison.Ordinal);
        Assert.DoesNotContain("# Detection evaluation: queue-mode", report, StringComparison.Ordinal);
        Assert.Contains("100.0% (4/4;", report, StringComparison.Ordinal);
        Assert.Contains("Labels for result.survivor exist but no detector produces them yet", report, StringComparison.Ordinal);
        Assert.Contains("cannot earn a tier", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Out + ".timing.md"));
        Assert.Contains("TUNING RUN", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ATuningRun_IgnoresHeldOutSessions_AndRefusesAManifest()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");
        AddLabelled("2026-10-24_20-30-12", "heldout");

        Assert.Equal(0, Evaluate("--split", "tuning"));

        var report = File.ReadAllText(Out);
        Assert.Contains("2026-10-24_19-30-12", report, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-24_20-30-12", report, StringComparison.Ordinal);
        Assert.Contains("| Sessions of the other split (not examined) | 1 |", report, StringComparison.Ordinal);

        Assert.Equal(1, Evaluate("--split", "tuning", "--manifest", "x.txt"));
        Assert.Contains("takes no manifest", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFakeRecogniser_MarksTheReportMechanicsOnly_NeverAccuracy()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");
        _w.Recognizer = kind => kind == OcrKind.Fake ? new BitmapFontOcr() : null;

        var code = _w.Run("evaluate", "--out", Out, "--split", "tuning", "--ocr", "fake", "--owner-name", HudScenes.Owner);

        Assert.Equal(0, code);
        var report = File.ReadAllText(Out);
        Assert.Contains("MECHANICS ONLY", report, StringComparison.Ordinal);
        Assert.Contains("FAKE (tests only)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("TUNING RUN", report, StringComparison.Ordinal);
        Assert.Contains("mechanics only", _w.Output + report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutEnglishTextRecognition_TheRunSaysSo_InsteadOfReportingEmptyReadings()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");
        _w.Recognizer = _ => null;

        Assert.Equal(1, Evaluate("--split", "tuning"));

        Assert.Contains("English", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void AFailingRecogniser_IsCountedAndShownInTheReport()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");
        _w.Recognizer = _ => new BitmapFontOcr { ForceStatus = TextRecognitionStatus.Unavailable };

        Assert.Equal(0, Evaluate("--split", "tuning"));

        Assert.Contains("did not run", _w.Output, StringComparison.Ordinal);
        Assert.Contains("0 / ", File.ReadAllText(Out), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOwnersName_IsUsedForTheRun_ButNeverWrittenToAnyFile()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");

        Assert.Equal(0, Evaluate("--split", "tuning"));

        var everything = _w.AllText().Append(File.ReadAllText(Out)).Append(File.ReadAllText(Out + ".timing.md")).Append(_w.Output).Append(_w.ErrorOutput);
        Assert.All(everything, text => Assert.DoesNotContain(HudScenes.Owner, text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("supplied on the command line for this run; not recorded", File.ReadAllText(Out), StringComparison.Ordinal);

        // The name matters: without it the owner's row cannot be found, so role and character are not read (the run says it was not supplied).
        var without = Path.Combine(_w.Root, "without.md");
        Assert.Equal(0, _w.Run("evaluate", "--out", without, "--split", "tuning"));
        Assert.Contains("not supplied", File.ReadAllText(without), StringComparison.Ordinal);
        Assert.NotEqual(File.ReadAllText(Out).Replace("supplied on the command line for this run; not recorded", string.Empty, StringComparison.Ordinal), File.ReadAllText(without));
    }

    [Fact]
    public void TheRunReadsEveryPictureInOrder_AndTheSettingsPriorComesFromTheLabelOfThePreviousRange()
    {
        var folder = AddLabelled("2026-10-24_19-30-12", "tuning");
        var session = DatasetLoader.LoadSession(folder);

        var priors = SessionObservations.PriorScenes(session);

        Assert.Equal(16, priors.Length);
        Assert.All(priors[..4], p => Assert.Equal(SceneKind.Unknown, p));
        Assert.All(priors[4..8], p => Assert.Equal(SceneKind.MainMenu, p));
        Assert.All(priors[8..12], p => Assert.Equal(SceneKind.Lobby, p));
        Assert.All(priors[12..], p => Assert.Equal(SceneKind.InMatchMenu, p));
    }

    [Fact]
    public void TheRolePrior_ComesFromTheOwnersRoleLabel_AndStaysUntilAnotherOneReplacesIt()
    {
        var at = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var frames = Enumerable.Range(0, 6).Select(i => new FrameRecord($"f{i}", at.AddSeconds(i), $"f{i}.png")).ToList();
        var meta = new SessionMetadata("s", at, "1", 1920, 1080, "Fullscreen", 1.0, 1.0, 1.0, "en", "off", "Standard", "tuning");
        LabelRecord Role(string value, string from, string to, bool legible = true) =>
            new("trial-fact", "t", "role", value, from, to, legible, "owner", at, "labelled");
        var session = new DatasetSession("x", meta, frames, [Role("Survivor", "f1", "f2"), Role("Killer", "f4", "f4"), Role("Survivor", "f5", "f5", legible: false)], "hash");

        var roles = SessionObservations.PriorRoles(session);

        Assert.Equal(
            [GameRole.None, GameRole.Survivor, GameRole.Survivor, GameRole.Survivor, GameRole.Killer, GameRole.Killer],
            roles);
    }

    [Fact]
    public void TheNewDetectors_CanBeEvaluatedByName_AndHaveNoLabelsInTheTestSession()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");

        Assert.Equal(0, Evaluate("--split", "tuning", "--detector", "own-status"));
        Assert.DoesNotContain("No detector exists", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(0, Evaluate("--split", "tuning", "--detector", "hook-count"));
        Assert.Equal(0, Evaluate("--split", "tuning", "--detector", "survivor-result"));
        Assert.Equal(0, Evaluate("--split", "tuning", "--detector", "killer-result"));
    }

    [Fact]
    public void UnknownDetectors_AreRefusedByName()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");

        Assert.Equal(1, Evaluate("--split", "tuning", "--detector", "generators"));
        Assert.Contains("generators", _w.ErrorOutput, StringComparison.Ordinal);

        Assert.Equal(0, Evaluate("--split", "tuning", "--detector", "scene"));
        var report = File.ReadAllText(Out);
        Assert.Contains("# Detection evaluation: scene v1", report, StringComparison.Ordinal);
        Assert.DoesNotContain("# Detection evaluation: role", report, StringComparison.Ordinal);
    }

    // ---- A held-out run ----------------------------------------------------------------------------------------------

    [Fact]
    public void AHeldOutRun_NeedsTheCommittedManifest_AndReproducesItsOwnReportBitForBit()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");
        AddLabelled("2026-10-24_20-30-12", "heldout");
        var (manifest, hash) = MakeManifest();

        Assert.Equal(1, Evaluate());
        Assert.Contains("needs the committed manifest", _w.ErrorOutput, StringComparison.Ordinal);

        Assert.Equal(0, Evaluate("--manifest", manifest, "--manifest-hash", hash, "--keep-pictures"));
        var first = File.ReadAllText(Out);
        Assert.Contains("Held-out run", first, StringComparison.Ordinal);
        Assert.Contains(hash, first, StringComparison.Ordinal);
        Assert.DoesNotContain("TUNING", first, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-24_19-30-12", first, StringComparison.Ordinal);
        Assert.Contains("2026-10-24_20-30-12", first, StringComparison.Ordinal);
        Assert.Contains("HELD-OUT RUN", _w.Output, StringComparison.Ordinal);

        Assert.Equal(0, Evaluate("--manifest", manifest, "--manifest-hash", hash, "--keep-pictures"));
        Assert.Equal(first, File.ReadAllText(Out));
        Assert.Contains("bit for bit", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeldOutReport_IsNeverReplacedByADifferentOne_TheOneShotRule()
    {
        AddLabelled("2026-10-24_20-30-12", "heldout");
        var (manifest, hash) = MakeManifest();
        Assert.Equal(0, Evaluate("--manifest", manifest, "--manifest-hash", hash, "--keep-pictures"));
        var first = File.ReadAllText(Out);

        // A second run that reads differently (here: without the owner's name) must not overwrite the first report.
        var code = _w.Run("evaluate", "--out", Out, "--manifest", manifest, "--manifest-hash", hash);

        Assert.Equal(1, code);
        Assert.Contains("evaluated once per frozen detector version", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(first, File.ReadAllText(Out));
    }

    [Fact]
    public void AHeldOutRun_RefusesATuningSessionInTheManifest()
    {
        var tuning = AddLabelled("2026-10-24_19-30-12", "tuning");
        var session = DatasetLoader.LoadSession(tuning);
        var forged = ManifestFile.For([session]);
        var file = Path.Combine(_w.Root, "forged.txt");
        File.WriteAllText(file, forged.ToText());

        Assert.Equal(1, Evaluate("--manifest", file, "--manifest-hash", forged.Hash));

        Assert.Contains("Tuning sessions are not allowed in a held-out run", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void AHeldOutRun_RefusesAWrongHash_AnEditedManifest_AndLabelsChangedAfterwards()
    {
        var folder = AddLabelled("2026-10-24_20-30-12", "heldout");
        var (manifest, hash) = MakeManifest();

        Assert.Equal(1, Evaluate("--manifest", manifest, "--manifest-hash", new string('0', 64)));
        Assert.Contains("not the hash in the manifest file", _w.ErrorOutput, StringComparison.Ordinal);

        var edited = Path.Combine(_w.Root, "edited.txt");
        File.WriteAllText(edited, File.ReadAllText(manifest).Replace("manifest-sha256: " + hash, "manifest-sha256: " + new string('a', 64), StringComparison.Ordinal));
        Assert.Equal(1, Evaluate("--manifest", edited, "--manifest-hash", new string('a', 64)));
        Assert.Contains("was edited", _w.ErrorOutput, StringComparison.Ordinal);

        var labels = Path.Combine(folder, SessionFiles.Labels);
        File.WriteAllText(labels, File.ReadAllText(labels).Replace("dwight-fairfield", "meg-thomas", StringComparison.Ordinal));
        Assert.Equal(1, Evaluate("--manifest", manifest, "--manifest-hash", hash));
        Assert.Contains("does not match the committed manifest", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void AHeldOutRun_RefusesAHeldOutSessionThatIsNotInTheManifest()
    {
        AddLabelled("2026-10-24_20-30-12", "heldout");
        var (manifest, hash) = MakeManifest();
        AddLabelled("2026-10-24_21-30-12", "heldout");

        Assert.Equal(1, Evaluate("--manifest", manifest, "--manifest-hash", hash));

        Assert.Contains("not in the committed manifest", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Contains("2026-10-24_21-30-12", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void LabelsThatDidNotPassTheBlindReCheck_CannotBeInAManifestOrEvaluated()
    {
        var folder = _w.AddRecording("2026-10-24_20-30-12", EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folder, 4);
        _w.Run("prepare", "--split", "heldout");
        var answers = Path.Combine(_w.Root, "a.json");
        File.WriteAllText(answers, EvalWorkspace.Answers("2026-10-24_20-30-12", s_scenes));
        _w.Run("apply", "--session", "2026-10-24_20-30-12", "--answers", answers);

        Assert.Equal(1, _w.Run("manifest", "--out", Path.Combine(_w.Root, "m.txt")));
        Assert.Contains("blind re-check", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_w.Root, "m.txt")));
    }

    [Fact]
    public void DisputedLabels_AreRefusedInEveryRun_AndAManifestIsNeverReplaced()
    {
        var folder = AddLabelled("2026-10-24_19-30-12", "tuning");
        var labels = Path.Combine(folder, SessionFiles.Labels);
        File.WriteAllText(labels, File.ReadAllText(labels).Replace("\"rechecked\"", "\"disputed\"", StringComparison.Ordinal));

        Assert.Equal(1, Evaluate("--split", "tuning"));
        Assert.Contains("disputed", _w.ErrorOutput, StringComparison.Ordinal);

        AddLabelled("2026-10-24_20-30-12", "heldout");
        var (manifest, _) = MakeManifest();
        Assert.Equal(1, _w.Run("manifest", "--out", manifest));
        Assert.Contains("already exists", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithNoSessionsOfTheRightKind_SaysSo()
    {
        AddLabelled("2026-10-24_19-30-12", "tuning");
        var file = Path.Combine(_w.Root, "m.txt");

        Assert.Equal(1, _w.Run("manifest", "--out", file));
        Assert.Contains("no held-out sessions", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManifestFormula_MatchesTheLibrarysManifestHash()
    {
        var a = DatasetLoader.LoadSession(AddLabelled("2026-10-24_19-30-12", "heldout"));
        var b = DatasetLoader.LoadSession(AddLabelled("2026-10-24_20-30-12", "heldout"));

        Assert.Equal(DatasetLoader.ManifestHash([a, b]), ManifestFile.For([b, a]).Hash);
        Assert.Throws<FormatException>(() => ManifestFile.Parse("manifest-sha256: abc\nx:y\n"));
        Assert.Throws<FormatException>(() => ManifestFile.Parse("# only a comment\n"));
        var rendered = ManifestFile.For([a, b]);
        Assert.Equal(rendered.Entries, ManifestFile.Parse(rendered.ToText()).Entries);
    }
}
