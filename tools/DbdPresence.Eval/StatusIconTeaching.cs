using System.Globalization;
using System.Text;
using DbdPresence.Core.Detection;
using DbdPresence.Study;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Matching;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Eval;

/// <summary>
/// Teaches the app the owner's four status icons (FR-AUTO-03, ADR-0004, DETECTION_VALIDATION §7 and §12). It reads the owner's own icon answers
/// (<c>own-status</c> labels, made on the review page), cuts the icon cell <c>hud.icon.0</c> out of the labelled pictures (the game's own picture, without a
/// window title bar), and writes <c>status-templates.json</c> where the app loads it. Only <b>tuning</b> recordings are used: a held-out recording is never
/// read here, so the later check on held-out recordings stays independent of the pictures the detector was taught with. A pack picture is never a template;
/// the file holds small grey crops of the owner's own screen and stays on this PC. Nothing is written unless every class is covered well enough, so the app
/// never gets a partial set (a detector given an incomplete set stays silent anyway). Synthetic pictures prove the mechanics only; how well the learned
/// pictures read the real game is UNKNOWN until the evaluator has measured it on held-out recordings.
/// </summary>
internal static class StatusIconTeaching
{
    /// <summary>The file name the app looks for in its data folder.</summary>
    public const string FileName = "status-templates.json";

    /// <summary>The fewest distinct labelled ranges each class needs (an initial choice, not a measurement: one picture is not evidence that the icon looks the same every time).</summary>
    public const int MinSegmentsPerClass = 3;

    /// <summary>The fewest different recordings each class must come from (a second sitting guards against learning one lighting, one HUD scale, one moment).</summary>
    public const int MinSessionsPerClass = 2;

    /// <summary>The width every learned picture is scaled to. The detector scales the live cell to the picture's size before comparing, so recordings at different sizes teach the same kind of picture.</summary>
    public const int PictureWidth = 48;

    private static readonly string[] s_classes = [OwnStatuses.Portrait, OwnStatuses.Hooked, OwnStatuses.Dying, OwnStatuses.Dead];

    private sealed record Candidate(string Session, string Class, ImageBuffer Picture);

    /// <summary>Runs the command. Returns 0 when the file was written and 1 when it was not (and says why).</summary>
    /// <param name="root">The study folder.</param>
    /// <param name="outFile">Where to write, or null for the app's data folder.</param>
    /// <param name="env">The machine.</param>
    public static int Run(string root, string? outFile, EvalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        try
        {
            return RunCore(root, outFile, env);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            env.Error.WriteLine("Nothing was written: " + ex.Message);
            return 1;
        }
    }

    private static int RunCore(string root, string? outFile, EvalEnvironment env)
    {
        var path = outFile ?? (env.AppDataFolder is { Length: > 0 } data
            ? Path.Combine(data, FileName)
            : throw new InvalidOperationException("The app's data folder is not known here. Give a place with --out <file>."));
        var rect = (HudProfile.Catalogue.Region(HudRegionIds.HudIcon(0)) ?? throw new InvalidOperationException("The HUD profile has no icon cell.")).Rect;
        var minScore = HudProfile.Catalogue.Thresholds.StatusMinScore;

        var all = StudySessions.LoadPrepared(root);
        var tuning = all.Where(s => s.Metadata.Split == "tuning").ToList();
        var heldOut = all.Count - tuning.Count;
        if (heldOut > 0)
        {
            env.Out.WriteLine($"{heldOut} held-out recordings are not used here, so that the later check on them stays independent.");
        }

        var decoder = new ExtensionFrameDecoder();
        var candidates = new List<Candidate>();
        var changing = 0;
        var unusable = 0;
        foreach (var session in tuning)
        {
            var id = session.Metadata.SessionId;
            var answers = session.Labels.Where(l => l.Type == "segment" && l.Detector == "own-status" && l.Legible && OwnStatuses.IsValid(l.Label)).ToList();
            if (answers.Count == 0)
            {
                continue;
            }

            if (session.Labels.Any(l => l.ReviewStatus == "disputed"))
            {
                env.Out.WriteLine($"Recording {id} is skipped: its second look disagreed with some answers. Run option 1 to rework it first.");
                continue;
            }

            if (session.Labels.Any(l => l.Source == LabelSources.Reviewer && l.ReviewStatus != "rechecked"))
            {
                env.Out.WriteLine($"Recording {id} is skipped: the reviewer's labels count only after your quick spot-check (option 1).");
                continue;
            }

            if (StudyFiles.MissingFrames(session) > 0)
            {
                env.Out.WriteLine($"Recording {id} is skipped: its pictures were deleted to free up space (or removed by hand), so its icons cannot be read again.");
                continue;
            }

            var area = ClientAreaSessions.Decide(session, decoder);
            var index = session.Frames.Select((f, i) => (f.FrameId, i)).ToDictionary(p => p.FrameId, p => p.i, StringComparer.Ordinal);
            foreach (var answer in answers)
            {
                var crops = new List<ImageBuffer>();
                foreach (var frame in SegmentProposer.ThumbnailFrames(index[answer.FromFrame], index[answer.ToFrame]))
                {
                    var picture = ClientAreaNormaliser.Apply(decoder.Decode(Path.Combine(session.Directory, session.Frames[frame].File)), area).Crop(rect);
                    crops.Add(Canonical(picture));
                }

                if (crops.Any(IsFlat))
                {
                    unusable++;
                    continue;
                }

                if (!Consistent(crops, minScore))
                {
                    changing++;
                    continue;
                }

                candidates.Add(new Candidate(id, answer.Label, crops[crops.Count / 2]));
            }
        }

        var lines = new List<string>();
        var enough = true;
        foreach (var name in s_classes)
        {
            var mine = candidates.Where(c => c.Class == name).ToList();
            var sessions = mine.Select(c => c.Session).Distinct(StringComparer.Ordinal).Count();
            var good = mine.Count >= MinSegmentsPerClass && sessions >= MinSessionsPerClass;
            enough &= good;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {Plain(name)}: {mine.Count} labelled ranges from {sessions} recordings{(good ? string.Empty : $" (needs at least {MinSegmentsPerClass} ranges from at least {MinSessionsPerClass} recordings)")}"));
        }

        env.Out.WriteLine("What your labelled practice recordings give:");
        foreach (var line in lines)
        {
            env.Out.WriteLine(line);
        }

        if (changing > 0)
        {
            env.Out.WriteLine($"  {changing} ranges were left out because their pictures differ from each other (the icon changed inside the range, or the cell is not where the tool looks).");
        }

        if (unusable > 0)
        {
            env.Out.WriteLine($"  {unusable} ranges were left out because the cell showed no picture at all.");
        }

        if (!enough)
        {
            env.Error.WriteLine("Nothing was written: not enough labelled icons yet. Every one of the four icons needs ranges from at least two recordings. Play and label more matches (on the review page, answer \"your own icon\" for the rows in a trial where you played survivor), keep them as practice recordings (not held-out ones), then choose this again. Until then the status icon stays off.");
            return 1;
        }

        var chosen = Choose(candidates);
        var clash = Lookalike(chosen, minScore);
        if (clash is not null)
        {
            env.Error.WriteLine($"Nothing was written: {clash} Either an answer was wrong, or the tool is not cutting the picture where your icon is.");
            return 1;
        }

        var set = new OwnStatusTemplates(chosen.GroupBy(c => c.Class, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<ImageBuffer>)[.. g.Select(c => c.Picture)], StringComparer.Ordinal));
        var json = set.ToJson();
        if (OwnStatusTemplates.TryParse(json, out _) is not { IsComplete: true })
        {
            throw new InvalidOperationException("The learned set could not be read back as a complete set, so it was not saved.");
        }

        var replaced = File.Exists(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);

        env.Out.WriteLine();
        env.Out.WriteLine($"Done. The app now has {string.Join(", ", s_classes.Select(n => $"{chosen.Count(c => c.Class == n)} {Plain(n).ToLowerInvariant()}"))} pictures to compare with{(replaced ? " (they replace the earlier ones)" : string.Empty)}.");
        env.Out.WriteLine($"Saved: {path}");
        env.Out.WriteLine("The app reads this file when it starts: close DBD Presence and open it again. To use it on another PC, copy that one file into the same place there (%LOCALAPPDATA%\\DBDPresence.Data).");
        env.Out.WriteLine("The file holds small pictures of your own screen. It stays on your PC: do not share it and do not put it in the repository.");
        env.Out.WriteLine("This does not turn anything on. The status icon stays off for Discord until the check on held-out recordings has been run and passed; how well these pictures read the real game is UNKNOWN until then.");
        SpaceFreeing.AfterTeachStatus(root, env);
        return 0;
    }

    /// <summary>
    /// Picks at most <see cref="OwnStatusTemplates.MaxPerClass"/> pictures per class, taking turns between the recordings so that no single sitting fills the class.
    /// </summary>
    private static List<Candidate> Choose(IReadOnlyList<Candidate> candidates)
    {
        var chosen = new List<Candidate>();
        foreach (var name in s_classes)
        {
            var bySession = candidates.Where(c => c.Class == name).GroupBy(c => c.Session, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
            for (var round = 0; chosen.Count(c => c.Class == name) < OwnStatusTemplates.MaxPerClass && bySession.Any(l => round < l.Count); round++)
            {
                foreach (var list in bySession.Where(l => round < l.Count))
                {
                    if (chosen.Count(c => c.Class == name) < OwnStatusTemplates.MaxPerClass)
                    {
                        chosen.Add(list[round]);
                    }
                }
            }
        }

        return chosen;
    }

    /// <summary>Returns a plain sentence when pictures of two different classes look alike (at or above the detector's own reading score), otherwise null.</summary>
    private static string? Lookalike(IReadOnlyList<Candidate> chosen, double minScore)
    {
        for (var i = 0; i < chosen.Count; i++)
        {
            for (var j = i + 1; j < chosen.Count; j++)
            {
                if (chosen[i].Class != chosen[j].Class && Correlation(chosen[i].Picture, chosen[j].Picture) >= minScore)
                {
                    return $"a \"{Plain(chosen[i].Class).ToLowerInvariant()}\" picture and a \"{Plain(chosen[j].Class).ToLowerInvariant()}\" picture look almost the same, so the app could not tell them apart.";
                }
            }
        }

        return null;
    }

    /// <summary>Whether the pictures of one range agree with each other, at the score the detector itself needs to read a class.</summary>
    private static bool Consistent(List<ImageBuffer> crops, double minScore)
    {
        for (var i = 0; i < crops.Count; i++)
        {
            for (var j = i + 1; j < crops.Count; j++)
            {
                if (Correlation(crops[i], crops[j]) < minScore)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static double Correlation(ImageBuffer a, ImageBuffer b) => TemplateMatcher.Match(a, b)?.Score ?? double.NegativeInfinity;

    private static bool IsFlat(ImageBuffer picture) => picture.Pixels.IndexOfAnyExcept(picture.Pixels[0]) < 0;

    private static ImageBuffer Canonical(ImageBuffer crop)
    {
        var gray = crop.ToGray();
        var height = Math.Clamp((int)Math.Round((double)PictureWidth * gray.Height / Math.Max(1, gray.Width), MidpointRounding.AwayFromZero), TemplateMatcher.MinimumTemplateSide, OwnStatusTemplates.MaxSide);
        return gray.Resize(PictureWidth, height);
    }

    private static string Plain(string name) => name switch
    {
        OwnStatuses.Portrait => "Normal",
        OwnStatuses.Hooked => "Hooked",
        OwnStatuses.Dying => "Down",
        _ => "Dead",
    };
}
