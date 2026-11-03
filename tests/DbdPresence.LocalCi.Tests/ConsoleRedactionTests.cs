// Covers: FR-CI-06 (no token, runner configuration or password in anything shown to the owner), local CI round 3, QA R7 and R8: every sentence set-up and uninstall print about a failure passes the
// text of the failure through the redactor, so a failure that quotes a secret (a GitHub answer that echoes the token, a Windows message that holds the account's password) never reaches the
// console or the log. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class ConsoleRedactionTests : IDisposable
{
    /// <summary>A token as GitHub makes them: recognised by its shape even when nobody registered it.</summary>
    private const string ShapedToken = "github_pat_11ABCDEFGHIJKLMNOP_abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A value of no known shape that is registered with the redactor, as the account's password and a runner configuration are.</summary>
    private const string Registered = "Registered-Value-ZYX-0123456789";

    private const string GoodToken = "github_pat_11GOODGOODGOODGOODGOOD_abcdefghijklmnopqrstuvwxyz";

    private static readonly string s_failureText = $"it failed; the answer was: {ShapedToken} and {Registered}";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>The text has neither secret. For the console the failure must also have been shown (as "[hidden]"), or the test would prove nothing; the log may say less.</summary>
    private static void AssertClean(IEnumerable<string> shown, string where, bool failureMustBeShown)
    {
        var text = string.Join("\n", shown);
        Assert.DoesNotContain(ShapedToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain("github_pat_11ABCDEF", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Registered, text, StringComparison.Ordinal);
        if (failureMustBeShown)
        {
            Assert.True(text.Contains("[hidden]", StringComparison.Ordinal), $"{where}: the failure was not shown at all, so the test proves nothing:\n{text}");
        }
    }

    /// <summary>R7: set-up.</summary>
    [Theory]
    [InlineData("account")]
    [InlineData("wsl-cannot-start")]
    [InlineData("saved-token-unreachable")]
    [InlineData("saved-token-refused")]
    [InlineData("typed-token-refused")]
    [InlineData("renewal-refused")]
    [InlineData("test-check")]
    public async Task EverythingSetupSaysAboutAFailure_HasTheSecretsRemoved(string where)
    {
        var redactor = new Redactor();
        redactor.Register(Registered);
        var log = new MemoryCiLog(redactor);
        var console = new FakeConsole(secrets: where is "typed-token-refused" ? ["github_pat_11TYPEDTYPEDTYPEDTYPED_abcdefghijklmnopqrstuvwxyz", "x", "y"] : where is "renewal-refused" ? ["github_pat_11NEWNEWNEWNEWNEWNEW_abcdefghijklmnopqrstuvwxyz"] : []);
        var api = new FakeGitHubApi();
        var store = new FakeSecretStore();
        var account = new FakeWslAccount();
        var wsl = new FakeWsl();
        switch (where)
        {
            case "account":
                account.EnsureFailure = new InvalidOperationException(s_failureText);
                break;
            case "wsl-cannot-start":
                wsl.UsableFailure = new InvalidOperationException(s_failureText);
                break;
            case "saved-token-unreachable":
                store.Save(new SecretText(GoodToken));
                api.RepoFailure = new GitHubApiException(s_failureText, 0, isTransient: true);
                break;
            case "saved-token-refused":
                store.Save(new SecretText(GoodToken));
                api.RepoFailure = new GitHubApiException(s_failureText, 401, isTransient: false);
                break;
            case "typed-token-refused":
                api.RepoFailure = new GitHubApiException(s_failureText, 401, isTransient: false);
                break;
            case "renewal-refused":
                store.Save(new SecretText(GoodToken));
                api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(2), IsPrivate: true);
                api.RepoFailure = new GitHubApiException(s_failureText, 401, isTransient: false);
                api.RepoCallsBeforeFailure = 1;
                break;
            default:
                api.DispatchFailure = new GitHubApiException(s_failureText, 500, isTransient: false);
                break;
        }

        var wizard = SetupTests.NewWizard(_folder, console, new FakeMachine(), wsl, api, store, new FakeScheduler(), redactor, log, out _, account: account);
        if (where is "test-check")
        {
            // The token step must pass first: a token is typed and accepted.
            console = new FakeConsole(secrets: [GoodToken]);
            wizard = SetupTests.NewWizard(_folder, console, new FakeMachine(), wsl, api, store, new FakeScheduler(), redactor, log, out _, account: account);
        }

        _ = await wizard.RunAsync(CancellationToken.None);

        AssertClean(console.Lines, where, failureMustBeShown: true);
        AssertClean(log.Lines, where + " (log)", failureMustBeShown: false);
    }

    /// <summary>R8: uninstall.</summary>
    [Theory]
    [InlineData("variable")]
    [InlineData("runners")]
    [InlineData("account")]
    public async Task EverythingUninstallSaysAboutAFailure_HasTheSecretsRemoved(string where)
    {
        var redactor = new Redactor();
        redactor.Register(Registered);
        var log = new MemoryCiLog(redactor);
        var console = new FakeConsole(answers: [true, false]);
        var api = new FakeGitHubApi();
        var account = new FakeWslAccount();
        switch (where)
        {
            case "variable":
                api.DeleteVariableFailure = new GitHubApiException(s_failureText, 401, isTransient: false);
                break;
            case "runners":
                api.RunnersFailure = new GitHubApiException(s_failureText, 500, isTransient: false);
                break;
            default:
                account.RemoveFailure = new InvalidOperationException(s_failureText);
                break;
        }

        var store = new FakeSecretStore();
        store.Save(new SecretText(GoodToken));
        var wsl = new FakeWsl();
        var sandbox = Sandboxes.New(wsl, account, new FakeWslSettingsFile(), log, redactor, TestTime.New());
        var uninstaller = new Uninstaller(console, api, new FakeScheduler { TaskExists = true }, new FakeJobHost(), wsl, account, sandbox, store, new FakeInstaller { IsInstalled = true }, _folder.Paths, log, redactor);

        _ = await uninstaller.RunAsync(CancellationToken.None);

        AssertClean(console.Lines, where, failureMustBeShown: true);
        AssertClean(log.Lines, where + " (log)", failureMustBeShown: false);
    }
}
