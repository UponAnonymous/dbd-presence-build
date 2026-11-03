// Covers: FR-CI-04, FR-CI-10 and local CI round 4 QA M-4 (R4, adopted from the reviewer's scratch tests D8 to D10): every call of the real GitHub client raises a typed error when GitHub refuses it or has a
// fault, so no caller ever takes a refusal for an answer (nine of the fourteen RequireSuccess calls could be deleted without any test failing); a dispatch that GitHub answers 404 raises it, so the test
// check knows the workflow is not on the default branch yet; a variable that does not exist yet and a create that GitHub refuses is an error. docs/owner/free-ci.md.
using System.Net;

namespace DbdPresence.LocalCi.Tests;

public sealed class GitHubClientRefusalTests
{
    private static GitHubRestClient NewClient(int status) =>
        new(new HttpClient(new StubHandler(_ => StubHandler.Json((HttpStatusCode)status, """{"message":"nope"}"""))), () => new SecretText("github_pat_SECRETSECRETSECRET"), "UponAnonymous", "dbd-presence");

    private static readonly (string Name, Func<GitHubRestClient, Task> Call)[] s_calls =
    [
        ("GetRepository", c => c.GetRepositoryAsync(CancellationToken.None)),
        ("GetLatestRunnerRelease", c => c.GetLatestRunnerReleaseAsync(CancellationToken.None)),
        ("GenerateJitConfig", c => c.GenerateJitConfigAsync("dbd-linux-20261029120000", ["self-hosted", "dbd-linux"], CancellationToken.None)),
        ("ListRunners", c => c.ListRunnersAsync(CancellationToken.None)),
        ("GetVariable", c => c.GetVariableAsync("LINUX_RUNNER", CancellationToken.None)),
        ("DeleteRunner", c => c.DeleteRunnerAsync(5, CancellationToken.None)),
        ("SetVariable", c => c.SetVariableAsync("LINUX_RUNNER", "dbd-linux", CancellationToken.None)),
        ("DeleteVariable", c => c.DeleteVariableAsync("LINUX_RUNNER", CancellationToken.None)),
        ("DispatchWorkflow", c => c.DispatchWorkflowAsync("local-ci-probe.yml", "main", CancellationToken.None)),
        ("ListWorkflowRuns", c => c.ListWorkflowRunsAsync("ci-linux.yml", null, CancellationToken.None)),
        ("GetWorkflowRun", c => c.GetWorkflowRunAsync(1, CancellationToken.None)),
        ("CancelWorkflowRun", c => c.CancelWorkflowRunAsync(1, CancellationToken.None)),
        ("ListWorkflowJobs", c => c.ListWorkflowJobsAsync(1, CancellationToken.None)),
    ];

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task EveryCall_RaisesATypedError_ForARefusalOrAServerFault(int status)
    {
        var silent = new List<string>();
        foreach (var (name, call) in s_calls)
        {
            var failure = await Record.ExceptionAsync(() => call(NewClient(status)));
            if (failure is not GitHubApiException typed || typed.StatusCode != status || typed.IsTransient != (status >= 500))
            {
                silent.Add($"{name} -> {failure?.GetType().Name ?? "no error"}");
            }
        }

        Assert.True(silent.Count == 0, "These calls do not raise a GitHubApiException with the status when GitHub answers " + status + ": " + string.Join(", ", silent));
    }

    [Fact]
    public async Task ADispatchThatGitHubAnswers404_RaisesTheError_SoTheTestCheckKnowsTheWorkflowIsNotOnTheDefaultBranch()
    {
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(404).DispatchWorkflowAsync("local-ci-probe.yml", "main", CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task AVariableThatDoesNotExistYet_AndACreateThatGitHubRefuses_IsAnError(int createStatus)
    {
        var client = new GitHubRestClient(
            new HttpClient(new StubHandler(request => request.Method == HttpMethod.Patch
                ? StubHandler.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""")
                : StubHandler.Json((HttpStatusCode)createStatus, """{"message":"nope"}"""))),
            () => new SecretText("github_pat_SECRETSECRETSECRET"),
            "UponAnonymous",
            "dbd-presence");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.SetVariableAsync("LINUX_RUNNER", "dbd-linux", CancellationToken.None));

        Assert.Equal(createStatus, ex.StatusCode);
    }

    [Fact]
    public async Task AnUpdateOfAVariableThatGitHubRefuses_IsAnError_NotACreate()
    {
        var methods = new List<HttpMethod>();
        var client = new GitHubRestClient(
            new HttpClient(new StubHandler(request =>
            {
                methods.Add(request.Method);
                return StubHandler.Json(HttpStatusCode.Forbidden, """{"message":"nope"}""");
            })),
            () => new SecretText("github_pat_SECRETSECRETSECRET"),
            "UponAnonymous",
            "dbd-presence");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.SetVariableAsync("LINUX_RUNNER", "dbd-linux", CancellationToken.None));

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal([HttpMethod.Patch], methods);
    }

    [Fact]
    public async Task AVariableThatIsGoneAlready_AndARunnerThatIsGoneAlready_AreNotErrors_ButAnythingElseIs()
    {
        await NewClient(404).DeleteVariableAsync("LINUX_RUNNER", CancellationToken.None);
        await NewClient(404).DeleteRunnerAsync(5, CancellationToken.None);
        Assert.Null(await NewClient(404).GetVariableAsync("LINUX_RUNNER", CancellationToken.None));

        await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(409).DeleteVariableAsync("LINUX_RUNNER", CancellationToken.None));
        await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(409).DeleteRunnerAsync(5, CancellationToken.None));
        await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(409).GetVariableAsync("LINUX_RUNNER", CancellationToken.None));
    }

    [Fact]
    public async Task AWorkflowRunListThatGitHubRefuses_IsAnError_AndSoIsAJobList()
    {
        // The list calls (255 in the reviewer's table) were the one RequireSuccess nobody checked: a refusal must not read as "no runs".
        await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(403).ListWorkflowRunsAsync("ci-linux.yml", "push", CancellationToken.None));
        await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(404).ListWorkflowRunsAsync("ci-linux.yml", null, CancellationToken.None));
        await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(500).ListWorkflowJobsAsync(7, CancellationToken.None));
    }
}
