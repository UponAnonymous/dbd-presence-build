// Covers: FR-AUTO-03 (the own-row status detector's pictures are learned from the owner's labelled captures, ADR-0004), DETECTION_VALIDATION section 7 and the
// provenance rule of section 12 (templates come from tuning sessions only), PLAN_AUTO_ICONS section 6, and the review page / reviewer-labels format for the
// optional own-icon answer (section 16.1).
// MECHANICS ONLY: every picture is a flat grey frame with a drawn pattern in the icon cell. Nothing here says how the real game's icons look, and none of
// it is accuracy; how well learned pictures read the real game is UNKNOWN until the evaluator has measured it on held-out recordings.
using System.Text.Json;
using DbdPresence.Eval;
using DbdPresence.Study;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using DbdPresence.Vision.Text;
using static DbdPresence.Core.Detection.OwnStatuses;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class EvalTeachStatusTests : IDisposable
{
    private const string First = "2026-10-24_19-30-12";
    private const string Second = "2026-10-24_20-30-12";
    private const string Third = "2026-10-24_21-30-12";

    private readonly EvalWorkspace _w = new();

    public void Dispose() => _w.Dispose();

    private string Templates => Path.Combine(_w.AppData, StatusIconTeaching.FileName);

    /// <summary>A grey frame whose icon cell shows a pattern that depends on the class; brightness differs per recording to show that only the shape matters.</summary>
    private static ImageBuffer Frame(string pattern, int width, int brightness)
    {
        var key = (pattern, width, brightness);
        if (!s_frames.TryGetValue(key, out var cached))
        {
            s_frames[key] = cached = Draw(pattern, width, brightness);
        }

        return cached;
    }

    private static readonly Dictionary<(string, int, int), ImageBuffer> s_frames = [];

    private static ImageBuffer Draw(string pattern, int width, int brightness)
    {
        var height = width * 9 / 16;
        var cell = (HudProfile.Catalogue.Region(HudRegionIds.HudIcon(0)) ?? throw new InvalidOperationException()).Rect.ToPixels(width, height);
        return ImageBuffer.FromFunction(width, height, (x, y) =>
        {
            if (x < cell.X || x >= cell.Right || y < cell.Y || y >= cell.Bottom)
            {
                return 40;
            }

            var u = (x - cell.X) * 48 / cell.Width;
            var v = (y - cell.Y) * 36 / cell.Height;
            var value = pattern switch
            {
                Portrait => u * 4,
                Hooked => v * 5,
                Dying => (((u / 8) + (v / 8)) % 2) * 190,
                Dead => ((((u + v) / 12) % 2) * 190) + 20,
                _ => 128,
            };
            return (byte)Math.Clamp(value + brightness, 1, 254);
        });
    }

    /// <summary>
    /// Adds a recording whose ranges are four pictures each, one per entry (a "|" joins two patterns inside one range: the icon changes half way), prepares it
    /// as <paramref name="split"/>, and applies answers that say the whole recording is one survivor trial and name the owner's icon in every range.
    /// </summary>
    private string Labelled(string name, string split, string[] ranges, IReadOnlyList<string>? answered = null, int width = 1280, int brightness = 0, string role = "Survivor")
    {
        var screens = new List<(string Scene, ImageBuffer Image, int Pictures)>();
        foreach (var range in ranges)
        {
            var parts = range.Split('|');
            foreach (var part in parts)
            {
                screens.Add(("InTrial", Frame(part, width, brightness), 4 / parts.Length));
            }
        }

        var folder = _w.AddRecording(name, screens);
        EvalWorkspace.WritePlan(folder, ranges.Length);
        Assert.Equal(0, _w.Run("prepare", "--split", split));
        var file = Path.Combine(_w.Root, "answers-" + name + ".json");
        File.WriteAllText(file, OwnAnswers(name, answered ?? [.. ranges.Select(r => r.Split('|')[0])], role));
        Assert.Equal(0, _w.Run("apply", "--session", name, "--answers", file));
        return folder;
    }

    private static string OwnAnswers(string sessionId, IReadOnlyList<string> own, string role = "Survivor") => JsonText.Compact(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteString("kind", "labels");
        w.WriteString("sessionId", sessionId);
        w.WriteStartArray("segments");
        for (var i = 0; i < own.Count; i++)
        {
            w.WriteStartObject();
            w.WriteNumber("i", i);
            w.WriteString("scene", "InTrial");
            w.WriteBoolean("matchStart", i == 0);
            if (own[i].Length > 0)
            {
                w.WriteString("ownStatus", own[i]);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("matches");
        w.WriteStartObject();
        w.WriteNumber("startIndex", 0);
        w.WriteString("role", role);
        w.WriteString("character", "dwight-fairfield");
        w.WriteString("map", "ormond/mount-ormond-resort");
        w.WriteString("result", "Escaped");
        w.WriteString("killer", string.Empty);
        w.WriteString("queue", string.Empty);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static readonly string[] s_oneOfEach = [Portrait, Hooked, Dying, Dead];
    private static readonly string[] s_twoOfEach = [Portrait, Hooked, Dying, Dead, Portrait, Hooked, Dying, Dead];

    private OwnStatusTemplates Taught()
    {
        var parsed = OwnStatusTemplates.TryParse(File.ReadAllText(Templates), out var problem);
        Assert.Null(problem);
        return parsed!;
    }

    // ---- The command --------------------------------------------------------------------------------------------------

    [Fact]
    public void WithEnoughLabelledIconsFromTwoRecordings_ItWritesACompleteSet_WhereTheAppLoadsIt_AndSaysHowToCopyIt()
    {
        Labelled(First, "tuning", s_twoOfEach, brightness: 0);
        Labelled(Second, "tuning", s_oneOfEach, width: 1920, brightness: 30);

        var code = _w.Run("teach-status");

        Assert.Equal(0, code);
        var set = Taught();
        Assert.True(set.IsComplete);
        Assert.Equal(3, set.For(Portrait).Count);
        Assert.Equal(3, set.For(Hooked).Count);
        Assert.Equal(3, set.For(Dying).Count);
        Assert.Equal(3, set.For(Dead).Count);
        Assert.All(set.Classes.SelectMany(set.For), p => Assert.Equal(StatusIconTeaching.PictureWidth, p.Width));
        Assert.Contains(Templates, _w.Output, StringComparison.Ordinal);
        Assert.Contains("DBDPresence.Data", _w.Output, StringComparison.Ordinal);
        Assert.Contains("copy", _w.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNKNOWN", _w.Output, StringComparison.Ordinal);
        Assert.Contains("stays off for Discord", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePicturesItLearnedFromOneSize_ReadTheSameIconAtAnotherSize_AndNotAnotherClass()
    {
        Labelled(First, "tuning", s_twoOfEach, width: 1280);
        Labelled(Second, "tuning", s_oneOfEach, width: 1280, brightness: 25);
        Assert.Equal(0, _w.Run("teach-status"));
        var set = Taught();
        var cell = HudProfile.Catalogue.Region(HudRegionIds.HudIcon(0))!.Rect;

        foreach (var name in s_oneOfEach)
        {
            var live = Frame(name, 1920, brightness: -10).Crop(cell);
            var scores = set.Score(live);
            Assert.Equal(name, scores.OrderByDescending(s => s.Value).First().Key);
            Assert.True(scores[name] >= HudProfile.Catalogue.Thresholds.StatusMinScore, $"{name} scored {scores[name]}");
        }
    }

    [Fact]
    public void ItUsesTuningRecordingsOnly_ARecordingFiledAsHeldOutIsNeverRead_AndNeverChanged()
    {
        Labelled(First, "tuning", s_twoOfEach);
        Labelled(Second, "tuning", s_oneOfEach);
        var held = Labelled(Third, "heldout", s_twoOfEach);
        var labels = File.ReadAllBytes(Path.Combine(held, SessionFiles.Labels));

        Assert.Equal(0, _w.Run("teach-status"));

        // Three tuning ranges per class were taught; the two held-out ranges per class did not add to them.
        Assert.Equal(3, Taught().For(Hooked).Count);
        Assert.Contains("1 held-out recordings are not used", _w.Output, StringComparison.Ordinal);
        Assert.Equal(labels, File.ReadAllBytes(Path.Combine(held, SessionFiles.Labels)));
    }

    [Fact]
    public void ARecordingFiledAsHeldOut_CannotMakeUpForMissingTuningIcons()
    {
        Labelled(First, "tuning", s_twoOfEach);
        Labelled(Second, "heldout", s_twoOfEach);

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("at least 2 recordings", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenAClassHasTooFewRanges_NothingIsWritten_AndTheOwnerIsToldPlainlyWhatIsMissing()
    {
        Labelled(First, "tuning", [Portrait, Hooked, Dying, Dead, Portrait, Hooked, Dying, Dead]);
        Labelled(Second, "tuning", [Portrait, Hooked, Dying, Portrait, Hooked, Dying]);

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("Dead: 2 labelled ranges from 1 recordings (needs at least 3 ranges from at least 2 recordings)", _w.Output, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Contains("stays off", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void WithOnlyOneRecording_NothingIsWritten_EvenWhenEveryClassHasManyRanges()
    {
        Labelled(First, "tuning", [.. s_twoOfEach, .. s_twoOfEach]);

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("from 1 recordings (needs at least 3 ranges from at least 2 recordings)", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARangeWhoseIconChangedInsideIt_IsLeftOut_AndSaid()
    {
        Labelled(First, "tuning", s_twoOfEach);
        Labelled(Second, "tuning", [Portrait, Hooked, Dying, $"{Dying}|{Dead}"], answered: s_oneOfEach);

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("1 ranges were left out because their pictures differ from each other", _w.Output, StringComparison.Ordinal);
        Assert.Contains("Dead: 2 labelled ranges from 1 recordings", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARangeTheOwnerMarkedAsChanging_IsNeverUsed()
    {
        Labelled(First, "tuning", s_twoOfEach);
        Labelled(Second, "tuning", s_oneOfEach, answered: [Portrait, Hooked, Dying, "?"]);

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("Dead: 2 labelled ranges from 1 recordings", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void IfTwoClassesLookAlike_NothingIsWritten_BecauseTheAppCouldNotTellThemApart()
    {
        // The owner labelled the "hooked" ranges, but the cell shows the same picture as the normal one: a wrong answer, or the cell is not where the tool looks.
        Labelled(First, "tuning", [Portrait, Portrait, Dying, Dead, Portrait, Portrait, Dying, Dead], answered: s_twoOfEach);
        Labelled(Second, "tuning", [Portrait, Portrait, Dying, Dead], answered: s_oneOfEach);

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("look almost the same", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordingWithoutIconAnswers_IsIgnored_AndWithNoneAtAll_ItSaysWhatToDo()
    {
        Labelled(First, "tuning", s_twoOfEach, answered: [.. s_twoOfEach.Select(_ => string.Empty)]);

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("your own icon", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Contains("practice recordings", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordingWhoseSecondLookDisagreed_IsSkipped_AndCannotMakeUpForMissingIcons()
    {
        Labelled(First, "tuning", s_twoOfEach);
        var disputed = Labelled(Second, "tuning", s_oneOfEach);
        var labels = Path.Combine(disputed, SessionFiles.Labels);
        var text = File.ReadAllText(labels);
        Assert.Contains("\"reviewStatus\":\"labelled\"", text, StringComparison.Ordinal);
        File.WriteAllText(labels, text.Replace("\"reviewStatus\":\"labelled\"", "\"reviewStatus\":\"disputed\"", StringComparison.Ordinal));

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains($"Recording {Second} is skipped: its second look disagreed with some answers", _w.Output, StringComparison.Ordinal);
        Assert.Contains("from 1 recordings (needs at least 3 ranges from at least 2 recordings)", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerLabelsNotYetSpotChecked_AreAcceptedByTheImport_ButNotUsedToTeach()
    {
        Labelled(First, "tuning", s_twoOfEach);
        var folder = _w.AddRecording(Second, [.. s_oneOfEach.Select(c => ("InTrial", Frame(c, 1280, 0), 4))]);
        EvalWorkspace.WritePlan(folder, 4);
        Assert.Equal(0, _w.Run("prepare", "--split", "tuning"));
        var labels = Path.Combine(_w.Root, "reviewer.json");
        File.WriteAllText(labels, OwnAnswers(Second, s_oneOfEach));
        Assert.Equal(0, _w.Run("import-labels", Second, labels));

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Contains("reviewer's labels count only after your quick spot-check", _w.Output, StringComparison.Ordinal);
        Assert.Contains(File.ReadAllLines(Path.Combine(folder, SessionFiles.Labels)), l => l.Contains("\"detector\":\"own-status\"", StringComparison.Ordinal) && l.Contains("\"source\":\"reviewer\"", StringComparison.Ordinal));
    }

    [Fact]
    public void WritingAgain_ReplacesTheFile_WithTheSameBytes_AndSaysSo()
    {
        Labelled(First, "tuning", s_twoOfEach);
        Labelled(Second, "tuning", s_oneOfEach);
        Assert.Equal(0, _w.Run("teach-status"));
        var first = File.ReadAllText(Templates);

        Assert.Equal(0, _w.Run("teach-status"));

        Assert.Equal(first, File.ReadAllText(Templates));
        Assert.Contains("replace the earlier ones", _w.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_w.AppData, "*.tmp"));
    }

    [Fact]
    public void AtMostEightPicturesAreKeptPerClass_TakingTurnsBetweenRecordings()
    {
        Labelled(First, "tuning", [.. Enumerable.Repeat(s_oneOfEach, 12).SelectMany(x => x)], brightness: 0);
        Labelled(Second, "tuning", s_oneOfEach, brightness: 20);

        Assert.Equal(0, _w.Run("teach-status"));

        var set = Taught();
        Assert.All(s_oneOfEach, name => Assert.Equal(OwnStatusTemplates.MaxPerClass, set.For(name).Count));
    }

    [Fact]
    public void TheOutOption_WritesThereInstead_AndWithoutAnyPlaceTheToolSaysSo()
    {
        Labelled(First, "tuning", s_twoOfEach);
        Labelled(Second, "tuning", s_oneOfEach);
        var elsewhere = Path.Combine(_w.Root, "somewhere", "status-templates.json");

        Assert.Equal(0, _w.Run("teach-status", "--out", elsewhere));

        Assert.True(File.Exists(elsewhere));
        Assert.False(File.Exists(Templates));
        var bare = new EvalEnvironment
        {
            Out = new StringWriter(),
            Error = new StringWriter(),
            Time = _w.Time,
            StudyRoot = _w.Root,
            CreateRecognizer = _ => null,
            ReadLine = () => null,
        };
        Assert.Equal(1, StatusIconTeaching.Run(_w.Root, null, bare));
        Assert.Contains("--out", bare.Error.ToString(), StringComparison.Ordinal);
    }

    // ---- Freeing up the practice recordings' pictures (DETECTION_VALIDATION section 21) ---------------------------------------

    /// <summary>The blind second look of a recording made by <see cref="Labelled"/> (every range is a trial screen), answered the same way.</summary>
    private void Recheck(string folder, int ranges)
    {
        var id = Path.GetFileName(folder);
        _w.Time.Advance(TimeSpan.FromHours(25));
        _ = ReviewSessions.BuildRecheckPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder());
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckPlan)));
        var picked = plan.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var file = Path.Combine(_w.Root, "recheck-" + id + ".json");
        File.WriteAllText(file, EvalWorkspace.Answers(id, [.. Enumerable.Repeat("InTrial", ranges)], killer: string.Empty, kind: "recheck", only: picked));
        Assert.Equal(0, _w.Run("apply", "--session", id, "--answers", file, "--recheck"));
    }

    private static int PictureCount(string folder) => Directory.GetFiles(folder, "frame-*.png").Length;

    private (string Manifest, string Hash) HeldOutReport(string out_)
    {
        var held = _w.AddRecording(Third, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(held, 4);
        _w.LabelAndRecheck(held, ["MainMenu", "Lobby", "InMatchMenu", "Results"], "heldout");
        var manifest = Path.Combine(_w.Root, "manifest.txt");
        Assert.Equal(0, _w.Run("manifest", "--out", manifest));
        var hash = File.ReadAllText(manifest).Split('\n').Single(l => l.StartsWith("manifest-sha256:", StringComparison.Ordinal))["manifest-sha256:".Length..].Trim();
        _w.Recognizer = _ => new BitmapFontOcr();
        Assert.Equal(0, _w.Run("evaluate", "--out", out_, "--owner-name", HudScenes.Owner, "--manifest", manifest, "--manifest-hash", hash));
        Assert.Empty(Directory.GetFiles(held, "frame-*.png"));
        return (manifest, hash);
    }

    [Fact]
    public void AfterAHeldOutReport_ASuccessfulTeaching_FreesThePracticePictures_AndTheirLabelsStay()
    {
        var one = Labelled(First, "tuning", s_twoOfEach);
        var two = Labelled(Second, "tuning", s_oneOfEach, width: 1920, brightness: 30);
        Recheck(one, s_twoOfEach.Length);
        Recheck(two, s_oneOfEach.Length);
        var pictures = PictureCount(one) + PictureCount(two);
        Assert.True(pictures > 0);

        HeldOutReport(Path.Combine(_w.Root, "report.md"));
        Assert.Equal(pictures, PictureCount(one) + PictureCount(two)); // the icons are not taught yet: the practice pictures calibrate the reader
        Assert.Contains("until the status icons are taught", _w.Output, StringComparison.Ordinal);

        Assert.Equal(0, _w.Run("teach-status"));

        Assert.True(File.Exists(Templates));
        Assert.Equal(0, PictureCount(one) + PictureCount(two));
        Assert.Contains("practice recordings", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(one, SessionFiles.Labels)));
        Assert.True(File.Exists(Path.Combine(two, SessionFiles.Answers)));

        // The learned set is complete and was read before the pictures went; teaching again now says the pictures are gone, and writes nothing new.
        var before = File.ReadAllText(Templates);
        Assert.Equal(1, _w.Run("teach-status"));
        Assert.Equal(before, File.ReadAllText(Templates));
        Assert.Contains("pictures were deleted to free up space", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ATeachingThatWritesNothing_NeverFreesAnyPicture_EvenAfterAHeldOutReport()
    {
        var one = Labelled(First, "tuning", s_twoOfEach); // only one recording: the teaching needs two, so it writes nothing
        Recheck(one, s_twoOfEach.Length);
        HeldOutReport(Path.Combine(_w.Root, "report.md"));
        var pictures = PictureCount(one);
        var printed = _w.Output.Length;

        Assert.Equal(1, _w.Run("teach-status"));

        Assert.False(File.Exists(Templates));
        Assert.Equal(pictures, PictureCount(one));
        Assert.DoesNotContain("Freed", _w.Output[printed..], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_w.Root, StudyState.FileName)) && File.ReadAllText(Path.Combine(_w.Root, StudyState.FileName)).Contains("statusTaughtUtc", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGuidedMenu_OffersItAsOption3_InPlainWords_AndRunsIt()
    {
        Labelled(First, "tuning", s_twoOfEach);
        Labelled(Second, "tuning", s_oneOfEach);
        _w.QueueEnter("3");

        var code = GuidedMenu.Run(_w.Environment());

        Assert.Equal(0, code);
        Assert.Contains("3. Teach the app your status icons", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Templates));
    }

    [Fact]
    public void TheGuidedMenu_WithNothingLabelled_SaysSoInsteadOfFailingSilently()
    {
        _w.QueueEnter("3");

        var code = GuidedMenu.Run(_w.Environment());

        Assert.Equal(1, code);
        Assert.False(File.Exists(Templates));
        Assert.NotEmpty(_w.ErrorOutput);
    }

    [Fact]
    public void TheHelp_ListsTheCommand()
    {
        Assert.Equal(0, _w.Run("help"));
        Assert.Contains("teach-status", _w.Output, StringComparison.Ordinal);
    }

    // ---- The own-icon answer ------------------------------------------------------------------------------------------

    private static ReviewPlan Plan(int segments) =>
        new("s", [.. Enumerable.Range(0, segments).Select(i => new PlanSegment(i, (i * 4) + 1, (i + 1) * 4, "19:30:12", 8.0))]);

    private static string AnswerJson(string scene, string? own, string role = "Survivor", bool ownAsNumber = false, string kind = "labels") => JsonText.Compact(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteString("kind", kind);
        w.WriteString("sessionId", "s");
        w.WriteStartArray("segments");
        w.WriteStartObject();
        w.WriteNumber("i", 0);
        w.WriteString("scene", scene);
        w.WriteBoolean("matchStart", kind == "labels");
        if (ownAsNumber)
        {
            w.WriteNumber("ownStatus", 1);
        }
        else if (own is not null)
        {
            w.WriteString("ownStatus", own);
        }

        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteStartArray("matches");
        if (kind == "labels")
        {
            w.WriteStartObject();
            w.WriteNumber("startIndex", 0);
            w.WriteString("role", role);
            w.WriteString("character", "dwight-fairfield");
            w.WriteString("map", "ormond/mount-ormond-resort");
            w.WriteString("result", "Escaped");
            w.WriteString("killer", string.Empty);
            w.WriteString("queue", string.Empty);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static ReviewAnswers Parse(string json, string kind = "labels") =>
        ReviewAnswers.Parse(json, HudVocabulary.Create(Vocabulary.LoadEmbedded()), Plan(1), kind);

    [Theory]
    [InlineData("Portrait")]
    [InlineData("Hooked")]
    [InlineData("Dying")]
    [InlineData("Dead")]
    [InlineData("?")]
    public void AnOwnIconAnswer_IsAcceptedForATrialRangeOfASurvivorMatch_AndRoundTrips(string own)
    {
        var answers = Parse(AnswerJson("InTrial", own));

        Assert.Equal(own, answers.Segments[0].OwnStatus);
        Assert.Equal(own, Parse(answers.ToJson()).Segments[0].OwnStatus);
    }

    [Fact]
    public void WithoutAnOwnIconAnswer_NothingChanges_NoLabelIsWritten_AndOldAnswerFilesStillParse()
    {
        var answers = Parse(AnswerJson("InTrial", null));
        var lines = LabelFactory.Build(Plan(1), answers, DateTimeOffset.UnixEpoch, "labelled");

        Assert.Equal(string.Empty, answers.Segments[0].OwnStatus);
        Assert.DoesNotContain("ownStatus", answers.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("own-status", StringComparison.Ordinal));
        Assert.Equal(string.Empty, Parse(AnswerJson("InTrial", string.Empty)).Segments[0].OwnStatus);
    }

    [Theory]
    [InlineData("InTrial", "Burning", "Survivor", false)]
    [InlineData("InTrial", "hooked", "Survivor", false)]
    [InlineData("InTrial", null, "Survivor", true)]
    [InlineData("Lobby", "Hooked", "Survivor", false)]
    [InlineData("InMatchMenu", "Portrait", "Survivor", false)]
    [InlineData("InTrial", "Hooked", "Killer", false)]
    [InlineData("InTrial", "Hooked", "?", false)]
    public void AnOwnIconAnswer_ThatIsInvalid_OrOutsideATrial_OrOfAKillerMatch_IsRefused(string scene, string? own, string role, bool number)
    {
        Assert.Throws<FormatException>(() => Parse(AnswerJson(scene, own, role, number)));
    }

    [Fact]
    public void AnOwnIconAnswer_IsNotPartOfTheSecondLook()
    {
        Assert.Throws<FormatException>(() => Parse(AnswerJson("InTrial", "Hooked", kind: "recheck"), "recheck"));
        Assert.Empty(Parse(AnswerJson("InTrial", null, kind: "recheck"), "recheck").Matches);
    }

    [Fact]
    public void TheLabelLines_CarryTheIconPerRange_AsOwnStatusSegments_AndAQuestionMarkIsWrittenAsNotLegible()
    {
        var plan = Plan(2);
        var json = JsonText.Compact(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteString("kind", "labels");
            w.WriteString("sessionId", "s");
            w.WriteStartArray("segments");
            foreach (var (i, own) in new[] { (0, "Hooked"), (1, "?") })
            {
                w.WriteStartObject();
                w.WriteNumber("i", i);
                w.WriteString("scene", "InTrial");
                w.WriteBoolean("matchStart", i == 0);
                w.WriteString("ownStatus", own);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("matches");
            w.WriteStartObject();
            w.WriteNumber("startIndex", 0);
            w.WriteString("role", "Survivor");
            w.WriteString("character", "dwight-fairfield");
            w.WriteString("map", "ormond/mount-ormond-resort");
            w.WriteString("result", "Escaped");
            w.WriteString("killer", string.Empty);
            w.WriteString("queue", string.Empty);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });
        var answers = ReviewAnswers.Parse(json, HudVocabulary.Create(Vocabulary.LoadEmbedded()), plan, "labels");

        var lines = LabelFactory.Build(plan, answers, DateTimeOffset.UnixEpoch, "labelled").Select(l => JsonDocument.Parse(l).RootElement).Where(e => e.GetProperty("detector").GetString() == "own-status").ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hooked", lines[0].GetProperty("label").GetString());
        Assert.True(lines[0].GetProperty("legible").GetBoolean());
        Assert.Equal("000001", lines[0].GetProperty("fromFrame").GetString());
        Assert.Equal("000004", lines[0].GetProperty("toFrame").GetString());
        Assert.Equal("segment", lines[0].GetProperty("type").GetString());
        Assert.Equal(LabelFactory.UnclearLabel, lines[1].GetProperty("label").GetString());
        Assert.False(lines[1].GetProperty("legible").GetBoolean());
    }

    [Fact]
    public void TheAnswerWordsOfThePage_AreTheStatusClassesOfTheDetector_Plus_AQuestionMark()
    {
        Assert.Equal(s_oneOfEach.Order(StringComparer.Ordinal), ReviewVocabulary.OwnStatusAnswers.Select(a => a.Id).Where(id => id != "?").Order(StringComparer.Ordinal));
        Assert.All(ReviewVocabulary.OwnStatusAnswers.Where(a => a.Id != "?"), a => Assert.True(IsValid(a.Id)));
    }

    [Fact]
    public void ThePage_ShowsTheIconCloseUpsAndTheQuestion_AndKeepsTheOwnersAnswerWhenOpenedAgain()
    {
        var folder = Labelled(First, "tuning", s_oneOfEach);

        var page = ReviewSessions.BuildLabelPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder(), _ => { });

        var text = File.ReadAllText(page);
        Assert.Contains("\"ownStatuses\"", text, StringComparison.Ordinal);
        Assert.Contains("\"icons\":[\"data:image/png;base64,", text, StringComparison.Ordinal);
        Assert.Contains("your own icon", text, StringComparison.Ordinal);
        Assert.Contains("\"ownStatus\":\"Hooked\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIconCloseUp_IsTheCellTheAppReads_CutFromTheGamesOwnPicture()
    {
        var region = ExportCrops.Regions().Single(r => r.Id == ExportCrops.OwnIconId);
        var cell = HudProfile.Catalogue.Region(HudRegionIds.HudIcon(0))!.Rect;

        Assert.True(region.Rect.X <= cell.X && region.Rect.Y <= cell.Y);
        Assert.True(region.Rect.X + region.Rect.Width >= cell.X + cell.Width);
        Assert.True(region.Rect.Y + region.Rect.Height >= cell.Y + cell.Height);
    }
}
