using System.Globalization;
using DbdPresence.Study;
using DbdPresence.Vision.Text;

namespace DbdPresence.Eval;

/// <summary>
/// The command-line front of the owner-run evaluator and labelling helper. Everything it needs from the machine arrives in an <see cref="EvalEnvironment"/>,
/// so the same code runs under test with a fake recogniser and synthetic pictures. It has no network code and starts no process.
/// </summary>
internal static class EvalApp
{
    private static readonly Dictionary<string, CommandSpec> s_specs = new(StringComparer.Ordinal)
    {
        ["label"] = new(["root", "window-mode", "game-version", "hud-scale", "menu-scale"], ["ask-new"]),
        ["prepare"] = new(["root", "split", "window-mode", "game-version", "hud-scale", "menu-scale"], []),
        ["apply"] = new(["root", "session", "answers"], ["recheck"]),
        ["export"] = new(["root", "max-mb"], [], Positionals: 1),
        ["import-labels"] = new(["root"], [], Positionals: 2),
        ["teach-status"] = new(["root", "out"], []),
        ["manifest"] = new(["root", "out"], []),
        ["evaluate"] = new(["root", "out", "split", "manifest", "manifest-hash", "owner-name", "detector", "ocr"], ["keep-pictures"]),
        ["help"] = new([], []),
    };

    /// <summary>Runs a command line and returns the process exit code: 0 done, 1 refused or failed, 2 usage error.</summary>
    public static int Run(IReadOnlyList<string> args, EvalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);
        var command = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : "label";
        var rest = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args.Skip(1).ToList() : [.. args];
        if (command is "--help" or "-h" || !s_specs.TryGetValue(command, out var spec))
        {
            PrintUsage(env.Out);
            return command is "help" or "--help" or "-h" ? 0 : 2;
        }

        if (command == "help")
        {
            PrintUsage(env.Out);
            return 0;
        }

        if (!CommandLine.TryParse(command, rest, spec, out var line, out var error))
        {
            env.Error.WriteLine(error);
            env.Error.WriteLine("Run 'DbdPresence.Eval help' for the commands.");
            return 2;
        }

        try
        {
            return Dispatch(line!, env);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            env.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Dispatch(CommandLine line, EvalEnvironment env)
    {
        var root = line.Value("root") ?? env.StudyRoot;
        switch (line.Command)
        {
            case "label":
                return GuidedLabel.Run(root, PrepareOptionsOf(line, split: null), env, askBeforeHandLabelling: line.Flag("ask-new"));
            case "prepare":
                return Prepare(root, PrepareOptionsOf(line, line.Value("split")), env);
            case "apply":
                return Apply(root, line, env);
            case "export":
                return ReviewCommands.Export(root, line, env);
            case "import-labels":
                return ReviewCommands.ImportLabels(root, line, env);
            case "teach-status":
                return StatusIconTeaching.Run(root, line.Value("out"), env);
            case "manifest":
                return Manifest(root, line, env);
            default:
                return Evaluate(root, line, env);
        }
    }

    private static PrepareOptions PrepareOptionsOf(CommandLine line, string? split)
    {
        if (split is not null and not ("tuning" or "heldout"))
        {
            throw new FormatException("--split must be tuning or heldout.");
        }

        var window = line.Value("window-mode");
        if (window is not null and not ("Windowed" or "Borderless" or "Fullscreen"))
        {
            throw new FormatException("--window-mode must be Windowed, Borderless or Fullscreen.");
        }

        return new PrepareOptions
        {
            Split = split,
            WindowMode = window,
            GameVersion = line.Value("game-version") ?? "unknown",
            HudScale = Scale(line.Value("hud-scale"), "--hud-scale"),
            MenuScale = Scale(line.Value("menu-scale"), "--menu-scale"),
        };
    }

    private static double Scale(string? text, string name)
    {
        if (text is null)
        {
            return 1.0;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 4
            ? value
            : throw new FormatException($"{name} must be a number above 0, for example 1 or 1.25.");
    }

    private static int Prepare(string root, PrepareOptions options, EvalEnvironment env)
    {
        var folders = StudySessions.RecordingFolders(root);
        var prepared = folders.Count(f => File.Exists(Path.Combine(f, SessionFiles.Session)));
        var done = 0;
        var failed = 0;
        foreach (var folder in folders.Where(f => !File.Exists(Path.Combine(f, SessionFiles.Session))))
        {
            try
            {
                if (StudySessions.Prepare(folder, options, prepared) is { } result)
                {
                    prepared++;
                    done++;
                    env.Out.WriteLine($"{result.SessionId}: {result.Frames} pictures, filed as {result.Split}; {result.Note}.");
                }
            }
            catch (FormatException ex)
            {
                failed++;
                env.Error.WriteLine($"Skipped {Path.GetFileName(folder)}: {ex.Message}");
            }
        }

        env.Out.WriteLine(done == 0 ? "Nothing new to prepare." : $"{done} recordings prepared.");
        FrameCompaction.RunAll(root, env);
        return failed == 0 ? 0 : 1;
    }

    private static int Apply(string root, CommandLine line, EvalEnvironment env)
    {
        var session = line.Value("session") ?? throw new FormatException("apply needs --session <id>.");
        var answers = line.Value("answers") ?? throw new FormatException("apply needs --answers <file>.");
        var folder = Path.Combine(root, session);
        if (!File.Exists(Path.Combine(folder, SessionFiles.Session)))
        {
            throw new FormatException("That session is not prepared. Run 'prepare' first.");
        }

        var vocabulary = Vocabulary.LoadEmbedded();
        var json = File.ReadAllText(answers);
        if (line.Flag("recheck"))
        {
            var outcome = ReviewSessions.ApplyRecheck(folder, json, vocabulary, env.Time.GetUtcNow());
            env.Out.WriteLine($"Second look: {outcome.Status}. Scene answers compared: {outcome.SceneAgreements}, differing: {outcome.SceneDisagreements}; match answers differing: {outcome.FactDisagreements.Count}; kappa {(outcome.Kappa is { } k ? k.ToString("0.00", CultureInfo.InvariantCulture) : "not defined (every answer was the same class)")}.");
            SpaceFreeing.AfterSpotCheck(folder, outcome, env);
            return 0;
        }

        var matches = ReviewSessions.ApplyLabels(folder, json, vocabulary, env.Time.GetUtcNow());
        env.Out.WriteLine($"Labels written: {matches} matches.");
        return 0;
    }

    private static int Manifest(string root, CommandLine line, EvalEnvironment env)
    {
        var path = line.Value("out") ?? throw new FormatException("manifest needs --out <file>.");
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"{path} already exists. A committed manifest is never replaced; use a new file for a new detector version.");
        }

        var held = StudySessions.LoadPrepared(root).Where(s => s.Metadata.Split == "heldout").ToList();
        if (held.Count == 0)
        {
            throw new InvalidOperationException("There are no held-out sessions.");
        }

        var notChecked = held.Where(s => s.Labels.Count == 0 || s.Labels.Any(l => l.ReviewStatus != "rechecked")).Select(s => s.Metadata.SessionId).ToList();
        if (notChecked.Count > 0)
        {
            throw new InvalidOperationException($"These held-out sessions have not passed the blind re-check, so their labels are not final: {string.Join(", ", notChecked)}.");
        }

        var manifest = ManifestFile.For(held);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, manifest.ToText());
        env.Out.WriteLine($"Manifest of {held.Count} held-out sessions written to {path}.");
        env.Out.WriteLine($"Hash: {manifest.Hash}");
        env.Out.WriteLine("Commit this file and write the hash in the commit message BEFORE you run 'evaluate'. Changing any of these sessions' labels afterwards makes the evaluation refuse to run.");
        return 0;
    }

    private static int Evaluate(string root, CommandLine line, EvalEnvironment env)
    {
        var split = line.Value("split") ?? "heldout";
        if (split is not ("heldout" or "tuning"))
        {
            throw new FormatException("--split must be heldout or tuning.");
        }

        var ocr = (line.Value("ocr") ?? "windows") switch
        {
            "windows" => OcrKind.Windows,
            "fake" => OcrKind.Fake,
            _ => throw new FormatException("--ocr must be windows (the fake recogniser is for tests only)."),
        };
        var detectors = line.Value("detector")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var request = new EvaluateRequest(
            root,
            line.Value("out") ?? throw new FormatException("evaluate needs --out <report file>."),
            split == "heldout",
            line.Value("manifest"),
            line.Value("manifest-hash"),
            line.Value("owner-name"),
            detectors,
            ocr,
            line.Flag("keep-pictures"));
        return EvaluateCommand.Run(request, env);
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("DBD Presence evaluator and labelling helper (test tool, not part of the app)");
        output.WriteLine();
        output.WriteLine("  DbdPresence.Eval                     Label your recordings (the usual way: just double-click).");
        output.WriteLine("  DbdPresence.Eval prepare             Only get new recordings ready; no pages.   [--split tuning|heldout --window-mode ...]");
        output.WriteLine("  DbdPresence.Eval apply               Apply saved answers without the guided flow.  --session ID --answers FILE [--recheck]");
        output.WriteLine("  DbdPresence.Eval export              Make a small review file (<session>-review.zip, in pieces under 25 MB when larger) to send for labelling.  SESSION [--max-mb 140]");
        output.WriteLine("  DbdPresence.Eval import-labels       Take in labels made elsewhere; the owner's blind spot-check comes next.  SESSION LABELS.json");
        output.WriteLine("  DbdPresence.Eval teach-status        Teach the app your four status icons from your labelled practice recordings (tuning ones only).  [--out FILE]");
        output.WriteLine("  DbdPresence.Eval manifest           List the held-out sessions to commit before evaluating.  --out FILE");
        output.WriteLine("  DbdPresence.Eval evaluate            Measure the detectors.  --out FILE [--split heldout|tuning] --manifest FILE --manifest-hash HASH [--owner-name NAME] [--detector scene,role,...] [--keep-pictures]");
        output.WriteLine("                                       A held-out run deletes the held-out pictures when its report is written (except those of wrong or missed units); --keep-pictures keeps them, for a repeat run.");
        output.WriteLine();
        output.WriteLine("SESSION is a recording's folder name (for example 2026-10-24_19-30-12) or the path of its folder.");
        output.WriteLine("Every command takes --root <folder> (default: Documents\\DBD Presence Study). The tool has no network code and never starts another program.");
        output.WriteLine("Your in-game name is only ever given on the command line (--owner-name), is kept in memory for that run, and is never written to a file.");
    }
}
