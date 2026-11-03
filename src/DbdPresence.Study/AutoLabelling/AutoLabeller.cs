using System.Globalization;
using System.Text.Json;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Study;

/// <summary>What labelling one recording produced.</summary>
/// <param name="Answers">The answers (source reviewer), or null when the recording could not be labelled.</param>
/// <param name="Requests">How many requests were sent (a retry counts).</param>
/// <param name="InputTokens">Tokens read over all requests.</param>
/// <param name="OutputTokens">Tokens written over all requests.</param>
/// <param name="Failure">A plain sentence that says why there are no answers, or null.</param>
/// <param name="SkippedImages">How many pictures could not be put into a request.</param>
/// <param name="Problem">The relay outcome that ended the run (not set up, not allowed, busy, cap, refused, failed), or null when the failure was not the relay's.</param>
internal sealed record AutoLabelResult(ReviewAnswers? Answers, int Requests, long InputTokens, long OutputTokens, string? Failure, int SkippedImages = 0, LabellerOutcome? Problem = null);

/// <summary>
/// Labels a recording from its review file by sending its pictures to the labelling relay (PLAN_AUTO_LABELLING section 4). The labeller sees only the review
/// file and the closed lists. Every answer is checked strictly; an answer that does not validate is asked for once more and then given up on: nothing is
/// repaired, filled in or guessed. The answers are reviewer labels and count only after the owner's blind spot-check (DETECTION_VALIDATION section 16.2).
/// </summary>
internal static class AutoLabeller
{
    /// <summary>The longest wait honoured after a busy answer.</summary>
    public static readonly TimeSpan MaxBusyWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most match starts one recording may hold, whatever its length. A sanity bound against a runaway or manipulated answer (every match start costs one more
    /// request), not a measurement of how many matches people play.
    /// </summary>
    public const int MaxMatchStartsPerRecording = 40;

    /// <summary>
    /// On average at most one match start per this many seconds of recording (four minutes). A sanity bound like <see cref="MaxMatchStartsPerRecording"/>, not a
    /// measurement: a scenes answer above it is treated as a failed reply.
    /// </summary>
    public const double SecondsPerMatchStart = 240;

    private static readonly TimeSpan s_defaultBusyWait = TimeSpan.FromSeconds(10);

    /// <summary>Gets how many match starts a recording of the given length may hold in a scenes answer (at least one).</summary>
    /// <param name="recordingSeconds">The length covered by the recording's picture ranges.</param>
    /// <returns>The bound.</returns>
    public static int AllowedMatchStarts(double recordingSeconds) =>
        Math.Min(MaxMatchStartsPerRecording, Math.Max(1, (int)Math.Ceiling(Math.Max(0, recordingSeconds) / SecondsPerMatchStart)));

    /// <summary>Labels one recording.</summary>
    /// <param name="folder">The recording's folder; it holds the review file.</param>
    /// <param name="vocabulary">The closed lists.</param>
    /// <param name="transport">Sends a request body.</param>
    /// <param name="progress">Told in plain words how far it is, or null.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="time">The clock used for the busy wait; the system clock when null.</param>
    /// <param name="maxRequests">The most requests this call may send; it stops with <see cref="LabellerOutcome.RunLimitReached"/> when they are used up.</param>
    /// <param name="log">Told the technical detail of a problem (type names and the code's own sentences, never file contents), or null.</param>
    /// <returns>The result; <see cref="AutoLabelResult.Failure"/> is set when there are no answers.</returns>
    public static async Task<AutoLabelResult> LabelAsync(string folder, HudVocabulary vocabulary, ILabellerTransport transport, IProgress<string>? progress, CancellationToken cancellationToken, TimeProvider? time = null, int maxRequests = int.MaxValue, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(transport);
        var run = new Run(transport, progress, time ?? TimeProvider.System, maxRequests, log, cancellationToken);
        try
        {
            using var reader = ReviewFileReader.Open(folder);
            var builder = new LabelRequestBuilder(reader, vocabulary);
            var answers = await run.LabelAsync(reader, builder, vocabulary).ConfigureAwait(false);
            return new AutoLabelResult(answers, run.Requests, run.InputTokens, run.OutputTokens, run.Failure, builder.SkippedImages, run.Problem);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log?.Invoke(StudyText.Line($"The review file of a recording could not be used ({ex.GetType().Name}{(ex is FormatException or InvalidDataException ? ": " + ex.Message : string.Empty)})."));
            // The review file's own sentences (written by this code, no file contents) are shown; a system error (a path, a code) never is.
            var why = ex is FormatException ? ex.Message + " " : "The review file could not be read. ";
            return new AutoLabelResult(null, run.Requests, run.InputTokens, run.OutputTokens, why + "Nothing was written.");
        }
    }

    private sealed class Run(ILabellerTransport transport, IProgress<string>? progress, TimeProvider time, int maxRequests, Action<string>? log, CancellationToken cancellationToken)
    {
        public int Requests { get; private set; }

        public long InputTokens { get; private set; }

        public long OutputTokens { get; private set; }

        public string? Failure { get; private set; }

        public LabellerOutcome? Problem { get; private set; }

        public async Task<ReviewAnswers?> LabelAsync(ReviewFileReader reader, LabelRequestBuilder builder, HudVocabulary vocabulary)
        {
            var chunks = builder.PlanScenes();
            var segments = new List<SegmentAnswer>();
            var allowedStarts = AllowedMatchStarts(reader.Manifest.Segments.Sum(s => s.Seconds));
            SegmentAnswer? previous = null;
            for (var n = 0; n < chunks.Count; n++)
            {
                progress?.Report(string.Create(CultureInfo.InvariantCulture, $"Labelling the screens, part {n + 1} of {chunks.Count}."));
                var chunk = chunks[n];
                var request = builder.BuildScenes(chunk, previous);
                var expected = chunk.Segments.Select(s => s.Segment.Index).ToList();
                var startsBefore = segments.Count(s => s.MatchStart);
                var parsed = await SendAsync<List<SegmentAnswer>>(request.Body, text => CheckStarts(ParseScenes(text, expected), startsBefore, allowedStarts)).ConfigureAwait(false);
                if (parsed is null)
                {
                    return null;
                }

                segments.AddRange(parsed);
                previous = parsed[^1];
            }

            var matches = new List<MatchAnswer>();
            var spans = LabelRequestBuilder.PlanMatches(segments);
            for (var n = 0; n < spans.Count; n++)
            {
                progress?.Report(string.Create(CultureInfo.InvariantCulture, $"Labelling the facts of match {n + 1} of {spans.Count}."));
                var request = builder.BuildFacts(spans[n], segments);
                var span = spans[n];
                var parsed = await SendAsync<MatchAnswer>(request.Body, text => ParseFacts(text, span.StartIndex, vocabulary)).ConfigureAwait(false);
                if (parsed is null)
                {
                    return null;
                }

                matches.Add(parsed);
            }

            var answers = new ReviewAnswers(ReviewAnswers.LabelsKind, reader.Manifest.SessionId, segments, matches, null, LabelSources.Reviewer);
            try
            {
                var plan = new ReviewPlan(answers.SessionId, [.. reader.Manifest.Segments.Select(s => new PlanSegment(s.Index, s.FromFrame, s.ToFrame, s.StartLocal, s.Seconds))]);
                _ = ReviewAnswers.Parse(answers.ToJson(), vocabulary, plan, ReviewAnswers.LabelsKind);
            }
            catch (FormatException ex)
            {
                log?.Invoke(StudyText.Line("The labeller's answers do not fit together: " + ex.Message));
                Failure = "The labeller's answers do not fit together. Nothing was written.";
                return null;
            }

            return answers;
        }

        /// <summary>Sends a request; one more try when the labeller is busy or the answer does not validate. Returns null (with <see cref="Failure"/> set) when it fails.</summary>
        private async Task<T?> SendAsync<T>(byte[] body, Func<string, T> parse)
            where T : class
        {
            string? problem = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Requests >= maxRequests)
                {
                    Failure = string.Create(CultureInfo.InvariantCulture, $"This run reached its limit of {maxRequests} requests and stopped. Nothing was written; the next run continues.");
                    Problem = LabellerOutcome.RunLimitReached;
                    return null;
                }

                Requests++;
                var reply = await transport.SendAsync(body, cancellationToken).ConfigureAwait(false);
                InputTokens += reply.InputTokens;
                OutputTokens += reply.OutputTokens;
                switch (reply.Outcome)
                {
                    case LabellerOutcome.Ok:
                        problem = Check(reply, parse, out var value);
                        if (problem is null)
                        {
                            return value;
                        }

                        break;
                    case LabellerOutcome.Busy:
                        problem = "The labeller is busy.";
                        if (attempt == 1)
                        {
                            var wait = reply.RetryAfter is { } asked && asked > TimeSpan.Zero ? (asked < MaxBusyWait ? asked : MaxBusyWait) : s_defaultBusyWait;
                            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
                        }

                        break;
                    default:
                        Failure = Plain(reply.Outcome);
                        Problem = reply.Outcome;
                        return null;
                }
            }

            Problem = problem == "The labeller is busy." ? LabellerOutcome.Busy : null;
            Failure = problem == "The labeller is busy."
                ? "The labeller is busy. Try again later; nothing was written."
                : $"The labeller's answer could not be used twice in a row: {problem} Nothing was written.";
            return null;
        }

        /// <summary>Refuses a scenes answer that brings the number of match starts above what the recording's length allows.</summary>
        private static List<SegmentAnswer> CheckStarts(List<SegmentAnswer> answers, int startsBefore, int allowed)
        {
            if (startsBefore + answers.Count(a => a.MatchStart) > allowed)
            {
                throw new FormatException("the answer marks more match starts than a recording of this length can hold.");
            }

            return answers;
        }

        private static string Plain(LabellerOutcome outcome) => outcome switch
        {
            LabellerOutcome.NotSetUp => "Automatic labelling is not set up on the service yet. Nothing was sent or written.",
            LabellerOutcome.NotAllowed => "This account may not use automatic labelling (sign in with the owner's account). Nothing was written.",
            LabellerOutcome.DailyCapReached => "The day's allowance of labelling requests is used up. Try again tomorrow; nothing was written.",
            LabellerOutcome.Refused => "The labeller refused the request. Nothing was written.",
            _ => "The request to the labeller did not complete (check the connection). Nothing was written.",
        };

        private const string UnreadableAnswer = "the answer is not in the required form.";

        private static string? Check<T>(LabellerReply reply, Func<string, T> parse, out T? value)
            where T : class
        {
            value = null;
            if (!string.Equals(reply.StopReason, "end_turn", StringComparison.Ordinal))
            {
                return string.Create(CultureInfo.InvariantCulture, $"the answer ended with '{reply.StopReason}' instead of a complete answer.");
            }

            try
            {
                value = parse(reply.Text);
                return null;
            }
            catch (FormatException ex)
            {
                return ex.Message;
            }
            catch (JsonException)
            {
                return UnreadableAnswer;
            }
        }
    }

    /// <summary>Reads and checks a scenes answer: exactly the expected ranges, once each, with known scenes and plain true or false match starts.</summary>
    internal static List<SegmentAnswer> ParseScenes(string text, IReadOnlyList<int> expected)
    {
        using var document = JsonText.ReadDocumentFromText(text);
        var root = document.RootElement;
        RequireOnly(root, "segments");
        var answers = new List<SegmentAnswer>();
        var seen = new HashSet<int>();
        foreach (var item in ReviewPlan.Array(root, "segments"))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("A segment answer is not an object.");
            }

            RequireOnly(item, "i", "scene", "matchStart");
            var index = ReviewPlan.IntOf(item, "i");
            var scene = ReviewPlan.StringOf(item, "scene");
            if (!ReviewVocabulary.IsSceneAnswer(scene))
            {
                throw new FormatException("A scene answer is not one of the known screens.");
            }

            if (!item.TryGetProperty("matchStart", out var start) || start.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new FormatException("A matchStart answer is not true or false.");
            }

            if (!expected.Contains(index))
            {
                throw new FormatException($"The answer names segment {index}, which was not asked about.");
            }

            if (!seen.Add(index))
            {
                throw new FormatException($"Segment {index} is answered twice.");
            }

            answers.Add(new SegmentAnswer(index, scene, start.ValueKind == JsonValueKind.True));
        }

        if (seen.Count != expected.Count)
        {
            throw new FormatException("Not every segment asked about is answered.");
        }

        return [.. answers.OrderBy(a => a.Index)];
    }

    /// <summary>Reads and checks a facts answer against the closed lists. A role of "?" is allowed; an empty character or map is not (the import needs one).</summary>
    internal static MatchAnswer ParseFacts(string text, int startIndex, HudVocabulary vocabulary)
    {
        using var document = JsonText.ReadDocumentFromText(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The facts answer is not an object.");
        }

        RequireOnly(root, "role", "character", "map", "result", "killer", "queue");
        var answer = new MatchAnswer(
            startIndex,
            ReviewPlan.StringOf(root, "role"),
            ReviewPlan.StringOf(root, "character"),
            ReviewPlan.StringOf(root, "map"),
            ReviewPlan.StringOf(root, "result"),
            ReviewPlan.StringOf(root, "killer"),
            ReviewPlan.StringOf(root, "queue"));
        ReviewVocabulary.Validate(answer, vocabulary);
        if (answer.Character.Length == 0 || answer.Map.Length == 0)
        {
            throw new FormatException("The character or the map is empty; it needs an id or '?'.");
        }

        return answer;
    }

    private static void RequireOnly(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("An answer is not an object.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new FormatException("The answer holds a member that was not asked for.");
            }
        }
    }
}
