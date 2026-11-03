using DbdPresence.Study;
using DbdPresence.Vision.Evaluation;

namespace DbdPresence.Eval;

/// <summary>What the held-out check hands to the clean-up once its report is written.</summary>
/// <param name="Root">The study folder.</param>
/// <param name="Sessions">The held-out recordings of the committed manifest (all of them were examined).</param>
/// <param name="Keep">Pictures to keep as (session id, file name): the ones of units the report counts as errors.</param>
/// <param name="AllMeasured">Whether every evaluable detector was measured in this run. If not, the pictures are still needed for the rest.</param>
/// <param name="ReportFile">The report file.</param>
/// <param name="ReportText">The text the report must hold, exactly.</param>
/// <param name="ManifestHash">The committed manifest hash.</param>
internal sealed record HeldOutFreeUp(string Root, IReadOnlyList<DatasetSession> Sessions, IReadOnlySet<(string Session, string File)> Keep, bool AllMeasured, string ReportFile, string ReportText, string ManifestHash);

/// <summary>
/// Removes what is no longer needed once a step is done, without touching what the check still depends on (FR-DET-24, DETECTION_VALIDATION §21).
/// <list type="bullet">
/// <item>The review file (<c>-review.zip</c> or its pieces) goes when the owner's spot-check of the reviewer's labels has passed: it only existed to be sent.</item>
/// <item>The pictures of the held-out recordings go right after the held-out report was written completely, except the pictures of units the report counts as errors, so that errors can be looked at.</item>
/// <item>The pictures of the practice (tuning) recordings go only when the held-out report exists <b>and</b> the status icons were taught: they calibrate the reader until then.</item>
/// </list>
/// The labels, answers, plan, <c>session.json</c>, <c>frames.jsonl</c>, <c>metadata.json</c> and the manifest are never deleted, so the manifest still verifies. The deletion follows the rules of the
/// study tool's 30-day clean-up (<c>SessionRetention</c>): only folders that carry its marker and are named like a session; a folder or file that is a link is never followed and nothing in a folder that holds a link is deleted;
/// only <c>frame-NNNNN.png</c> and the review file's exact names are deleted, whatever else is in the folder is left alone and said so; anything that could not be deleted is reported.
/// </summary>
internal static class SpaceFreeing
{
    private static readonly string[] s_knownFiles =
    [
        "metadata.json", SessionFiles.Session, SessionFiles.Frames, SessionFiles.Labels, SessionFiles.Plan, SessionFiles.Answers, SessionFiles.ReviewPage,
        SessionFiles.RecheckPlan, SessionFiles.RecheckPage, SessionFiles.RecheckResult,
    ];

    /// <summary>
    /// After the owner's spot-check of a reviewer's labels: when it passed, deletes that recording's review file (or its pieces) and says so. Does nothing for hand labels or a check that did not pass.
    /// </summary>
    public static void AfterSpotCheck(string folder, RecheckOutcome outcome, EvalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(env);
        try
        {
            if (outcome.Status != "rechecked" || ReviewSessions.AnswersSource(folder) != LabelSources.Reviewer || ReviewExport.ExistingFiles(folder).Count == 0)
            {
                return;
            }

            var id = Path.GetFileName(folder);
            var result = Sweep(folder, pictures: false, reviewFiles: true, keep: new HashSet<string>(StringComparer.Ordinal));
            if (result.ReviewFiles > 0)
            {
                env.Out.WriteLine($"Your spot-check passed, so the review file of {id} ({result.ReviewFiles} {(result.ReviewFiles == 1 ? "file" : "files")}, {SizeText.Of(result.Bytes)}) was deleted from this PC: it only existed to be sent. The recording's pictures are still here.");
            }

            Report(result, id, env);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            env.Error.WriteLine($"The review file of {Path.GetFileName(folder)} was not deleted: {ex.Message}");
        }
    }

    /// <summary>
    /// Right after a held-out report was written: confirms the report on disk is complete, records that it exists, deletes the held-out pictures (except those of error units) and,
    /// if the status icons were taught too, the practice recordings' pictures. Nothing is deleted when the report is not confirmed complete.
    /// </summary>
    public static void AfterHeldOutReport(HeldOutFreeUp request, EvalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(env);
        try
        {
            env.Out.WriteLine();
            if (!request.AllMeasured)
            {
                env.Out.WriteLine("Pictures kept: only some detectors were measured in this run, and the others still need the pictures. Run the check for all detectors to free up the space.");
                return;
            }

            if (!ReportIsComplete(request))
            {
                env.Out.WriteLine($"Pictures kept: the report in {request.ReportFile} could not be confirmed as complete, so nothing was deleted.");
                return;
            }

            var state = StudyState.Load(request.Root).WithHeldOutReport(request.ManifestHash, env.Time.GetUtcNow(), request.Sessions.Select(s => s.Metadata.SessionId));
            state.Save(request.Root);

            var folders = StudySessions.RecordingFolders(request.Root).ToDictionary(f => Path.GetFileName(f)!, f => f, StringComparer.Ordinal);
            env.Out.WriteLine("The check is done and its report is written, so the pictures of the held-out recordings are no longer needed. Freeing up space:");
            long total = 0;
            var kept = 0;
            foreach (var session in request.Sessions.OrderBy(s => s.Metadata.SessionId, StringComparer.Ordinal))
            {
                var id = session.Metadata.SessionId;
                if (!folders.TryGetValue(id, out var folder))
                {
                    env.Out.WriteLine($"  Recording {id}: its folder was not found among the study recordings, so nothing was deleted there.");
                    continue;
                }

                var keep = request.Keep.Where(k => k.Session == id).Select(k => k.File).ToHashSet(StringComparer.Ordinal);
                var result = Sweep(folder, pictures: true, reviewFiles: true, keep);
                total += result.Bytes;
                kept += result.Kept;
                Describe(result, id, env);
            }

            env.Out.WriteLine(total > 0
                ? $"  Freed {SizeText.Of(total)} in all. {(kept > 0 ? $"{kept} pictures were kept because the report lists their units as wrong or missed; delete them by hand when you no longer need them. " : "No unit was wrong or missed, so no picture was kept. ")}The labels, answers and manifest are kept, and the manifest still matches."
                : "  Nothing was left to delete.");
            FreeTuning(request.Root, env, state.HasHeldOutReport, StatusTaught(state, env));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            env.Error.WriteLine($"Freeing up space stopped: {ex.Message} Nothing more was deleted.");
        }
    }

    /// <summary>After the status icons were taught: records it and, if a held-out report exists, deletes the practice recordings' pictures.</summary>
    public static void AfterTeachStatus(string root, EvalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        try
        {
            var state = StudyState.Load(root);
            if (state.StatusTaughtUtc is null)
            {
                state = state.WithStatusTaught(env.Time.GetUtcNow());
                state.Save(root);
            }

            FreeTuning(root, env, state.HasHeldOutReport, taught: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            env.Error.WriteLine($"Freeing up space stopped: {ex.Message} Nothing was deleted.");
        }
    }

    private static bool StatusTaught(StudyState state, EvalEnvironment env) =>
        state.StatusTaughtUtc is not null || (env.AppDataFolder is { Length: > 0 } data && File.Exists(Path.Combine(data, StatusIconTeaching.FileName)));

    private static void FreeTuning(string root, EvalEnvironment env, bool heldOutReport, bool taught)
    {
        var practice = StudySessions.LoadPrepared(root).Where(s => s.Metadata.Split == "tuning").ToList();
        var folders = StudySessions.RecordingFolders(root).ToDictionary(f => Path.GetFileName(f)!, f => f, StringComparer.Ordinal);
        var withPictures = practice.Where(s => s.Frames.Any(f => File.Exists(Path.Combine(s.Directory, f.File)))).ToList();
        if (withPictures.Count == 0)
        {
            return;
        }

        if (!heldOutReport || !taught)
        {
            env.Out.WriteLine(!heldOutReport
                ? "Your practice recordings keep their pictures until the held-out check has been run: they are deleted automatically after it, once the status icons are taught as well."
                : "Your practice recordings keep their pictures until the status icons are taught (choice 3 in the tool's menu), because that needs them. They are deleted automatically after it.");
            return;
        }

        env.Out.WriteLine("The held-out check is done and the status icons are taught, so the pictures of your practice recordings are no longer needed. Freeing up space:");
        long total = 0;
        foreach (var session in withPictures.OrderBy(s => s.Metadata.SessionId, StringComparer.Ordinal))
        {
            var id = session.Metadata.SessionId;
            if (session.Labels.Count == 0 || session.Labels.Any(l => l.ReviewStatus != "rechecked"))
            {
                env.Out.WriteLine($"  Recording {id}: its pictures are kept because its labels are not final yet (the second look is not done).");
                continue;
            }

            if (!folders.TryGetValue(id, out var folder))
            {
                env.Out.WriteLine($"  Recording {id}: its folder was not found among the study recordings, so nothing was deleted there.");
                continue;
            }

            var result = Sweep(folder, pictures: true, reviewFiles: true, new HashSet<string>(StringComparer.Ordinal));
            total += result.Bytes;
            Describe(result, id, env);
        }

        env.Out.WriteLine(total > 0 ? $"  Freed {SizeText.Of(total)} from your practice recordings. Their labels and small files are kept." : "  Nothing was left to delete.");
    }

    private static bool ReportIsComplete(HeldOutFreeUp request) =>
        File.Exists(request.ReportFile) && File.Exists(request.ReportFile + ".timing.md") && string.Equals(File.ReadAllText(request.ReportFile), request.ReportText, StringComparison.Ordinal);

    private static void Describe(SweepResult result, string id, EvalEnvironment env)
    {
        if (result.Pictures > 0 || result.ReviewFiles > 0)
        {
            env.Out.WriteLine($"  Recording {id}: deleted {result.Pictures} pictures{(result.ReviewFiles > 0 ? $" and {result.ReviewFiles} review {(result.ReviewFiles == 1 ? "file" : "files")}" : string.Empty)} ({SizeText.Of(result.Bytes)}){(result.Kept > 0 ? $"; kept {result.Kept} pictures of units that were wrong or missed" : string.Empty)}.");
        }
        else if (result.Kept > 0)
        {
            env.Out.WriteLine($"  Recording {id}: kept {result.Kept} pictures of units that were wrong or missed; nothing else was left.");
        }

        Report(result, id, env);
    }

    private static void Report(SweepResult result, string id, EvalEnvironment env)
    {
        if (result.OtherLeft > 0)
        {
            env.Out.WriteLine($"  Recording {id}: {result.OtherLeft} other {(result.OtherLeft == 1 ? "item" : "items")} in its folder that this tool did not create {(result.OtherLeft == 1 ? "was" : "were")} left alone.");
        }

        foreach (var problem in result.Problems)
        {
            env.Out.WriteLine($"  Recording {id}: {problem}");
        }
    }

    /// <summary>What one folder's clean-up did.</summary>
    private sealed record SweepResult(int Pictures, int ReviewFiles, long Bytes, int Kept, int OtherLeft, IReadOnlyList<string> Problems);

    /// <summary>Deletes the tool's own pictures and/or review files in one session folder under the retention rules. Never deletes the folder or any other file.</summary>
    private static SweepResult Sweep(string folder, bool pictures, bool reviewFiles, HashSet<string> keep)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists || StudyFiles.IsLink(directory))
        {
            return Refused("its folder is missing or is a link, so it was not followed and nothing was deleted");
        }

        var metadata = new FileInfo(Path.Combine(folder, "metadata.json"));
        if (!metadata.Exists || StudyFiles.IsLink(metadata))
        {
            return Refused("it has no usable metadata.json of the study tool, so it is not treated as one of its recordings and nothing was deleted");
        }

        var reviewNames = reviewFiles ? ReviewExport.ExistingFiles(folder).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal) : [];
        var candidates = new List<(FileInfo File, bool Review)>();
        var other = 0;
        var kept = 0;
        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (StudyFiles.IsLink(entry))
            {
                return Refused($"it holds a link ({entry.Name}), which is never followed, so nothing in it was deleted");
            }

            if (entry is not FileInfo file)
            {
                other++;
            }
            else if (FrameCompaction.FrameFileName().IsMatch(file.Name))
            {
                if (pictures && keep.Contains(file.Name))
                {
                    kept++;
                }
                else if (pictures)
                {
                    candidates.Add((file, false));
                }
            }
            else if (reviewNames.Contains(file.Name))
            {
                candidates.Add((file, true));
            }
            else if (!s_knownFiles.Contains(file.Name, StringComparer.Ordinal))
            {
                other++;
            }
        }

        var deletedPictures = 0;
        var deletedReview = 0;
        long bytes = 0;
        var problems = new List<string>();
        foreach (var (file, review) in candidates)
        {
            try
            {
                var length = file.Length;
                file.Delete();
                bytes += length;
                if (review)
                {
                    deletedReview++;
                }
                else
                {
                    deletedPictures++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file.Name} could not be deleted: {ex.Message}");
            }
        }

        return new SweepResult(deletedPictures, deletedReview, bytes, kept, other, problems);

        static SweepResult Refused(string why) => new(0, 0, 0, 0, 0, [why + "."]);
    }
}
