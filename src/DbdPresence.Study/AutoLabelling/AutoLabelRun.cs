using System.Globalization;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;

namespace DbdPresence.Study;

/// <summary>What happened to one recording in an automatic run.</summary>
/// <param name="SessionId">The recording (its folder name).</param>
/// <param name="MadeReviewFile">Whether the review file was made in this run.</param>
/// <param name="LabelledSegments">How many ranges were labelled and imported, or 0.</param>
/// <param name="LabelledMatches">How many matches were labelled and imported, or 0.</param>
/// <param name="Requests">How many labelling requests were sent.</param>
/// <param name="InputTokens">Tokens read.</param>
/// <param name="OutputTokens">Tokens written.</param>
/// <param name="Failure">A plain sentence that says why the recording has no labels, or null (cleaned: one line, at most 200 characters, no control characters).</param>
/// <param name="SkippedImages">How many pictures could not be sent.</param>
/// <param name="Problem">The relay outcome that stopped the labelling, or null.</param>
internal sealed record RecordingRunSummary(string SessionId, bool MadeReviewFile, int LabelledSegments, int LabelledMatches, int Requests, long InputTokens, long OutputTokens, string? Failure, int SkippedImages = 0, LabellerOutcome? Problem = null)
{
    /// <summary>Gets the plain sentence for the page and the run record.</summary>
    public string? Failure { get; } = StudyText.Plain(Failure);

    /// <summary>
    /// Gets a value indicating whether this failure counts toward "failed twice, so no more automatic tries": the recording's own labelling or import failed. A stop for
    /// a reason outside the recording (not set up, not allowed, today's limit, busy, no connection, this run's own limit) does not.
    /// </summary>
    public bool CountsAsFailure => Failure is not null && Problem is not (LabellerOutcome.NotSetUp or LabellerOutcome.NotAllowed or LabellerOutcome.DailyCapReached or LabellerOutcome.Busy or LabellerOutcome.Failed or LabellerOutcome.RunLimitReached);

    /// <summary>Gets a value indicating whether the recording was labelled and the labels imported.</summary>
    public bool Labelled => Failure is null && LabelledSegments > 0;

    /// <summary>Gets one plain line for the Study page and the log.</summary>
    public string Describe()
    {
        if (Failure is not null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{SessionId}: failed: {Failure}{(MadeReviewFile ? " (the review file was made)" : string.Empty)}{TokenText()}");
        }

        return Labelled
            ? string.Create(CultureInfo.InvariantCulture, $"{SessionId}: {(MadeReviewFile ? "made the review file, " : string.Empty)}labelled {LabelledSegments} segments and {LabelledMatches} matches{TokenText()}")
            : string.Create(CultureInfo.InvariantCulture, $"{SessionId}: made the review file");
    }

    private string TokenText() => Requests > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({Requests} requests, {InputTokens} tokens read, {OutputTokens} written)") : string.Empty;
}

/// <summary>
/// The automatic run (PLAN_AUTO_LABELLING section 4): for every recording that has no labels, makes the review file when it is missing, has it labelled and takes the
/// labels in through the same import as a labels file from a reviewer (source reviewer, spot-check due, not final). A recording that already has labels is not touched.
/// A failure leaves the recording without labels; nothing is guessed.
/// </summary>
internal static class AutoLabelRun
{
    /// <summary>
    /// The most labelling requests one run sends. A guard against runaway spend (a repeating failure, a manipulated answer), not a measurement: an hour of play is
    /// about a dozen requests. A run that reaches it stops with a plain message and the next run continues.
    /// </summary>
    public const int MaxRequestsPerRun = 200;

    /// <summary>Runs over the study folder.</summary>
    /// <param name="root">The study folder.</param>
    /// <param name="env">The machine (clock, output, study folder).</param>
    /// <param name="vocabulary">The closed lists, the same merged lists (embedded and signed roster) that the labeller and the spot-check use.</param>
    /// <param name="transport">Sends a request body.</param>
    /// <param name="progress">Told in plain words how far it is, or null.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="skip">Recordings (by id) that are not tried in this run, or null.</param>
    /// <param name="maxRequests">The most requests this run sends.</param>
    /// <param name="log">Told the technical detail of a problem (no file contents), or null.</param>
    /// <returns>One summary per recording that needed work, oldest first.</returns>
    public static async Task<IReadOnlyList<RecordingRunSummary>> RunAsync(string root, StudyEnvironment env, Vocabulary vocabulary, ILabellerTransport transport, IProgress<string>? progress, CancellationToken cancellationToken, IReadOnlySet<string>? skip = null, int maxRequests = MaxRequestsPerRun, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(transport);
        var summaries = new List<RecordingRunSummary>();
        var lexicon = HudVocabulary.Create(vocabulary);
        var used = 0;
        foreach (var folder in StudySessions.RecordingFolders(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(folder);
            if (File.Exists(Path.Combine(folder, SessionFiles.Answers)) || skip?.Contains(id) == true)
            {
                continue;
            }

            var made = false;
            if (ReviewExport.ExistingFiles(folder).Count == 0)
            {
                progress?.Report($"Making the review file for {id}.");
                try
                {
                    ReviewFiles.ExportFolder(folder, ReviewExport.DefaultLimitMegabytes, env);
                    made = true;
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    log?.Invoke(StudyText.Line($"The review file of {id} could not be made ({ex.GetType().Name}{(ex is FormatException or InvalidDataException ? ": " + ex.Message : string.Empty)})."));
                    summaries.Add(new RecordingRunSummary(id, false, 0, 0, 0, 0, 0, "The review file could not be made (the recording could not be read or is incomplete)."));
                    continue;
                }
            }

            if (!ReviewFiles.IsWaitingForReviewer(folder))
            {
                summaries.Add(new RecordingRunSummary(id, made, 0, 0, 0, 0, 0, made ? null : "The recording has no review file to label."));
                continue;
            }

            if (used >= maxRequests)
            {
                summaries.Add(new RecordingRunSummary(id, made, 0, 0, 0, 0, 0, string.Create(CultureInfo.InvariantCulture, $"This run reached its limit of {maxRequests} requests before this recording; the next run continues."), 0, LabellerOutcome.RunLimitReached));
                break;
            }

            var summary = await LabelOneAsync(folder, id, made, env, vocabulary, lexicon, transport, progress, maxRequests - used, log, cancellationToken).ConfigureAwait(false);
            used += summary.Requests;
            summaries.Add(summary);

            // The same answer would come back for every other recording, so the run stops here and the rest wait for the next run.
            if (summary.Problem is LabellerOutcome.NotSetUp or LabellerOutcome.NotAllowed or LabellerOutcome.DailyCapReached or LabellerOutcome.RunLimitReached)
            {
                break;
            }
        }

        return summaries;
    }

    private static async Task<RecordingRunSummary> LabelOneAsync(string folder, string id, bool made, StudyEnvironment env, Vocabulary vocabulary, HudVocabulary lexicon, ILabellerTransport transport, IProgress<string>? progress, int maxRequests, Action<string>? log, CancellationToken cancellationToken)
    {
        progress?.Report($"Labelling {id}.");
        var result = await AutoLabeller.LabelAsync(folder, lexicon, transport, progress, cancellationToken, env.Time, maxRequests, log).ConfigureAwait(false);
        if (result.Answers is null)
        {
            return new RecordingRunSummary(id, made, 0, 0, result.Requests, result.InputTokens, result.OutputTokens, result.Failure ?? "The recording could not be labelled.", result.SkippedImages, result.Problem);
        }

        // The answers go into the import as text: no labels file is written into the recording's folder, so nothing can be left behind for the 30-day
        // clean-up to trip over, and the import checks them against the same merged lists the labeller used.
        try
        {
            _ = ReviewSessions.ImportReviewerLabels(folder, result.Answers.ToJson(), vocabulary, env.Time.GetUtcNow());
            return new RecordingRunSummary(id, made, result.Answers.Segments.Count, result.Answers.Matches.Count, result.Requests, result.InputTokens, result.OutputTokens, null, result.SkippedImages);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            log?.Invoke(StudyText.Line($"The labels of {id} could not be taken in ({ex.GetType().Name}{(ex is FormatException or InvalidOperationException ? ": " + ex.Message : string.Empty)})."));
            return new RecordingRunSummary(id, made, 0, 0, result.Requests, result.InputTokens, result.OutputTokens, "The labels could not be taken in (they did not fit the recording's files). Nothing was changed.", result.SkippedImages);
        }
    }

    private static bool IsExpected(Exception ex) => ex is FormatException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException;
}
