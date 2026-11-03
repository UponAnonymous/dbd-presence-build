// Covers: local CI round 3, red-team M5 (only a private repository is served: the answer is asked for and remembered, a failure or a missing answer is "not known" and starts nothing, the status says
// what GitHub said and when), FR-CI-07 (the status says it in plain words). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class RepositoryGuardTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = TestTime.New();
    private readonly Redactor _redactor = new();
    private readonly MemoryCiLog _log;
    private readonly FakeGitHubApi _api = new();
    private readonly StateStore _state;
    private readonly RepositoryGuard _guard;

    public RepositoryGuardTests()
    {
        _log = new MemoryCiLog(_redactor);
        _state = new StateStore(_folder.Paths.StateFile);
        _guard = new RepositoryGuard(_api, _state, _time, _log, _redactor);
    }

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(true, RepositoryPrivacy.Private)]
    [InlineData(false, RepositoryPrivacy.NotPrivate)]
    [InlineData(null, RepositoryPrivacy.Unknown)]
    public async Task WhatGitHubSays_IsJudged_AndRemembered_WithTheTime(bool? isPrivate, RepositoryPrivacy expected)
    {
        _api.Repo = new RepoInfo("main", null, isPrivate);

        var verdict = await _guard.AskAsync(CancellationToken.None);

        Assert.Equal(expected, verdict.Privacy);
        var state = _state.Load();
        Assert.Equal(isPrivate, state.RepositoryPrivate);
        Assert.Equal(_time.GetUtcNow(), state.RepositoryCheckedAt);
    }

    [Fact]
    public async Task ACallThatFails_IsNotKnown_StartsNothing_KeepsTheEarlierAnswer_AndHidesASecretInTheReason()
    {
        _state.Update(s =>
        {
            s.RepositoryPrivate = true;
            s.RepositoryCheckedAt = _time.GetUtcNow().AddHours(-1);
        });
        _api.RepoFailure = new GitHubApiException("Bad credentials github_pat_11ABCDEFGHIJKLMNOPQRSTUV", 401, isTransient: false);

        var verdict = await _guard.AskAsync(CancellationToken.None);

        Assert.Equal(RepositoryPrivacy.Unknown, verdict.Privacy);
        Assert.False(verdict.MayServe);
        Assert.Contains("could not be asked whether the repository is private", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("github_pat_", verdict.Reason, StringComparison.Ordinal);
        Assert.True(_state.Load().RepositoryPrivate);
        Assert.Equal(_time.GetUtcNow().AddHours(-1), _state.Load().RepositoryCheckedAt);
        Assert.All(_log.Lines, l => Assert.DoesNotContain("github_pat_", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStateFileThatCannotBeWritten_DoesNotChangeTheAnswer()
    {
        var broken = new StateStore(new LocalCiPaths(Path.Combine(_folder.Path, "gone")).StateFile);
        var guard = new RepositoryGuard(new FakeGitHubApi { Repo = new RepoInfo("main", null, IsPrivate: false) }, broken, _time, _log, _redactor);

        var verdict = await guard.AskAsync(CancellationToken.None);

        Assert.Equal(RepositoryPrivacy.NotPrivate, verdict.Privacy);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("Could not write down what GitHub said about the repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLoopsOwnStop_IsNotTurnedIntoAnAnswer()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _api.RepoFailure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _guard.AskAsync(cts.Token));
    }

    [Fact]
    public async Task ARepositoryCallThatTimesOutByItself_IsNotKnown_ButNotAStop()
    {
        _api.RepoFailure = new TaskCanceledException("the call timed out");

        var verdict = await _guard.AskAsync(CancellationToken.None);

        Assert.Equal(RepositoryPrivacy.Unknown, verdict.Privacy);
    }

    // ---- the status

    private StatusFacts Facts(bool? isPrivate, DateTimeOffset? checkedAt, bool tokenSaved = true) =>
        new(_time.GetUtcNow(), tokenSaved, TaskExists: true, LoopRunning: true, [], null, [], null, 0, null, BaseImageBuilt: true, RepositoryPrivate: isPrivate, RepositoryCheckedAt: checkedAt);

    [Fact]
    public void TheStatus_SaysPrivate_WithTheTimeGitHubSaidSo()
    {
        var text = StatusReport.Build(Facts(true, _time.GetUtcNow()));

        Assert.Contains("Repository UponAnonymous/dbd-presence: private (GitHub said so at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PROBLEM. UponAnonymous", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatus_SaysPlainlyThatARepositoryThatIsNotPrivateIsAProblem_AndWhatItMeans()
    {
        var text = StatusReport.Build(Facts(false, _time.GetUtcNow()));

        Assert.Contains("Repository UponAnonymous/dbd-presence: PROBLEM (GitHub said so at ", text, StringComparison.Ordinal);
        Assert.Contains("is not private", text, StringComparison.Ordinal);
        Assert.Contains("no runner is started", text, StringComparison.Ordinal);
        Assert.Contains("Make the repository private", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatus_SaysAnUnknownAnswerIsAProblemToo()
    {
        var text = StatusReport.Build(Facts(null, null));

        Assert.Contains("Repository UponAnonymous/dbd-presence: PROBLEM. GitHub did not say whether the repository", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAToken_TheStatusDoesNotClaimAnythingAboutTheRepository()
    {
        var text = StatusReport.Build(Facts(true, _time.GetUtcNow(), tokenSaved: false));

        Assert.DoesNotContain("Repository UponAnonymous", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassicToken_IsSaidPlainlyInTheStatus_NotAsAnExpiryDate()
    {
        var text = StatusReport.Build(Facts(true, _time.GetUtcNow()) with { TokenIsClassic = true, TokenExpiresAt = _time.GetUtcNow().AddDays(20) });

        Assert.Contains("this is a CLASSIC token", text, StringComparison.Ordinal);
        Assert.Contains("starts no runner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("expires on", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCollector_ReadsWhatWasRemembered_AndSeesAClassicTokenFromGitHub()
    {
        _state.Update(s =>
        {
            s.RepositoryPrivate = false;
            s.RepositoryCheckedAt = _time.GetUtcNow();
        });
        var api = new FakeGitHubApi { RunnersFailure = new GitHubApiException("classic", 403, isTransient: false) { IsClassicToken = true } };
        var secrets = new FakeSecretStore();
        secrets.Save(new SecretText("x"));
        var collector = new StatusCollector(secrets, new FakeScheduler(), api, _state, _folder.Paths, _time, _redactor);

        var facts = await collector.CollectAsync(CancellationToken.None);

        Assert.False(facts.RepositoryPrivate);
        Assert.Equal(_time.GetUtcNow(), facts.RepositoryCheckedAt);
        Assert.True(facts.TokenIsClassic);
        Assert.True(facts.TokenRefused);
    }
}
