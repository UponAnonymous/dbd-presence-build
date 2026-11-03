// Covers: PLAN_V2 D17 (windowed mode), FR-DET-16 (the evaluator), DETECTION_VALIDATION section 5 (labels are never changed by the evaluator).
// MECHANICS ONLY: the recordings are drawn text on flat colour inside a drawn white title bar and a fake recogniser reads them back exactly. They prove
// that the evaluator cuts, reports and leaves labels alone. They say nothing about the owner's real 1922x1112 recordings, which stay UNKNOWN until the
// evaluator has run on them and the crop has been checked by eye (PENDING-ENV).
using System.Security.Cryptography;
using DbdPresence.Core.Capture;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class EvalClientAreaTests : IDisposable
{
    private static readonly string[] s_scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];
    private const string Session = "2026-10-24_19-30-12";
    private readonly EvalWorkspace _w = new();

    public EvalClientAreaTests()
    {
        _w.Recognizer = _ => new BitmapFontOcr();
    }

    public void Dispose() => _w.Dispose();

    private string Out => Path.Combine(_w.Root, "report.md");

    private static IReadOnlyList<(string Scene, ImageBuffer Image)> Framed() =>
        [.. EvalWorkspace.MatchScreens().Select(s => (s.Scene, WindowFrames.Around(s.Image)))];

    private string Labelled(IReadOnlyList<(string Scene, ImageBuffer Image)> screens, WindowDisplayMode mode, bool? clientAreaOnly)
    {
        var folder = _w.AddRecording(Session, screens, 4, mode, clientAreaOnly);
        EvalWorkspace.WritePlan(folder, 4);
        _w.LabelAndRecheck(folder, s_scenes, "tuning");
        return folder;
    }

    private int Evaluate() => _w.Run("evaluate", "--out", Out, "--split", "tuning", "--owner-name", HudScenes.Owner);

    [Fact]
    public void AWindowedRecordingWithATitleBarAndNoFlag_IsCutBeforeTheDetectorsRun_AndTheReportSaysSo()
    {
        Labelled(Framed(), WindowDisplayMode.Windowed, null);

        Assert.Equal(0, Evaluate());

        var report = File.ReadAllText(Out);
        Assert.Contains($"| Client area, session {Session} | Applied:", report, StringComparison.Ordinal);
        Assert.Contains("Heuristic, not a measurement.", report, StringComparison.Ordinal);
        Assert.Contains("1280x720; Windowed", report, StringComparison.Ordinal);
        Assert.DoesNotContain("; 1282x752; Windowed", report, StringComparison.Ordinal);
        Assert.Contains("# Detection evaluation: scene v1", report, StringComparison.Ordinal);
        Assert.Contains("100.0% (4/4;", report, StringComparison.Ordinal);
        Assert.Contains("client area: Applied", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordingThatSaysItIsClientArea_IsUsedAsItIs()
    {
        Labelled(EvalWorkspace.MatchScreens(), WindowDisplayMode.Windowed, true);

        Assert.Equal(0, Evaluate());

        var report = File.ReadAllText(Out);
        Assert.Contains($"| Client area, session {Session} | NotNeeded: Recorded as the game's client area", report, StringComparison.Ordinal);
        Assert.Contains("1280x720; Windowed", report, StringComparison.Ordinal);
        Assert.Contains("100.0% (4/4;", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlaggedRecording_IsNeverCut_EvenWhenItsSizeLooksLikeAFrame()
    {
        // The recorder's own flag wins over the heuristic: this picture would be cut if it carried no flag.
        Labelled(Framed(), WindowDisplayMode.Windowed, true);

        Assert.Equal(0, Evaluate());

        var report = File.ReadAllText(Out);
        Assert.Contains("NotNeeded: Recorded as the game's client area", report, StringComparison.Ordinal);
        Assert.Contains("1282x752; Windowed", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullscreenRecording_HasNoTitleBarToLookFor()
    {
        Labelled(EvalWorkspace.MatchScreens(), WindowDisplayMode.FullscreenOrBorderless, null);

        Assert.Equal(0, Evaluate());

        Assert.Contains($"| Client area, session {Session} | NotNeeded: Borderless recording", File.ReadAllText(Out), StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowedRecordingOfAStandardSizeWithNoFlag_IsUsedAsItIs_AndTheReportSaysWhy()
    {
        // A windowed recording of exactly a standard size cannot carry a title bar of this kind, so nothing is cut and the report says why.
        Labelled(EvalWorkspace.MatchScreens(), WindowDisplayMode.Windowed, null);

        Assert.Equal(0, Evaluate());

        Assert.Contains("NotNeeded: 1280x720 is a standard game size", File.ReadAllText(Out), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEvaluator_NeverChangesALabelFile()
    {
        var folder = Labelled(Framed(), WindowDisplayMode.Windowed, null);
        var labels = Path.Combine(folder, "labels.jsonl");
        var before = SHA256.HashData(File.ReadAllBytes(labels));
        var sessionBefore = File.ReadAllText(Path.Combine(folder, "session.json"));

        Assert.Equal(0, Evaluate());

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(labels)));
        Assert.Equal(sessionBefore, File.ReadAllText(Path.Combine(folder, "session.json")));
        Assert.NotEmpty(File.ReadAllBytes(labels));
    }

    [Fact]
    public void PreparingARecording_CarriesTheRecordersFlagIntoSessionJson_AndLeavesItOutWhenThereIsNone()
    {
        var flagged = _w.AddRecording("2026-10-24_19-30-12", EvalWorkspace.MatchScreens(), 4, WindowDisplayMode.Windowed, true);
        var legacy = _w.AddRecording("2026-10-24_20-30-12", EvalWorkspace.MatchScreens(), 4, WindowDisplayMode.Windowed, null);

        Assert.Equal(0, _w.Run("prepare", "--split", "tuning"));

        Assert.Contains("\"clientArea\":true", File.ReadAllText(Path.Combine(flagged, "session.json")), StringComparison.Ordinal);
        Assert.DoesNotContain("clientArea", File.ReadAllText(Path.Combine(legacy, "session.json")), StringComparison.Ordinal);
    }
}
