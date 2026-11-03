using DbdPresence.Core.Game;
using DbdPresence.Core.Roster;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// The state between frames of the unknown-character path (D35): which match this is, what was already reported in it, and which names were seen on
/// consecutive frames. It decides nothing about names (<see cref="HudAnalyzer.ReadUnknownCharacters"/> does); it only makes sure a name goes to the
/// <see cref="IUnknownCharacterSink"/>
/// <list type="bullet">
/// <item>once per match (the key stays the same from the pre-match lobby to the scoreboard and changes after the menus were up again),</item>
/// <item>only when the same name was read on <see cref="FramesNeeded"/> consecutive frames of a reading screen that are at least <see cref="MinFrameSpacing"/> apart (a frame that changes screen shows mixed content; frames closer together may share one cached OCR read),</item>
/// <item>with the role of its position. A name that shows up in both roles in one match is reported for the first role only.</item>
/// </list>
/// Names are held in memory until the next match begins and are never written or logged.
/// </summary>
internal sealed class UnknownCharacterWatch
{
    /// <summary>On how many consecutive frames a name must be read before it is reported (an initial guess).</summary>
    public const int FramesNeeded = 2;

    /// <summary>
    /// The least time between two frames that count as evidence for the same name. The change gate returns the cached text for an unchanged region, so
    /// two quick frames are one OCR read, not two (round-2 review F6).
    /// </summary>
    public static readonly TimeSpan MinFrameSpacing = TimeSpan.FromSeconds(1);

    /// <summary>How many consecutive menu frames end a match (the menus have strong anchors; this only guards against a single misread frame).</summary>
    public const int MenuFramesToEndMatch = 3;

    /// <summary>The most names held for one match.</summary>
    public const int MaxNamesPerMatch = 16;

    private readonly IUnknownCharacterSink _sink;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private Dictionary<string, (string Name, Role Role, int Frames, DateTimeOffset Counted)> _pending = new(StringComparer.Ordinal);
    private long _matchKey;
    private bool _inMatch;
    private bool _matchEnded;
    private int _menuFrames;
    private int _lobbyFrames;

    /// <summary>Initializes a new instance of the <see cref="UnknownCharacterWatch"/> class.</summary>
    /// <param name="sink">Receives each sighting.</param>
    /// <param name="firstMatchKey">The key of the first match; a later match gets a higher one. Use a value that differs between app runs.</param>
    public UnknownCharacterWatch(IUnknownCharacterSink sink, long firstMatchKey)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _matchKey = firstMatchKey;
    }

    /// <summary>Gets the key of the match in progress.</summary>
    public long MatchKey => _matchKey;

    /// <summary>Forgets what was seen on consecutive frames. The match and what it already reported are kept: a reset in mid-match (detection paused) is not a new match.</summary>
    public void Reset()
    {
        _pending = new Dictionary<string, (string, Role, int, DateTimeOffset)>(StringComparer.Ordinal);
        _menuFrames = 0;
        _lobbyFrames = 0;
    }

    /// <summary>Takes one frame's reading.</summary>
    /// <param name="screen">The screen the frame showed.</param>
    /// <param name="found">The unknown names read on this frame (empty on every screen that is not read).</param>
    /// <param name="now">The time of the frame.</param>
    public void Observe(ScreenKind screen, IReadOnlyList<UnknownCharacterCandidate> found, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(found);
        TrackMatch(screen);
        var next = new Dictionary<string, (string, Role, int, DateTimeOffset)>(StringComparer.Ordinal);
        foreach (var candidate in found)
        {
            if (candidate.Role is not (Role.Killer or Role.Survivor) || RosterNameFold.FoldForReport(candidate.Name) is not { } folded || _reported.Contains(folded) || next.ContainsKey(folded))
            {
                continue;
            }

            var known = _pending.TryGetValue(folded, out var before) && before.Role == candidate.Role;
            if (known && now - before.Counted < MinFrameSpacing)
            {
                // Too soon after the frame that last counted: probably the same OCR read again, so it adds no evidence.
                next[folded] = before;
                continue;
            }

            var frames = known ? before.Frames + 1 : 1;
            if (frames >= FramesNeeded && _reported.Count < MaxNamesPerMatch)
            {
                _ = _reported.Add(folded);
                _sink.Report(new UnknownCharacterSighting(candidate.Name, candidate.Role, _matchKey));
                continue;
            }

            next[folded] = (candidate.Name, candidate.Role, frames, now);
        }

        _pending = next;
    }

    private void TrackMatch(ScreenKind screen)
    {
        var menu = screen is ScreenKind.MainMenu or ScreenKind.ModeList or ScreenKind.Lobby or ScreenKind.Searching;
        _menuFrames = menu ? _menuFrames + 1 : 0;
        _lobbyFrames = screen == ScreenKind.PreMatchLobby ? _lobbyFrames + 1 : 0;
        if (_inMatch && (_menuFrames >= MenuFramesToEndMatch || (_matchEnded && _lobbyFrames >= FramesNeeded)))
        {
            NextMatch();
        }

        if (screen is ScreenKind.PreMatchLobby or ScreenKind.MatchStart or ScreenKind.TrialHud or ScreenKind.Spectating or ScreenKind.MatchDetails or ScreenKind.Scoreboard or ScreenKind.MatchResult)
        {
            _inMatch = true;
            _matchEnded |= screen is ScreenKind.Scoreboard or ScreenKind.MatchResult;
        }
    }

    private void NextMatch()
    {
        _matchKey++;
        _reported.Clear();
        _inMatch = false;
        _matchEnded = false;
    }
}
