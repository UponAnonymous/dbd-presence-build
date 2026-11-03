// Covers: D35 and ADR-0017 (new characters are learned from the game: unknown character names are reported from character-name positions only),
// FR-ROS-05 (reports hold the name, the role and a per-match key, never a player name), DETECTION_VALIDATION section 7 (nothing here publishes; detectors stay Disabled).
// MECHANICS ONLY: frames are drawn text on flat colour read back by a fake recogniser, and the names are invented. They prove the rules (position, two lines,
// the name filters, once per match, the vocabulary swap), not that Windows OCR reads the real game's rows this way. Detection accuracy on real captures is UNKNOWN (PENDING-ENV).
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Core.Roster;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class UnknownCharacterTests
{
    private const string NewSurvivor = "ZELDA QUILLFEATHER";
    private const string NewKiller = "THE LANTERN";

    private sealed class RecordingSink : IUnknownCharacterSink
    {
        public List<UnknownCharacterSighting> Seen { get; } = [];

        public void Report(UnknownCharacterSighting sighting) => Seen.Add(sighting);
    }

    private static List<PlayerLine> Game(string? survivorRow2 = null, string? killer = null, string row2Player = "CHARLIE", string? row3Player = null)
    {
        var rows = HudScenes.SurvivorGame(killer: killer ?? "THE LICH");
        rows[2] = new PlayerLine(survivorRow2 ?? rows[2].Character, row2Player);
        if (row3Player is not null)
        {
            rows[3] = rows[3] with { Name = row3Player };
        }

        return rows;
    }

    private static ImageBuffer Scoreboard(List<PlayerLine> rows) => HudScenes.Scoreboard(1920, rows, 1);

    private static ImageBuffer Tab(List<PlayerLine> rows) => HudScenes.MatchDetails(1920, rows, 1, "ORMOND - MOUNT ORMOND RESORT");

    private static async Task Frames(HudHarness h, ImageBuffer image, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _ = await h.Frame(image);
        }
    }

    // ---- What is reported, with which role ------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownSurvivorName_OnTheScoreboard_IsReportedOnceWithTheSurvivorRole()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Scoreboard(Game(survivorRow2: NewSurvivor)), 6);

        var sighting = Assert.Single(sink.Seen);
        Assert.Equal(NewSurvivor, sighting.Name);
        Assert.Equal(Role.Survivor, sighting.Role);
    }

    [Fact]
    public async Task AnUnknownKillerName_OnTheTwoLineLastRow_IsReportedWithTheKillerRole()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Scoreboard(Game(killer: NewKiller)), 3);

        var sighting = Assert.Single(sink.Seen);
        Assert.Equal(NewKiller, sighting.Name);
        Assert.Equal(Role.Killer, sighting.Role);
    }

    [Fact]
    public async Task OnTabMatchDetails_AnUnknownNameIsReportedToo()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Tab(Game(killer: NewKiller)), 2);

        Assert.Equal(Role.Killer, Assert.Single(sink.Seen).Role);
    }

    [Fact]
    public async Task OnThePreMatchLobby_AnUnknownSurvivorPairIsReported_AndItIsTheSameMatchAsTheScoreboard()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, HudScenes.PreMatchSurvivor(1920, NewSurvivor), 2);
        Assert.Equal(Role.Survivor, Assert.Single(sink.Seen).Role);
        await Frames(h, HudScenes.TrialHud(1920), 2);
        await Frames(h, Scoreboard(Game(survivorRow2: NewSurvivor)), 3);

        var sighting = Assert.Single(sink.Seen);
        Assert.Equal(NewSurvivor, sighting.Name);
    }

    [Fact]
    public async Task TwoNewCharactersInOneMatch_AreBothReported_UnderOneKey()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Scoreboard(Game(survivorRow2: NewSurvivor, killer: NewKiller)), 3);

        Assert.Equal([Role.Survivor, Role.Killer], sink.Seen.Select(s => s.Role).Order());
        Assert.Single(sink.Seen.Select(s => s.MatchKey).Distinct());
    }

    // ---- Once per match, a new key for the next match -------------------------------------------------------------------

    [Fact]
    public async Task ANewMatch_AfterTheMenusWereUp_ReportsTheSameNameAgainUnderANewKey()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var board = Scoreboard(Game(survivorRow2: NewSurvivor));

        await Frames(h, board, 3);
        await Frames(h, HudScenes.MainMenu(1920), UnknownCharacterWatch.MenuFramesToEndMatch);
        await Frames(h, board, 3);

        Assert.Equal(2, sink.Seen.Count);
        Assert.NotEqual(sink.Seen[0].MatchKey, sink.Seen[1].MatchKey);
    }

    [Fact]
    public async Task OneMisreadMenuFrame_DoesNotMakeANewMatch()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var board = Scoreboard(Game(survivorRow2: NewSurvivor));

        await Frames(h, board, 3);
        await Frames(h, HudScenes.MainMenu(1920), 2);
        await Frames(h, board, 3);

        _ = Assert.Single(sink.Seen);
    }

    [Fact]
    public async Task AScoreboardThenTheNextPreMatchLobby_IsANewMatch_EvenWithoutAMenuBetween()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Scoreboard(Game(survivorRow2: NewSurvivor)), 3);
        await Frames(h, HudScenes.PreMatchSurvivor(1920, NewSurvivor), 3);

        Assert.Equal(2, sink.Seen.Count);
        Assert.NotEqual(sink.Seen[0].MatchKey, sink.Seen[1].MatchKey);
    }

    [Fact]
    public async Task AResetInTheMiddleOfAMatch_IsNotANewMatch()
    {
        // The loop resets the pipeline whenever it stops reading (detection paused, a failed cycle): the same match must not be counted twice.
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var board = Scoreboard(Game(survivorRow2: NewSurvivor));

        await Frames(h, board, 3);
        h.Pipeline.Reset();
        await Frames(h, board, 3);

        _ = Assert.Single(sink.Seen);
    }

    [Fact]
    public async Task ANameSeenOnOneFrameOnly_IsNotReported()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        _ = await h.Frame(Scoreboard(Game(survivorRow2: NewSurvivor)));
        await Frames(h, Scoreboard(Game()), 3);

        Assert.Empty(sink.Seen);
    }

    // ---- What is never reported ----------------------------------------------------------------------------------------

    [Fact]
    public async Task KnownCharacters_AreNeverReported_AlsoWhenOcrDropsOrChangesLetters()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        h.Ocr.Noise = text => text.Replace("CLAUDETTE", "CLAUDETE", StringComparison.Ordinal).Replace("KARLSSON", "KARLS5ON", StringComparison.Ordinal).Replace("LICH", "LlCH", StringComparison.Ordinal);

        await Frames(h, Scoreboard(Game()), 4);
        await Frames(h, Tab(Game()), 4);
        await Frames(h, HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task AKillerRowOfOneLine_IsNeverAKillerName()
    {
        // Review U5 F7: a player may be called like a killer, and a row without a second line is not understood.
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var rows = Game();
        rows[4] = new PlayerLine(null, NewKiller);

        await Frames(h, Scoreboard(rows), 4);
        await Frames(h, Tab(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task ASurvivorRowOfOneLine_IsNotReportedEither()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var rows = Game();
        rows[2] = new PlayerLine(null, NewSurvivor);

        await Frames(h, Scoreboard(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task AKnownRowOfOneLine_DoesNotMakeAOneLineRowACharacterPosition()
    {
        // Round-2 review F1 (FR-ROS-07): a known one-line row must not let a one-line row holding a two-word player name through.
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var rows = Game();
        rows[0] = new PlayerLine(null, "MEG THOMAS");
        rows[2] = new PlayerLine(null, NewSurvivor);

        await Frames(h, Scoreboard(rows), 4);
        await Frames(h, Tab(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task WhileTheOwnersNameIsUnknown_NothingIsReported()
    {
        // Round-2 review M1: the owner's own player name repeats in every match, and it can only be excluded when it is known.
        var sink = new RecordingSink();
        var h = new HudHarness(ownerName: null, unknownSink: sink);

        await Frames(h, Scoreboard(Game(survivorRow2: NewSurvivor)), 5);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task TheDiscordDisplayName_IsNeverReported_AndOtherNamesStillAre()
    {
        var excluded = new RecordingSink();
        var hExcluded = new HudHarness(unknownSink: excluded, excludedName: () => "Zelda Quillfeather");
        var open = new RecordingSink();
        var hOpen = new HudHarness(unknownSink: open, excludedName: () => "Somebody Else");

        await Frames(hExcluded, Scoreboard(Game(survivorRow2: NewSurvivor)), 4);
        await Frames(hOpen, Scoreboard(Game(survivorRow2: NewSurvivor)), 4);

        Assert.Empty(excluded.Seen);
        _ = Assert.Single(open.Seen);
    }

    [Fact]
    public async Task FramesCloserThanOneSecond_AreOneReadOfTheSameText_AndDoNotConfirmAName()
    {
        // Round-2 review F6: the change gate returns cached text for an unchanged region, so quick frames are not independent evidence.
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var board = Scoreboard(Game(survivorRow2: NewSurvivor));

        _ = await h.Frame(board, advance: 0.2);
        _ = await h.Frame(board, advance: 0.2);
        _ = await h.Frame(board, advance: 0.2);
        Assert.Empty(sink.Seen);

        _ = await h.Frame(board, advance: 1.0);
        _ = await h.Frame(board, advance: 1.0);
        _ = Assert.Single(sink.Seen);
    }

    [Fact]
    public async Task ANameOnTheSecondLine_IsAPlayerName_AndNeverReported()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var rows = Game(row2Player: NewSurvivor, row3Player: NewKiller);

        await Frames(h, Scoreboard(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Theory]
    [InlineData("ZELDA QUILLFEATHER")]
    [InlineData("ZELDAQUILLFEATHER")]
    [InlineData("ZELDAQUILLFEATHER77")]
    [InlineData("ZELDA-QUILLFEATHER")]
    [InlineData("ZELDA QUILLFEATHER 99")]
    [InlineData("ZELDA QUILLFEATHFR")]
    public async Task ACharacterLookingLineThatIsAlsoAPlayerNameOnScreen_IsNeverReported(string playerName)
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var rows = Game(survivorRow2: NewSurvivor, row3Player: playerName);

        await Frames(h, Scoreboard(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Theory]
    [InlineData("ZELDA QUILLFEATHER77")]
    [InlineData("ZELDA QUILL 3")]
    [InlineData("NIGHTSHADE")]
    [InlineData("THE LANTERN 45")]
    public async Task AFirstLineThatIsNotShapedLikeACharacterName_IsNeverReported(string line)
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Scoreboard(Game(survivorRow2: line)), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task APlayerNameWithAnUnderscore_IsStillAPlayerName()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        h.Ocr.Noise = text => text.Replace('-', '_');
        var rows = Game(survivorRow2: NewSurvivor, row3Player: "ZELDA-QUILLFEATHER");

        await Frames(h, Scoreboard(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task TheOwnersName_IsNeverReported()
    {
        var sink = new RecordingSink();
        var h = new HudHarness("SILENT WATCHER", unknownSink: sink);
        var rows = Game();
        rows[2] = new PlayerLine("SILENT WATCHER", "CHARLIE");

        await Frames(h, Scoreboard(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task AnUnknownLineOnAScreenWithoutAKnownRowToCompareWith_IsNotReported()
    {
        // No row shows a known character with a second line, so nothing says that the first line of a row is the character position.
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var rows = new List<PlayerLine>
        {
            new("SILENT WATCHER", "ALPHA"),
            new("QUIET LISTENER", "BRAVO"),
            new("LOUD SPEAKER", "CHARLIE"),
            new("OTHER PERSON", "DELTA"),
            new("LAST PERSON", "ECHO"),
        };

        await Frames(h, Scoreboard(rows), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task ARowWithMoreLinesThanTheKnownRows_IsNotUnderstood_AndNotReported()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var image = Scoreboard(Game());
        h.Ocr.Noise = text => text.Contains("CHARLIE", StringComparison.Ordinal) ? "ZELDA QUILLFEATHER\nCHARLIE\n45 MS" : text;

        await Frames(h, image, 4);

        Assert.Empty(sink.Seen);
    }

    [Theory]
    [InlineData("DWIGHT FAIRWOOD")]
    [InlineData("NEA KARLSTAD")]
    public async Task ANameNearAKnownCharacter_IsTakenForAMisreading_AndNotReported(string text)
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Scoreboard(Game(survivorRow2: text)), 4);

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task ANameFartherFromEveryKnownCharacterThanTheGuard_IsReported()
    {
        // The guard distance is an initial guess (UNKNOWN until measured); this pins that a clearly different name still gets through.
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        await Frames(h, Scoreboard(Game(survivorRow2: "DWIGHT FAIRMONT")), 3);

        _ = Assert.Single(sink.Seen);
    }

    [Fact]
    public async Task OnEveryOtherScreen_NothingIsReported()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);

        foreach (var image in new[] { HudScenes.MainMenu(1920), HudScenes.Lobby(1920), HudScenes.TrialHud(1920), HudScenes.MatchResult(1920), HudScenes.Blank(1920) })
        {
            await Frames(h, image, 3);
        }

        Assert.Empty(sink.Seen);
    }

    [Fact]
    public async Task WithoutASink_NothingIsReadForUnknownNames_AndDetectionIsUnchanged()
    {
        var withSink = new HudHarness(unknownSink: new RecordingSink());
        var without = new HudHarness();
        var image = Scoreboard(Game(survivorRow2: NewSurvivor));

        var a = await withSink.Frame(image);
        var b = await without.Frame(image);

        Assert.Equal(a.Observations.Select(o => (o.Kind, o.Value)), b.Observations.Select(o => (o.Kind, o.Value)));
    }

    // ---- The shape of a name ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Zelda Quillfeather")]
    [InlineData("THE LANTERN")]
    [InlineData("Marie-Anne Tremblay")]
    [InlineData("Leon S. Kennedy")]
    [InlineData("Jean O'Neil")]
    [InlineData("Jean O’Neil")]
    public void ShapedNames_AreAccepted(string line) => Assert.True(CharacterNameShape.IsShaped(line));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xX_Slayer_Xx")]
    [InlineData("DEATHSLINGER77")]
    [InlineData("Player 1")]
    [InlineData("THE LANTERN 45")]
    [InlineData("Shadow_Wolf Xyz")]
    [InlineData("Nightshade")]
    [InlineData("A B")]
    [InlineData("The X")]
    [InlineData("-Zelda Quill-")]
    [InlineData("Zelda -- Quill")]
    [InlineData("Zelda [Quill]")]
    [InlineData("Zelda QuillЖ")]
    [InlineData("A Very Long Name That Has Too Many Words In It")]
    [InlineData("One Two Three Four Five Six")]
    public void OtherLines_AreRejected(string line) => Assert.False(CharacterNameShape.IsShaped(line));

    // ---- A larger vocabulary mid-session ----------------------------------------------------------------------------------

    [Fact]
    public async Task AfterTheVocabularyIsSwapped_ANewCharacterIsRecognised_AndNoLongerReportedAsUnknown()
    {
        var sink = new RecordingSink();
        var h = new HudHarness(unknownSink: sink);
        var board = Scoreboard(HudScenes.SurvivorGame(ownerCharacter: NewSurvivor));

        var before = await h.Frame(board);
        _ = await h.Frame(board);
        Assert.Null(HudHarness.Of(before, ObservationKind.Character)?.Value);
        _ = Assert.Single(sink.Seen);

        var learned = new VocabularyEntry("zelda-quillfeather", "Zelda Quillfeather", null, null, "test", "S99");
        h.Pipeline.SetVocabulary(HudVocabulary.Create(HudHarness.Vocabulary.WithCharacters([], [learned])));
        await Frames(h, HudScenes.MainMenu(1920), UnknownCharacterWatch.MenuFramesToEndMatch);
        var after = await h.Frame(board);
        _ = await h.Frame(board);

        Assert.Equal("zelda-quillfeather", HudHarness.Of(after, ObservationKind.Character)!.Value);
        _ = Assert.Single(sink.Seen);
    }

    [Fact]
    public async Task AfterTheSwap_TheOwnersNameAndTheMatchStateAreKept()
    {
        var h = new HudHarness(ownerName: null, unknownSink: new RecordingSink());
        for (var i = 0; i < 4; i++)
        {
            _ = await h.Frame(HudScenes.MainMenu(1920));
        }

        Assert.True(h.Pipeline.OwnerNameKnown);

        var learned = new VocabularyEntry("zelda-quillfeather", "Zelda Quillfeather", null, null, "test", "S99");
        h.Pipeline.SetVocabulary(HudVocabulary.Create(HudHarness.Vocabulary.WithCharacters([], [learned])));

        Assert.True(h.Pipeline.OwnerNameKnown);
        Assert.Equal(11, h.Pipeline.Detectors.Count);
    }
}
