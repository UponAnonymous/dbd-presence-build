// Covers: FR-CI-06 (the GitHub token, the runner configuration and the throwaway password never appear in the log, the status text, an error
// message shown to the owner or the state file), NFR-SEC-01. docs/owner/free-ci.md.
using System.Net;
using DbdPresence.LocalCi.Windows;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class SecretHandlingTests : IDisposable
{
    private const string Token = "github_pat_11AAAAAAA0abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void ASecret_NeverPrintsItself()
    {
        var secret = new SecretText("hunter2-hunter2");

        Assert.DoesNotContain("hunter2", secret.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", $"{secret}", StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", new RunnerLaunch("dbd-linux-20261029120000", secret, "package").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", new JitRunner(1, "dbd-linux-20261029120000", secret).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", new AccountCredentials("dbd-ci-wsl", "S-1-5-21-1-2-3-1000", secret).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredSecret_IsRemovedFromAnyText_AndTokenShapedTextIsRemovedEvenWhenNotRegistered()
    {
        var redactor = new Redactor();
        redactor.Register(new SecretText("registered-value-123"));

        var text = redactor.Redact($"a registered-value-123 b {Token} c");

        Assert.DoesNotContain("registered-value-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("github_pat_", text, StringComparison.Ordinal);
        Assert.Contains("[hidden]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongerSecretIsRemovedWhole_EvenWhenAShorterOneIsInsideIt()
    {
        var redactor = new Redactor();
        redactor.Register("abc");
        redactor.Register("abcdef-long-secret");

        Assert.Equal("x [hidden] y", redactor.Redact("x abcdef-long-secret y"));
    }

    [Fact]
    public async Task AFailingApiCall_ThatEchoesTheToken_IsLoggedWithoutIt()
    {
        var redactor = new Redactor();
        redactor.Register(new SecretText(Token));
        var log = new MemoryCiLog(redactor);
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Unauthorized, $$"""{"message":"Bad credentials for {{Token}}"}"""));
        var client = new GitHubRestClient(new HttpClient(handler), () => new SecretText(Token), "UponAnonymous", "dbd-presence");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepositoryAsync(CancellationToken.None));
        log.Failure(redactor.Describe(ex));
        log.Warn("raw: " + ex.Message);

        Assert.All(log.Lines, line => Assert.DoesNotContain(Token, line, StringComparison.Ordinal));
        Assert.All(log.Lines, line => Assert.DoesNotContain("github_pat_", line, StringComparison.Ordinal));
    }

    [Fact]
    public void TheLogFile_NeverHoldsARegisteredSecret_AndRotatesAfterFourteenDays()
    {
        var redactor = new Redactor();
        redactor.Register(new SecretText("JIT-CONFIGURATION-VALUE"));
        var directory = _folder.Paths.LogDirectory;
        Directory.CreateDirectory(directory);
        var today = _time.GetUtcNow();
        foreach (var age in new[] { 20, 14, 13, 1 })
        {
            File.WriteAllText(Path.Combine(directory, FileCiLog.FileNameFor(today.AddDays(-age))), "old");
        }

        var echo = new StringWriter();
        var log = new FileCiLog(directory, _time, redactor, echo);
        log.Info("starting with JIT-CONFIGURATION-VALUE now");

        var written = File.ReadAllText(Path.Combine(directory, FileCiLog.FileNameFor(today)));
        Assert.DoesNotContain("JIT-CONFIGURATION-VALUE", written, StringComparison.Ordinal);
        Assert.DoesNotContain("JIT-CONFIGURATION-VALUE", echo.ToString(), StringComparison.Ordinal);
        Assert.Contains("starting with [hidden] now", written, StringComparison.Ordinal);
        var kept = Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [FileCiLog.FileNameFor(today.AddDays(-13)), FileCiLog.FileNameFor(today.AddDays(-1)), FileCiLog.FileNameFor(today)],
            kept);
    }

    [Fact]
    public void TheStatusText_HoldsNoSecret()
    {
        var facts = new StatusFacts(
            _time.GetUtcNow(), TokenSaved: true, TaskExists: true, LoopRunning: true, [new RunnerInfo(1, "dbd-linux-1", "online", false)], null, [],
            _time.GetUtcNow().AddDays(30), 1024, null, BaseImageBuilt: true);

        var text = StatusReport.Build(facts);

        Assert.DoesNotContain("github_pat_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("JITCONFIG", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedTokenThatTheLoopLoads_IsRegisteredWithTheRedactor_NoMatterWhatItLooksLike()
    {
        var redactor = new Redactor();
        var store = new FakeSecretStore();
        store.Save(new SecretText("token-in-no-known-shape-0123456789"));
        var source = new SecretSource(store, redactor);

        // Until it is loaded the redactor cannot know it (this shape is not recognised by pattern).
        Assert.Contains("token-in-no-known-shape-0123456789", redactor.Redact("x token-in-no-known-shape-0123456789 y"), StringComparison.Ordinal);

        _ = source.Current();

        Assert.Equal("x [hidden] y", redactor.Redact("x token-in-no-known-shape-0123456789 y"));
    }

    [Fact]
    public void ATokenReplacedWhileTheLoopRuns_IsRegisteredAtTheNextLoad()
    {
        var redactor = new Redactor();
        var store = new FakeSecretStore();
        store.Save(new SecretText("first-token-value-0123456789"));
        var source = new SecretSource(store, redactor);
        _ = source.Current();

        store.Save(new SecretText("second-token-value-9876543210"));
        _ = source.Current();

        Assert.Equal("[hidden] [hidden]", redactor.Redact("first-token-value-0123456789 second-token-value-9876543210"));
    }

    [Fact]
    public void ATokenBeingCheckedDuringSetup_IsRegisteredBeforeAnythingIsSentWithIt()
    {
        var redactor = new Redactor();
        var source = new SecretSource(new FakeSecretStore(), redactor);

        source.TryCandidate(new SecretText("candidate-token-0123456789"));

        Assert.Equal("[hidden]", redactor.Redact("candidate-token-0123456789"));
        Assert.NotNull(source.Current());
        source.ClearCandidate();
        Assert.Null(source.Current());
    }

    [Fact]
    public async Task ATokenTypedDuringSetup_IsNotEchoedByAnyMessage_AndANotAcceptedOneIsNotSaved()
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var api = new FakeGitHubApi { RepoFailure = new GitHubApiException($"Bad credentials {Token}", 401, isTransient: false) };
        var console = new FakeConsole(secrets: [Token, Token, Token]);
        var store = new FakeSecretStore();
        var wizard = SetupTests.NewWizard(_folder, console, new FakeMachine(), new FakeWsl(), api, store, new FakeScheduler(), redactor, log, out _);

        var outcome = await wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.False(store.Exists());
        Assert.DoesNotContain(Token, console.AllText, StringComparison.Ordinal);
        Assert.All(log.Lines, line => Assert.DoesNotContain(Token, line, StringComparison.Ordinal));
    }
}
