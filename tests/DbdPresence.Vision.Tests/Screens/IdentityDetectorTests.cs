// Covers: FR-DET-19 (the owner's own character and role from the owner's row, two cues that must agree, anonymous mode), FR-DET-22 (the killer's
// name only where it is printed). MECHANICS ONLY: drawn text and flat colour bands read back by a fake recogniser; the colour thresholds are
// initial guesses (UNKNOWN until calibrated). Not accuracy.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class IdentityDetectorTests
{
    private const string Dwight = "dwight-fairfield";
    private const string Lich = "the-lich";
    private const string Nightmare = "the-nightmare";

    private static Observation? Role(HudFrame f) => HudHarness.Of(f, ObservationKind.Role);

    private static Observation? Character(HudFrame f) => HudHarness.Of(f, ObservationKind.Character);

    private static Observation? Killer(HudFrame f) => HudHarness.Of(f, ObservationKind.Killer);

    // ---- TAB match details and the scoreboard, survivor ------------------------------------------------------------

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TabAsSurvivor_GivesRoleCharacterAndTheKillersName_AtEverySize(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.MatchDetails(width, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT"));

        Assert.Equal("Survivor", Role(frame)!.Value);
        Assert.Equal(Dwight, Character(frame)!.Value);
        Assert.Equal(Lich, Killer(frame)!.Value);
        Assert.InRange(Character(frame)!.Score, 0.6, 1.0);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TheScoreboardAsSurvivor_GivesRoleCharacterAndKiller_AtEverySize(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(width, HudScenes.SurvivorGame("DWIGHT FAIRFIELD", 2), 2));

        Assert.Equal("Survivor", Role(frame)!.Value);
        Assert.Equal(Dwight, Character(frame)!.Value);
        Assert.Equal(Lich, Killer(frame)!.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TheOwnersRowIsFound_WhereverItIs_NotJustFirst(int ownerRow)
    {
        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame("MEG THOMAS", ownerRow), ownerRow));

        Assert.Equal("meg-thomas", Character(frame)!.Value);
    }

    // ---- TAB and the scoreboard, killer ----------------------------------------------------------------------------

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TabAsKiller_TheOwnerIsTheRedLastRow_AndTheKillerNameIsTheOwnersCharacter(int width)
    {
        var rows = HudScenes.SurvivorGame("THE NIGHTMARE", 4);
        var frame = await new HudHarness().Frame(HudScenes.MatchDetails(width, rows, 4, "ORMOND - MOUNT ORMOND RESORT", survivorView: false));

        Assert.Equal("Killer", Role(frame)!.Value);
        Assert.Equal(Nightmare, Character(frame)!.Value);
        Assert.Equal(Nightmare, Killer(frame)!.Value);
    }

    [Fact]
    public async Task TheScoreboardAsKiller_HasNoMatchLine_AndTheOwnerIsTheRedRow()
    {
        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame("THE LICH", 4), 4, survivorView: false, matchLine: false));

        Assert.Equal("Killer", Role(frame)!.Value);
        Assert.Equal(Lich, Character(frame)!.Value);
    }

    [Fact]
    public async Task ARedRowUnderTheSurvivorOnlyMatchLine_IsContradictory_SoRoleAndCharacterAreUnreadable()
    {
        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame("THE LICH", 4), 4, survivorView: false, matchLine: true));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("contradicts", Role(frame)!.Evidence, StringComparison.Ordinal);
    }

    // ---- Two cues must agree ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ARowWithTheOwnersNameButNoHighlight_IsNotAccepted()
    {
        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1, ownerColour: HudScenes.Plain));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("not highlighted", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHighlightedRowWithAnotherPlayersName_IsNotTheOwners()
    {
        var rows = HudScenes.SurvivorGame();
        rows[1] = new PlayerLine("DWIGHT FAIRFIELD", "SOMEONEELSE");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("no row carries the owner's name", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOwnersNameMisreadByOneLetter_StillFindsTheRow_ButTwoLettersDoNot()
    {
        var one = HudScenes.SurvivorGame();
        one[1] = new PlayerLine("DWIGHT FAIRFIELD", "TESTOWNEQ");
        var two = HudScenes.SurvivorGame();
        two[1] = new PlayerLine("DWIGHT FAIRFIELD", "TESTOWQEQ");

        var found = await new HudHarness().Frame(HudScenes.Scoreboard(1920, one, 1));
        var lost = await new HudHarness().Frame(HudScenes.Scoreboard(1920, two, 1));

        Assert.Equal(Dwight, Character(found)!.Value);
        Assert.Null(Character(lost)!.Value);
    }

    [Fact]
    public async Task TwoHighlightedRowsWithTheOwnersName_AreRefused_NotChosenBetween()
    {
        var rows = HudScenes.SurvivorGame();
        rows[3] = new PlayerLine("LEON S. KENNEDY", HudScenes.Owner);
        var canvas = HudScenes.Canvas(1920);
        // Rows 1 and 3 are both gold and both carry the owner's name.
        for (var i = 0; i < rows.Count; i++)
        {
            canvas.Rect(HudScenes.R(HudRegionIds.ScoreBand(i)), i is 1 or 3 ? HudScenes.Gold : HudScenes.Plain);
            canvas.TextIn(HudScenes.R(HudRegionIds.ScoreRow(i)), rows[i].Character!, rows[i].Name);
        }

        canvas.TextIn(HudScenes.R(HudRegionIds.ScoreHeader), "MATCH", "SCOREBOARD");
        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
    }

    [Fact]
    public async Task AnotherGoldRowWithoutTheOwnersName_MakesTheHighlightAmbiguous()
    {
        // The colour test says two rows are gold; only one has the owner's name, but then the highlight no longer singles the owner out.
        var rows = HudScenes.SurvivorGame();
        var canvas = HudScenes.Canvas(1920);
        for (var i = 0; i < rows.Count; i++)
        {
            canvas.Rect(HudScenes.R(HudRegionIds.ScoreBand(i)), i is 1 or 3 ? HudScenes.Gold : HudScenes.Plain);
            canvas.TextIn(HudScenes.R(HudRegionIds.ScoreRow(i)), rows[i].Character!, rows[i].Name);
        }

        canvas.TextIn(HudScenes.R(HudRegionIds.ScoreHeader), "MATCH", "SCOREBOARD");
        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Character(frame)!.Value);
        Assert.Contains("same highlight colour", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InASurvivorsView_TheRedKillerRowDoesNotDisturbTheGoldOwnerRow()
    {
        // HudScenes paints the killer row (the last) red in a survivor's view; the owner's gold row is still the single candidate.
        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1));

        Assert.Equal(Dwight, Character(frame)!.Value);
    }

    [Fact]
    public async Task AGoldRowWithAKillersName_IsAContradiction_SoNeitherRoleNorCharacterIsReported()
    {
        // Review U5 F2: the row's two cues disagree with each other, so the row is not understood and it gives no role.
        var rows = HudScenes.SurvivorGame("THE LICH", 1);

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("contradicts", Role(frame)!.Evidence, StringComparison.Ordinal);
        Assert.Contains("contradicts", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedRowWithASurvivorsName_IsAContradiction_SoNeitherRoleNorCharacterIsReported()
    {
        // A killer's view (no MATCH line, so the header does not already contradict): the owner's red row names a survivor.
        var rows = HudScenes.SurvivorGame("DWIGHT FAIRFIELD", 4);

        var tab = await new HudHarness().Frame(HudScenes.MatchDetails(1920, rows, 4, "ORMOND - MOUNT ORMOND RESORT", survivorView: false));
        var scoreboard = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 4, survivorView: false, matchLine: false));

        foreach (var frame in new[] { tab, scoreboard })
        {
            Assert.Null(Role(frame)!.Value);
            Assert.Null(Character(frame)!.Value);
            Assert.Contains("contradicts", Role(frame)!.Evidence, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARowWhoseCharacterNameIsNotInTheList_StillGivesTheRole_BecauseTheTwoCuesDoNotContradict()
    {
        // The name matches and the band is gold; the character line is garbage. Nothing disagrees, so the role stands and only the character is lost.
        var rows = HudScenes.SurvivorGame();
        rows[1] = new PlayerLine("ZZZZ QQQQ", HudScenes.Owner);

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Equal("Survivor", Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
    }

    [Fact]
    public async Task WithoutTheOwnersName_NoRowIsEverChosen()
    {
        var frame = await new HudHarness(ownerName: null).Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("not known", Character(frame)!.Evidence, StringComparison.Ordinal);
        Assert.Equal(Lich, Killer(frame)!.Value); // the killer's printed name does not depend on the owner
    }

    // ---- Anonymous mode --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnonymousMode_APlayerNameThatEqualsTheCharacterName_NeverSelectsARow()
    {
        // Every player is listed by character name only: the "player name" line repeats the character. The gold highlight remains, but the name
        // cue cannot agree with it.
        var rows = new List<PlayerLine>
        {
            new("MEG THOMAS", "MEG THOMAS"),
            new("DWIGHT FAIRFIELD", "DWIGHT FAIRFIELD"),
            new("CLAUDETTE MOREL", "CLAUDETTE MOREL"),
            new("NEA KARLSSON", "NEA KARLSSON"),
            new("THE LICH", "THE LICH"),
        };

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
    }

    [Fact]
    public async Task AnOwnersNameInARow_ThatAlsoRepeatsACharacterName_IsAnonymousAndNeverSelected()
    {
        // Review U5 F5(a): the owner's name is in the row and the band is gold, but the row prints a character name twice (the character line and
        // again in the player-name line, as anonymous mode does). The row is not trusted, even though the name cue and the colour cue agree.
        var rows = HudScenes.SurvivorGame();
        rows[1] = new PlayerLine("DWIGHT FAIRFIELD", "TESTOWNER DWIGHT FAIRFIELD");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("anonymous", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameRowWithoutTheRepeatedCharacterName_IsSelectedAsUsual()
    {
        var rows = HudScenes.SurvivorGame();
        rows[1] = new PlayerLine("DWIGHT FAIRFIELD", "TESTOWNER");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Equal("Survivor", Role(frame)!.Value);
        Assert.Equal(Dwight, Character(frame)!.Value);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.2)]
    public async Task ARowThatIsBothGoldAndRed_CountsAsUnhighlighted_SoNothingIsSelected(double eachShare)
    {
        // Review U5 F5(b): the colour test cannot decide when the band passes both the gold and the red test, so the owner's row has no highlight.
        var rows = HudScenes.SurvivorGame();
        var canvas = HudScenes.Canvas(1920);
        for (var i = 0; i < rows.Count; i++)
        {
            var band = HudScenes.R(HudRegionIds.ScoreBand(i));
            canvas.Rect(band, i == 4 ? HudScenes.Red : HudScenes.Plain);
            if (i == 1)
            {
                canvas.Rect(new NormalizedRect(band.X, band.Y, band.Width * eachShare, band.Height), HudScenes.Gold);
                canvas.Rect(new NormalizedRect(band.X + (band.Width * (1 - eachShare)), band.Y, band.Width * eachShare, band.Height), HudScenes.Red);
            }

            canvas.TextIn(HudScenes.R(HudRegionIds.ScoreRow(i)), rows[i].Character!, rows[i].Name);
        }

        canvas.TextIn(HudScenes.R(HudRegionIds.ScoreHeader), "MATCH", "SCOREBOARD");
        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("not highlighted", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousMode_AnotherPlayersCharacterNameDoesNotBecomeTheOwnersRow()
    {
        // The highlighted row is another player's anonymous row; the owner's real name appears nowhere in a highlighted row.
        var rows = HudScenes.SurvivorGame();
        rows[2] = new PlayerLine("NEA KARLSSON", "NEA KARLSSON");
        rows[1] = new PlayerLine("DWIGHT FAIRFIELD", "DWIGHT FAIRFIELD");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 2));

        Assert.Null(Character(frame)!.Value);
    }

    [Fact]
    public async Task AnOwnerWhoseNameIsACharacterName_CannotUseTheNameCue_SoNothingIsPublished()
    {
        var rows = HudScenes.SurvivorGame();
        rows[1] = new PlayerLine("DWIGHT FAIRFIELD", "MEG THOMAS");

        var frame = await new HudHarness(ownerName: "Meg Thomas").Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("character name", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowThatNamesTwoDifferentCharacters_IsAmbiguous_NotGuessed()
    {
        var rows = HudScenes.SurvivorGame();
        rows[1] = new PlayerLine("DWIGHT FAIRFIELD", "MEG THOMAS TESTOWNER");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Character(frame)!.Value);
        Assert.Contains("two characters", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACharacterNameAndThePlayerNameOnOneLine_AreSplitCorrectly()
    {
        var canvas = HudScenes.Canvas(1920);
        var rows = HudScenes.SurvivorGame();
        for (var i = 0; i < rows.Count; i++)
        {
            canvas.Rect(HudScenes.R(HudRegionIds.ScoreBand(i)), i == 1 ? HudScenes.Gold : i == 4 ? HudScenes.Red : HudScenes.Plain);
            canvas.TextIn(HudScenes.R(HudRegionIds.ScoreRow(i)), rows[i].Character + " " + rows[i].Name);
        }

        canvas.TextIn(HudScenes.R(HudRegionIds.ScoreHeader), "MATCH", "SCOREBOARD");
        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Equal(Dwight, Character(frame)!.Value);
    }

    // ---- The killer's name -----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheKillersName_IsNotInferred_WhenTheKillerRowShowsOnlyAPlayerName()
    {
        // The catalogue saw a TAB capture whose killer row had no name. Then there is no killer name, and the last survivor row is not it.
        var rows = HudScenes.SurvivorGame();
        rows[4] = new PlayerLine(null, "ECHO");

        var frame = await new HudHarness().Frame(HudScenes.MatchDetails(1920, rows, 1, "ORMOND - MOUNT ORMOND RESORT"));

        Assert.Null(Killer(frame)!.Value);
        Assert.Contains("does not always print", Killer(frame)!.Evidence, StringComparison.Ordinal);
        Assert.Equal(Dwight, Character(frame)!.Value); // the owner's own row is unaffected
    }

    [Fact]
    public async Task TheKillersName_IsTheLastRowEvenWhenFewerSurvivorsAreListed()
    {
        var rows = new List<PlayerLine> { new("MEG THOMAS", "ALPHA"), new("DWIGHT FAIRFIELD", HudScenes.Owner), new("THE CANNIBAL", "ECHO") };

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Equal("the-cannibal", Killer(frame)!.Value);
    }

    [Fact]
    public async Task ASurvivorNameInTheLastRow_IsNeverReportedAsTheKiller()
    {
        var rows = HudScenes.SurvivorGame(killer: "NEA KARLSSON");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Killer(frame)!.Value);
    }

    [Theory]
    [InlineData("THE NIGHTMARE")]
    [InlineData("The Nightmare")]
    [InlineData("THE LICH")]
    public async Task APlayerCalledLikeAKiller_InALastRowWithoutACharacterLine_IsNeverTheKillersName(string playerName)
    {
        // Review U5 F7: the catalogue saw a killer row with no character name, only the player's name. A player named like a killer must not
        // become the killer.
        var rows = HudScenes.SurvivorGame();
        rows[4] = new PlayerLine(null, playerName);

        var tab = await new HudHarness().Frame(HudScenes.MatchDetails(1920, rows, 1, "ORMOND - MOUNT ORMOND RESORT"));
        var scoreboard = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Killer(tab)!.Value);
        Assert.Null(Killer(scoreboard)!.Value);
        Assert.Equal(Dwight, Character(tab)!.Value); // the owner's row is unaffected
    }

    [Fact]
    public async Task APlayerCalledLikeAKiller_UnderANonKillerCharacterName_IsAmbiguous_AndNeverTheKillersName()
    {
        var rows = HudScenes.SurvivorGame();
        rows[4] = new PlayerLine("NEA KARLSSON", "THE NIGHTMARE");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Killer(frame)!.Value);
    }

    [Fact]
    public async Task AKillerNameOnTheSecondLineOfTheLastRow_IsNotTheKillersName()
    {
        // The player name sits under the character line; a killer's name there (anonymous mode or a joke name) is not the character line.
        var rows = HudScenes.SurvivorGame();
        rows[4] = new PlayerLine("ZZZZ QQQQ", "THE LICH");

        var frame = await new HudHarness().Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Null(Killer(frame)!.Value);
    }

    [Fact]
    public async Task TheKillersName_NeedsBothLinesOfTheRow_NotJustAMatchOnASingleLine()
    {
        var rows = HudScenes.SurvivorGame();
        var canvas = HudScenes.Canvas(1920);
        for (var i = 0; i < rows.Count; i++)
        {
            canvas.Rect(HudScenes.R(HudRegionIds.ScoreBand(i)), i == 1 ? HudScenes.Gold : i == 4 ? HudScenes.Red : HudScenes.Plain);
            canvas.TextIn(HudScenes.R(HudRegionIds.ScoreRow(i)), i == 4 ? ["THE LICH"] : [rows[i].Character!, rows[i].Name]);
        }

        canvas.TextIn(HudScenes.R(HudRegionIds.ScoreHeader), "MATCH", "SCOREBOARD");
        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Killer(frame)!.Value);
        Assert.Equal(Dwight, Character(frame)!.Value);
    }

    // ---- The pre-match lobby ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task ThePreMatchLobby_PairsTheOwnersNameWithTheCharacterAbove_AsSurvivor(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.PreMatchSurvivor(width, "DWIGHT FAIRFIELD", ownerColumn: 2));

        Assert.Equal("Survivor", Role(frame)!.Value);
        Assert.Equal(Dwight, Character(frame)!.Value);
        Assert.Null(Killer(frame)); // the killer's name is not shown in the lobby
    }

    [Fact]
    public async Task ThePreMatchLobby_WithoutTheOwnersNameInAnyPair_IsUnreadable()
    {
        var frame = await new HudHarness().Frame(HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD", owner: "SOMEONEELSE"));

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
    }

    [Fact]
    public async Task ThePreMatchLobby_APairWhoseNameLineIsACharacterName_IsAnonymousAndNeverAccepted()
    {
        var others = new List<(string Character, string Name)> { ("MEG THOMAS", "MEG THOMAS"), ("CLAUDETTE MOREL", "CLAUDETTE MOREL"), ("NEA KARLSSON", "NEA KARLSSON") };
        var canvas = HudScenes.Canvas(1920).TextIn(HudScenes.R(HudRegionIds.Button), "READY");
        for (var column = 0; column < HudProfile.LobbyColumns; column++)
        {
            var (character, name) = column == 1 ? ("DWIGHT FAIRFIELD", "DWIGHT FAIRFIELD") : others[column == 0 ? 0 : column - 1];
            canvas.TextIn(HudScenes.R(HudRegionIds.LobbyPair(column)), character, name);
        }

        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TheKillerPreMatchLobby_OwnNameBesideTheRedIcon_IsKiller_ButNoCharacterIsShown(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.PreMatchKiller(width));

        Assert.Equal("Killer", Role(frame)!.Value);
        Assert.Null(Character(frame)!.Value);
        Assert.Contains("not shown in the lobby", Character(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKillerPreMatchLobby_WithoutTheRedIcon_OrWithAnotherFirstName_IsUnreadable()
    {
        var noIcon = await new HudHarness().Frame(HudScenes.PreMatchKiller(1920, redIcon: false));
        var otherName = await new HudHarness().Frame(HudScenes.PreMatchKiller(1920, firstName: "OTHERGUY"));

        Assert.Null(Role(noIcon)!.Value);
        Assert.Null(Role(otherName)!.Value);
    }

    [Fact]
    public async Task ASurvivorSeesAKillersNameFirstInTheList_ThatIsNotTheOwner()
    {
        // If the survivor lobby also lists the killer first with a red icon, the owner is still not the killer.
        var frame = await new HudHarness().Frame(HudScenes.PreMatchKiller(1920, firstName: "THE LICH"));

        Assert.Null(Role(frame)!.Value);
    }

    [Fact]
    public async Task IfTheLobbyShowsTheOwnerAsBothSurvivorAndKiller_NothingIsReported()
    {
        var canvas = HudScenes.Canvas(1920).TextIn(HudScenes.R(HudRegionIds.Button), "READY");
        canvas.TextIn(HudScenes.R(HudRegionIds.LobbyPair(0)), "DWIGHT FAIRFIELD", HudScenes.Owner);
        var list = HudScenes.R(HudRegionIds.KillerList);
        canvas.Rect(new NormalizedRect(list.X + (list.Width * 0.03), list.Y + (list.Height * 0.08), list.Width * 0.2, list.Height * 0.25), HudScenes.Red);
        canvas.TextIn(new NormalizedRect(list.X + (list.Width * 0.25), list.Y, list.Width * 0.75, list.Height), HudScenes.Owner, "ALPHA");

        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Role(frame)!.Value);
        Assert.Contains("both survivor and killer", Role(frame)!.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.03, 0.62, 0.20, 0.25)] // red beside the third line, in the icon column
    [InlineData(0.03, 0.40, 0.20, 0.20)] // red beside the second line
    [InlineData(0.70, 0.08, 0.25, 0.25)] // red beside the first line, but at the far right, outside the icon column
    public async Task TheKillerPreMatchLobby_RedAnywhereButBesideTheFirstLinesIcon_IsNotTheKillerIcon(double x, double y, double w, double h)
    {
        // Review U5 F6: the red test looks at the narrow icon column beside line 0, not at the whole list. The first name is the owner's, but the
        // only red is somewhere else in the list, so the second cue is missing.
        var list = HudScenes.R(HudRegionIds.KillerList);
        var canvas = HudScenes.Canvas(1920).TextIn(HudScenes.R(HudRegionIds.Button), "READY");
        canvas.Rect(new NormalizedRect(list.X + (list.Width * x), list.Y + (list.Height * y), list.Width * w, list.Height * h), HudScenes.Red);
        canvas.TextIn(new NormalizedRect(list.X + (list.Width * 0.25), list.Y, list.Width * 0.75, list.Height), HudScenes.Owner, "ALPHA", "BRAVO");

        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Role(frame)!.Value);
    }

    [Fact]
    public async Task TheKillerPreMatchLobby_ASmallRedIconBesideTheFirstLine_IsEnough_AndALargerListStillWorks()
    {
        // Five entries (the owner and four survivors): line 0 is the top fifth; an icon that fills the icon column of that line counts.
        var list = HudScenes.R(HudRegionIds.KillerList);
        var canvas = HudScenes.Canvas(1920).TextIn(HudScenes.R(HudRegionIds.Button), "READY");
        canvas.Rect(new NormalizedRect(list.X + (list.Width * 0.03), list.Y + (list.Height * 0.03), list.Width * 0.2, list.Height * 0.15), HudScenes.Red);
        canvas.TextIn(new NormalizedRect(list.X + (list.Width * 0.25), list.Y, list.Width * 0.75, list.Height), HudScenes.Owner, "ALPHA", "BRAVO", "CHARLIE", "DELTA");

        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Equal("Killer", Role(frame)!.Value);
    }

    [Fact]
    public void TheIconColumn_IsAProfileValue_AndMustBeUsable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithHighlight(RowHighlightSettings.InitialGuess with { IconColumnWidth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithHighlight(RowHighlightSettings.InitialGuess with { IconColumnWidth = 0.9 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithHighlight(RowHighlightSettings.InitialGuess with { IconColumnWidth = double.NaN }));
    }

    // ---- Where they report -----------------------------------------------------------------------------------------

    [Fact]
    public async Task OtherScreens_ProduceNoIdentityObservations_AtAll()
    {
        var h = new HudHarness();
        foreach (var image in new[] { HudScenes.MainMenu(1920), HudScenes.Lobby(1920), HudScenes.TrialHud(1920), HudScenes.MatchStart(1920, "ROTTEN FIELDS", "COLDWIND FARM"), HudScenes.MatchResult(1920) })
        {
            var frame = await h.Frame(image);

            Assert.Null(Role(frame));
            Assert.Null(Character(frame));
            Assert.Null(Killer(frame));
        }
    }

    [Fact]
    public async Task TheColourTestIsAProfileValue_ARedGoldProfile_ChangesWhichRowIsHighlighted()
    {
        // Swap the thresholds: with a gold band that expects blue, the gold row is no longer highlighted and nothing is accepted.
        var blue = new HueBand(200, 250, 0.4, 0.4);
        var profile = HudProfile.Catalogue.WithHighlight(RowHighlightSettings.InitialGuess with { Gold = blue });
        var h = new HudHarness(profile: profile);

        var frame = await h.Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1));

        Assert.Null(Character(frame)!.Value);
    }
}
