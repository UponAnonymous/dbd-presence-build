using System.Globalization;
using System.Text.Json;
using DbdPresence.Study;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Text;

namespace DbdPresence.Eval;

/// <summary>
/// The labelling flow the owner runs after a play session: it prepares new recordings, makes a page to confirm each one, reads the saved answers and, a day
/// later, makes the blind second look. The owner types nothing but Enter. Matches are labelled by the owner from the pictures; the tool only proposes
/// where screens change, from the pictures' brightness (see <see cref="SegmentProposer"/>), never from a detector.
/// </summary>
internal static class GuidedLabel
{
    /// <summary>How many labelled matches the owner is working towards (DETECTION_VALIDATION §7: at least 30 trials for the Experimental tier).</summary>
    public const int MatchGoal = 30;

    /// <summary>Runs the flow. Returns 0 when it finished (even if the owner chose to stop early) and 1 when nothing could be done.</summary>
    /// <param name="root">The study folder holding the recordings.</param>
    /// <param name="options">How new recordings are filed when they are prepared.</param>
    /// <param name="env">Console, clock and folders.</param>
    /// <param name="askBeforeHandLabelling">From the double-click menu: a recording nobody has labelled yet is not opened for hand
    /// labelling straight away; the owner is told that the usual way is a review file (choice 2) and labels it only on request.</param>
    public static int Run(string root, PrepareOptions options, EvalEnvironment env, bool askBeforeHandLabelling = false)
    {
        var folders = StudySessions.RecordingFolders(root);
        if (folders.Count == 0)
        {
            env.Error.WriteLine($"No recordings were found in {root}. Start the recorder (DbdPresence.StudyCapture), play, then run this again.");
            return 1;
        }

        var vocabulary = Vocabulary.LoadEmbedded();
        var decoder = new ExtensionFrameDecoder();
        PrepareAll(folders, options, env);
        FrameCompaction.RunAll(root, env);
        ReviewFiles.ImportWaiting(root, env);

        foreach (var folder in StudySessions.PreparedFolders(root))
        {
            if (ReviewFiles.IsWaitingForReviewer(folder))
            {
                var id = Path.GetFileName(folder);
                env.Out.WriteLine($"Recording {id}: its review file was made, so it waits for the reviewer's labels. No file named {ReviewExport.ReviewerLabelsName(id)} was found in {env.DownloadsFolder ?? "your Downloads folder"} or in the recording's folder. When you have it, save it in one of those two places and run this again. (To label this recording yourself instead, delete {string.Join(", ", ReviewExport.ExistingFiles(folder).Select(Path.GetFileName))} and run this again.)");
                continue;
            }

            if (!NeedsLabels(folder))
            {
                continue;
            }

            if (askBeforeHandLabelling && !File.Exists(Path.Combine(folder, SessionFiles.Answers)))
            {
                var choice = AskAboutNewRecording(folder, env);
                if (choice == NewRecordingChoice.Stop)
                {
                    return 0;
                }

                if (choice == NewRecordingChoice.Skip)
                {
                    continue;
                }
            }

            if (!LabelSession(folder, root, vocabulary, decoder, env))
            {
                return 0;
            }
        }

        foreach (var folder in StudySessions.PreparedFolders(root))
        {
            if (File.Exists(Path.Combine(folder, SessionFiles.Answers)) && !RecheckSession(folder, root, vocabulary, decoder, env))
            {
                return 0;
            }
        }

        Summarise(root, env);
        return 0;
    }

    /// <summary>Counts the matches the owner has labelled: distinct trials among the per-trial facts of all prepared sessions. Reads label files only.</summary>
    public static int CountMatches(string root)
    {
        var count = 0;
        foreach (var folder in StudySessions.PreparedFolders(root))
        {
            var path = Path.Combine(folder, SessionFiles.Labels);
            if (!File.Exists(path))
            {
                continue;
            }

            var trials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
            {
                using var document = JsonText.ReadDocumentFromText(line);
                var e = document.RootElement;
                if (e.TryGetProperty("type", out var type) && type.GetString() == "trial-fact" && e.TryGetProperty("trialId", out var trial) && trial.ValueKind == JsonValueKind.String
                    && !IsUncheckedReviewerLabel(e))
                {
                    trials.Add(trial.GetString()!);
                }
            }

            count += trials.Count;
        }

        return count;
    }

    /// <summary>A label imported from a reviewer counts only after the owner's blind spot-check passed it (DETECTION_VALIDATION §16.1).</summary>
    private static bool IsUncheckedReviewerLabel(JsonElement label) =>
        label.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == LabelSources.Reviewer
        && !(label.TryGetProperty("reviewStatus", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "rechecked");

    private static void PrepareAll(IReadOnlyList<string> folders, PrepareOptions options, EvalEnvironment env)
    {
        var prepared = folders.Count(f => File.Exists(Path.Combine(f, SessionFiles.Session)));
        foreach (var folder in folders.Where(f => !File.Exists(Path.Combine(f, SessionFiles.Session))))
        {
            try
            {
                if (StudySessions.Prepare(folder, options, prepared) is { } result)
                {
                    prepared++;
                    env.Out.WriteLine($"Found a new recording ({Path.GetFileName(folder)}, {result.Frames} pictures). Filed as {result.Split}; {result.Note}.");
                }
            }
            catch (FormatException ex)
            {
                env.Error.WriteLine($"Skipped {Path.GetFileName(folder)}: {ex.Message}");
            }
        }
    }

    private enum NewRecordingChoice
    {
        Skip,
        Label,
        Stop,
    }

    private static NewRecordingChoice AskAboutNewRecording(string folder, EvalEnvironment env)
    {
        env.Out.WriteLine();
        env.Out.WriteLine($"Recording {Path.GetFileName(folder)} is new and nobody has labelled it yet.");
        env.Out.WriteLine("  The usual way: send it to the developer. Type S and press Enter to stop, then run the tool again and choose 2.");
        env.Out.WriteLine("  To label it yourself instead (about 5 minutes a sitting), type L and press Enter.");
        env.Out.WriteLine("  Just Enter skips it for now and carries on with any spot-check that is waiting.");
        var answer = env.ReadLine()?.Trim() ?? string.Empty;
        return answer.ToUpperInvariant() switch
        {
            "L" => NewRecordingChoice.Label,
            "S" => NewRecordingChoice.Stop,
            _ => NewRecordingChoice.Skip,
        };
    }

    private static bool NeedsLabels(string folder)
    {
        if (!File.Exists(Path.Combine(folder, SessionFiles.Answers)))
        {
            return true;
        }

        return File.Exists(Path.Combine(folder, SessionFiles.RecheckResult)) && ReadStatus(folder) == "disputed";
    }

    private static string? ReadStatus(string folder)
    {
        using var document = JsonText.ReadDocument(Path.Combine(folder, SessionFiles.RecheckResult));
        return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
    }

    private static bool LabelSession(string folder, string root, Vocabulary vocabulary, IFrameDecoder decoder, EvalEnvironment env)
    {
        var id = Path.GetFileName(folder);
        var rework = File.Exists(Path.Combine(folder, SessionFiles.Answers));
        env.Out.WriteLine();
        env.Out.WriteLine(rework
            ? $"Recording {id}: the second look disagreed with some answers, so the page opens with your answers filled in. Please check them again."
            : $"Recording {id}: making the page to check your matches. This takes a minute.");
        var page = ReviewSessions.BuildLabelPage(folder, vocabulary, decoder, m => env.Out.WriteLine("  " + m));
        return Await(folder, root, page, ReviewSessions.AnswerFileName(id), env, json =>
        {
            var matches = ReviewSessions.ApplyLabels(folder, json, vocabulary, env.Time.GetUtcNow());
            env.Out.WriteLine($"Saved. {matches} matches recorded from this session. A second look is due after {env.Time.GetUtcNow().Add(ReviewSessions.RecheckDelay).ToLocalTime():yyyy-MM-dd HH:mm}.");
        });
    }

    private static bool RecheckSession(string folder, string root, Vocabulary vocabulary, IFrameDecoder decoder, EvalEnvironment env)
    {
        var due = ReviewSessions.RecheckDueAt(folder);
        if (due is not { } at)
        {
            return true;
        }

        var id = Path.GetFileName(folder);
        if (env.Time.GetUtcNow() < at)
        {
            env.Out.WriteLine($"Second look for {id}: not yet. Run this again after {at.ToLocalTime():yyyy-MM-dd HH:mm}.");
            return true;
        }

        env.Out.WriteLine();
        env.Out.WriteLine(ReviewSessions.AnswersSource(folder) == LabelSources.Reviewer
            ? $"Recording {id}: the reviewer's labels are in. Time for your quick spot-check (a few rows, answered without seeing the reviewer's answers). The labels count only after it."
            : $"Recording {id}: a day has passed. Time for the quick second look (a few rows, answered without seeing your earlier answers).");
        var page = ReviewSessions.BuildRecheckPage(folder, vocabulary, decoder);
        return Await(folder, root, page, ReviewSessions.RecheckFileName(id), env, json =>
        {
            var outcome = ReviewSessions.ApplyRecheck(folder, json, vocabulary, env.Time.GetUtcNow());
            env.Out.WriteLine(outcome.Status == "rechecked"
                ? "Second look done: your answers agree. This session's labels are ready to use."
                : $"Second look done: {outcome.SceneDisagreements} screen answers and {outcome.FactDisagreements.Count} match answers differed. Run this again to correct them.");
            SpaceFreeing.AfterSpotCheck(folder, outcome, env);
        });
    }

    /// <summary>Tells the owner which file to open, waits for Enter, finds the saved answers and hands them to <paramref name="apply"/>. Returns false when the owner stopped.</summary>
    private static bool Await(string folder, string root, string page, string answerFile, EvalEnvironment env, Action<string> apply)
    {
        var made = env.Time.GetUtcNow().UtcDateTime.AddSeconds(-5);
        env.Out.WriteLine($"Open this file in your browser (double-click it): {page}");
        env.Out.WriteLine("Check each row, click \"Save my answers\", then come back here and press Enter. Type S and Enter to stop for now.");
        var line = env.ReadLine();
        if (line is null || line.Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var found = StudyFiles.FindSaved(answerFile, [env.DownloadsFolder, folder, root], made);
        if (found is null)
        {
            env.Out.WriteLine($"I could not find the saved answers ({answerFile}). Nothing was changed. Run this again when you have saved them.");
            return false;
        }

        try
        {
            apply(File.ReadAllText(found));
            return true;
        }
        catch (FormatException ex)
        {
            env.Error.WriteLine($"The saved answers could not be used: {ex.Message} Nothing was changed. Open the page and save again.");
            return false;
        }
    }

    private static void Summarise(string root, EvalEnvironment env)
    {
        var matches = CountMatches(root);
        env.Out.WriteLine();
        env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Matches recorded so far: {matches} of {MatchGoal}."));
    }
}
