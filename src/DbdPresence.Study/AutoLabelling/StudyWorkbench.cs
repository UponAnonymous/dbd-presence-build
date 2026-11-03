using System.Text;
using System.Text.Json;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Text;

namespace DbdPresence.Study;

/// <summary>
/// The labelling pipeline as the owner's Study page uses it (PLAN_AUTO_LABELLING section 4, FR-LAB-03 and FR-LAB-04): lists the recordings with their state,
/// runs the automatic run, and does the blind spot-check with the existing grading. Nothing here grades, imports or decides by itself: it calls
/// <see cref="AutoLabelRun"/> and <see cref="ReviewSessions"/>, so the study tool and the page follow the same rules. Pictures stay on this PC.
/// The last automatic run of every recording (token counts, skipped pictures, failure, how many runs in a row failed and when) is kept in one small file of ids and numbers.
/// </summary>
internal sealed class StudyWorkbench : IStudyWorkbench
{
    /// <summary>After this many failed runs in a row a recording is not tried again automatically; "Label new recordings now" still tries it (every try costs tokens).</summary>
    public const int MaxAutomaticTries = 2;

    private const string PngPrefix = "data:image/png;base64,";
    private readonly string _root;
    private readonly Vocabulary _vocabulary;
    private readonly ILabellerTransport _transport;
    private readonly TimeProvider _time;
    private readonly string? _runLogPath;
    private readonly Action<string> _warn;
    private readonly IFrameDecoder _decoder;
    private readonly Lock _logGate = new();

    /// <summary>Initializes a new instance of the <see cref="StudyWorkbench"/> class.</summary>
    /// <param name="root">The study folder.</param>
    /// <param name="vocabulary">The closed lists.</param>
    /// <param name="transport">Sends a request body to the labelling relay.</param>
    /// <param name="time">The clock.</param>
    /// <param name="runLogPath">Where the last automatic run of every recording is kept, or null to keep it in memory only.</param>
    /// <param name="warn">Told about a file that could not be read, in words without file contents.</param>
    /// <param name="decoder">Reads the recording's pictures; the standard one when null.</param>
    public StudyWorkbench(string root, Vocabulary vocabulary, ILabellerTransport transport, TimeProvider time, string? runLogPath, Action<string> warn, IFrameDecoder? decoder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(warn);
        _root = root;
        _vocabulary = vocabulary;
        _transport = transport;
        _time = time;
        _runLogPath = runLogPath;
        _warn = warn;
        _decoder = decoder ?? new ExtensionFrameDecoder();
    }

    /// <summary>Gets a value indicating whether a labelling run makes the recording's pictures smaller first (FR-DET-24). On in the app; tests that are not
    /// about the compaction switch it off (see <see cref="StudyEnvironment.CompactPictures"/>).</summary>
    public bool CompactPictures { get; init; } = true;

    /// <inheritdoc />
    public Task<IReadOnlyList<RecordingStatus>> ListAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<RecordingStatus>>(() => List(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<StudyRunReport> LabelNewAsync(bool manual, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // The review file's privacy lines and the run's own detail go to the log; the page shows the same words (StudyPrivacy) and plain sentences.
        using var sink = new LineSink(_warn);
        var env = new StudyEnvironment
        {
            Out = sink,
            Error = TextWriter.Null,
            Time = _time,
            StudyRoot = _root,
            ReadLine = static () => null,
            PrivacyNotice = StudyPrivacy.Notice,
            CompactPictures = CompactPictures,
        };
        var skip = manual ? null : AutomaticallyGivenUp();
        var summaries = await Task.Run(() => AutoLabelRun.RunAsync(_root, env, _vocabulary, _transport, progress, cancellationToken, skip, AutoLabelRun.MaxRequestsPerRun, _warn), cancellationToken).ConfigureAwait(false);
        Remember(summaries);
        var problem = summaries.Select(s => s.Problem).FirstOrDefault(p => p is not null) switch
        {
            LabellerOutcome.NotSetUp => StudyProblem.NotSetUp,
            LabellerOutcome.NotAllowed => StudyProblem.NotAllowed,
            LabellerOutcome.DailyCapReached => StudyProblem.DailyCap,
            LabellerOutcome.Busy => StudyProblem.Busy,
            LabellerOutcome.RunLimitReached => StudyProblem.RunLimit,
            _ => summaries.Any(s => s.Failure is not null) ? StudyProblem.Other : StudyProblem.None,
        };
        return new StudyRunReport(
            summaries.Count(s => s.Labelled),
            summaries.Count(s => s.Failure is not null),
            summaries.Count(s => s.MadeReviewFile),
            problem,
            summaries.Sum(s => s.Requests),
            summaries.Sum(s => s.InputTokens),
            summaries.Sum(s => s.OutputTokens));
    }

    /// <inheritdoc />
    public Task<SpotCheckSheet?> OpenSpotCheckAsync(string recordingId, CancellationToken cancellationToken) =>
        Task.Run(() => OpenSpotCheck(recordingId), cancellationToken);

    /// <inheritdoc />
    public Task<SpotCheckSaveResult> SaveSpotCheckAsync(string recordingId, SpotCheckAnswers answers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answers);
        return Task.Run(() => SaveSpotCheck(recordingId, answers), cancellationToken);
    }

    /// <summary>The recordings the automatic run leaves alone: they failed <see cref="MaxAutomaticTries"/> runs in a row.</summary>
    private HashSet<string> AutomaticallyGivenUp() =>
        [.. ReadLog().Where(p => p.Value.Failures >= MaxAutomaticTries).Select(p => p.Key)];

    private static string? ReadRecheckStatus(string folder)
    {
        var path = Path.Combine(folder, SessionFiles.RecheckResult);
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonText.ReadDocument(path);
        return document.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null;
    }

    private static byte[] Picture(string dataAddress) =>
        dataAddress.StartsWith(PngPrefix, StringComparison.Ordinal) ? Convert.FromBase64String(dataAddress[PngPrefix.Length..]) : [];

    private List<RecordingStatus> List(CancellationToken cancellationToken)
    {
        var log = ReadLog();
        var list = new List<RecordingStatus>();
        foreach (var folder in StudySessions.RecordingFolders(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(folder);
            log.TryGetValue(id, out var run);
            try
            {
                list.Add(StatusOf(folder, id, run));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or JsonException)
            {
                _warn($"The study recording {id} could not be read ({ex.GetType().Name}).");
                list.Add(new RecordingStatus(id, RecordingStage.Failed, false, "Its files could not be read.", run?.InputTokens, run?.OutputTokens, run?.Skipped ?? 0, null));
            }
        }

        return list;
    }

    private RecordingStatus StatusOf(string folder, string id, RunEntry? run)
    {
        var tokensIn = run is { Requests: > 0 } ? run.InputTokens : (long?)null;
        var tokensOut = run is { Requests: > 0 } ? run.OutputTokens : (long?)null;
        var skipped = run?.Skipped ?? 0;
        if (!File.Exists(Path.Combine(folder, SessionFiles.Answers)))
        {
            if (run?.Failure is { Length: > 0 } failure)
            {
                return new RecordingStatus(id, RecordingStage.Failed, false, failure, tokensIn, tokensOut, skipped, null, run.Failures >= MaxAutomaticTries);
            }

            return new RecordingStatus(id, ReviewExport.ExistingFiles(folder).Count > 0 ? RecordingStage.ReviewFileMade : RecordingStage.New, false, null, tokensIn, tokensOut, skipped, null);
        }

        var automatic = ReviewSessions.AnswersSource(folder) == LabelSources.Reviewer;
        var due = ReviewSessions.RecheckDueAt(folder);
        var verdict = ReadRecheckStatus(folder);
        var stage = verdict switch
        {
            "rechecked" => RecordingStage.SpotCheckDone,
            "disputed" => RecordingStage.Disputed,
            _ => due is { } at && at <= _time.GetUtcNow() ? RecordingStage.SpotCheckReady : RecordingStage.SpotCheckLater,
        };
        return new RecordingStatus(id, stage, automatic, null, tokensIn, tokensOut, skipped, due);
    }

    private SpotCheckSheet? OpenSpotCheck(string recordingId)
    {
        var folder = DueFolder(recordingId);
        if (folder is null)
        {
            return null;
        }

        var model = ReviewSessions.BuildRecheckModel(folder, _vocabulary, _decoder);
        return new SpotCheckSheet(
            recordingId,
            [.. model.Segments.Select(s => new SpotCheckSegment(s.Segment.Index, s.Segment.StartLocal, s.Segment.Seconds, [.. s.Thumbnails.Select(Picture)]))],
            [.. model.Matches.Select(m => new SpotCheckMatch(m.StartIndex, [.. m.Thumbnails.Select(Picture)]))],
            [.. ReviewVocabulary.Scenes.Select(s => new SpotCheckChoice(s.Id, s.Text))],
            [new("Survivor", "Survivor"), new("Killer", "Killer"), new("?", "Can't tell")],
            [new("?", "Not shown / can't tell"), .. Names(_vocabulary.Entries(VocabularyKind.Survivor).Concat(_vocabulary.Entries(VocabularyKind.Killer)))],
            [new("?", "Not shown / can't tell"), .. Names(_vocabulary.Entries(VocabularyKind.Map))],
            [.. ReviewVocabulary.SurvivorResults.Select(r => new SpotCheckChoice(r, r)), new("?", "Can't tell")],
            [new(string.Empty, "Skip"), new("?", "Not shown / can't tell"), .. Names(_vocabulary.Entries(VocabularyKind.Killer))],
            [new(string.Empty, "Skip"), new("?", "Not shown / can't tell"), new(DbdPresence.Core.Detection.QueueModes.Normal, "Normal queue"), .. Names(_vocabulary.Entries(VocabularyKind.EventMode))]);
    }

    private static IEnumerable<SpotCheckChoice> Names(IEnumerable<VocabularyEntry> entries) =>
        entries.Select(e => new SpotCheckChoice(e.Id, string.IsNullOrEmpty(e.Group) ? e.Name : $"{e.Name} ({e.Group})")).OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase);

    private SpotCheckSaveResult SaveSpotCheck(string recordingId, SpotCheckAnswers answers)
    {
        var folder = DueFolder(recordingId);
        if (folder is null)
        {
            return new SpotCheckSaveResult(null, "This recording has no spot-check due.");
        }

        try
        {
            var second = new ReviewAnswers(
                ReviewAnswers.RecheckKind,
                recordingId,
                [.. answers.Segments.Select(s => new SegmentAnswer(s.Index, s.Scene, false))],
                [.. answers.Matches.Select(m => new MatchAnswer(m.StartIndex, m.Role, m.Character, m.Map, m.Result, m.Killer, m.Queue))],
                null);
            var outcome = ReviewSessions.ApplyRecheck(folder, second.ToJson(), _vocabulary, _time.GetUtcNow());
            return new SpotCheckSaveResult(new SpotCheckVerdict(outcome.Status == "rechecked", outcome.SampledUnits, outcome.Kappa, outcome.SceneDisagreements, outcome.FactDisagreements), null);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new SpotCheckSaveResult(null, $"The answers could not be saved: {ex.Message} Nothing was changed.");
        }
    }

    /// <summary>Finds the folder of a recording whose spot-check is due, or null.</summary>
    private string? DueFolder(string recordingId)
    {
        var folder = StudySessions.RecordingFolders(_root).FirstOrDefault(f => string.Equals(Path.GetFileName(f), recordingId, StringComparison.Ordinal));
        if (folder is null)
        {
            return null;
        }

        try
        {
            var due = ReviewSessions.RecheckDueAt(folder);
            return due is { } at && at <= _time.GetUtcNow() ? folder : null;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or JsonException)
        {
            _warn($"The study recording {recordingId} could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    private Dictionary<string, RunEntry> ReadLog()
    {
        var entries = new Dictionary<string, RunEntry>(StringComparer.Ordinal);
        if (_runLogPath is null)
        {
            return entries;
        }

        lock (_logGate)
        {
            if (!File.Exists(_runLogPath))
            {
                return entries;
            }

            try
            {
                using var document = JsonText.ReadDocument(_runLogPath, 4 * 1024 * 1024);
                foreach (var item in document.RootElement.GetProperty("recordings").EnumerateArray())
                {
                    entries[item.GetProperty("id").GetString()!] = new RunEntry(
                        item.GetProperty("requests").GetInt32(),
                        item.GetProperty("inputTokens").GetInt64(),
                        item.GetProperty("outputTokens").GetInt64(),
                        item.GetProperty("skipped").GetInt32(),
                        item.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.String ? StudyText.Plain(failure.GetString()) : null,
                        item.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Number && failures.TryGetInt32(out var count) && count > 0 ? count : 0,
                        item.TryGetProperty("lastFailureUtc", out var at) && at.ValueKind == JsonValueKind.String ? StudyText.Plain(at.GetString()) : null);
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                _warn($"The record of the last automatic run could not be read ({ex.GetType().Name}); it starts again.");
                entries.Clear();
            }
        }

        return entries;
    }

    private void Remember(IReadOnlyList<RecordingRunSummary> summaries)
    {
        if (_runLogPath is null || summaries.Count == 0)
        {
            return;
        }

        lock (_logGate)
        {
            var entries = ReadLog();
            foreach (var s in summaries)
            {
                entries.TryGetValue(s.SessionId, out var earlier);
                var failures = s.Labelled ? 0 : (earlier?.Failures ?? 0) + (s.CountsAsFailure ? 1 : 0);
                var lastFailure = s.Labelled ? null : s.CountsAsFailure ? JsonText.Time(_time.GetUtcNow()) : earlier?.LastFailureUtc;
                entries[s.SessionId] = s.Requests > 0
                    ? new RunEntry(s.Requests, s.InputTokens, s.OutputTokens, s.SkippedImages, s.Failure, failures, lastFailure)
                    : new RunEntry(earlier?.Requests ?? 0, earlier?.InputTokens ?? 0, earlier?.OutputTokens ?? 0, earlier?.Skipped ?? 0, s.Failure, failures, lastFailure);
            }

            var text = JsonText.Compact(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", 1);
                w.WriteStartArray("recordings");
                foreach (var (id, e) in entries.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WriteStartObject();
                    w.WriteString("id", id);
                    w.WriteNumber("requests", e.Requests);
                    w.WriteNumber("inputTokens", e.InputTokens);
                    w.WriteNumber("outputTokens", e.OutputTokens);
                    w.WriteNumber("skipped", e.Skipped);
                    if (e.Failure is not null)
                    {
                        w.WriteString("failure", e.Failure);
                    }

                    if (e.Failures > 0)
                    {
                        w.WriteNumber("failures", e.Failures);
                    }

                    if (e.LastFailureUtc is not null)
                    {
                        w.WriteString("lastFailureUtc", e.LastFailureUtc);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            });
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_runLogPath))!);
                var temporary = _runLogPath + ".tmp";
                File.WriteAllText(temporary, text + "\n", new UTF8Encoding(false));
                File.Move(temporary, _runLogPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn($"The record of the automatic run could not be saved ({ex.GetType().Name}); the token counts are not kept.");
            }
        }
    }

    private sealed record RunEntry(int Requests, long InputTokens, long OutputTokens, int Skipped, string? Failure, int Failures, string? LastFailureUtc);

    /// <summary>A writer that hands the lines the review code prints to the log: the privacy lines are kept, the rest (paths, progress) is dropped.</summary>
    private sealed class LineSink(Action<string> log) : TextWriter
    {
        private readonly System.Text.StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value != '\n')
            {
                _ = _line.Append(value);
                return;
            }

            var text = _line.ToString().TrimEnd('\r');
            _ = _line.Clear();
            if (text.StartsWith("PRIVACY:", StringComparison.Ordinal))
            {
                log(StudyText.Line(text));
            }
        }
    }
}
