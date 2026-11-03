using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Study;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;
using GameRole = DbdPresence.Core.Game.Role;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Eval;

/// <summary>What an evaluation run was asked to do.</summary>
/// <param name="Root">The folder holding the study sessions.</param>
/// <param name="OutFile">The report file.</param>
/// <param name="HeldOut">True for a held-out run (needs a manifest); false for a tuning run.</param>
/// <param name="ManifestFile">The committed manifest file (held-out run).</param>
/// <param name="ManifestHash">The committed manifest hash, copied from the commit (held-out run).</param>
/// <param name="OwnerName">The owner's in-game name, from the command line only; held in memory for this run and never written anywhere.</param>
/// <param name="Detectors">The detectors to evaluate, or null for every detector that has labels.</param>
/// <param name="Ocr">Which recogniser to use.</param>
/// <param name="KeepPictures">True to keep the pictures after a held-out report is written (for a repeat run that must reproduce the report); by default they are deleted (DETECTION_VALIDATION §21).</param>
internal sealed record EvaluateRequest(string Root, string OutFile, bool HeldOut, string? ManifestFile, string? ManifestHash, string? OwnerName, IReadOnlyList<string>? Detectors, OcrKind Ocr, bool KeepPictures = false);

/// <summary>Counts what the recogniser did, so a run whose recognitions failed says so in its report instead of looking like a run that read nothing.</summary>
internal sealed class CountingRecognizer(ITextRecognizer inner) : ITextRecognizer
{
    private readonly Dictionary<TextRecognitionStatus, int> _statuses = [];

    public int Calls { get; private set; }

    public IReadOnlyDictionary<TextRecognitionStatus, int> Statuses => _statuses;

    public int Failed => _statuses.Where(s => s.Key != TextRecognitionStatus.Recognized).Sum(s => s.Value);

    public async Task<TextRecognitionResult> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
    {
        var result = await inner.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
        Calls++;
        _statuses[result.Status] = _statuses.GetValueOrDefault(result.Status) + 1;
        return result;
    }
}

/// <summary>
/// Runs the real HUD detectors over labelled study sessions (FR-DET-16, FR-DET-24) and writes the metrics report. A held-out run is refused unless every
/// session in it is held-out, its labels passed the blind re-check, and the set matches the manifest committed beforehand; a re-run for a report must
/// reproduce the first report bit for bit. The owner's name is used in memory only.
/// </summary>
internal static class EvaluateCommand
{
    private static readonly (string Name, ObservationKind Kind)[] s_supported =
    [
        ("scene", ObservationKind.Scene),
        ("role", ObservationKind.Role),
        ("map", ObservationKind.Map),
        ("character", ObservationKind.Character),
        ("killer", ObservationKind.Killer),
        ("queue-mode", ObservationKind.QueueMode),
        ("own-status", ObservationKind.OwnStatus),
        ("survivor-result", ObservationKind.SurvivorResult),
        ("killer-result", ObservationKind.KillerResult),
        ("hook-count", ObservationKind.HookCount),
    ];

    /// <summary>Runs the evaluation. Returns 0 on success and 1 when it refuses or cannot read the data.</summary>
    public static int Run(EvaluateRequest request, EvalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(env);
        try
        {
            return RunCore(request, env);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            env.Error.WriteLine("Evaluation stopped: " + ex.Message);
            return 1;
        }
    }

    private static int RunCore(EvaluateRequest request, EvalEnvironment env)
    {
        var requested = request.Detectors ?? [.. s_supported.Select(s => s.Name)];
        var unknown = requested.Where(d => s_supported.All(s => s.Name != d)).ToList();
        if (unknown.Count > 0)
        {
            env.Error.WriteLine($"No detector exists to evaluate '{string.Join("', '", unknown)}'. Evaluable: {string.Join(", ", s_supported.Select(s => s.Name))}.");
            return 1;
        }

        var all = StudySessions.LoadPrepared(request.Root);
        var disputed = all.Where(s => s.Labels.Any(l => l.ReviewStatus == "disputed")).Select(s => s.Metadata.SessionId).ToList();
        string? manifestHash = null;
        List<DatasetSession> sessions;
        int ignored;
        if (request.HeldOut)
        {
            sessions = SelectHeldOut(request, all, env, out manifestHash);
            ignored = all.Count - sessions.Count;
        }
        else
        {
            if (request.ManifestFile is not null || request.ManifestHash is not null)
            {
                throw new InvalidOperationException("A tuning run takes no manifest. The manifest belongs to the held-out run only.");
            }

            sessions = [.. all.Where(s => s.Metadata.Split == "tuning")];
            ignored = all.Count - sessions.Count;
        }

        var disputedHere = sessions.Where(s => disputed.Contains(s.Metadata.SessionId)).Select(s => s.Metadata.SessionId).ToList();
        if (disputedHere.Count > 0)
        {
            throw new InvalidOperationException($"These sessions have labels the blind re-check disputed and must be reworked first: {string.Join(", ", disputedHere)}.");
        }

        if (sessions.Count == 0)
        {
            throw new InvalidOperationException(request.HeldOut ? "There are no held-out sessions to evaluate." : "There are no tuning sessions to evaluate.");
        }

        StudyFiles.RequireFrames(sessions);
        var recognizer = env.CreateRecognizer(request.Ocr) ?? throw new InvalidOperationException(request.Ocr == OcrKind.Fake
            ? "The fake recogniser is for tests only and is not available in this build."
            : "Windows text recognition with the English language is not available on this PC. Add the English language pack in Windows Settings (Time and language), then run again.");
        var counting = new CountingRecognizer(recognizer);
        var vocabulary = Vocabulary.LoadEmbedded();
        var lists = HudVocabulary.Create(vocabulary);
        var profile = HudProfile.Catalogue;
        var origin = request.Ocr == OcrKind.Fake ? DataOrigin.Synthetic : request.HeldOut ? DataOrigin.Real : DataOrigin.Tuning;
        var decoder = new ExtensionFrameDecoder();
        var options = new EngineOptions();

        var timings = new List<double>();
        var decodeMs = new List<double>();
        var perContext = new SortedDictionary<string, List<(DatasetSession Session, SessionObservations Observed)>>(StringComparer.Ordinal);
        var totalFrames = 0;
        var areas = new Dictionary<string, ClientAreaDecision>(StringComparer.Ordinal);
        var errorFrames = new HashSet<(string Session, string File)>();
        var byId = sessions.ToDictionary(s => s.Metadata.SessionId, StringComparer.Ordinal);
        var wall = Stopwatch.StartNew();
        foreach (var session in sessions)
        {
            env.Out.WriteLine($"Reading session {session.Metadata.SessionId} ({session.Frames.Count} pictures)...");
            var area = ClientAreaSessions.Decide(session, decoder);
            areas[session.Metadata.SessionId] = area;
            env.Out.WriteLine($"  client area: {area.Outcome}. {area.Reason}");
            var observed = SessionObservations.Collect(session, profile, lists, counting, request.OwnerName, decoder, timings, decodeMs, area);
            totalFrames += session.Frames.Count;
            var key = ContextOf(session.Metadata, profile, area);
            if (!perContext.TryGetValue(key, out var group))
            {
                perContext[key] = group = [];
            }

            group.Add((session, observed));
        }

        wall.Stop();
        var report = new StringBuilder();
        AppendHeader(report, request, origin, profile, sessions, ignored, totalFrames, counting, manifestHash, requested, areas);
        var summary = new List<string>();
        var notEvaluated = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (context, group) in perContext)
        {
            report.Append("\n---\n\n## Context: ").Append(context).Append("\n\n");
            foreach (var (name, kind) in s_supported.Where(s => requested.Contains(s.Name)))
            {
                var units = new List<EvaluationUnit>();
                foreach (var (session, observed) in group)
                {
                    units.AddRange(DatasetLoader.BuildUnits(session, name, observed.Decoder));
                }

                if (units.Count == 0)
                {
                    continue;
                }

                var version = group[0].Observed.VersionOf(kind) ?? 0;
                var rule = RuleOf(kind, options);
                FrameClassifier classify = frame => Lookup(group, frame, kind);
                var evaluationOptions = new EvaluationOptions("study-recordings", name, version, context, origin, rule);
                var evaluation = DetectionEvaluator.Run(units, classify, evaluationOptions);
                if (request.HeldOut)
                {
                    ErrorUnits.Collect(units, byId, name, classify, evaluationOptions, errorFrames);
                }

                report.Append(EvaluationReportWriter.ToMarkdown(evaluation)).Append('\n');
                summary.Add(Summarise(name, context, evaluation));
            }

            foreach (var (session, _) in group)
            {
                foreach (var detector in session.Labels.Select(l => l.Detector).Where(d => s_supported.All(s => s.Name != d)))
                {
                    notEvaluated.Add(detector);
                }
            }
        }

        if (notEvaluated.Count > 0)
        {
            report.Append("\n---\n\nLabels for ").Append(string.Join(", ", notEvaluated)).Append(" exist but no detector produces them yet, so nothing was evaluated for them.\n");
        }

        var text = report.ToString();
        var reproduced = WriteReport(request, text, env);
        WriteTiming(request.OutFile + ".timing.md", timings, decodeMs, counting, totalFrames, wall.Elapsed);

        env.Out.WriteLine();
        env.Out.WriteLine(origin switch
        {
            DataOrigin.Synthetic => "MECHANICS ONLY: the fake recogniser was used. These numbers are not accuracy.",
            DataOrigin.Tuning => "TUNING RUN: these sessions are the calibration data. These numbers are not accuracy and cannot earn a tier.",
            _ => "HELD-OUT RUN. The numbers below are the evaluator's own; nothing here is typed by hand.",
        });
        foreach (var line in summary)
        {
            env.Out.WriteLine(line);
        }

        if (counting.Failed > 0)
        {
            env.Out.WriteLine($"WARNING: {counting.Failed} of {counting.Calls} text recognitions did not run ({string.Join(", ", counting.Statuses.Where(s => s.Key != TextRecognitionStatus.Recognized).Select(s => $"{s.Key}: {s.Value}"))}). Those readings are abstentions.");
        }

        env.Out.WriteLine(reproduced ? $"This report matches the earlier report in {request.OutFile} bit for bit." : $"Report written to {request.OutFile}");
        env.Out.WriteLine($"Timing (this machine only, not part of the report): {request.OutFile}.timing.md");
        if (request.HeldOut && request.KeepPictures)
        {
            env.Out.WriteLine("Pictures kept (--keep-pictures). Run again without it to delete them.");
        }
        else if (request.HeldOut && manifestHash is not null)
        {
            // The report is on the disk and printed: only now are the held-out pictures no longer needed (the unit judgements above are the report's own).
            SpaceFreeing.AfterHeldOutReport(new HeldOutFreeUp(request.Root, sessions, errorFrames, s_supported.All(s => requested.Contains(s.Name)), request.OutFile, text, manifestHash), env);
        }

        return 0;
    }

    private static List<DatasetSession> SelectHeldOut(EvaluateRequest request, IReadOnlyList<DatasetSession> all, EvalEnvironment env, out string hash)
    {
        if (request.ManifestFile is null || request.ManifestHash is null)
        {
            throw new InvalidOperationException("A held-out run needs the committed manifest: pass --manifest <file> and --manifest-hash <the hash from the commit>.");
        }

        var manifest = ManifestFile.Parse(File.ReadAllText(request.ManifestFile));
        if (!string.Equals(manifest.Hash, request.ManifestHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The hash you gave is not the hash in the manifest file. Use the hash that was committed before the evaluation.");
        }

        var listed = manifest.SessionIds;
        var byId = all.ToDictionary(s => s.Metadata.SessionId, StringComparer.Ordinal);
        var tuningListed = listed.Where(id => byId.TryGetValue(id, out var s) && s.Metadata.Split != "heldout").ToList();
        if (tuningListed.Count > 0)
        {
            throw new InvalidOperationException($"Tuning sessions are not allowed in a held-out run: {string.Join(", ", tuningListed)}.");
        }

        var missing = listed.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"These sessions are in the manifest but not on this PC: {string.Join(", ", missing)}.");
        }

        var heldOut = all.Where(s => s.Metadata.Split == "heldout").ToList();
        var unlisted = heldOut.Where(s => !listed.Contains(s.Metadata.SessionId, StringComparer.Ordinal)).Select(s => s.Metadata.SessionId).ToList();
        if (unlisted.Count > 0)
        {
            throw new InvalidOperationException($"These held-out sessions are not in the committed manifest: {string.Join(", ", unlisted)}. Make a new manifest and commit it before evaluating them.");
        }

        var notChecked = heldOut.Where(s => s.Labels.Count == 0 || s.Labels.Any(l => l.ReviewStatus != "rechecked")).Select(s => s.Metadata.SessionId).ToList();
        if (notChecked.Count > 0)
        {
            throw new InvalidOperationException($"These held-out sessions have not passed the blind re-check: {string.Join(", ", notChecked)}.");
        }

        DatasetLoader.VerifyManifest(heldOut, manifest.Hash);
        hash = manifest.Hash;
        env.Out.WriteLine($"Held-out run: {heldOut.Count} sessions match the committed manifest {manifest.Hash[..12]}...");
        return heldOut;
    }

    private static FramePrediction Lookup(List<(DatasetSession Session, SessionObservations Observed)> group, EvaluationFrame frame, ObservationKind kind)
    {
        foreach (var (_, observed) in group)
        {
            if (observed.TryFind(frame, kind, out var prediction))
            {
                return prediction;
            }
        }

        return new FramePrediction(null, 0);
    }

    private static ConfirmationRule RuleOf(ObservationKind kind, EngineOptions o) => kind switch
    {
        ObservationKind.Scene => o.Scene,
        ObservationKind.Role => o.Role,
        ObservationKind.Map => o.Map,
        ObservationKind.Character => o.Character,
        ObservationKind.QueueMode => o.QueueMode,
        ObservationKind.Killer => o.Killer,
        ObservationKind.OwnStatus => o.OwnStatus,
        ObservationKind.SurvivorResult or ObservationKind.KillerResult => o.ResultOutcome,
        ObservationKind.HookCount => o.HookCount,
        _ => o.Generators,
    };

    private static string ContextOf(SessionMetadata m, HudProfile profile, ClientAreaDecision area) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"profile {profile.ProfileId} ({profile.Provenance}); {area.EffectiveWidth}x{area.EffectiveHeight}; {m.WindowMode}; HUD scale {m.HudScale:0.##}; menu scale {m.MenuScale:0.##}; language {m.Language}; game version {m.GameVersion}; mode {m.DeclaredMode}");

    private static string Summarise(string detector, string context, EvaluationReport r)
    {
        static string P(Proportion p) => p.Value is { } v && p.Wilson is { } w
            ? string.Create(CultureInfo.InvariantCulture, $"{v * 100:0.0}% ({p.Successes}/{p.Trials}, 95% Wilson {w.Lower * 100:0.0}% to {w.Upper * 100:0.0}%)")
            : "UNKNOWN";
        var p95 = r.P95SecondsToDetect is { } s ? string.Create(CultureInfo.InvariantCulture, $"{s:0.0} s") : "UNKNOWN";
        var gates = r.Gates.Applicable
            ? $"measured gates: Experimental {(r.Gates.ExperimentalMeasuredGatesMet ? "met" : "not met")}, Validated {(r.Gates.ValidatedMeasuredGatesMet ? "met" : "not met")}"
            : "no tier can be earned from this run";
        return $"  {detector} [{context}]: {r.LegibleUnits} units from {r.Sessions} sessions; precision {P(r.Precision)}; coverage {P(r.Coverage)}; time to detect p95 {p95}; {gates}.";
    }

    private static void AppendHeader(
        StringBuilder sb, EvaluateRequest request, DataOrigin origin, HudProfile profile, IReadOnlyList<DatasetSession> sessions, int ignored, int frames, CountingRecognizer recognizer, string? manifestHash, IReadOnlyList<string> detectors, Dictionary<string, ClientAreaDecision> areas)
    {
        sb.Append("# Evaluation run\n\n");
        switch (origin)
        {
            case DataOrigin.Synthetic:
                sb.Append("> **MECHANICS ONLY.** A fake recogniser read the pictures. These numbers show that the tool works. They are not detector accuracy and cannot earn a tier.\n\n");
                break;
            case DataOrigin.Tuning:
                sb.Append("> **TUNING RUN.** These sessions are the data the thresholds are calibrated on. These numbers are not accuracy and cannot earn a tier.\n\n");
                break;
            default:
                break;
        }

        sb.Append("| Item | Value |\n|---|---|\n");
        void Row(string name, string value) => sb.Append("| ").Append(name).Append(" | ").Append(value).Append(" |\n");
        Row("Run type", request.HeldOut ? "Held-out run (one evaluation per frozen detector version)" : "Tuning run");
        Row("Manifest SHA-256", manifestHash ?? "not applicable");
        Row("HUD profile", $"{profile.ProfileId} ({profile.Provenance} thresholds)");
        Row("Text recogniser", request.Ocr == OcrKind.Fake ? "FAKE (tests only)" : "Windows.Media.Ocr, English");
        Row("Owner name", string.IsNullOrWhiteSpace(request.OwnerName) ? "not supplied (learned from the menus only)" : "supplied on the command line for this run; not recorded");
        Row("Detectors asked for", string.Join(", ", detectors.Order(StringComparer.Ordinal)));
        Row("Sessions evaluated", string.Join(", ", sessions.Select(s => s.Metadata.SessionId).Order(StringComparer.Ordinal)));
        foreach (var session in sessions.OrderBy(s => s.Metadata.SessionId, StringComparer.Ordinal))
        {
            var area = areas[session.Metadata.SessionId];
            Row($"Client area, session {session.Metadata.SessionId}", $"{area.Outcome}: {area.Reason}");
        }

        Row("Sessions of the other split (not examined)", ignored.ToString(CultureInfo.InvariantCulture));
        Row("Pictures processed", frames.ToString(CultureInfo.InvariantCulture));
        Row("Text recognitions run / did not run", string.Create(CultureInfo.InvariantCulture, $"{recognizer.Calls - recognizer.Failed} / {recognizer.Failed}"));
        var repeats = sessions.Sum(s => s.Labels.Where(l => l.Detector == "scene").GroupBy(l => (l.TrialId, l.Label)).Sum(g => g.Count() - 1));
        Row("Repeat visits among scene units", string.Create(CultureInfo.InvariantCulture, $"{repeats} (the gate wants one unit per screen class per trial: count that many fewer scene units towards it)"));
        sb.Append("\nUnits are scene ranges and matches the owner confirmed. Each report below is for one context; evidence in one context says nothing about another.\n");
    }

    /// <summary>Writes the report. A held-out report is never replaced by a different one: a re-run must reproduce it exactly (§5). Returns true when the file already held this exact report.</summary>
    private static bool WriteReport(EvaluateRequest request, string text, EvalEnvironment env)
    {
        if (File.Exists(request.OutFile) && request.HeldOut)
        {
            if (string.Equals(File.ReadAllText(request.OutFile), text, StringComparison.Ordinal))
            {
                return true;
            }

            throw new InvalidOperationException($"{request.OutFile} already holds a held-out report and this run differs from it. A held-out set is evaluated once per frozen detector version; if the detector changed, use fresh held-out sessions. The earlier report was kept.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutFile));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(request.OutFile, text, new UTF8Encoding(false));
        _ = env;
        return false;
    }

    private static void WriteTiming(string path, List<double> frameMs, List<double> decodeMs, CountingRecognizer recognizer, int frames, TimeSpan wall)
    {
        static string Stat(List<double> values)
        {
            if (values.Count == 0)
            {
                return "UNKNOWN";
            }

            var sorted = values.Order().ToList();
            var p95 = sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Count), 1, sorted.Count) - 1];
            return string.Create(CultureInfo.InvariantCulture, $"median {sorted[sorted.Count / 2]:0.0} ms, p95 {p95:0.0} ms, max {sorted[^1]:0.0} ms (n={sorted.Count})");
        }

        var sb = new StringBuilder();
        sb.Append("# Timing of this run\n\n");
        sb.Append("Machine dependent and not reproducible, so it is kept out of the report. Resource cost of the detector (DETECTION_VALIDATION section 6) on stated hardware needs the machine described by the person who ran it: UNKNOWN here.\n\n");
        sb.Append("| Item | Value |\n|---|---|\n");
        sb.Append("| Detector time per picture (all six detectors, text recognition included) | ").Append(Stat(frameMs)).Append(" |\n");
        sb.Append("| Picture decode time | ").Append(Stat(decodeMs)).Append(" |\n");
        sb.Append("| Pictures | ").Append(frames.ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        sb.Append("| Text recognitions | ").Append(recognizer.Calls.ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        sb.Append("| Wall time of the reading pass | ").Append(wall.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s |\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>
/// The observations of every picture of one session, collected in one ordered pass the way the live loop would see the pictures. Detector evaluations
/// then look their readings up instead of reading the pictures again, so each picture is recognised once however many detectors are evaluated.
/// </summary>
internal sealed class SessionObservations
{
    private readonly ConditionalWeakTable<ImageBuffer, IReadOnlyList<Observation>> _byImage = [];
    private readonly Dictionary<string, IReadOnlyList<Observation>> _byPath = new(StringComparer.Ordinal);
    private readonly HudEvaluationAdapter _adapter;

    private SessionObservations(HudEvaluationAdapter adapter)
    {
        _adapter = adapter;
        Decoder = new LookupDecoder(this);
    }

    /// <summary>Gets a decoder that returns a one-pixel placeholder tied to the picture's stored observations, for building units without decoding again.</summary>
    public IFrameDecoder Decoder { get; }

    /// <summary>Gets the version of the detector that emits a kind.</summary>
    public int? VersionOf(ObservationKind kind) => _adapter.VersionOf(kind);

    /// <summary>Finds what the detector for <paramref name="kind"/> read on a picture handed out by <see cref="Decoder"/>.</summary>
    public bool TryFind(EvaluationFrame frame, ObservationKind kind, out FramePrediction prediction)
    {
        if (_byImage.TryGetValue(frame.Image, out var observations))
        {
            var o = observations.FirstOrDefault(x => x.Kind == kind);
            prediction = o is null ? new FramePrediction(null, 0) : new FramePrediction(o.Value, o.Score);
            return true;
        }

        prediction = default;
        return false;
    }

    /// <summary>Reads every picture of a session in time order.</summary>
    public static SessionObservations Collect(
        DatasetSession session,
        HudProfile profile,
        HudVocabulary lists,
        ITextRecognizer recognizer,
        string? ownerName,
        IFrameDecoder decoder,
        List<double> frameMs,
        List<double> decodeMs,
        ClientAreaDecision area)
    {
        var m = session.Metadata;
        var cut = new ClientAreaDecoder(decoder, area);
        var context = new ContextKey(profile.ProfileId, $"{area.EffectiveWidth}x{area.EffectiveHeight}", m.Language == "en" ? "en-US" : m.Language, m.GameVersion);
        var adapter = new HudEvaluationAdapter(profile, lists, recognizer, context, ownerName);
        var result = new SessionObservations(adapter);
        var priors = PriorScenes(session);
        var roles = PriorRoles(session);
        for (var i = 0; i < session.Frames.Count; i++)
        {
            var path = Path.Combine(session.Directory, session.Frames[i].File);
            var watch = Stopwatch.StartNew();
            var image = cut.Decode(path);
            decodeMs.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            var observations = adapter.Observe(new EvaluationFrame(session.Frames[i].TimestampUtc, image), priors[i], roles[i]);
            frameMs.Add(watch.Elapsed.TotalMilliseconds);
            result._byPath[path] = observations;
        }

        return result;
    }

    /// <summary>
    /// The scene to pass as "prior" for each picture. Only the settings screen depends on it (in a trial it is the ESC menu, from the main menu it is
    /// another menu), so it is taken from the owner's label of the range before the one the picture is in. The live engine uses the scene it confirmed;
    /// the labels stand in for it here. Illegible ranges give no prior.
    /// </summary>
    internal static SceneKind[] PriorScenes(DatasetSession session)
    {
        var index = session.Frames.Select((f, i) => (f.FrameId, i)).ToDictionary(p => p.FrameId, p => p.i, StringComparer.Ordinal);
        var ranges = session.Labels
            .Where(l => l.Detector == "scene" && l.Legible && Enum.TryParse<SceneKind>(l.Label, out var s) && s != SceneKind.Unknown)
            .Select(l => (From: index[l.FromFrame], To: index[l.ToFrame], Scene: Enum.Parse<SceneKind>(l.Label)))
            .OrderBy(r => r.From)
            .ToList();
        var priors = new SceneKind[session.Frames.Count];
        for (var i = 0; i < priors.Length; i++)
        {
            var containing = ranges.FindIndex(r => r.From <= i && i <= r.To);
            if (containing >= 0)
            {
                priors[i] = containing > 0 ? ranges[containing - 1].Scene : SceneKind.Unknown;
            }
            else
            {
                var before = ranges.FindLastIndex(r => r.To < i);
                priors[i] = before >= 0 ? ranges[before].Scene : SceneKind.Unknown;
            }
        }

        return priors;
    }

    /// <summary>
    /// The role to pass as "prior" for each picture: the owner's role label of the range the picture is in, or the latest one before it (the live engine
    /// confirms the role in the lobby and carries it into the trial). The own-row status detector reads for a survivor and the hook counter for a killer,
    /// so without a legible role label they read nothing. Illegible ranges give no prior.
    /// </summary>
    internal static GameRole[] PriorRoles(DatasetSession session)
    {
        var index = session.Frames.Select((f, i) => (f.FrameId, i)).ToDictionary(p => p.FrameId, p => p.i, StringComparer.Ordinal);
        var ranges = session.Labels
            .Where(l => l.Detector == "role" && l.Legible && Enum.TryParse<GameRole>(l.Label, out var r) && r != GameRole.None)
            .Select(l => (From: index[l.FromFrame], To: index[l.ToFrame], Role: Enum.Parse<GameRole>(l.Label)))
            .OrderBy(r => r.From)
            .ToList();
        var priors = new GameRole[session.Frames.Count];
        for (var i = 0; i < priors.Length; i++)
        {
            var containing = ranges.FindIndex(r => r.From <= i && i <= r.To);
            if (containing >= 0)
            {
                priors[i] = ranges[containing].Role;
            }
            else
            {
                var before = ranges.FindLastIndex(r => r.To < i);
                priors[i] = before >= 0 ? ranges[before].Role : GameRole.None;
            }
        }

        return priors;
    }

    private sealed class LookupDecoder(SessionObservations owner) : IFrameDecoder
    {
        public ImageBuffer Decode(string path)
        {
            var placeholder = ImageBuffer.CreateGray(1, 1);
            owner._byImage.Add(placeholder, owner._byPath[path]);
            return placeholder;
        }
    }
}
