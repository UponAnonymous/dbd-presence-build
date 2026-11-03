using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Vision.Tests.Support;

/// <summary>One player row of TAB match details or the scoreboard.</summary>
/// <param name="Character">The character name as printed (null prints only the player name, as the game did for one killer row).</param>
/// <param name="Name">The player name.</param>
internal sealed record PlayerLine(string? Character, string Name);

/// <summary>
/// Synthetic frames of the screens in <c>hud-cue-catalogue.md</c>, drawn with drawn text and flat coloured bands at the catalogue's regions
/// (<see cref="HudProfile.Catalogue"/>). They are not game screens and use no game art; they test the mechanics of reading, never accuracy.
/// </summary>
internal static class HudScenes
{
    public const string Owner = "TESTOWNER";

    public static readonly (byte R, byte G, byte B) Background = (34, 34, 42);
    public static readonly (byte R, byte G, byte B) Gold = (212, 175, 55);
    public static readonly (byte R, byte G, byte B) Red = (170, 28, 28);
    public static readonly (byte R, byte G, byte B) Plain = (58, 58, 70);

    /// <summary>The sizes every detector is exercised at: 1080p, 1440p and 720p (all 16:9).</summary>
    public static readonly int[] Widths = [1920, 2560, 1280];

    public static HudProfile Profile { get; } = HudProfile.Catalogue;

    public static NormalizedRect R(string id) => Profile.Region(id)?.Rect ?? throw new ArgumentException($"No region '{id}'.");

    public static HudCanvas Canvas(int width) => new(width, width * 9 / 16, Background);

    private static HudCanvas WithOwnerName(HudCanvas canvas, string? owner) => owner is null ? canvas : canvas.TextIn(R(HudRegionIds.ProfileName), owner);

    public static ImageBuffer MainMenu(int width, string? owner = Owner) =>
        WithOwnerName(Canvas(width), owner)
            .TextIn(R(HudRegionIds.Escape), "QUIT [ESC]")
            .TextIn(R(HudRegionIds.MenuList), "PLAY", "PLAY EVENT", "HAUNTED BY DAYLIGHT", "ARCHIVES", "RIFT PASS", "QUESTS", "STORE")
            .ToImage();

    public static ImageBuffer ModeList(int width, bool eventList, string? owner = Owner, string eventName = "HAUNTED BY DAYLIGHT")
    {
        var canvas = WithOwnerName(Canvas(width), owner).TextIn(R(HudRegionIds.Escape), "BACK [ESC]");
        return (eventList
            ? canvas.TextIn(R(HudRegionIds.MenuList), "PLAY EVENT", eventName, "KILLER", "SURVIVOR")
            : canvas.TextIn(R(HudRegionIds.MenuList), "PLAY", "KILLER", "SURVIVOR", "LEARN TO PLAY", "CUSTOM GAME")).ToImage();
    }

    public static ImageBuffer Lobby(int width, string? owner = Owner) =>
        WithOwnerName(Canvas(width), owner)
            .TextIn(R(HudRegionIds.Escape), "BACK [ESC]")
            .TextIn(R(HudRegionIds.Button), "PLAY")
            .ToImage();

    public static ImageBuffer Searching(int width, string? eventLabel = null, string? owner = Owner)
    {
        var canvas = WithOwnerName(Canvas(width), owner)
            .TextIn(R(HudRegionIds.Button), "CANCEL")
            .TextIn(R(HudRegionIds.SearchTimer), "EST. 1:30");
        return (eventLabel is null ? canvas : canvas.TextIn(R(HudRegionIds.SearchEventLabel), eventLabel)).ToImage();
    }

    /// <summary>The survivor pre-match lobby: four survivors in four columns, character name over player name; the owner is in column <paramref name="ownerColumn"/>.</summary>
    public static ImageBuffer PreMatchSurvivor(int width, string ownerCharacter, int ownerColumn = 1, string? owner = Owner, IReadOnlyList<(string Character, string Name)>? others = null)
    {
        var canvas = WithOwnerName(Canvas(width), owner).TextIn(R(HudRegionIds.Button), "READY");
        var pool = others ?? [("MEG THOMAS", "ALPHA"), ("CLAUDETTE MOREL", "BRAVO"), ("NEA KARLSSON", "CHARLIE")];
        var next = 0;
        for (var column = 0; column < HudProfile.LobbyColumns; column++)
        {
            var (character, name) = column == ownerColumn ? (ownerCharacter, owner ?? Owner) : pool[next++ % pool.Count];
            canvas.TextIn(R(HudRegionIds.LobbyPair(column)), character, name);
        }

        return canvas.ToImage();
    }

    /// <summary>The killer pre-match lobby: the list at the right starts with the owner's name next to a red icon, then the survivors.</summary>
    public static ImageBuffer PreMatchKiller(int width, string? firstName = Owner, bool redIcon = true, string? owner = Owner)
    {
        var canvas = WithOwnerName(Canvas(width), owner).TextIn(R(HudRegionIds.Button), "READY");
        var list = R(HudRegionIds.KillerList);
        if (redIcon)
        {
            canvas.Rect(new NormalizedRect(list.X + (list.Width * 0.03), list.Y + (list.Height * 0.08), list.Width * 0.2, list.Height * 0.25), Red);
        }

        var shifted = new NormalizedRect(list.X + (list.Width * 0.25), list.Y, list.Width * 0.75, list.Height);
        return canvas.TextIn(shifted, firstName ?? "ZULU", "ALPHA", "BRAVO").ToImage();
    }

    public static ImageBuffer MatchStart(int width, string map, string realm, bool hint = true)
    {
        var canvas = Canvas(width).TextIn(R(HudRegionIds.StartMap), map).TextIn(R(HudRegionIds.StartRealm), realm);
        return (hint ? canvas.TextIn(R(HudRegionIds.StartTabHint), "TAB MATCH DETAILS") : canvas).ToImage();
    }

    /// <summary>
    /// The trial HUD: the player column. Survivors list four names (the owner first); a killer sees only the survivors, two of them, lower down.
    /// <paramref name="prompt"/> is drawn at the bottom centre, where the killer HUD prints its power prompts.
    /// </summary>
    public static ImageBuffer TrialHud(int width, bool survivorView = true, string? owner = Owner, bool spectatorBar = false, string? prompt = null, Action<HudCanvas>? decorate = null)
    {
        var canvas = Canvas(width);
        string[] names = survivorView ? [owner ?? Owner, "ALPHA", "BRAVO", "CHARLIE"] : ["", "", "ALPHA", "BRAVO"];
        for (var i = 0; i < names.Length; i++)
        {
            if (names[i].Length > 0)
            {
                canvas.TextIn(R(HudRegionIds.HudRow(i)), names[i]);
            }
        }

        if (prompt is not null)
        {
            canvas.TextIn(R(HudRegionIds.PowerPrompt), prompt);
        }

        decorate?.Invoke(canvas);
        return (spectatorBar ? canvas.TextIn(R(HudRegionIds.SpectateBar), "A ALPHA D") : canvas).ToImage();
    }

    public static ImageBuffer MatchResult(int width, string label = "MATCH RESULT", string? outcome = "ESCAPED")
    {
        var canvas = Canvas(width).TextIn(R(HudRegionIds.ResultLabel), label);
        return (outcome is null ? canvas : canvas.TextIn(R(HudRegionIds.ResultOutcome), outcome)).ToImage();
    }

    /// <summary>The scoreboard. The owner's row has <paramref name="ownerColour"/> behind it; in a survivor's view the killer row (the last) is red.</summary>
    public static ImageBuffer Scoreboard(int width, IReadOnlyList<PlayerLine> rows, int ownerRow, bool survivorView = true, bool matchLine = true, (byte R, byte G, byte B)? ownerColour = null) =>
        PlayerScreen(width, rows, ownerRow, survivorView, ownerColour, scoreboard: true, matchLine);

    public static ImageBuffer MatchDetails(int width, IReadOnlyList<PlayerLine> rows, int ownerRow, string realmMap, bool survivorView = true, (byte R, byte G, byte B)? ownerColour = null)
    {
        var canvas = Canvas(width);
        PaintRows(canvas, rows, ownerRow, survivorView, ownerColour, scoreboard: false);
        canvas.TextIn(R(HudRegionIds.TabTabs), "PLAYERS  QUESTS  KILLER POWER").TextIn(R(HudRegionIds.TabRealmMap), realmMap);
        return canvas.ToImage();
    }

    public static ImageBuffer Settings(int width) =>
        Canvas(width).TextIn(R(HudRegionIds.SettingsTabs), "GENERAL GRAPHICS CONTROLS GAMEPLAY AUDIO ACCESSIBILITY").TextIn(R(HudRegionIds.Escape), "BACK [ESC]").ToImage();

    public static ImageBuffer Blank(int width) => Canvas(width).ToImage();

    public static List<PlayerLine> SurvivorGame(string ownerCharacter = "DWIGHT FAIRFIELD", int ownerRow = 1, string killer = "THE LICH")
    {
        var rows = new List<PlayerLine>
        {
            new("MEG THOMAS", "ALPHA"),
            new("CLAUDETTE MOREL", "BRAVO"),
            new("NEA KARLSSON", "CHARLIE"),
            new("LEON S. KENNEDY", "DELTA"),
            new(killer, "ECHO"),
        };
        rows[ownerRow] = new PlayerLine(ownerCharacter, Owner);
        return rows;
    }

    private static ImageBuffer PlayerScreen(int width, IReadOnlyList<PlayerLine> rows, int ownerRow, bool survivorView, (byte R, byte G, byte B)? ownerColour, bool scoreboard, bool matchLine)
    {
        var canvas = Canvas(width);
        PaintRows(canvas, rows, ownerRow, survivorView, ownerColour, scoreboard);
        if (matchLine)
        {
            canvas.TextIn(R(HudRegionIds.ScoreHeader), "MATCH", "SCOREBOARD");
        }
        else
        {
            canvas.TextIn(R(HudRegionIds.ScoreHeader), "SCOREBOARD");
        }

        return canvas.ToImage();
    }

    private static void PaintRows(HudCanvas canvas, IReadOnlyList<PlayerLine> rows, int ownerRow, bool survivorView, (byte R, byte G, byte B)? ownerColour, bool scoreboard)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var band = R(scoreboard ? HudRegionIds.ScoreBand(i) : HudRegionIds.TabBand(i));
            var text = R(scoreboard ? HudRegionIds.ScoreRow(i) : HudRegionIds.TabRow(i));
            var colour = i == ownerRow ? ownerColour ?? (survivorView ? Gold : Red) : survivorView && i == rows.Count - 1 ? Red : Plain;
            canvas.Rect(band, colour);
            var lines = rows[i].Character is { } character ? new[] { character, rows[i].Name } : [rows[i].Name];
            canvas.TextIn(text, lines);
        }
    }
}
