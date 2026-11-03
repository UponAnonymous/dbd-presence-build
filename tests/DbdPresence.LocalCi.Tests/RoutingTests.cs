// Covers: FR-CI-09 (which copy of the program does what: setup in the copy that was started, everything else in the installed copy, the loop only there and only with the administrator
// rights, the loop's start checks), security review 2026-10-30 C1 H3. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class RoutingTests
{
    [Theory]
    [InlineData(LocalCiCommand.Help)]
    [InlineData(LocalCiCommand.Unknown)]
    [InlineData(LocalCiCommand.Menu)]
    public void TheMenuAndTheHelp_NeedNoAdministratorRights_AndShowNothingFromTheDataFolder(LocalCiCommand command)
    {
        Assert.Equal(RoutingAction.RunHere, RoutingDecision.For(command, isAdministrator: false, isInstalledCopy: false, isInstalled: false).Action);
    }

    [Fact]
    public void Setup_RunsInTheCopyThatWasStarted_AfterTheAdministratorPrompt()
    {
        Assert.Equal(new RoutingDecision(RoutingAction.RelaunchElevated, UseInstalledCopy: false), RoutingDecision.For(LocalCiCommand.Setup, false, false, true));
        Assert.Equal(RoutingAction.RunHere, RoutingDecision.For(LocalCiCommand.Setup, true, false, true).Action);
        Assert.Equal(RoutingAction.RunHere, RoutingDecision.For(LocalCiCommand.Setup, true, true, true).Action);
    }

    [Theory]
    [InlineData(LocalCiCommand.Status)]
    [InlineData(LocalCiCommand.SelfTest)]
    [InlineData(LocalCiCommand.Uninstall)]
    public void TheOtherCommands_AskForAdministratorRights_ThenGoToTheInstalledCopy(LocalCiCommand command)
    {
        Assert.Equal(new RoutingDecision(RoutingAction.RelaunchElevated, UseInstalledCopy: true), RoutingDecision.For(command, false, false, true));
        Assert.Equal(new RoutingDecision(RoutingAction.RelaunchElevated, UseInstalledCopy: false), RoutingDecision.For(command, false, false, false));
        Assert.Equal(RoutingAction.HandOverToInstalled, RoutingDecision.For(command, true, false, true).Action);
        Assert.Equal(RoutingAction.RunHere, RoutingDecision.For(command, true, true, true).Action);
        Assert.Equal(RoutingAction.RunHere, RoutingDecision.For(command, true, false, false).Action);
    }

    [Fact]
    public void TheLoop_RunsOnlyFromTheInstalledCopy_WithAdministratorRights()
    {
        Assert.Equal(RoutingAction.RunHere, RoutingDecision.For(LocalCiCommand.Serve, true, true, true).Action);
        Assert.Equal(RoutingAction.HandOverToInstalled, RoutingDecision.For(LocalCiCommand.Serve, true, false, true).Action);

        var notInstalled = RoutingDecision.For(LocalCiCommand.Serve, true, false, false);
        Assert.Equal(RoutingAction.Refuse, notInstalled.Action);
        Assert.Contains("setup", notInstalled.Message, StringComparison.OrdinalIgnoreCase);

        var plain = RoutingDecision.For(LocalCiCommand.Serve, false, true, true);
        Assert.Equal(RoutingAction.Refuse, plain.Action);
        Assert.Contains("administrator", plain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheElevatedWords_NameTheCommandAndTheOwner()
    {
        var parsed = new ParsedCommand(LocalCiCommand.SelfTest, true);

        Assert.Equal("selftest --elevated --owner S-1-5-21-9", parsed.ElevatedArguments("S-1-5-21-9"));
        Assert.Equal("setup --elevated", new ParsedCommand(LocalCiCommand.Setup, false).ElevatedArguments(null));
        Assert.Throws<InvalidOperationException>(() => new ParsedCommand(LocalCiCommand.Menu, false).ElevatedArguments(null));
    }

    [Fact]
    public void TheElevatedWords_AreParsedBack()
    {
        var parsed = ParsedCommand.Parse(["selftest", "--elevated", "--owner", "S-1-5-21-9"]);

        Assert.Equal(LocalCiCommand.SelfTest, parsed.Command);
        Assert.True(parsed.Elevated);
        Assert.Equal("S-1-5-21-9", parsed.OwnerSid);
        Assert.Null(ParsedCommand.Parse(["status", "--elevated"]).OwnerSid);
        Assert.Null(ParsedCommand.Parse(["status", "--owner"]).OwnerSid);
    }

    [Fact]
    public void TheLoopDoesNotStart_UnlessItRunsFromTheInstalledCopy_InAPlaceNobodyCanChange_WithAClosedDataFolder()
    {
        var installer = new FakeInstaller { IsInstalled = true };
        var storage = new FakeProtectedStorage();

        Assert.Empty(ServeStartCheck.Problems(installer, installer.InstalledPath, storage));

        var elsewhere = ServeStartCheck.Problems(installer, @"C:\Users\owner\Downloads\DbdLocalCi.exe", storage);
        Assert.Contains(elsewhere, p => p.Contains("runs only from", StringComparison.Ordinal));

        installer.LocationProblems.Add("the program folder lets every user change the program");
        storage.DataFolderProblems.Add("the data folder gives access to all users");
        var both = ServeStartCheck.Problems(installer, installer.InstalledPath, storage);
        Assert.Contains("the program folder lets every user change the program", both);
        Assert.Contains("the data folder gives access to all users", both);
    }

    /// <summary>QA M8: variables of the owner's own account can make the .NET runtime load code into the loop, which runs with administrator rights.</summary>
    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER")]
    [InlineData("CORECLR_PROFILER_PATH")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    [InlineData("COR_ENABLE_PROFILING")]
    [InlineData("COR_PROFILER_PATH")]
    public void TheLoopDoesNotStart_WhenTheEnvironmentAsksTheRuntimeToLoadOtherCode(string variable)
    {
        var installer = new FakeInstaller { IsInstalled = true };
        var storage = new FakeProtectedStorage();

        var problems = ServeStartCheck.Problems(installer, installer.InstalledPath, storage, name => name == variable ? @"C:\Users\owner\hook.dll" : null);

        var problem = Assert.Single(problems);
        Assert.Contains(variable, problem, StringComparison.Ordinal);
        Assert.Contains("will not start", problem, StringComparison.Ordinal);
    }

    // ---- round 4, F13: every command that runs with administrator rights refuses the variables, not only the loop

    public static TheoryData<LocalCiCommand> ElevatedCommands => [LocalCiCommand.Setup, LocalCiCommand.Serve, LocalCiCommand.Status, LocalCiCommand.SelfTest, LocalCiCommand.Probe, LocalCiCommand.Uninstall];

    [Theory]
    [MemberData(nameof(ElevatedCommands))]
    public void EveryCommandThatRunsElevated_RefusesEachRuntimeVariable_WithAMessageThatNamesIt(LocalCiCommand command)
    {
        foreach (var variable in ServeStartCheck.CodeLoadingVariables)
        {
            var problems = ElevatedStartCheck.Problems(command, name => name == variable ? @"C:\Users\owner\hook.dll" : null);

            var problem = Assert.Single(problems);
            Assert.StartsWith($"The environment variable {variable} is set.", problem, StringComparison.Ordinal);
            Assert.Contains("runs with administrator rights, so it will not run", problem, StringComparison.Ordinal);
            Assert.Contains("start the program again", problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(ElevatedCommands))]
    public void EveryCommandThatRunsElevated_SaysEveryVariableThatIsSet_AndNothingWhenNoneIs(LocalCiCommand command)
    {
        Assert.Empty(ElevatedStartCheck.Problems(command, _ => null));
        Assert.Empty(ElevatedStartCheck.Problems(command, _ => string.Empty));
        var all = ElevatedStartCheck.Problems(command, _ => "1");
        Assert.Equal(ServeStartCheck.CodeLoadingVariables.Count, all.Count);
        Assert.All(ServeStartCheck.CodeLoadingVariables, name => Assert.Contains(all, p => p.Contains(name + " is set", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(LocalCiCommand.Help)]
    [InlineData(LocalCiCommand.Menu)]
    [InlineData(LocalCiCommand.Unknown)]
    public void TheHelpTheMenuAndAnUnknownWord_DoNotRunElevated_SoTheyAreNotRefused(LocalCiCommand command)
    {
        Assert.False(ElevatedStartCheck.Applies(command));
        Assert.Empty(ElevatedStartCheck.Problems(command, _ => "1"));
    }

    [Fact]
    public void TheCommandsThatApply_AreExactlyTheOnesTheRoutingSendsToTheAdministratorPrompt()
    {
        foreach (var command in Enum.GetValues<LocalCiCommand>())
        {
            var routed = RoutingDecision.For(command, isAdministrator: false, isInstalledCopy: false, isInstalled: true).Action;

            Assert.Equal(routed == RoutingAction.RelaunchElevated || (command == LocalCiCommand.Serve && routed == RoutingAction.Refuse), ElevatedStartCheck.Applies(command));
        }
    }

    [Fact]
    public void ANullEnvironment_IsAMistakeOfTheProgram()
    {
        Assert.Throws<ArgumentNullException>(() => ElevatedStartCheck.Problems(LocalCiCommand.Setup, null!));
    }

    [Fact]
    public void AnEmptyOrAbsentVariable_IsNoProblem_AndEveryListedNameIsChecked()
    {
        var installer = new FakeInstaller { IsInstalled = true };
        var storage = new FakeProtectedStorage();

        Assert.Empty(ServeStartCheck.Problems(installer, installer.InstalledPath, storage, _ => null));
        Assert.Empty(ServeStartCheck.Problems(installer, installer.InstalledPath, storage, _ => string.Empty));
        var all = ServeStartCheck.Problems(installer, installer.InstalledPath, storage, _ => "1");
        Assert.Equal(ServeStartCheck.CodeLoadingVariables.Count, all.Count);
        Assert.All(ServeStartCheck.CodeLoadingVariables, name => Assert.Contains(all, p => p.Contains(name + " ", StringComparison.Ordinal)));
    }
}
