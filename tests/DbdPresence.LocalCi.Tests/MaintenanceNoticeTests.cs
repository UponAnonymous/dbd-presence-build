// Covers: FR-CI-07 and local CI round 3, QA M-3 (the token notices of round-2 M9 were untested: G7, G17, G18): the owner is told once a day while the token ends within 7 days, when GitHub refuses it and when
// it is a classic token, never while the game runs, and not when the token has more than 7 days left; red-team M5 (a repository that is not private is announced once a day, and recorded for the
// status). Each is shown in a window and always in the log. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class MaintenanceNoticeTests : IDisposable
{
    private readonly MaintenanceRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private Task RunAsync() => _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

    private void TokenCheckIsDue() => _rig.State.Update(s =>
    {
        s.LastTokenCheck = null;
        s.LastRunnerCheck = _rig.Time.GetUtcNow();
        s.LastRunnerAttempt = _rig.Time.GetUtcNow();
    });

    /// <summary>G7: one notice a day in the warning window, none while the game runs (and it comes when the game ends), and a new one after a day.</summary>
    [Fact]
    public async Task TheTokenNotice_IsShownOncePerDay_InTheWarningWindow_AndNeverWhileTheGameRuns()
    {
        _rig.Api.Repo = new RepoInfo("main", _rig.Time.GetUtcNow().AddDays(3), IsPrivate: true);
        _rig.Processes.Running.Add("DeadByDaylight");
        TokenCheckIsDue();

        await RunAsync();
        Assert.Empty(_rig.Notifier.Messages);

        _rig.Processes.Running.Clear();
        TokenCheckIsDue();
        await RunAsync();
        var message = Assert.Single(_rig.Notifier.Messages);
        Assert.Contains("token ends on", message, StringComparison.Ordinal);
        Assert.Contains("choose 1 (Set up)", message, StringComparison.Ordinal);

        TokenCheckIsDue();
        await RunAsync();
        Assert.Single(_rig.Notifier.Messages);

        _rig.Time.Advance(TimeSpan.FromHours(25));
        _rig.Api.Repo = new RepoInfo("main", _rig.Time.GetUtcNow().AddDays(2), IsPrivate: true);
        TokenCheckIsDue();
        await RunAsync();
        Assert.Equal(2, _rig.Notifier.Messages.Count);
    }

    [Fact]
    public async Task TheTokenNoticeIsInTheLogEveryTime_EvenWhenNoWindowIsShown()
    {
        _rig.Api.Repo = new RepoInfo("main", _rig.Time.GetUtcNow().AddDays(3), IsPrivate: true);
        _rig.Processes.Running.Add("DeadByDaylight");
        TokenCheckIsDue();

        await RunAsync();

        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("token ends on", StringComparison.Ordinal));
    }

    /// <summary>G17: the edge of the warning window is exactly 7 days.</summary>
    [Theory]
    [InlineData(8.0, 0)]
    [InlineData(7.0001, 0)]
    [InlineData(7.0, 1)]
    [InlineData(6.0, 1)]
    [InlineData(0.5, 1)]
    public async Task TheTokenNoticeEdge_IsExactlySevenDays(double days, int notices)
    {
        _rig.Api.Repo = new RepoInfo("main", _rig.Time.GetUtcNow().AddDays(days), IsPrivate: true);
        TokenCheckIsDue();

        await RunAsync();

        Assert.Equal(notices, _rig.Notifier.Messages.Count);
    }

    [Fact]
    public async Task ATokenThatDoesNotSayWhenItEnds_GivesNoExpiryNotice_AndKeepsTheEarlierDate()
    {
        _rig.State.Update(s => s.TokenExpiresAt = _rig.Time.GetUtcNow().AddDays(20));
        _rig.Api.Repo = new RepoInfo("main", null, IsPrivate: true);
        TokenCheckIsDue();

        await RunAsync();

        Assert.Empty(_rig.Notifier.Messages);
        Assert.Equal(_rig.Time.GetUtcNow().AddDays(20), _rig.State.Load().TokenExpiresAt);
    }

    /// <summary>G18: a token GitHub refuses is announced once a day, in the log every time, and not while the game runs.</summary>
    [Fact]
    public async Task ARefusedToken_IsAnnouncedOncePerDay_AndNeverWhileTheGameRuns()
    {
        _rig.Api.RepoFailure = new GitHubApiException("GitHub refused the token (wrong, expired or revoked).", 401, isTransient: false);
        _rig.Processes.Running.Add("DeadByDaylight-Win64-Shipping");
        TokenCheckIsDue();

        await RunAsync();
        Assert.Empty(_rig.Notifier.Messages);

        _rig.Processes.Running.Clear();
        TokenCheckIsDue();
        await RunAsync();
        var message = Assert.Single(_rig.Notifier.Messages);
        Assert.Contains("refuses the saved token", message, StringComparison.Ordinal);

        TokenCheckIsDue();
        await RunAsync();
        Assert.Single(_rig.Notifier.Messages);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("token check failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(403, false)]
    [InlineData(401, false)]
    [InlineData(500, true)]
    [InlineData(0, true)]
    public async Task OnlyARefusalIsAnnounced_ANetworkOrServerFailureIsOnlyLogged(int status, bool transient)
    {
        _rig.Api.RepoFailure = new GitHubApiException("failed", status, transient);
        TokenCheckIsDue();

        await RunAsync();

        Assert.Equal(transient ? 0 : 1, _rig.Notifier.Messages.Count);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("token check failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClassicToken_IsAnnouncedWithItsOwnWords()
    {
        _rig.Api.RepoFailure = new GitHubApiException("GitHub says this token is a classic token.", 403, isTransient: false) { IsClassicToken = true };
        TokenCheckIsDue();

        await RunAsync();

        var message = Assert.Single(_rig.Notifier.Messages);
        Assert.Contains("classic token", message, StringComparison.Ordinal);
        Assert.DoesNotContain("refuses the saved token", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTokenAndTheLeftoverNotices_AreCountedApart()
    {
        _rig.Api.Repo = new RepoInfo("main", _rig.Time.GetUtcNow().AddDays(2), IsPrivate: true);
        _rig.State.Update(s => s.ResidueItems = [Left.Words("a folder is left")]);
        TokenCheckIsDue();

        await RunAsync();

        Assert.Equal(2, _rig.Notifier.Messages.Count);
        Assert.Contains(_rig.Notifier.Messages, m => m.Contains("token ends on", StringComparison.Ordinal));
        Assert.Contains(_rig.Notifier.Messages, m => m.Contains("could not remove everything", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANoticeTimeInTheFuture_CountsAsDue_SoASetBackClockCannotSilenceTheNoticesForDays()
    {
        _rig.Api.Repo = new RepoInfo("main", _rig.Time.GetUtcNow().AddDays(2), IsPrivate: true);
        _rig.State.Update(s => s.LastTokenNotice = _rig.Time.GetUtcNow().AddDays(5));
        TokenCheckIsDue();

        await RunAsync();

        Assert.Single(_rig.Notifier.Messages);
    }

    // ---- red-team M5: a repository that is not private

    [Fact]
    public async Task ARepositoryThatIsNotPrivate_IsRecordedAndAnnouncedOncePerDay_NotWhileTheGameRuns()
    {
        _rig.Api.Repo = new RepoInfo("main", null, IsPrivate: false);
        _rig.Processes.Running.Add("DeadByDaylight");
        TokenCheckIsDue();

        await RunAsync();
        Assert.Empty(_rig.Notifier.Messages);
        Assert.False(_rig.State.Load().RepositoryPrivate);

        _rig.Processes.Running.Clear();
        TokenCheckIsDue();
        await RunAsync();
        var message = Assert.Single(_rig.Notifier.Messages);
        Assert.Contains("is not private", message, StringComparison.Ordinal);

        TokenCheckIsDue();
        await RunAsync();
        Assert.Single(_rig.Notifier.Messages);
        _rig.Time.Advance(TimeSpan.FromHours(25));
        TokenCheckIsDue();
        await RunAsync();
        Assert.Equal(2, _rig.Notifier.Messages.Count);
    }

    [Fact]
    public async Task APrivateRepository_IsRecordedWithTheTimeGitHubSaidSo_AndNoNoticeIsShown()
    {
        TokenCheckIsDue();

        await RunAsync();

        var state = _rig.State.Load();
        Assert.True(state.RepositoryPrivate);
        Assert.Equal(_rig.Time.GetUtcNow(), state.RepositoryCheckedAt);
        Assert.Empty(_rig.Notifier.Messages);
    }

    [Fact]
    public async Task AnAnswerThatDoesNotSayIfTheRepositoryIsPrivate_IsRecordedAsUnknown_AndAnnounced()
    {
        _rig.Api.Repo = new RepoInfo("main", null, IsPrivate: null);
        TokenCheckIsDue();

        await RunAsync();

        Assert.Null(_rig.State.Load().RepositoryPrivate);
        Assert.Contains("did not say whether the repository", Assert.Single(_rig.Notifier.Messages), StringComparison.Ordinal);
    }

    // ---- the edges the mutation run of round 3 found open

    /// <summary>The log warns about a leftover again after exactly a day (24 hours, not 23 and not 25), and the window comes with it.</summary>
    [Fact]
    public async Task ALeftoverIsWarnedAboutInTheLogAndShownInAWindowAgainAfterExactlyADay()
    {
        _rig.State.Update(s => s.ResidueItems = [Left.Words("a folder is left")]);
        int Warnings() => _rig.Log.Lines.Count(l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("could not remove everything", StringComparison.Ordinal));

        _rig.EverythingJustDone();
        await RunAsync();
        Assert.Equal(1, Warnings());
        Assert.Single(_rig.Notifier.Messages);

        _rig.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        _rig.EverythingJustDone();
        await RunAsync();
        Assert.Equal(1, Warnings());
        Assert.Single(_rig.Notifier.Messages);

        _rig.Time.Advance(TimeSpan.FromSeconds(1));
        _rig.EverythingJustDone();
        await RunAsync();
        Assert.Equal(2, Warnings());
        Assert.Equal(2, _rig.Notifier.Messages.Count);
    }

    /// <summary>The caller can tell whether the window was shown: not twice in a day, not while the game runs, and again after exactly a day.</summary>
    [Fact]
    public void ANoticeSaysWhetherItWasShown_AndComesBackAfterExactlyADay()
    {
        var notices = new OwnerNotices(_rig.Notifier, _rig.State, _rig.Time);

        Assert.True(notices.OncePerDay(NoticeTopic.Residue, "one", gateAllowed: true));
        Assert.False(notices.OncePerDay(NoticeTopic.Residue, "two", gateAllowed: true));
        Assert.False(notices.OncePerDay(NoticeTopic.DiskFull, "three", gateAllowed: false));
        Assert.Equal(["one"], _rig.Notifier.Messages);

        _rig.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.False(notices.OncePerDay(NoticeTopic.Residue, "four", gateAllowed: true));
        _rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(notices.OncePerDay(NoticeTopic.Residue, "five", gateAllowed: true));
        Assert.Equal(["one", "five"], _rig.Notifier.Messages);
    }
}
