using System.Globalization;
using System.Text.Json;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Study;

/// <summary>One proposed range of pictures (a navigation aid, see <see cref="SegmentProposer"/>).</summary>
/// <param name="Index">Position in the plan, from zero.</param>
/// <param name="FromFrame">First picture, as its number in the session (1 is the first picture).</param>
/// <param name="ToFrame">Last picture (inclusive).</param>
/// <param name="StartLocal">The clock time of the first picture, for the owner to recognise it.</param>
/// <param name="Seconds">How long the range lasts.</param>
internal sealed record PlanSegment(int Index, int FromFrame, int ToFrame, string StartLocal, double Seconds);

/// <summary>The proposed ranges of one session. Written when the review page is made, so applying the answers never has to look at the pictures again.</summary>
internal sealed record ReviewPlan(string SessionId, IReadOnlyList<PlanSegment> Segments)
{
    /// <summary>Gets the dataset frame id of a picture number.</summary>
    public static string FrameId(int number) => number.ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>Serialises the plan.</summary>
    public string ToJson() => JsonText.Compact(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteString("sessionId", SessionId);
        w.WriteStartArray("segments");
        foreach (var s in Segments)
        {
            w.WriteStartObject();
            w.WriteNumber("index", s.Index);
            w.WriteNumber("from", s.FromFrame);
            w.WriteNumber("to", s.ToFrame);
            w.WriteString("start", s.StartLocal);
            w.WriteNumber("seconds", s.Seconds);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>Reads a plan.</summary>
    public static ReviewPlan Parse(string json)
    {
        using var document = JsonText.ReadDocumentFromText(json);
        var root = document.RootElement;
        var segments = new List<PlanSegment>();
        foreach (var s in Array(root, "segments"))
        {
            segments.Add(new PlanSegment(IntOf(s, "index"), IntOf(s, "from"), IntOf(s, "to"), StringOf(s, "start"), s.GetProperty("seconds").GetDouble()));
        }

        var plan = new ReviewPlan(StringOf(root, "sessionId"), segments);
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Index != i || segments[i].FromFrame < 1 || segments[i].ToFrame < segments[i].FromFrame || (i > 0 && segments[i].FromFrame != segments[i - 1].ToFrame + 1))
            {
                throw new FormatException("The review plan is not a gap-free list of picture ranges.");
            }
        }

        return plan;
    }

    internal static JsonElement.ArrayEnumerator Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw new FormatException($"'{name}' must be a list.");

    internal static int IntOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : throw new FormatException($"'{name}' must be a whole number.");

    internal static string StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException($"'{name}' must be text.");
}

/// <summary>The owner's answer for one proposed range.</summary>
/// <param name="Index">The range's position in the plan.</param>
/// <param name="Scene">A scene class, or <see cref="ReviewVocabulary.NoScene"/> when the pictures are none of them or show several.</param>
/// <param name="MatchStart">Whether a new match starts with this range (label pass only).</param>
/// <param name="OwnStatus">What the owner's own icon in the first row of the survivor list shows through the whole range (one of <see cref="ReviewVocabulary.OwnStatusAnswers"/>), or empty when not answered. Only for in-trial ranges of a survivor match (label pass only).</param>
internal sealed record SegmentAnswer(int Index, string Scene, bool MatchStart, string OwnStatus = "");

/// <summary>The owner's answers for one match. Empty text means "not recorded", "?" means "could not be told from the pictures".</summary>
internal sealed record MatchAnswer(int StartIndex, string Role, string Character, string Map, string Result, string Killer, string Queue);

/// <summary>The answers saved by a review page.</summary>
/// <param name="Kind">"labels" for the first pass, "recheck" for the blind second pass.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Segments">Answers per range.</param>
/// <param name="Matches">Answers per match.</param>
/// <param name="AppliedAtUtc">When the labels were made from these answers (set by the tool, null in a file straight from the page).</param>
/// <param name="Source">Who made the answers: <see cref="LabelSources.Owner"/> (the default) or <see cref="LabelSources.Reviewer"/> (imported). The tool sets it when it applies a file and never takes it from the file.</param>
internal sealed record ReviewAnswers(string Kind, string SessionId, IReadOnlyList<SegmentAnswer> Segments, IReadOnlyList<MatchAnswer> Matches, string? AppliedAtUtc, string Source = LabelSources.Owner)
{
    public const string LabelsKind = "labels";
    public const string RecheckKind = "recheck";

    /// <summary>Serialises the answers.</summary>
    public string ToJson() => JsonText.Compact(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteString("kind", Kind);
        w.WriteString("sessionId", SessionId);
        if (AppliedAtUtc is not null)
        {
            w.WriteString("appliedAtUtc", AppliedAtUtc);
        }

        if (Source != LabelSources.Owner)
        {
            w.WriteString("source", Source);
        }

        w.WriteStartArray("segments");
        foreach (var s in Segments)
        {
            w.WriteStartObject();
            w.WriteNumber("i", s.Index);
            w.WriteString("scene", s.Scene);
            w.WriteBoolean("matchStart", s.MatchStart);
            if (s.OwnStatus.Length > 0)
            {
                w.WriteString("ownStatus", s.OwnStatus);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("matches");
        foreach (var m in Matches)
        {
            w.WriteStartObject();
            w.WriteNumber("startIndex", m.StartIndex);
            w.WriteString("role", m.Role);
            w.WriteString("character", m.Character);
            w.WriteString("map", m.Map);
            w.WriteString("result", m.Result);
            w.WriteString("killer", m.Killer);
            w.WriteString("queue", m.Queue);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>
    /// Reads and checks answers: every scene is one of the known classes, every character, map, killer and queue is in the closed lists (or empty or
    /// "?"), and (label pass) every range of the plan is answered exactly once.
    /// </summary>
    /// <exception cref="FormatException">An answer is not valid; the message says which kind of answer.</exception>
    public static ReviewAnswers Parse(string json, HudVocabulary vocabulary, ReviewPlan plan, string expectedKind)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(plan);
        using var document = JsonText.ReadDocumentFromText(json);
        var root = document.RootElement;
        var kind = ReviewPlan.StringOf(root, "kind");
        if (kind != expectedKind)
        {
            throw new FormatException($"These are '{kind}' answers but '{expectedKind}' answers were expected.");
        }

        var sessionId = ReviewPlan.StringOf(root, "sessionId");
        if (sessionId != plan.SessionId)
        {
            throw new FormatException("These answers belong to another session.");
        }

        var segments = new List<SegmentAnswer>();
        foreach (var s in ReviewPlan.Array(root, "segments"))
        {
            var scene = ReviewPlan.StringOf(s, "scene");
            if (!ReviewVocabulary.IsSceneAnswer(scene))
            {
                throw new FormatException("A scene answer is not one of the known screens.");
            }

            var ownStatus = string.Empty;
            if (s.TryGetProperty("ownStatus", out var icon))
            {
                ownStatus = icon.ValueKind == JsonValueKind.String ? icon.GetString()! : throw new FormatException("An icon answer must be text.");
            }

            if (!ReviewVocabulary.IsOwnStatusAnswer(ownStatus))
            {
                throw new FormatException("An icon answer is not Portrait, Hooked, Dying, Dead or '?'.");
            }

            if (ownStatus.Length > 0 && (scene != ReviewVocabulary.InTrial || kind != LabelsKind))
            {
                throw new FormatException("An icon answer belongs only to a picture range in the trial, in the first labelling.");
            }

            segments.Add(new SegmentAnswer(ReviewPlan.IntOf(s, "i"), scene, s.TryGetProperty("matchStart", out var ms) && ms.ValueKind == JsonValueKind.True, ownStatus));
        }

        if (segments.Any(s => s.Index < 0 || s.Index >= plan.Segments.Count) || segments.Select(s => s.Index).Distinct().Count() != segments.Count)
        {
            throw new FormatException("A scene answer points at a picture range that is not in the plan, or twice at the same one.");
        }

        if (kind == LabelsKind && segments.Count != plan.Segments.Count)
        {
            throw new FormatException("Every picture range needs an answer.");
        }

        var matches = new List<MatchAnswer>();
        foreach (var m in ReviewPlan.Array(root, "matches"))
        {
            var answer = new MatchAnswer(
                ReviewPlan.IntOf(m, "startIndex"),
                ReviewPlan.StringOf(m, "role"),
                ReviewPlan.StringOf(m, "character"),
                ReviewPlan.StringOf(m, "map"),
                ReviewPlan.StringOf(m, "result"),
                ReviewPlan.StringOf(m, "killer"),
                ReviewPlan.StringOf(m, "queue"));
            ReviewVocabulary.Validate(answer, vocabulary);
            if (answer.Character.Length == 0 || answer.Map.Length == 0)
            {
                throw new FormatException("Every match needs a character and a map answer (or '?' when the pictures do not show it).");
            }

            matches.Add(answer);
        }

        if (matches.Any(m => m.StartIndex < 0 || m.StartIndex >= plan.Segments.Count) || matches.Select(m => m.StartIndex).Distinct().Count() != matches.Count)
        {
            throw new FormatException("A match answer points at a picture range that is not in the plan, or twice at the same one.");
        }

        if (kind == LabelsKind)
        {
            var starts = segments.Where(s => s.MatchStart).Select(s => s.Index).Order().ToList();
            if (!starts.SequenceEqual(matches.Select(m => m.StartIndex).Order()))
            {
                throw new FormatException("Every match start needs its match answers, and every match answer a match start.");
            }

            foreach (var answered in segments.Where(s => s.OwnStatus.Length > 0))
            {
                var start = starts.LastOrDefault(i => i <= answered.Index, -1);
                if (start < 0 || matches.Single(m => m.StartIndex == start).Role != "Survivor")
                {
                    throw new FormatException("An icon answer belongs only to a survivor match: say that you played survivor on the row where the match starts.");
                }
            }
        }

        var applied = root.TryGetProperty("appliedAtUtc", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null;
        var source = root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String && src.GetString() == LabelSources.Reviewer ? LabelSources.Reviewer : LabelSources.Owner;
        return new ReviewAnswers(kind, sessionId, [.. segments.OrderBy(s => s.Index)], [.. matches.OrderBy(m => m.StartIndex)], applied, source);
    }
}

/// <summary>Who made a set of labels (DETECTION_VALIDATION §16.1).</summary>
internal static class LabelSources
{
    /// <summary>The owner, from the review page.</summary>
    public const string Owner = "owner";

    /// <summary>A reviewer who looked at the review file (whole pictures and text crops, never a detector's reading); the owner's blind spot-check decides whether they count.</summary>
    public const string Reviewer = "reviewer";
}

/// <summary>The fixed answer lists of the review pages.</summary>
internal static class ReviewVocabulary
{
    /// <summary>The scene answer for pictures that are none of the screens, mixed, or a loading screen: excluded from the counts.</summary>
    public const string NoScene = "None";

    /// <summary>Scene classes the owner can choose (DETECTION_VALIDATION §15), with the plain wording of the page.</summary>
    public static readonly IReadOnlyList<(string Id, string Text)> Scenes =
    [
        ("MainMenu", "Main menu"),
        ("Other", "Another menu (play modes, store, settings)"),
        ("Lobby", "Lobby, before or after searching"),
        ("Matchmaking", "Searching for a match"),
        ("InTrial", "In the trial"),
        ("InMatchMenu", "TAB or ESC menu in a trial"),
        ("Spectating", "Spectating"),
        ("Results", "Results screen or scoreboard"),
        (NoScene, "None of these, loading, or mixed"),
    ];

    /// <summary>The scene answer of a trial.</summary>
    public const string InTrial = "InTrial";

    /// <summary>
    /// What the owner's own icon can show (the small picture left of the owner's name in the first row of the survivor list during a trial), with the plain
    /// wording of the page. "?" means it changes within the range or cannot be told: the range is then written as not legible and never used to teach icons.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Text)> OwnStatusAnswers =
    [
        ("Portrait", "Normal: my character's face"),
        ("Hooked", "Hooked: the hook icon"),
        ("Dying", "Down: the dying icon"),
        ("Dead", "Dead: the dead icon"),
        ("?", "It changes in this row, or I can't tell"),
    ];

    /// <summary>The survivor results the owner can choose.</summary>
    public static readonly IReadOnlyList<string> SurvivorResults = ["Escaped", "Sacrificed", "Dead", "Other"];

    /// <summary>Gets the queue answers of a closed list: the normal queue and every event mode.</summary>
    public static IReadOnlyList<string> QueueIds(HudVocabulary v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return [DbdPresence.Core.Detection.QueueModes.Normal, .. v.EventModeIds];
    }

    /// <summary>Returns whether text is an icon answer (or empty, meaning not answered).</summary>
    public static bool IsOwnStatusAnswer(string text) => text.Length == 0 || OwnStatusAnswers.Any(a => a.Id == text);

    /// <summary>Returns whether text is a scene answer.</summary>
    public static bool IsSceneAnswer(string text) => Scenes.Any(s => s.Id == text);

    /// <summary>Checks the closed-list answers of one match.</summary>
    public static void Validate(MatchAnswer m, HudVocabulary v)
    {
        if (m.Role is not ("Survivor" or "Killer" or "?"))
        {
            throw new FormatException("A role answer is not Survivor, Killer or '?'.");
        }

        if (m.Character.Length > 0 && m.Character != "?" && !v.IsCharacterId(m.Character))
        {
            throw new FormatException("A character answer is not in the closed list.");
        }

        if (m.Map.Length > 0 && m.Map != "?" && v.MapEntry(m.Map) is null)
        {
            throw new FormatException("A map answer is not in the closed list.");
        }

        if (m.Killer.Length > 0 && m.Killer != "?" && !v.IsKillerId(m.Killer))
        {
            throw new FormatException("A killer answer is not in the closed list.");
        }

        if (m.Queue.Length > 0 && m.Queue != "?" && m.Queue != DbdPresence.Core.Detection.QueueModes.Normal && !v.IsEventModeId(m.Queue))
        {
            throw new FormatException("A queue answer is not in the closed list.");
        }

        if (m.Result.Length > 0 && m.Result != "?" && !SurvivorResults.Contains(m.Result, StringComparer.Ordinal))
        {
            throw new FormatException("A result answer is not one of the known results.");
        }
    }
}
