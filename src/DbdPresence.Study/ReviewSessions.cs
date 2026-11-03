using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;

namespace DbdPresence.Study;

/// <summary>The outcome of the blind second look at a sample of a session's labels (DETECTION_VALIDATION §4.2).</summary>
/// <param name="SampledUnits">How many units (scene ranges and matches) were asked again.</param>
/// <param name="SceneAgreements">Scene ranges asked again.</param>
/// <param name="Kappa">Cohen's kappa over the scene answers, or null when it is undefined because every answer was the same class (agreement is then total).</param>
/// <param name="SceneDisagreements">Scene ranges whose two answers differ.</param>
/// <param name="FactDisagreements">Which facts differ, as "role", "character", "map", "result", "killer" or "queue" (one entry per differing fact).</param>
/// <param name="Status">"rechecked" when kappa is at least 0.9 and no role, character, map, result or killer disagrees, otherwise "disputed".</param>
internal sealed record RecheckOutcome(int SampledUnits, int SceneAgreements, double? Kappa, int SceneDisagreements, IReadOnlyList<string> FactDisagreements, string Status);

/// <summary>
/// Makes the review pages, applies the answers, and runs the blind re-check. Pictures are decoded one at a time and never kept; the only picture data
/// that leaves a decode is a small thumbnail embedded in the page, which lives in the session folder and is deleted with it.
/// </summary>
internal static class ReviewSessions
{
    /// <summary>The share of units asked again.</summary>
    public const double RecheckShare = 0.15;

    /// <summary>The fewest units asked again (all of them when a session has fewer).</summary>
    public const int RecheckMinimum = 20;

    /// <summary>The kappa at or above which a session's labels are accepted (§4.2).</summary>
    public const double MinimumKappa = 0.9;

    /// <summary>How long after labelling the blind second look may be done.</summary>
    public static readonly TimeSpan RecheckDelay = TimeSpan.FromHours(24);

    private const int ThumbnailWidth = 160;

    /// <summary>The study tool takes a picture every 2 seconds, so a range of n pictures lasts about n times that.</summary>
    private const double PictureSeconds = 2.0;

    /// <summary>Gets the file name the first review page saves its answers under.</summary>
    public static string AnswerFileName(string sessionId) => $"dbd-presence-answers-{sessionId}.json";

    /// <summary>Gets the file name the blind second look saves its answers under.</summary>
    public static string RecheckFileName(string sessionId) => $"dbd-presence-recheck-{sessionId}.json";

    /// <summary>Makes (or reuses) the plan of a session and writes the first review page. Returns the page's path.</summary>
    public static string BuildLabelPage(string folder, Vocabulary vocabulary, IFrameDecoder decoder, Action<string> progress)
    {
        var session = DatasetLoader.LoadSession(folder);
        var plan = LoadOrMakePlan(folder, session, decoder, progress);

        ReviewAnswers? prefill = null;
        var answersPath = Path.Combine(folder, SessionFiles.Answers);
        if (File.Exists(answersPath))
        {
            prefill = ReviewAnswers.Parse(File.ReadAllText(answersPath), HudVocabulary.Create(vocabulary), plan, ReviewAnswers.LabelsKind);
        }

        // The close-ups of the owner's own icon are cut from the game's own picture (without a window title bar), like every region the app reads.
        var area = ClientAreaSessions.Decide(session, decoder);
        var iconRegion = ExportCrops.Regions().Single(r => r.Id == ExportCrops.OwnIconId);
        var segments = new List<PageSegment>();
        foreach (var segment in plan.Segments)
        {
            var frames = SegmentProposer.ThumbnailFrames(segment.FromFrame - 1, segment.ToFrame - 1);
            var thumbnails = new List<string>();
            var icons = new List<string>();
            foreach (var frame in frames)
            {
                var image = decoder.Decode(Path.Combine(session.Directory, session.Frames[frame].File));
                thumbnails.Add(Thumbnail(image));
                var (cut, _) = ExportCrops.Cut(ClientAreaNormaliser.Apply(image, area), iconRegion);
                var pixels = cut.ToPixelBuffer();
                icons.Add("data:image/png;base64," + Convert.ToBase64String(PngEncoder.Encode(pixels.Width, pixels.Height, pixels.Bgra.Span)));
            }

            segments.Add(new PageSegment(segment, thumbnails, icons));
        }

        progress("Writing the page...");
        var model = new ReviewPageModel(ReviewAnswers.LabelsKind, session.Metadata.SessionId, AnswerFileName(session.Metadata.SessionId), segments, [], prefill);
        var page = Path.Combine(folder, SessionFiles.ReviewPage);
        File.WriteAllText(page, ReviewPage.Build(model, vocabulary), new UTF8Encoding(false));
        return page;
    }

    /// <summary>
    /// Reads the session's plan or, the first time, makes it from the brightness and timing of the pictures and keeps it. Both the review page and the
    /// review file for the reviewer use this one plan, so answers made from either are for the same ranges.
    /// </summary>
    public static ReviewPlan LoadOrMakePlan(string folder, DatasetSession session, IFrameDecoder decoder, Action<string> progress)
    {
        var planPath = Path.Combine(folder, SessionFiles.Plan);
        var plan = File.Exists(planPath) ? ReviewPlan.Parse(File.ReadAllText(planPath)) : MakePlan(session, decoder, progress);
        File.WriteAllText(planPath, plan.ToJson());
        return plan;
    }

    /// <summary>Turns saved first-pass answers into labels. The labels are written, the answers are kept for a later rework, and the 24-hour clock for the re-check starts.</summary>
    public static int ApplyLabels(string folder, string answersJson, Vocabulary vocabulary, DateTimeOffset now) =>
        ApplyLabels(folder, answersJson, vocabulary, now, LabelSources.Owner);

    /// <summary>
    /// Imports labels made elsewhere (a vision model looking at the review file, DETECTION_VALIDATION §16.1). They are marked as the reviewer's and are not
    /// final: the owner's blind spot-check comes next and is due at once (a different person, so there is no memory to wait out). Labels the owner made
    /// are never replaced.
    /// </summary>
    /// <returns>The number of matches in the labels.</returns>
    /// <exception cref="InvalidOperationException">The session has no review file yet, or the owner has labelled it.</exception>
    public static int ImportReviewerLabels(string folder, string answersJson, Vocabulary vocabulary, DateTimeOffset now)
    {
        if (!File.Exists(Path.Combine(folder, SessionFiles.Session)) || !File.Exists(Path.Combine(folder, SessionFiles.Plan)))
        {
            throw new InvalidOperationException("That session has no review file yet. Make the review file first (DbdPresence.Eval export).");
        }

        if (AnswersSource(folder) == LabelSources.Owner)
        {
            throw new InvalidOperationException("The owner has already labelled this session, so labels from elsewhere are not imported over them.");
        }

        return ApplyLabels(folder, answersJson, vocabulary, now, LabelSources.Reviewer);
    }

    /// <summary>Gets who made the session's saved answers (<see cref="LabelSources"/>), or null when there are none yet.</summary>
    public static string? AnswersSource(string folder)
    {
        var path = Path.Combine(folder, SessionFiles.Answers);
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonText.ReadDocument(path);
        return document.RootElement.TryGetProperty("source", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.String && source.GetString() == LabelSources.Reviewer
            ? LabelSources.Reviewer
            : LabelSources.Owner;
    }

    private static int ApplyLabels(string folder, string answersJson, Vocabulary vocabulary, DateTimeOffset now, string source)
    {
        var plan = ReviewPlan.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Plan)));
        var parsed = ReviewAnswers.Parse(answersJson, HudVocabulary.Create(vocabulary), plan, ReviewAnswers.LabelsKind);

        // The source is set here, never taken from the file: a file cannot call itself the owner's.
        var answers = parsed with { AppliedAtUtc = JsonText.Time(now), Source = source };
        var lines = LabelFactory.Build(plan, answers, now, "labelled");
        JsonText.WriteLines(Path.Combine(folder, SessionFiles.Labels), lines);
        File.WriteAllText(Path.Combine(folder, SessionFiles.Answers), answers.ToJson());

        // New labels need a new second look; an old verdict, plan or page no longer applies.
        foreach (var stale in new[] { SessionFiles.RecheckResult, SessionFiles.RecheckPlan, SessionFiles.RecheckPage })
        {
            File.Delete(Path.Combine(folder, stale));
        }

        return answers.Segments.Count(s => s.MatchStart);
    }

    /// <summary>Gets when the blind second look becomes due (24 hours after the owner made the labels, at once for a reviewer's labels), or null when the session is not labelled or already checked.</summary>
    public static DateTimeOffset? RecheckDueAt(string folder)
    {
        if (File.Exists(Path.Combine(folder, SessionFiles.RecheckResult)))
        {
            return null;
        }

        var answers = Path.Combine(folder, SessionFiles.Answers);
        if (!File.Exists(answers))
        {
            return null;
        }

        using var document = JsonText.ReadDocument(answers);
        var reviewer = document.RootElement.TryGetProperty("source", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.String && source.GetString() == LabelSources.Reviewer;
        return document.RootElement.TryGetProperty("appliedAtUtc", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.String
            && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var applied)
            ? applied + (reviewer ? TimeSpan.Zero : RecheckDelay)
            : null;
    }

    /// <summary>Picks the units asked again, writes the plan of the second look and its page. The pick depends only on the session and the time of the labels, never on the answers.</summary>
    public static string BuildRecheckPage(string folder, Vocabulary vocabulary, IFrameDecoder decoder)
    {
        var model = BuildRecheckModel(folder, vocabulary, decoder);
        var page = Path.Combine(folder, SessionFiles.RecheckPage);
        File.WriteAllText(page, ReviewPage.Build(model, vocabulary), new UTF8Encoding(false));
        return page;
    }

    /// <summary>Picks the units asked again, writes the plan of the second look and returns what its page shows (the web page and the Study page of the app both use this).</summary>
    public static ReviewPageModel BuildRecheckModel(string folder, Vocabulary vocabulary, IFrameDecoder decoder)
    {
        var session = DatasetLoader.LoadSession(folder);
        var plan = ReviewPlan.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Plan)));
        var lexicon = HudVocabulary.Create(vocabulary);
        var original = ReviewAnswers.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Answers)), lexicon, plan, ReviewAnswers.LabelsKind);
        var (segmentPick, matchPick) = Sample(session.Metadata.SessionId, original);
        File.WriteAllText(
            Path.Combine(folder, SessionFiles.RecheckPlan),
            JsonText.Compact(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", 1);
                w.WriteStartArray("segments");
                foreach (var i in segmentPick)
                {
                    w.WriteNumberValue(i);
                }

                w.WriteEndArray();
                w.WriteStartArray("matches");
                foreach (var i in matchPick)
                {
                    w.WriteNumberValue(i);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }));

        var segments = segmentPick.Select(i => plan.Segments[i])
            .Select(s => new PageSegment(s, [.. SegmentProposer.ThumbnailFrames(s.FromFrame - 1, s.ToFrame - 1).Select(f => Thumbnail(session, f, decoder))]))
            .ToList();
        var starts = original.Segments.Where(s => s.MatchStart).Select(s => s.Index).Order().ToList();
        var matches = new List<PageMatch>();
        foreach (var start in matchPick)
        {
            var next = starts.FirstOrDefault(s => s > start, -1);
            var from = plan.Segments[start].FromFrame - 1;
            var to = next < 0 ? plan.Segments[^1].ToFrame - 1 : plan.Segments[next].FromFrame - 2;
            var strip = Enumerable.Range(0, 6).Select(k => from + (int)((long)(to - from) * k / 5)).Distinct().ToList();
            matches.Add(new PageMatch(start, [.. strip.Select(f => Thumbnail(session, f, decoder))]));
        }

        return new ReviewPageModel(ReviewAnswers.RecheckKind, session.Metadata.SessionId, RecheckFileName(session.Metadata.SessionId), segments, matches, null);
    }

    /// <summary>Grades the second look, writes <c>recheck.json</c> and gives every label of the session the verdict as its review status.</summary>
    public static RecheckOutcome ApplyRecheck(string folder, string answersJson, Vocabulary vocabulary, DateTimeOffset now)
    {
        var session = DatasetLoader.LoadSession(folder);
        var plan = ReviewPlan.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Plan)));
        var lexicon = HudVocabulary.Create(vocabulary);
        var original = ReviewAnswers.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Answers)), lexicon, plan, ReviewAnswers.LabelsKind);
        var second = ReviewAnswers.Parse(answersJson, lexicon, plan, ReviewAnswers.RecheckKind);
        var (segmentPick, matchPick) = Sample(session.Metadata.SessionId, original);
        if (!second.Segments.Select(s => s.Index).Order().SequenceEqual(segmentPick.Order()) || !second.Matches.Select(m => m.StartIndex).Order().SequenceEqual(matchPick.Order()))
        {
            throw new FormatException("The second-look answers are not for the units this session asked about.");
        }

        var first = original.Segments.ToDictionary(s => s.Index, s => s.Scene);
        var firstScenes = second.Segments.Select(s => first[s.Index]).ToList();
        var secondScenes = second.Segments.Select(s => s.Scene).ToList();
        var kappa = firstScenes.Count > 0 ? AgreementStatistics.CohensKappa(firstScenes, secondScenes) : null;
        var sceneDisagreements = firstScenes.Zip(secondScenes).Count(p => p.First != p.Second);

        var facts = new List<string>();
        foreach (var again in second.Matches)
        {
            var before = original.Matches.Single(m => m.StartIndex == again.StartIndex);
            void Compare(string name, string a, string b)
            {
                if (a != b)
                {
                    facts.Add(name);
                }
            }

            Compare("role", before.Role, again.Role);
            Compare("character", before.Character, again.Character);
            Compare("map", before.Map, again.Map);
            Compare("killer", before.Killer, again.Killer);
            Compare("queue", before.Queue, again.Queue);
            if (before.Role == "Survivor")
            {
                Compare("result", before.Result, again.Result);
            }
        }

        var seriousFacts = facts.Any(f => f is "role" or "character" or "map" or "result" or "killer");
        var agreed = kappa is null ? sceneDisagreements == 0 : kappa >= MinimumKappa;
        var status = agreed && !seriousFacts ? "rechecked" : "disputed";
        var outcome = new RecheckOutcome(segmentPick.Count + matchPick.Count, segmentPick.Count, kappa, sceneDisagreements, facts, status);

        File.WriteAllText(
            Path.Combine(folder, SessionFiles.RecheckResult),
            JsonText.Compact(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", 1);
                w.WriteString("checkedAtUtc", JsonText.Time(now));
                w.WriteNumber("sampledUnits", outcome.SampledUnits);
                w.WriteNumber("sceneAnswersCompared", outcome.SceneAgreements);
                if (kappa is { } k)
                {
                    w.WriteNumber("kappa", k);
                }
                else
                {
                    w.WriteNull("kappa");
                }

                w.WriteNumber("sceneDisagreements", outcome.SceneDisagreements);
                w.WriteStartArray("factDisagreements");
                foreach (var f in facts)
                {
                    w.WriteStringValue(f);
                }

                w.WriteEndArray();
                w.WriteString("status", status);
                w.WriteEndObject();
            }));

        // The verdict becomes each label's review status, so the evaluator and the manifest can refuse disputed labels.
        var applied = DateTimeOffset.Parse(original.AppliedAtUtc!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        JsonText.WriteLines(Path.Combine(folder, SessionFiles.Labels), LabelFactory.Build(plan, original, applied, status));
        return outcome;
    }

    /// <summary>Chooses the scene ranges and matches asked again: 15% of all units, at least 20, ordered by a hash so the choice cannot be steered.</summary>
    internal static (IReadOnlyList<int> Segments, IReadOnlyList<int> Matches) Sample(string sessionId, ReviewAnswers original)
    {
        var units = new List<(string Kind, int Index)>();
        units.AddRange(original.Segments.Select(s => ("s", s.Index)));
        units.AddRange(original.Matches.Select(m => ("m", m.StartIndex)));
        var count = Math.Min(units.Count, Math.Max(RecheckMinimum, (int)Math.Ceiling(units.Count * RecheckShare)));
        var seed = $"{sessionId}|{original.AppliedAtUtc}";
        var chosen = units
            .OrderBy(u => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{u.Kind}{u.Index}"))), StringComparer.Ordinal)
            .Take(count)
            .ToList();
        return ([.. chosen.Where(u => u.Kind == "s").Select(u => u.Index).Order()], [.. chosen.Where(u => u.Kind == "m").Select(u => u.Index).Order()]);
    }

    private static ReviewPlan MakePlan(DatasetSession session, IFrameDecoder decoder, Action<string> progress)
    {
        var signatures = new List<byte[]>();
        var times = new List<DateTimeOffset>();
        var step = Math.Max(1, session.Frames.Count / 10);
        for (var i = 0; i < session.Frames.Count; i++)
        {
            signatures.Add(SegmentProposer.Signature(decoder.Decode(Path.Combine(session.Directory, session.Frames[i].File))));
            times.Add(session.Frames[i].TimestampUtc);
            if ((i + 1) % step == 0)
            {
                progress($"Looking through the pictures: {(i + 1) * 100 / session.Frames.Count}%");
            }
        }

        var segments = SegmentProposer.Propose(signatures, times)
            .Select((r, index) => new PlanSegment(
                index,
                r.From + 1,
                r.To + 1,
                times[r.From].ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                (times[r.To] - times[r.From]).TotalSeconds + PictureSeconds))
            .ToList();
        return new ReviewPlan(session.Metadata.SessionId, segments);
    }

    private static string Thumbnail(DatasetSession session, int frameIndex, IFrameDecoder decoder) =>
        Thumbnail(decoder.Decode(Path.Combine(session.Directory, session.Frames[frameIndex].File)));

    private static string Thumbnail(ImageBuffer image)
    {
        var height = Math.Max(1, (int)Math.Round((double)ThumbnailWidth * image.Height / image.Width));
        var small = image.Resize(ThumbnailWidth, height).ToPixelBuffer();
        var png = PngEncoder.Encode(small.Width, small.Height, small.Bgra.Span);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
