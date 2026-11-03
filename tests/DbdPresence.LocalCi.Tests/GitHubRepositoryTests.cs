// Covers: FR-CI-05 (only the approved host, a classic token is refused), FR-CI-06 (the token's kind), FR-CI-07 (the runner list is the whole list), local CI round 3: red-team L9 (only a fine-grained token
// is taken: a typed token must start with github_pat_, and an answer that carries X-OAuth-Scopes shows a classic token, which is refused), red-team M5 (the repository call reads whether it is
// private), QA minor (the runner list follows every page: ADV6). No real network: every request goes to a stub handler. docs/owner/free-ci.md.
using System.Net;
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class GitHubRepositoryTests
{
    private static GitHubRestClient NewClient(StubHandler handler, string? token = "github_pat_SECRETSECRETSECRET") =>
        new(new HttpClient(handler), () => token is null ? null : new SecretText(token), "UponAnonymous", "dbd-presence");

    // ---- red-team M5: is the repository private?

    [Theory]
    [InlineData("""{"default_branch":"main","private":true}""", true)]
    [InlineData("""{"default_branch":"main","private":false}""", false)]
    [InlineData("""{"default_branch":"main"}""", null)]
    [InlineData("""{"default_branch":"main","private":"yes"}""", null)]
    [InlineData("""{"default_branch":"main","private":null}""", null)]
    [InlineData("""{"default_branch":"main","private":1}""", null)]
    public async Task TheRepositoryCall_ReadsWhetherTheRepositoryIsPrivate_AndAnythingUnclearIsUnknown(string json, bool? expected)
    {
        var client = NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, json)));

        var repo = await client.GetRepositoryAsync(CancellationToken.None);

        Assert.Equal(expected, repo.IsPrivate);
    }

    [Theory]
    [InlineData(true, RepositoryPrivacy.Private, true)]
    [InlineData(false, RepositoryPrivacy.NotPrivate, false)]
    [InlineData(null, RepositoryPrivacy.Unknown, false)]
    public void OnlyAClearPrivate_MayBeServed(bool? isPrivate, RepositoryPrivacy expected, bool mayServe)
    {
        var verdict = RepositoryGuard.Judge(isPrivate);

        Assert.Equal(expected, verdict.Privacy);
        Assert.Equal(mayServe, verdict.MayServe);
        Assert.Equal(mayServe, verdict.Reason.Length == 0);
    }

    [Fact]
    public void TheWordsForARepositoryThatIsNotPrivate_SayWhyAndWhatToDo()
    {
        var text = RepositoryGuard.NotPrivateSentence;

        Assert.Contains("UponAnonymous/dbd-presence", text, StringComparison.Ordinal);
        Assert.Contains("is not private", text, StringComparison.Ordinal);
        Assert.Contains("pull request", text, StringComparison.Ordinal);
        Assert.Contains("no runner is started", text, StringComparison.Ordinal);
        Assert.Contains("Make the repository private", text, StringComparison.Ordinal);
    }

    // ---- red-team L9: a classic token is refused

    [Fact]
    public async Task AnAnswerThatCarriesTheScopesHeader_ShowsAClassicToken_AndIsRefused()
    {
        var client = NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"default_branch":"main","private":true}""", ("X-OAuth-Scopes", "repo, workflow"))));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepositoryAsync(CancellationToken.None));

        Assert.True(ex.IsClassicToken);
        Assert.False(ex.IsTransient);
        Assert.Equal(403, ex.StatusCode);
        Assert.True(ex.IsAuthenticationFailure);
        Assert.Contains("classic token", ex.Message, StringComparison.Ordinal);
        Assert.Contains("github_pat_", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Round 4, QA minor 3: that only classic tokens get the header is an assumption nobody has checked on GitHub, and the refusal says so, so a wrong assumption is not read as the owner's mistake.</summary>
    [Fact]
    public async Task TheRefusalOfAClassicToken_SaysThatItRestsOnAnUnverifiedAssumption()
    {
        var client = NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"default_branch":"main","private":true}""", ("X-OAuth-Scopes", "repo"))));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepositoryAsync(CancellationToken.None));

        Assert.Contains("assumes that GitHub lists scopes only for classic tokens; that is not verified on a real GitHub yet", ex.Message, StringComparison.Ordinal);
        Assert.Contains("starts with github_pat_ and was made as set-up describes, that assumption is wrong", ex.Message, StringComparison.Ordinal);
        Assert.Contains("do not use a classic token instead", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyScopesHeader_IsAClassicTokenToo()
    {
        var client = NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"runners":[],"total_count":0}""", ("X-OAuth-Scopes", string.Empty))));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.ListRunnersAsync(CancellationToken.None));

        Assert.True(ex.IsClassicToken);
    }

    [Fact]
    public async Task TheScopesHeader_IsRefusedOnEveryAuthenticatedCall()
    {
        var calls = new Func<GitHubRestClient, Task>[]
        {
            c => c.GetRepositoryAsync(CancellationToken.None),
            c => c.ListRunnersAsync(CancellationToken.None),
            c => c.GetVariableAsync("LINUX_RUNNER", CancellationToken.None),
            c => c.SetVariableAsync("LINUX_RUNNER", "dbd-linux", CancellationToken.None),
            c => c.DeleteVariableAsync("LINUX_RUNNER", CancellationToken.None),
            c => c.DeleteRunnerAsync(5, CancellationToken.None),
            c => c.GenerateJitConfigAsync("dbd-linux-20261029120000", ["self-hosted", "dbd-linux"], CancellationToken.None),
            c => c.DispatchWorkflowAsync("local-ci-probe.yml", "main", CancellationToken.None),
            c => c.ListWorkflowRunsAsync("ci-linux.yml", null, CancellationToken.None),
            c => c.GetWorkflowRunAsync(1, CancellationToken.None),
            c => c.CancelWorkflowRunAsync(1, CancellationToken.None),
            c => c.ListWorkflowJobsAsync(1, CancellationToken.None),
        };

        foreach (var call in calls)
        {
            var client = NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{}", ("X-OAuth-Scopes", "repo"))));

            var ex = await Assert.ThrowsAsync<GitHubApiException>(() => call(client));
            Assert.True(ex.IsClassicToken);
        }
    }

    [Fact]
    public async Task AFineGrainedTokensAnswers_CarryNoScopesHeader_AndAreAccepted()
    {
        var client = NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"default_branch":"main","private":true}""", ("X-Accepted-GitHub-Permissions", "metadata=read"))));

        var repo = await client.GetRepositoryAsync(CancellationToken.None);

        Assert.True(repo.IsPrivate);
    }

    [Fact]
    public async Task TheScopesHeader_IsIgnoredOnACallWithoutTheToken()
    {
        // The public release call sends no token; whatever headers come back are not about this program's token.
        var json = """{"tag_name":"v2.338.0","body":"","assets":[{"name":"actions-runner-linux-x64-2.338.0.tar.gz","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz"}]}""";
        var client = NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, json, ("X-OAuth-Scopes", "repo"))));

        var release = await client.GetLatestRunnerReleaseAsync(CancellationToken.None);

        Assert.Equal("2.338.0", release.Version);
    }

    [Theory]
    [InlineData("github_pat_11AAAAAAA0abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("github_pat_", false)]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("gho_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("GITHUB_PAT_11AAAAAAA0abcdefghijklmnopqrstuvwxyz", true)]
    [InlineData(" github_pat_11AAAA", true)]
    [InlineData("", true)]
    [InlineData("yes", true)]
    public void OnlyTextThatStartsWithGithubPat_IsTakenAsAToken(string typed, bool refused) =>
        Assert.Equal(refused, SetupWizard.NotFineGrained(typed));

    // ---- QA minor (ADV6): the whole list of runners

    private static string RunnerPage(int from, int to, int total)
    {
        var runners = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            runners.Append(i > from ? "," : string.Empty).Append("{\"id\":").Append(i).Append(",\"name\":\"r").Append(i).Append("\",\"status\":\"online\",\"busy\":false}");
        }

        return "{\"total_count\":" + total + ",\"runners\":[" + runners + "]}";
    }

    private static int PageOf(HttpRequestMessage request)
    {
        var query = request.RequestUri!.Query;
        var marker = "page=";
        var at = query.LastIndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? 1 : int.Parse(query[(at + marker.Length)..].Split('&')[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task TheRunnerList_FollowsMoreThanOnePage()
    {
        var handler = new StubHandler(request => StubHandler.Json(HttpStatusCode.OK, PageOf(request) == 1 ? RunnerPage(1, 100, 150) : RunnerPage(101, 150, 150)));

        var list = await NewClient(handler).ListRunnersAsync(CancellationToken.None);

        Assert.Equal(150, list.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("per_page=100&page=1", handler.Requests[0].Url.Query, StringComparison.Ordinal);
        Assert.Contains("per_page=100&page=2", handler.Requests[1].Url.Query, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, 150).Select(i => (long)i), list.Select(r => r.Id));
    }

    [Fact]
    public async Task AListOfExactlyOneFullPage_IsReadWithOneCall_WhenGitHubsCountSaysSo()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, RunnerPage(1, 100, 100)));

        var list = await NewClient(handler).ListRunnersAsync(CancellationToken.None);

        Assert.Equal(100, list.Count);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AFullPageWithoutACount_IsFollowedByAskingForTheNextOne()
    {
        var handler = new StubHandler(request => StubHandler.Json(HttpStatusCode.OK, PageOf(request) == 1 ? RunnerPage(1, 100, 100).Replace("\"total_count\":100,", string.Empty, StringComparison.Ordinal) : "{\"runners\":[]}"));

        var list = await NewClient(handler).ListRunnersAsync(CancellationToken.None);

        Assert.Equal(100, list.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AnEmptyList_IsOneCall()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"total_count\":0,\"runners\":[]}"));

        var list = await NewClient(handler).ListRunnersAsync(CancellationToken.None);

        Assert.Empty(list);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ARunnerOnTheSecondPage_IsFound_SoItDoesNotLookIdle()
    {
        var handler = new StubHandler(request => StubHandler.Json(HttpStatusCode.OK, PageOf(request) == 1
            ? RunnerPage(1, 100, 101)
            : "{\"total_count\":101,\"runners\":[{\"id\":900,\"name\":\"dbd-linux-20261029120000\",\"status\":\"online\",\"busy\":true}]}"));

        var list = await NewClient(handler).ListRunnersAsync(CancellationToken.None);

        Assert.True(list.Single(r => r.Name == "dbd-linux-20261029120000").Busy);
    }

    [Fact]
    public async Task AnEndlessList_StopsAtTenPages()
    {
        var handler = new StubHandler(request => StubHandler.Json(HttpStatusCode.OK, RunnerPage(((PageOf(request) - 1) * 100) + 1, PageOf(request) * 100, 100000)));

        var list = await NewClient(handler).ListRunnersAsync(CancellationToken.None);

        Assert.Equal(10, handler.Requests.Count);
        Assert.Equal(1000, list.Count);
    }

    [Fact]
    public async Task AFailureOnALaterPage_IsAGitHubError_NotAShorterList()
    {
        var handler = new StubHandler(request => PageOf(request) == 1
            ? StubHandler.Json(HttpStatusCode.OK, RunnerPage(1, 100, 150))
            : StubHandler.Json(HttpStatusCode.BadGateway, """{"message":"bad gateway"}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(handler).ListRunnersAsync(CancellationToken.None));

        Assert.True(ex.IsTransient);
    }
}
