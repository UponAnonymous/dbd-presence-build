using System.Collections.Concurrent;
using System.Globalization;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Prompts;
using DbdPresence.Vision.Statistics;
using DbdPresence.Vision.Text;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Screens;

/// <summary>The screens of the HUD catalogue that the text anchors tell apart.</summary>
public enum ScreenKind
{
    /// <summary>No screen was read with enough evidence.</summary>
    Unknown,

    /// <summary>Main menu: <c>QUIT [ESC]</c> and the menu entries.</summary>
    MainMenu,

    /// <summary>The mode list: header <c>PLAY</c> or <c>PLAY EVENT</c>, entries <c>KILLER</c> and <c>SURVIVOR</c>, <c>BACK [ESC]</c>.</summary>
    ModeList,

    /// <summary>The lobby: the <c>PLAY</c> button and <c>BACK [ESC]</c>.</summary>
    Lobby,

    /// <summary>Searching: the <c>CANCEL</c> button and the <c>EST.</c> timer.</summary>
    Searching,

    /// <summary>The pre-match lobby: the <c>READY</c> button.</summary>
    PreMatchLobby,

    /// <summary>The match start card: map over realm, bottom left.</summary>
    MatchStart,

    /// <summary>The trial HUD, from the player column alone (weak).</summary>
    TrialHud,

    /// <summary>The spectator view: the top bar and the player column (weak).</summary>
    Spectating,

    /// <summary>The match result: <c>MATCH RESULT</c> and the outcome.</summary>
    MatchResult,

    /// <summary>The scoreboard: <c>SCOREBOARD</c> header and one row per player.</summary>
    Scoreboard,

    /// <summary>TAB match details: the <c>PLAYERS</c>, <c>QUESTS</c> and <c>KILLER POWER</c> tabs.</summary>
    MatchDetails,

    /// <summary>The settings menu: its tabs.</summary>
    Settings,
}

/// <summary>What the screen classifier decided for one frame.</summary>
/// <param name="Kind">The screen, or <see cref="ScreenKind.Unknown"/> when unreadable.</param>
/// <param name="Score">The raw score of the screen (0 when unreadable).</param>
/// <param name="Scene">The engine scene the screen belongs to, or null when unreadable.</param>
/// <param name="Problem">Why the frame is unreadable, when it is.</param>
/// <param name="Evidence">Region and metric summary. Never contains names read from the screen.</param>
internal sealed record ScreenReading(ScreenKind Kind, double Score, Scene? Scene, string? Problem, string Evidence)
{
    public bool IsReadable => Kind != ScreenKind.Unknown;
}

/// <summary>The colour of a highlighted row band.</summary>
internal enum RowHighlight
{
    None,
    Gold,
    Red,
}

/// <summary>One player row of TAB match details or the scoreboard.</summary>
internal sealed record RowReading(
    int Index,
    bool HasText,
    string? CharacterId,
    bool CharacterIsKiller,
    double CharacterScore,
    bool CharacterAmbiguous,
    int LineCount,
    int CharacterLine,
    bool Anonymous,
    bool NameMatchesOwner,
    double NameScore,
    RowHighlight Highlight,
    double HighlightScore);

/// <summary>What the identity cues of one frame say about the owner.</summary>
internal sealed record IdentityReading(
    bool Applicable,
    string Source,
    string? Role,
    double RoleScore,
    string? RoleProblem,
    string? CharacterId,
    double CharacterScore,
    string? CharacterProblem,
    string? KillerId,
    double KillerScore,
    string? KillerProblem,
    bool OwnerRowReadNoMatch = false,
    bool KillerRowReadNoMatch = false);

/// <summary>A map reading from the match start card or from the <c>REALM - MAP</c> line.</summary>
internal sealed record MapReading(bool Applicable, string? MapId, double Score, string Evidence);

/// <summary>The outcome printed on the result screen.</summary>
/// <param name="Applicable">True only while the match result screen is up.</param>
/// <param name="OutcomeId">The id of the listed outcome that was read, or null when the label is up but no listed outcome was read.</param>
/// <param name="Score">The raw score: one minus the normalised edit distance of the match.</param>
/// <param name="Evidence">A plain sentence without screen text.</param>
internal sealed record OutcomeReading(bool Applicable, string? OutcomeId, double Score, string Evidence);

/// <summary>A queue reading from the mode list or the searching screen.</summary>
internal sealed record QueueReading(bool Applicable, string? Value, double Score, string Evidence);

/// <summary>What the power prompt region of the trial HUD showed in one frame.</summary>
/// <param name="Applicable">True only on the trial HUD.</param>
/// <param name="Match">The mapped prompt that was identified, or none.</param>
/// <param name="Candidates">The prompt-shaped lines (upper case letters), before the name filters of the learning path.</param>
/// <param name="Evidence">A plain sentence without screen text.</param>
internal sealed record PromptReading(bool Applicable, PromptMatch Match, IReadOnlyList<string> Candidates, string Evidence);

/// <summary>
/// The shared reading logic of the HUD detectors (<c>docs/evidence/hud-cue-catalogue.md</c>): which screen is up, where the owner's row is, what
/// map and queue are shown. It is a pure function of the frame's text and colours, the profile and the closed vocabulary. Every detector asks it
/// and the per-frame memo of <see cref="FrameCrops"/> makes sure the work is done once.
/// </summary>
internal sealed partial class HudAnalyzer
{
    private const int MaxSpanWords = 4;
    private const int MaxRowCache = 512;

    private readonly HudProfile _profile;
    private readonly HudVocabulary _vocabulary;
    private readonly ConcurrentDictionary<string, ParsedRow> _rowCache = new(StringComparer.Ordinal);

    public HudAnalyzer(HudProfile profile, HudVocabulary vocabulary)
    {
        _profile = profile;
        _vocabulary = vocabulary;
    }

    public HudProfile Profile => _profile;

    /// <summary>Gets how many parsed rows are held in memory (bounded by <see cref="MaxRowCache"/>).</summary>
    internal int CachedRowCount => _rowCache.Count;

    /// <summary>Forgets every parsed row: the cache holds other players' names as read, so it is cleared when the screen changes and on reset.</summary>
    public void ClearCaches() => _rowCache.Clear();

    // ---- Screen --------------------------------------------------------------------------------------------------

    public ScreenReading Classify(FrameCrops crops, DetectionContext context) =>
        crops.Memo($"screen|{context.PriorScene}|{context.OwnerName}", () => ClassifyCore(crops, context));

    private ScreenReading ClassifyCore(FrameCrops crops, DetectionContext context)
    {
        if (!context.Context.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return Unreadable("unsupported game language: detection unavailable");
        }

        if (!_profile.AspectMatches(crops.FrameWidth, crops.FrameHeight))
        {
            return Unreadable("aspect ratio not calibrated: the regions were measured on 16:9");
        }

        var t = _profile.Thresholds;
        var f = t.MaxEditFraction;
        IReadOnlyList<string>? Lines(string id) => crops.GetText(id) is { } text ? AnchorText.Lines(text) : null;

        var corner = Lines(HudRegionIds.Escape);
        var quit = AnchorText.ExactLine(corner, "QUIT", f);
        var back = AnchorText.ExactLine(corner, "BACK", f);

        var list = Lines(HudRegionIds.MenuList);
        var header = list is { Count: > 0 } ? (IReadOnlyList<string>)[list[0]] : null;
        var headerPlay = AnchorText.ExactLine(header, "PLAY", f);
        var headerEvent = AnchorText.ExactLine(header, "PLAY EVENT", f);
        var killerEntry = AnchorText.ExactLine(list, "KILLER", f);
        var survivorEntry = AnchorText.ExactLine(list, "SURVIVOR", f);
        var menuEntries = TopTwoMean(
            AnchorText.ExactLine(list, "ARCHIVES", f),
            AnchorText.ExactLine(list, "RIFT PASS", f),
            AnchorText.ExactLine(list, "QUESTS", f),
            AnchorText.ExactLine(list, "STORE", f));

        var button = Lines(HudRegionIds.Button);
        var buttonPlay = AnchorText.ExactLine(button, "PLAY", f);
        var buttonReady = AnchorText.ExactLine(button, "READY", f);
        var buttonCancel = AnchorText.ExactLine(button, "CANCEL", f);

        var timerRaw = crops.GetText(HudRegionIds.SearchTimer);
        var estimate = AnchorText.Contains(Lines(HudRegionIds.SearchTimer), "EST", f);
        if (timerRaw is null || !timerRaw.Any(char.IsDigit))
        {
            estimate = Math.Min(estimate, 0.5); // an estimate without a time is a weak reading
        }

        var start = ReadMatchStart(crops);
        var hudRows = HudRowCount(crops, context.OwnerName);
        var barText = crops.GetText(HudRegionIds.SpectateBar);
        var barHoldsText = AnchorText.Letters(barText) >= t.MinNameLetters;
        var barHasShape = AnchorText.HasSpectatorBarShape(barText, t.MinNameLetters);

        var resultLabel = AnchorText.ExactLine(Lines(HudRegionIds.ResultLabel), "MATCH RESULT", f);
        var outcome = ReadOutcome(crops);
        var scoreHeader = Lines(HudRegionIds.ScoreHeader);
        var scoreboard = AnchorText.Contains(scoreHeader, "SCOREBOARD", f);

        var tabLines = Lines(HudRegionIds.TabTabs);
        var tabs = TopMean(2, AnchorText.Contains(tabLines, "PLAYERS", f), AnchorText.Contains(tabLines, "QUESTS", f), AnchorText.Contains(tabLines, "KILLER POWER", f));
        var settingLines = Lines(HudRegionIds.SettingsTabs);
        var settings = TopMean(
            3,
            AnchorText.Contains(settingLines, "GENERAL", f),
            AnchorText.Contains(settingLines, "GRAPHICS", f),
            AnchorText.Contains(settingLines, "CONTROLS", f),
            AnchorText.Contains(settingLines, "GAMEPLAY", f),
            AnchorText.Contains(settingLines, "AUDIO", f),
            AnchorText.Contains(settingLines, "ACCESSIBILITY", f));

        var scores = new Dictionary<ScreenKind, double>
        {
            [ScreenKind.MainMenu] = Math.Min(quit, menuEntries),
            [ScreenKind.ModeList] = Min(back, Math.Max(headerPlay, headerEvent), killerEntry, survivorEntry),
            [ScreenKind.Lobby] = Math.Min(buttonPlay, back),
            [ScreenKind.Searching] = Math.Min(buttonCancel, estimate),
            [ScreenKind.PreMatchLobby] = Math.Min(buttonReady, t.SingleCueCap),
            [ScreenKind.MatchStart] = start.Score,
            [ScreenKind.MatchResult] = resultLabel <= 0 ? 0 : Math.Min(resultLabel, outcome > 0 ? outcome : t.ResultLabelOnlyScore),
            [ScreenKind.Scoreboard] = scoreboard,
            [ScreenKind.MatchDetails] = tabs,
            [ScreenKind.Settings] = settings,
        };

        // The player column of the trial HUD and the spectator bar are the weakest cues of the catalogue (free text in a fixed place): they are
        // used only when no screen with a label, button or title was read at all.
        scores[ScreenKind.TrialHud] = 0;
        scores[ScreenKind.Spectating] = 0;
        if (scores.Values.Max() < t.MinScore && hudRows.Count >= t.MinHudRows && !OtherScreenAnchorsHoldText(crops))
        {
            if (barHasShape)
            {
                scores[ScreenKind.Spectating] = t.WeakCueScore;
            }
            else if (!barHoldsText)
            {
                scores[ScreenKind.TrialHud] = hudRows.OwnerInFirstRow ? t.OwnNameBonusScore : t.WeakCueScore;
            }

            // Text in the top bar that does not have the spectator shape (a banner, a notice) supports neither screen: silence over error (review U5 F3).
        }

        var evidence = string.Join(';', scores.Where(s => s.Value > 0).OrderByDescending(s => s.Value).ThenBy(s => s.Key).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key}={s.Value:0.000}")));
        var ranked = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).ToList();
        var best = ranked[0];
        var second = ranked[1];
        if (best.Value < t.MinScore)
        {
            return new ScreenReading(ScreenKind.Unknown, best.Value, null, "below score floor; " + (evidence.Length == 0 ? "no anchor read" : evidence), evidence);
        }

        if (best.Value - second.Value < t.MinMargin)
        {
            return new ScreenReading(ScreenKind.Unknown, best.Value, null, "ambiguous; " + evidence, evidence);
        }

        return new ScreenReading(best.Key, best.Value, SceneOf(best.Key, context.PriorScene), null, evidence);
    }

    public static Scene SceneOf(ScreenKind kind, Scene prior) => kind switch
    {
        ScreenKind.MainMenu => Scene.MainMenu,
        ScreenKind.ModeList => Scene.Other,
        ScreenKind.Lobby or ScreenKind.PreMatchLobby => Scene.Lobby,
        ScreenKind.Searching => Scene.Matchmaking,
        ScreenKind.MatchStart or ScreenKind.TrialHud => Scene.InTrial,
        ScreenKind.Spectating => Scene.Spectating,
        ScreenKind.MatchResult or ScreenKind.Scoreboard => Scene.Results,
        ScreenKind.MatchDetails => Scene.InMatchMenu,
        ScreenKind.Settings => prior is Scene.InTrial or Scene.Spectating or Scene.InMatchMenu ? Scene.InMatchMenu : Scene.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The trial HUD is recognised by the absence of menu anchors, so a frame where the anchor regions of the other screens hold text that was
    /// not understood (a title, a button, a timer) is not called a trial: an unread menu must not become a trial.
    /// </summary>
    private bool OtherScreenAnchorsHoldText(FrameCrops crops)
    {
        string[] ids =
        [
            HudRegionIds.Button, HudRegionIds.SearchTimer, HudRegionIds.ScoreHeader, HudRegionIds.ResultLabel, HudRegionIds.TabTabs,
            HudRegionIds.SettingsTabs, HudRegionIds.StartMap, HudRegionIds.Escape, HudRegionIds.ProfileName,
        ];
        return ids.Any(id => AnchorText.Letters(crops.GetText(id)) >= _profile.Thresholds.MinNameLetters) || MenuListHoldsAnEntry(crops);
    }

    /// <summary>
    /// The menu list overlaps the HUD player column (the catalogue says so), so text in it cannot block the trial HUD by itself. A line that reads as
    /// one of the menu's own entries does: a menu whose key hint or title went unread is still a menu (review U5 F10).
    /// </summary>
    private bool MenuListHoldsAnEntry(FrameCrops crops)
    {
        var lines = crops.GetText(HudRegionIds.MenuList) is { } text ? AnchorText.Lines(text) : null;
        var f = _profile.Thresholds.MaxEditFraction;
        string[] entries = ["PLAY", "PLAY EVENT", "KILLER", "SURVIVOR", "ARCHIVES", "RIFT PASS", "QUESTS", "STORE", "LEARN TO PLAY", "CUSTOM GAME"];
        return entries.Any(entry => AnchorText.ExactLine(lines, entry, f) > 0);
    }

    private static ScreenReading Unreadable(string problem) => new(ScreenKind.Unknown, 0, null, problem, string.Empty);

    private static double Min(params double[] values) => values.Min();

    /// <summary>The mean of the best <paramref name="count"/> values, or 0 when fewer than that many were read at all.</summary>
    private static double TopMean(int count, params double[] values)
    {
        var read = values.Where(v => v > 0).OrderByDescending(v => v).ToList();
        return read.Count < count ? 0 : read.Take(count).Average();
    }

    private static double TopTwoMean(params double[] values) => TopMean(2, values);

    private readonly record struct HudRowSummary(int Count, bool OwnerInFirstRow);

    private HudRowSummary HudRowCount(FrameCrops crops, string? ownerName)
    {
        var count = 0;
        for (var i = 2; i < HudProfile.HudRows; i++)
        {
            if (AnchorText.Letters(crops.GetText(HudRegionIds.HudRow(i))) >= _profile.Thresholds.MinNameLetters)
            {
                count++;
            }
        }

        return new HudRowSummary(count, count > 0 && OwnerInFirstHudRow(crops, ownerName));
    }

    // ---- Match start and map -------------------------------------------------------------------------------------

    private readonly record struct StartReading(double Score, string? MapId);

    private StartReading ReadMatchStart(FrameCrops crops)
    {
        var mapLines = crops.GetText(HudRegionIds.StartMap) is { } m ? AnchorText.Lines(m) : [];
        var realmLines = crops.GetText(HudRegionIds.StartRealm) is { } r ? AnchorText.Lines(r) : [];
        var best = 0.0;
        string? bestId = null;
        foreach (var mapLine in mapLines)
        {
            var map = _vocabulary.Maps.Match(mapLine);
            if (!map.IsAccepted)
            {
                continue;
            }

            foreach (var realmLine in realmLines)
            {
                var realm = _vocabulary.Realms.Match(realmLine);
                if (realm.IsAccepted && string.Equals(realm.Entry!.Id, map.Entry!.Group, StringComparison.Ordinal))
                {
                    var score = Math.Min(1.0 - map.Distance!.Value, 1.0 - realm.Distance!.Value);
                    if (score > best)
                    {
                        best = score;
                        bestId = map.Entry.Id;
                    }
                }
            }
        }

        return new StartReading(best, bestId);
    }

    public MapReading ReadMap(FrameCrops crops, DetectionContext context)
    {
        var screen = Classify(crops, context);
        if (!screen.IsReadable)
        {
            return new MapReading(false, null, 0, screen.Problem ?? "screen unreadable");
        }

        switch (screen.Kind)
        {
            case ScreenKind.MatchStart:
                var start = ReadMatchStart(crops);
                return start.MapId is null
                    ? new MapReading(true, null, 0, "match start: map and realm do not agree")
                    : new MapReading(true, start.MapId, Math.Min(screen.Score, start.Score), "match start card: map and realm agree");
            case ScreenKind.MatchDetails:
                var line = ReadRealmMapLine(crops);
                return line.MapId is null
                    ? new MapReading(true, null, 0, "match details: no REALM - MAP line that agrees with the vocabulary")
                    : new MapReading(true, line.MapId, Math.Min(screen.Score, line.Score), "match details: REALM - MAP line");
            default:
                return new MapReading(false, null, 0, "screen shows no map");
        }
    }

    private StartReading ReadRealmMapLine(FrameCrops crops)
    {
        var found = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in AnchorText.Lines(crops.GetText(HudRegionIds.TabRealmMap)))
        {
            // The separator may be read as a hyphen, a dash, or not at all; hyphens and dashes stand alone and are dropped as words.
            var words = AnchorText.Words(line).Where(w => w.Any(char.IsLetterOrDigit)).Take(12).ToArray();
            for (var split = 1; split < words.Length; split++)
            {
                var realm = _vocabulary.Realms.Match(string.Join(' ', words[..split]));
                if (!realm.IsAccepted)
                {
                    continue;
                }

                var map = _vocabulary.Maps.Match(string.Join(' ', words[split..]));
                if (map.IsAccepted && string.Equals(map.Entry!.Group, realm.Entry!.Id, StringComparison.Ordinal))
                {
                    var score = Math.Min(1.0 - realm.Distance!.Value, 1.0 - map.Distance!.Value);
                    found[map.Entry.Id] = Math.Max(found.GetValueOrDefault(map.Entry.Id), score);
                }
            }
        }

        return found.Count == 1 ? new StartReading(found.Values.First(), found.Keys.First()) : new StartReading(0, null);
    }

    private double ReadOutcome(FrameCrops crops)
    {
        foreach (var line in AnchorText.Lines(crops.GetText(HudRegionIds.ResultOutcome)))
        {
            var match = _vocabulary.Outcomes.Match(line);
            if (match.IsAccepted)
            {
                return 1.0 - match.Distance!.Value;
            }
        }

        return 0;
    }

    /// <summary>Reads the outcome under <c>MATCH RESULT</c> against the closed list of outcomes (FR-AUTO-04).</summary>
    public OutcomeReading ReadOutcomeEntry(FrameCrops crops, DetectionContext context)
    {
        var screen = Classify(crops, context);
        if (!screen.IsReadable || screen.Kind != ScreenKind.MatchResult)
        {
            return new OutcomeReading(false, null, 0, screen.Problem ?? "not the result screen");
        }

        foreach (var line in AnchorText.Lines(crops.GetText(HudRegionIds.ResultOutcome)))
        {
            var match = _vocabulary.Outcomes.Match(line);
            if (match.IsAccepted)
            {
                return new OutcomeReading(true, match.Entry!.Id, 1.0 - match.Distance!.Value, "outcome matched a listed outcome");
            }
        }

        return new OutcomeReading(true, null, 0, "no listed outcome read");
    }

    // ---- Queue ---------------------------------------------------------------------------------------------------

    public QueueReading ReadQueue(FrameCrops crops, DetectionContext context)
    {
        var screen = Classify(crops, context);
        var f = _profile.Thresholds.MaxEditFraction;
        if (!screen.IsReadable)
        {
            return new QueueReading(false, null, 0, screen.Problem ?? "screen unreadable");
        }

        switch (screen.Kind)
        {
            case ScreenKind.ModeList:
                var list = AnchorText.Lines(crops.GetText(HudRegionIds.MenuList));
                var first = list.Count > 0 ? (IReadOnlyList<string>)[list[0]] : [];
                var play = AnchorText.ExactLine(first, "PLAY", f);
                var evt = AnchorText.ExactLine(first, "PLAY EVENT", f);
                if (play > 0 && play >= evt)
                {
                    return new QueueReading(true, QueueModes.Normal, Math.Min(screen.Score, play), "mode list header PLAY");
                }

                if (evt > 0 && list.Count > 1)
                {
                    var name = _vocabulary.EventModes.Match(list[1]);
                    return name.IsAccepted
                        ? new QueueReading(true, name.Entry!.Id, Math.Min(screen.Score, Math.Min(evt, 1.0 - name.Distance!.Value)), "mode list header PLAY EVENT and the event name")
                        : new QueueReading(true, null, 0, "mode list header PLAY EVENT: the event name is not in the closed list");
                }

                return new QueueReading(true, null, 0, "mode list: header unreadable");
            case ScreenKind.Searching:
                var label = AnchorText.Lines(crops.GetText(HudRegionIds.SearchEventLabel));
                if (label.Sum(AnchorText.Letters) < _profile.Thresholds.MinNameLetters)
                {
                    // A normal queue prints nothing there. Absence of a label is not evidence of the normal queue, so nothing is observed.
                    return new QueueReading(false, null, 0, "searching: no event label");
                }

                foreach (var line in label)
                {
                    var match = _vocabulary.EventModes.Match(line);
                    if (match.IsAccepted)
                    {
                        return new QueueReading(true, match.Entry!.Id, Math.Min(screen.Score, 1.0 - match.Distance!.Value), "searching: event label");
                    }
                }

                return new QueueReading(true, null, 0, "searching: the event label is not in the closed list");
            default:
                return new QueueReading(false, null, 0, "screen shows no queue");
        }
    }

    // ---- Power prompts -------------------------------------------------------------------------------------------

    public PromptReading ReadPrompt(FrameCrops crops, DetectionContext context) =>
        crops.Memo("prompt", () => ReadPromptCore(crops, context));

    private PromptReading ReadPromptCore(FrameCrops crops, DetectionContext context)
    {
        var screen = Classify(crops, context);
        if (screen.Kind != ScreenKind.TrialHud)
        {
            return new PromptReading(false, PromptMatch.None, [], "not the trial HUD");
        }

        if (crops.GetText(HudRegionIds.PowerPrompt) is not { } text)
        {
            return new PromptReading(true, PromptMatch.None, [], "power prompt region not read");
        }

        var match = _vocabulary.Prompts.Match(text);
        var candidates = PromptText.Lines(text).Select(PromptText.Candidate).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return new PromptReading(true, match, candidates, match.Accepted ? "a mapped power prompt" : "no mapped power prompt in the region");
    }

    // ---- Identity ------------------------------------------------------------------------------------------------

    public IdentityReading ReadIdentity(FrameCrops crops, DetectionContext context) =>
        crops.Memo($"identity|{context.PriorScene}|{context.OwnerName}", () => ReadIdentityCore(crops, context));

    private IdentityReading ReadIdentityCore(FrameCrops crops, DetectionContext context)
    {
        var screen = Classify(crops, context);
        if (!screen.IsReadable)
        {
            return NotApplicable(screen.Problem ?? "screen unreadable");
        }

        return screen.Kind switch
        {
            ScreenKind.MatchDetails => ReadRowsIdentity(crops, context, screen, scoreboard: false),
            ScreenKind.Scoreboard => ReadRowsIdentity(crops, context, screen, scoreboard: true),
            ScreenKind.PreMatchLobby => ReadLobbyIdentity(crops, context, screen),
            _ => NotApplicable("screen carries no owner row"),
        };
    }

    private static IdentityReading NotApplicable(string reason) => new(false, "none", null, 0, reason, null, 0, reason, null, 0, reason);

    private IdentityReading ReadRowsIdentity(FrameCrops crops, DetectionContext context, ScreenReading screen, bool scoreboard)
    {
        var source = scoreboard ? "scoreboard" : "tab";
        var rows = new List<RowReading>();
        var ownerKey = OwnerKey(context.OwnerName, out var ownerProblem);
        for (var i = 0; i < HudProfile.PlayerRows; i++)
        {
            var textId = scoreboard ? HudRegionIds.ScoreRow(i) : HudRegionIds.TabRow(i);
            var bandId = scoreboard ? HudRegionIds.ScoreBand(i) : HudRegionIds.TabBand(i);
            rows.Add(ReadRow(i, crops.GetText(textId), crops.Get(bandId), ownerKey));
        }

        var (killerId, killerScore, killerProblem) = ReadKillerRow(rows, screen.Score);
        if (ownerProblem is not null)
        {
            return new IdentityReading(true, source, null, 0, ownerProblem, null, 0, ownerProblem, killerId, killerScore, killerProblem);
        }

        var candidates = rows.Where(r => r.NameMatchesOwner && !r.Anonymous && r.Highlight != RowHighlight.None).ToList();
        string? problem = null;
        RowReading? owner = null;
        if (candidates.Count == 0)
        {
            problem = rows.Any(r => r.NameMatchesOwner) ? "a row carries the owner's name but is not highlighted (or the name is an anonymous character name)" : "no row carries the owner's name";
        }
        else if (candidates.Count > 1)
        {
            problem = "more than one row carries the owner's name and a highlight";
        }
        else if (rows.Count(r => r.Highlight == candidates[0].Highlight) > 1)
        {
            problem = "another row has the same highlight colour";
        }
        else
        {
            owner = candidates[0];
        }

        if (owner is null)
        {
            return new IdentityReading(true, source, null, 0, problem, null, 0, problem, killerId, killerScore, killerProblem);
        }

        var role = owner.Highlight == RowHighlight.Gold ? nameof(GameRole.Survivor) : nameof(GameRole.Killer);
        string? roleProblem = null;
        if (scoreboard && owner.Highlight == RowHighlight.Red && AnchorText.ExactLine(AnchorText.Lines(crops.GetText(HudRegionIds.ScoreHeader)), "MATCH", _profile.Thresholds.MaxEditFraction) > 0)
        {
            // Only survivors see the MATCH line above SCOREBOARD, so a red row under it is contradictory.
            roleProblem = "the red row contradicts the survivor-only MATCH header";
        }
        else if (owner.CharacterId is not null && !owner.CharacterAmbiguous && owner.CharacterIsKiller != (owner.Highlight == RowHighlight.Red))
        {
            // The row's two cues disagree with each other (a red band with a survivor's name, a gold band with a killer's name): the row is not
            // understood, so it gives no role either (review U5 F2).
            roleProblem = "the character's kind contradicts the row colour";
        }

        var roleScore = roleProblem is null ? Math.Min(screen.Score, Math.Min(owner.NameScore, owner.HighlightScore)) : 0;
        string? characterId = null;
        var characterScore = 0.0;
        string? characterProblem = roleProblem;
        if (roleProblem is null)
        {
            if (owner.CharacterId is null || owner.CharacterAmbiguous)
            {
                characterProblem = owner.CharacterAmbiguous ? "the owner's row names two characters" : "the owner's row has no name from the closed list";
            }
            else
            {
                characterId = owner.CharacterId;
                characterScore = Math.Min(roleScore, owner.CharacterScore);
            }
        }

        // "Read, no match" is not "unread": a row of two lines or more (character name over player name) whose character line names nothing of the closed list
        // was read and is not in the list (a new character); a row that is missing a line may simply have been misread, and is neither (review F11).
        var ownerNoMatch = roleProblem is null && owner.CharacterId is null && !owner.CharacterAmbiguous && owner.LineCount >= 2;
        var lastRow = rows.LastOrDefault(r => r.HasText);
        var killerNoMatch = lastRow is not null && lastRow.CharacterId is null && !lastRow.CharacterAmbiguous && lastRow.LineCount >= 2;
        return new IdentityReading(true, source, roleProblem is null ? role : null, roleScore, roleProblem, characterId, characterScore, characterProblem, killerId, killerScore, killerProblem, ownerNoMatch, killerNoMatch);
    }

    private static (string? Id, double Score, string? Problem) ReadKillerRow(List<RowReading> rows, double screenScore)
    {
        var last = rows.LastOrDefault(r => r.HasText);
        if (last is null)
        {
            return (null, 0, "no player rows were read");
        }

        // A killer row prints the character name on its first line and the player name under it. A row of one line, or a name found anywhere but on
        // the first line, may be a player who is called like a killer (review U5 F7), so it is never a killer name.
        if (last.CharacterId is null || last.CharacterAmbiguous || !last.CharacterIsKiller || last.LineCount < 2 || last.CharacterLine != 0)
        {
            return (null, 0, "the last row shows no killer name (the game does not always print it)");
        }

        return (last.CharacterId, Math.Min(screenScore, last.CharacterScore), null);
    }

    private IdentityReading ReadLobbyIdentity(FrameCrops crops, DetectionContext context, ScreenReading screen)
    {
        var ownerKey = OwnerKey(context.OwnerName, out var ownerProblem);
        if (ownerProblem is not null)
        {
            return new IdentityReading(true, "lobby", null, 0, ownerProblem, null, 0, ownerProblem, null, 0, "the killer's name is not shown in the lobby");
        }

        // Survivor: a pair of character name over the owner's player name, in one of the columns.
        var pairs = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var column = 0; column < HudProfile.LobbyColumns; column++)
        {
            var lines = AnchorText.Lines(crops.GetText(HudRegionIds.LobbyPair(column)));
            for (var k = 0; k + 1 < lines.Count; k++)
            {
                var character = _vocabulary.Survivors.Match(lines[k]);
                if (!character.IsAccepted)
                {
                    continue;
                }

                var underneath = _vocabulary.Survivors.Match(lines[k + 1]);
                var underneathKiller = _vocabulary.Killers.Match(lines[k + 1]);
                if (underneath.IsAccepted || underneathKiller.IsAccepted)
                {
                    continue; // The "player name" is itself a character name (anonymous mode): never accept a row on that.
                }

                var name = NameScore(AnchorText.Words(lines[k + 1]), ownerKey!);
                if (name > 0)
                {
                    pairs[character.Entry!.Id] = Math.Max(pairs.GetValueOrDefault(character.Entry.Id), Math.Min(1.0 - character.Distance!.Value, name));
                }
            }
        }

        // Killer: the first entry of the list at the right is the owner's name next to the red killer icon.
        var killerLines = AnchorText.Lines(crops.GetText(HudRegionIds.KillerList));
        var redFraction = KillerIconRedFraction(crops, killerLines.Count);
        var killerName = killerLines.Count > 0 ? NameScore(AnchorText.Words(killerLines[0]), ownerKey!) : 0.0;
        var killerCue = killerName > 0 && redFraction >= _profile.Highlight.IconMinFraction;

        if (killerCue && pairs.Count > 0)
        {
            const string Conflict = "the lobby shows the owner as both survivor and killer";
            return new IdentityReading(true, "lobby", null, 0, Conflict, null, 0, Conflict, null, 0, "the killer's name is not shown in the lobby");
        }

        if (killerCue)
        {
            var score = Math.Min(screen.Score, Math.Min(killerName, IconScore(redFraction)));
            return new IdentityReading(true, "lobby", nameof(GameRole.Killer), score, null, null, 0, "the killer's own character is not shown in the lobby", null, 0, "the killer's name is not shown in the lobby");
        }

        if (pairs.Count == 1)
        {
            var pair = pairs.First();
            var score = Math.Min(screen.Score, pair.Value);
            return new IdentityReading(true, "lobby", nameof(GameRole.Survivor), score, null, pair.Key, score, null, null, 0, "the killer's name is not shown in the lobby");
        }

        var problem = pairs.Count > 1 ? "more than one character is paired with the owner's name" : "no character is paired with the owner's name";
        return new IdentityReading(true, "lobby", null, 0, problem, null, 0, problem, null, 0, "the killer's name is not shown in the lobby");
    }

    /// <summary>
    /// The share of red in the icon column beside the first line of the killer list (review U5 F6): the left <see cref="RowHighlightSettings.IconColumnWidth"/>
    /// of the region, as high as one line (the list is cut into as many equal lines as were read, at least two). Red elsewhere in the list (another
    /// player's icon, a red status mark) does not count.
    /// </summary>
    private double KillerIconRedFraction(FrameCrops crops, int lineCount)
    {
        if (crops.Get(HudRegionIds.KillerList) is not { } list || list.Width < 4 || list.Height < 4)
        {
            return 0.0;
        }

        var h = _profile.Highlight;
        var icon = list.Crop(new NormalizedRect(0, 0, h.IconColumnWidth, 1.0 / Math.Max(lineCount, 2)));
        return HueBandStatistics.FractionInBand(icon, h.Red.HueFrom, h.Red.HueTo, h.Red.MinSaturation, h.Red.MinValue);
    }

    private string? OwnerKey(string? ownerName, out string? problem)
    {
        var key = TextNormalizer.Skeleton(ownerName);
        var compact = key.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length < _profile.Thresholds.MinOwnerNameLength)
        {
            problem = ownerName is null ? "the owner's name is not known yet" : "the owner's name is too short to find a row";
            return null;
        }

        if (_vocabulary.Killers.Match(ownerName).IsAccepted || _vocabulary.Survivors.Match(ownerName).IsAccepted)
        {
            problem = "the owner's name is a character name, so the name cannot identify a row";
            return null;
        }

        problem = null;
        return compact;
    }

    // ---- Rows ----------------------------------------------------------------------------------------------------

    private RowReading ReadRow(int index, string? text, ImageBuffer? band, string? ownerKey)
    {
        var parsed = ParseRow(text);
        var name = ownerKey is null ? 0.0 : BestNameScore(parsed, ownerKey);
        var (highlight, highlightScore) = ReadHighlight(band);
        return new RowReading(
            index,
            parsed.HasText,
            parsed.CharacterId,
            parsed.IsKiller,
            parsed.Score,
            parsed.Ambiguous,
            parsed.Words.Count,
            parsed.Chosen?.Line ?? -1,
            parsed.Anonymous,
            name > 0,
            name,
            highlight,
            highlightScore);
    }

    private (RowHighlight Highlight, double Score) ReadHighlight(ImageBuffer? band)
    {
        if (band is null)
        {
            return (RowHighlight.None, 0);
        }

        var h = _profile.Highlight;
        var gold = HueBandStatistics.FractionInBand(band, h.Gold.HueFrom, h.Gold.HueTo, h.Gold.MinSaturation, h.Gold.MinValue);
        var red = HueBandStatistics.FractionInBand(band, h.Red.HueFrom, h.Red.HueTo, h.Red.MinSaturation, h.Red.MinValue);
        var isGold = gold >= h.MinFraction;
        var isRed = red >= h.MinFraction;
        if (isGold == isRed)
        {
            return (RowHighlight.None, 0); // neither, or both: the colour test does not decide
        }

        return isGold ? (RowHighlight.Gold, HighlightScore(gold)) : (RowHighlight.Red, HighlightScore(red));
    }

    private double IconScore(double fraction)
    {
        var h = _profile.Highlight;
        return Math.Clamp(0.6 + (0.4 * (fraction - h.IconMinFraction) / (h.IconFullFraction - h.IconMinFraction)), 0.6, 1.0);
    }

    private double HighlightScore(double fraction)
    {
        var h = _profile.Highlight;
        return Math.Clamp(0.6 + (0.4 * (fraction - h.MinFraction) / (h.FullFraction - h.MinFraction)), 0.6, 1.0);
    }

    /// <summary>The best score of the owner's name inside a row, ignoring the words used by the character name.</summary>
    private double BestNameScore(ParsedRow row, string ownerKey)
    {
        var best = 0.0;
        for (var li = 0; li < row.Words.Count; li++)
        {
            var words = row.Words[li];
            foreach (var segment in row.Segments(li))
            {
                best = Math.Max(best, NameScore(words.Skip(segment.Start).Take(segment.Length).ToArray(), ownerKey));
            }
        }

        return best;
    }

    /// <summary>Compares windows of words with the owner's name (letters and digits only, folded). Returns a score in (0, 1], or 0 for no match.</summary>
    private double NameScore(string[] words, string ownerKey)
    {
        var best = 0.0;
        var allowed = (int)Math.Floor((ownerKey.Length * _profile.Thresholds.OwnerNameMaxEditFraction) + 1e-9);
        for (var start = 0; start < words.Length; start++)
        {
            var text = new System.Text.StringBuilder();
            for (var end = start; end < words.Length && end - start < 5; end++)
            {
                text.Append(TextNormalizer.Skeleton(words[end]).Replace(" ", string.Empty, StringComparison.Ordinal));
                var edits = TextNormalizer.Levenshtein(text.ToString(), ownerKey);
                if (edits <= allowed)
                {
                    best = Math.Max(best, 1.0 - ((double)edits / ownerKey.Length));
                }
            }
        }

        return best;
    }

    private ParsedRow ParseRow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ParsedRow.Empty;
        }

        if (_rowCache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var parsed = ParseRowCore(text);
        if (_rowCache.Count >= MaxRowCache)
        {
            _rowCache.Clear();
        }

        _rowCache[text] = parsed;
        return parsed;
    }

    private ParsedRow ParseRowCore(string text)
    {
        var lines = AnchorText.Lines(text);
        var words = lines.Select(AnchorText.Words).ToList();
        var candidates = new List<SpanMatch>();
        for (var li = 0; li < words.Count; li++)
        {
            for (var start = 0; start < words[li].Length; start++)
            {
                for (var length = 1; length <= MaxSpanWords && start + length <= words[li].Length; length++)
                {
                    var span = string.Join(' ', words[li].Skip(start).Take(length));
                    if (AnchorText.Letters(span) < 4)
                    {
                        continue;
                    }

                    var killer = _vocabulary.Killers.Match(span);
                    if (killer.IsAccepted)
                    {
                        candidates.Add(new SpanMatch(li, start, length, killer.Entry!.Id, true, 1.0 - killer.Distance!.Value));
                    }

                    var survivor = _vocabulary.Survivors.Match(span);
                    if (survivor.IsAccepted)
                    {
                        candidates.Add(new SpanMatch(li, start, length, survivor.Entry!.Id, false, 1.0 - survivor.Distance!.Value));
                    }
                }
            }
        }

        var hasText = lines.Any(l => AnchorText.Letters(l) > 0);
        var ids = candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return new ParsedRow(hasText, words, null, false, 0, false, false, null);
        }

        if (ids.Count > 1)
        {
            return new ParsedRow(hasText, words, null, false, 0, true, false, null);
        }

        var best = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Length).First();
        var anonymous = candidates.Any(c => !ReferenceEquals(c, best) && !Overlaps(c, best));
        return new ParsedRow(hasText, words, best.Id, best.IsKiller, best.Score, false, anonymous, best);
    }

    private static bool Overlaps(SpanMatch a, SpanMatch b) => a.Line == b.Line && a.Start < b.Start + b.Length && b.Start < a.Start + a.Length;

    private sealed record SpanMatch(int Line, int Start, int Length, string Id, bool IsKiller, double Score);

    private sealed record ParsedRow(
        bool HasText,
        IReadOnlyList<string[]> Words,
        string? CharacterId,
        bool IsKiller,
        double Score,
        bool Ambiguous,
        bool Anonymous,
        SpanMatch? Chosen)
    {
        public static ParsedRow Empty { get; } = new(false, [], null, false, 0, false, false, null);

        /// <summary>The runs of words of a line that are not part of the chosen character name.</summary>
        public IEnumerable<(int Start, int Length)> Segments(int line)
        {
            var count = Words[line].Length;
            if (Chosen is null || Chosen.Line != line)
            {
                yield return (0, count);
                yield break;
            }

            if (Chosen.Start > 0)
            {
                yield return (0, Chosen.Start);
            }

            if (Chosen.Start + Chosen.Length < count)
            {
                yield return (Chosen.Start + Chosen.Length, count - Chosen.Start - Chosen.Length);
            }
        }
    }

    // ---- Owner name ----------------------------------------------------------------------------------------------

    /// <summary>Returns the text of the profile-name region that may be the owner's name (a single line of at least three letters), or null.</summary>
    public string? ReadProfileName(FrameCrops crops)
    {
        var lines = AnchorText.Lines(crops.GetText(HudRegionIds.ProfileName));
        if (lines.Count != 1 || AnchorText.Letters(lines[0]) < _profile.Thresholds.MinOwnerNameLength || lines[0].Length > 40)
        {
            return null;
        }

        return lines[0];
    }

    /// <summary>The share of the first trial HUD row text that equals the owner's name (used for the trial HUD bonus).</summary>
    public bool OwnerInFirstHudRow(FrameCrops crops, string? ownerName)
    {
        var key = OwnerKey(ownerName, out var problem);
        return problem is null && key is not null && NameScore(AnchorText.Words(crops.GetText(HudRegionIds.HudRow(0)) ?? string.Empty), key) > 0;
    }
}
