using System.Globalization;
using System.Text.Json;
using DbdPresence.Core.Capture;
using DbdPresence.Eval;
using DbdPresence.Study;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Vision.Tests.Eval;

/// <summary>
/// A throwaway study folder on disk plus a fake machine for the evaluator tool. MECHANICS ONLY: every recording is drawn text on flat colour, written
/// with the study tool's own PNG writer and metadata writer, and read back by a fake recogniser. Nothing here says anything about the game.
/// </summary>
internal sealed class EvalWorkspace : IDisposable
{
    public const string FirstRecording = "2026-10-24_19-30-12";

    private readonly Queue<string?> _input = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public EvalWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbd-presence-eval-" + Guid.NewGuid().ToString("N"));
        Downloads = Path.Combine(Root, "..", "downloads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Downloads);
        AppData = Path.Combine(Root, "..", "appdata-" + Guid.NewGuid().ToString("N"));
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    }

    public string Root { get; }

    public string Downloads { get; }

    /// <summary>Stands for the app's data folder (where status-templates.json is looked for). Created by the tool when it writes.</summary>
    public string AppData { get; }

    public FakeTimeProvider Time { get; }

    public string Output => _out.ToString();

    public string ErrorOutput => _error.ToString();

    public Func<OcrKind, ITextRecognizer?> Recognizer { get; set; } = kind => kind == OcrKind.Fake ? new BitmapFontOcr() : null;

    public void QueueEnter(params string?[] lines)
    {
        foreach (var line in lines)
        {
            _input.Enqueue(line);
        }
    }

    /// <summary>Gets or sets a value indicating whether the evaluator makes the pictures smaller while preparing. Off unless a test is about the compaction.</summary>
    public bool CompactPictures { get; set; }

    public EvalEnvironment Environment() => new()
    {
        CompactPictures = CompactPictures,
        Out = _out,
        Error = _error,
        Time = Time,
        StudyRoot = Root,
        DownloadsFolder = Downloads,
        AppDataFolder = AppData,
        CreateRecognizer = Recognizer,
        ReadLine = () => _input.Count > 0 ? _input.Dequeue() : null,
    };

    public int Run(params string[] args) => EvalApp.Run(args, Environment());

    /// <summary>The nine screens of one synthetic match, four pictures each.</summary>
    public static IReadOnlyList<(string Scene, ImageBuffer Image)> MatchScreens(int width = 1280)
    {
        var rows = HudScenes.SurvivorGame();
        return
        [
            ("MainMenu", HudScenes.MainMenu(width)),
            ("Lobby", HudScenes.Lobby(width)),
            ("InMatchMenu", HudScenes.MatchDetails(width, rows, 1, "ORMOND - MOUNT ORMOND RESORT")),
            ("Results", HudScenes.Scoreboard(width, rows, 1)),
        ];
    }

    /// <summary>
    /// Writes a recording the way the study tool does: <c>frame-NNNNN.png</c> and <c>metadata.json</c> (the study tool's own writer) in a folder named by the start time.
    /// Returns the folder. Pictures are two seconds apart.
    /// </summary>
    public string AddRecording(string name, IReadOnlyList<(string Scene, ImageBuffer Image)> screens, int picturesPerScreen = 4, WindowDisplayMode mode = WindowDisplayMode.FullscreenOrBorderless, bool? clientAreaOnly = null) =>
        AddRecording(name, [.. screens.Select(s => (s.Scene, s.Image, picturesPerScreen))], mode, clientAreaOnly);

    /// <summary>The same with a number of pictures for each screen. A picture that is the same object as an earlier one is encoded once and its bytes are written again.</summary>
    public string AddRecording(string name, IReadOnlyList<(string Scene, ImageBuffer Image, int Pictures)> screens, WindowDisplayMode mode = WindowDisplayMode.FullscreenOrBorderless, bool? clientAreaOnly = null)
    {
        var folder = Path.Combine(Root, name);
        Directory.CreateDirectory(folder);
        var start = DateTimeOffset.ParseExact(name, SessionRetention.NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var metadata = new SessionMetadata
        {
            ToolVersion = "test",
            OsVersion = "test",
            OsBuild = 26100,
            StartedUtc = start,
            FirstFrameWidth = screens[0].Image.Width,
            FirstFrameHeight = screens[0].Image.Height,
            Dpi = 96,
            WindowMode = mode,
            ClientAreaOnly = clientAreaOnly,
            StopReason = "owner pressed Q",
        };
        metadata.DistinctResolutions.Add($"{screens[0].Image.Width}x{screens[0].Image.Height}");
        var index = 1;
        var encoded = new Dictionary<ImageBuffer, byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var (_, image, pictures) in screens)
        {
            for (var i = 0; i < pictures; i++, index++)
            {
                var file = $"frame-{index:D5}.png";
                if (!encoded.TryGetValue(image, out var bytes))
                {
                    var pixels = image.ToPixelBuffer();
                    bytes = PngEncoder.Encode(pixels.Width, pixels.Height, pixels.Bgra.Span);
                    encoded[image] = bytes;
                }

                var size = (image.Width, image.Height);
                File.WriteAllBytes(Path.Combine(folder, file), bytes);
                metadata.Frames.Add(new DbdPresence.StudyCapture.FrameRecord(index, file, start.AddSeconds(2 * (index - 1)), size.Width, size.Height, bytes.Length, "scheduled"));
            }
        }

        File.WriteAllText(Path.Combine(folder, "metadata.json"), metadata.ToJson());
        return folder;
    }

    /// <summary>Writes the plan of a recording as one range per screen (so a test does not depend on the brightness proposals).</summary>
    public static ReviewPlan WritePlan(string folder, int screens, int picturesPerScreen = 4)
    {
        var plan = new ReviewPlan(
            Path.GetFileName(folder),
            [.. Enumerable.Range(0, screens).Select(i => new PlanSegment(i, (i * picturesPerScreen) + 1, (i + 1) * picturesPerScreen, "19:30:12", picturesPerScreen * 2.0))]);
        File.WriteAllText(Path.Combine(folder, SessionFiles.Plan), plan.ToJson());
        return plan;
    }

    /// <summary>Answers for a one-match recording of <see cref="MatchScreens"/>.</summary>
    public static string Answers(string sessionId, IReadOnlyList<string> scenes, string role = "Survivor", string character = "dwight-fairfield", string map = "ormond/mount-ormond-resort", string killer = "the-lich", string kind = "labels", IEnumerable<int>? only = null)
    {
        var include = only?.ToHashSet() ?? [.. Enumerable.Range(0, scenes.Count)];
        return JsonText.Compact(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteString("kind", kind);
            w.WriteString("sessionId", sessionId);
            w.WriteStartArray("segments");
            for (var i = 0; i < scenes.Count; i++)
            {
                if (!include.Contains(i))
                {
                    continue;
                }

                w.WriteStartObject();
                w.WriteNumber("i", i);
                w.WriteString("scene", scenes[i]);
                w.WriteBoolean("matchStart", kind == "labels" && i == 0);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("matches");
            w.WriteStartObject();
            w.WriteNumber("startIndex", 0);
            w.WriteString("role", role);
            w.WriteString("character", character);
            w.WriteString("map", map);
            w.WriteString("result", "Escaped");
            w.WriteString("killer", killer);
            w.WriteString("queue", string.Empty);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>Prepares every recording, labels it with the given answers and (after a day) passes the blind re-check with identical answers.</summary>
    public void LabelAndRecheck(string folder, IReadOnlyList<string> scenes, string split)
    {
        var id = Path.GetFileName(folder);
        var file = Path.Combine(Root, "answers-" + id + ".json");
        File.WriteAllText(file, Answers(id, scenes));
        Assert.Equal(0, Run("prepare", "--split", split));
        Assert.Equal(0, Run("apply", "--session", id, "--answers", file));
        Time.Advance(TimeSpan.FromHours(25));

        var vocabulary = DbdPresence.Vision.Text.Vocabulary.LoadEmbedded();
        _ = ReviewSessions.BuildRecheckPage(folder, vocabulary, new DbdPresence.Vision.Evaluation.ExtensionFrameDecoder());
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckPlan)));
        var picked = plan.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var again = Path.Combine(Root, "recheck-" + id + ".json");
        File.WriteAllText(again, Answers(id, scenes, kind: "recheck", only: picked));
        Assert.Equal(0, Run("apply", "--session", id, "--answers", again, "--recheck"));
    }

    public IEnumerable<string> AllText()
    {
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".png", StringComparison.Ordinal)))
        {
            yield return File.ReadAllText(path);
        }
    }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
        Directory.Delete(Downloads, recursive: true);
        if (Directory.Exists(AppData))
        {
            Directory.Delete(AppData, recursive: true);
        }

        _out.Dispose();
        _error.Dispose();
    }
}
