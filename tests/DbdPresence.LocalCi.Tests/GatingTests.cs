// Covers: FR-CI-03 (no new runner, and no download or image build, while Dead by Daylight runs; the process list reads names only). A Linux job does not wait for the owner to be idle
// (decision D41: only Linux jobs run on the PC, in their own virtual machine); QA round 3, minor (a build of the game whose program has another name: the owner adds the name in game-names.txt in the
// data folder; the product's own settings file, which lives where any program of the owner can change it, is not read). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class GatingTests : IDisposable
{
    private readonly FakeProcesses _processes = new();
    private readonly TempFolder _folder = new();
    private readonly MemoryCiLog _log = new(new Redactor());
    private readonly JobGate _gate;

    public GatingTests()
    {
        _gate = new JobGate(_processes);
    }

    public void Dispose() => _folder.Dispose();

    private GameNames Names() => new(_folder.Paths.GameNamesFile, _log);

    // ---- the owner's extra names

    [Theory]
    [InlineData("DeadByDaylight-Win64-Shipping")]
    [InlineData("DeadByDaylight-EGS-Shipping")]
    [InlineData("DeadByDaylight")]
    public void WhileTheGameRuns_NothingMayStart(string process)
    {
        _processes.Running.Add(process);

        var decision = _gate.Check();

        Assert.False(decision.Allowed);
        Assert.Contains("Dead by Daylight", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheGame_AJobMayStartAtAnyTime_EvenWhileTheOwnerUsesThePc()
    {
        _processes.Running.Add("notepad");
        _processes.Running.Add("Discord");

        Assert.True(_gate.Check().Allowed);
    }

    [Fact]
    public void TheGameNamesAreOnlyLookedAt_ByName_AndTheListIsTheThreeProgramNames()
    {
        Assert.Equal(["DeadByDaylight-Win64-Shipping", "DeadByDaylight-EGS-Shipping", "DeadByDaylight"], LocalCiDefaults.GameProcessNames);
    }

    [Fact]
    public void WithoutTheFile_TheGameNamesAreTheThreeBuiltInOnes()
    {
        Assert.Equal(LocalCiDefaults.GameProcessNames, Names().Current());
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void TheOwnersNames_AreAddedAfterTheBuiltInOnes_WithoutExe_WithoutDuplicates_AndWithoutComments()
    {
        File.WriteAllText(_folder.Paths.GameNamesFile, "# my other build of the game\nDeadByDaylight-WinGDK-Shipping.exe\r\n  OtherGame  # note\nDeadByDaylight-EGS-Shipping.EXE\notherGAME\n\n");

        Assert.Equal([.. LocalCiDefaults.GameProcessNames, "DeadByDaylight-WinGDK-Shipping", "OtherGame"], Names().Current());
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void AGateWithTheOwnersNames_StopsWhileThatProgramRuns()
    {
        File.WriteAllText(_folder.Paths.GameNamesFile, "DeadByDaylight-WinGDK-Shipping\n");
        var gate = new JobGate(_processes, Names().Current);
        _processes.Running.Add("DeadByDaylight-WinGDK-Shipping");

        var decision = gate.Check();

        Assert.False(decision.Allowed);
        Assert.Contains("Dead by Daylight", decision.Reason, StringComparison.Ordinal);
        Assert.True(_gate.Check().Allowed);
    }

    [Fact]
    public void TheFileIsReadAtEveryCheck_SoAnEditTakesEffectWithoutARestart()
    {
        var gate = new JobGate(_processes, Names().Current);
        _processes.Running.Add("NewBuild");
        Assert.True(gate.Check().Allowed);

        File.WriteAllText(_folder.Paths.GameNamesFile, "NewBuild\n");

        Assert.False(gate.Check().Allowed);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("-leading-dash")]
    [InlineData("bad;name")]
    [InlineData("n\u00e4me")]
    public void ALineThatIsNotAPlainProgramName_IsIgnored_AndSaidInTheLogOnce(string line)
    {
        File.WriteAllText(_folder.Paths.GameNamesFile, line + "\nGood\n");
        var names = Names();

        Assert.Equal([.. LocalCiDefaults.GameProcessNames, "Good"], names.Current());
        _ = names.Current();

        Assert.Single(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("not a plain program name", StringComparison.Ordinal));
    }

    [Fact]
    public void AtMostTwentyNamesCount_AndAnEmptyOrCommentOnlyFileAddsNothing()
    {
        var many = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"Game{i}"));

        var names = GameNames.Parse(many, out var ignored);

        Assert.Equal(GameNames.MostExtraNames, names.Count);
        Assert.Equal("Game0", names[0]);
        Assert.Equal("Game19", names[^1]);
        Assert.Empty(ignored);
        Assert.Empty(GameNames.Parse(null, out _));
        Assert.Empty(GameNames.Parse("# nothing\n   \n", out _));
    }

    [Fact]
    public void AFileThatIsTooBig_IsNotRead_AndSaidInTheLog()
    {
        File.WriteAllText(_folder.Paths.GameNamesFile, new string('x', GameNames.MostBytes + 1));

        Assert.Equal(LocalCiDefaults.GameProcessNames, Names().Current());
        Assert.Contains(_log.Lines, l => l.Contains("is larger than 8 KB, so it was not read", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderWhereTheFileShouldBe_IsSaidInTheLog_AndTheBuiltInNamesStay()
    {
        Directory.CreateDirectory(_folder.Paths.GameNamesFile);

        Assert.Equal(LocalCiDefaults.GameProcessNames, Names().Current());
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("is a folder, not a file", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameThatIsAlreadyBuiltIn_IsNotAddedTwice_WhateverItsCase()
    {
        File.WriteAllText(_folder.Paths.GameNamesFile, "deadbydaylight\n");

        Assert.Equal(LocalCiDefaults.GameProcessNames, Names().Current());
    }

    [Fact]
    public void AnIgnoredLine_IsShownWhole_UpTo40Characters_AndShortenedBeyond()
    {
        var exactly = "bad name " + new string('x', 31);
        var longer = "bad name " + new string('y', 32);

        _ = GameNames.Parse(exactly + "\n" + longer + "\n", out var ignored);

        Assert.Equal([exactly, longer[..40] + "..."], ignored);
    }
}
