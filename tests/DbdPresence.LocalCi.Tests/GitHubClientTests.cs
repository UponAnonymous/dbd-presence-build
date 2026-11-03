// Covers: FR-CI-02 (the just-in-time runner request: runner group 1, labels, name), FR-CI-01 (repository variables created or updated, deleted),
// FR-CI-05 (HTTPS to the approved hosts only; redirects are checked hop by hop), FR-CI-07 (token expiry read from the response). No real network:
// every request goes to a stub handler. docs/owner/free-ci.md.
using System.Net;
using System.Text.Json;

namespace DbdPresence.LocalCi.Tests;

public sealed class GitHubClientTests
{
    private static GitHubRestClient NewClient(StubHandler handler, string? token = "github_pat_SECRETSECRETSECRET") =>
        new(new HttpClient(handler), () => token is null ? null : new SecretText(token), "UponAnonymous", "dbd-presence");

    [Fact]
    public void TheJitRequestBody_HasTheNameGroupLabelsAndWorkFolder()
    {
        var body = GitHubRestClient.BuildJitRequestBody("dbd-linux-20261029120000", ["self-hosted", "dbd-linux"]);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("dbd-linux-20261029120000", root.GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("runner_group_id").GetInt32());
        Assert.Equal(["self-hosted", "dbd-linux"], root.GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("_work", root.GetProperty("work_folder").GetString());
    }

    [Fact]
    public async Task GenerateJitConfig_PostsToTheRepositoryEndpointWithTheBearerToken_AndKeepsTheConfigurationSecret()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Created, """{"runner":{"id":42,"name":"x"},"encoded_jit_config":"QUJD"}"""));
        var client = NewClient(handler);

        var runner = await client.GenerateJitConfigAsync("dbd-linux-20261029120000", ["self-hosted", "dbd-linux"], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/repos/UponAnonymous/dbd-presence/actions/runners/generate-jitconfig", request.Url.ToString());
        Assert.Equal("Bearer github_pat_SECRETSECRETSECRET", request.Authorization);
        Assert.Equal(42, runner.RunnerId);
        Assert.Equal("QUJD", runner.EncodedConfig.Reveal());
        Assert.DoesNotContain("QUJD", runner.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("QUJD", runner.EncodedConfig.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetVariable_UpdatesAnExistingOne_AndCreatesAMissingOne()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Patch
            ? StubHandler.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""")
            : StubHandler.Json(HttpStatusCode.Created, "{}"));
        var client = NewClient(handler);

        await client.SetVariableAsync("LINUX_RUNNER", "dbd-linux", CancellationToken.None);

        Assert.Equal([HttpMethod.Patch, HttpMethod.Post], handler.Requests.Select(r => r.Method).ToArray());
        Assert.EndsWith("/actions/variables/LINUX_RUNNER", handler.Requests[0].Url.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("/actions/variables", handler.Requests[1].Url.ToString(), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("LINUX_RUNNER", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("dbd-linux", body.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task SetVariable_UsesOnlyOneCallWhenTheVariableExists()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = NewClient(handler);

        await client.SetVariableAsync("LINUX_RUNNER", "dbd-linux", CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DeleteVariable_TreatsAMissingOneAsDone()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        var client = NewClient(handler);

        await client.DeleteVariableAsync("LINUX_RUNNER", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task TheRepositoryCall_ReadsTheDefaultBranchAndTheTokenExpiry()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            HttpStatusCode.OK, """{"default_branch":"trunk"}""", ("github-authentication-token-expiration", "2027-01-27 10:10:10 UTC")));
        var client = NewClient(handler);

        var repo = await client.GetRepositoryAsync(CancellationToken.None);

        Assert.Equal("trunk", repo.DefaultBranch);
        Assert.Equal(new DateTimeOffset(2027, 1, 27, 10, 10, 10, TimeSpan.Zero), repo.TokenExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    public void ATokenExpiryThatIsMissingOrUnreadable_IsUnknown(string? header)
    {
        Assert.Null(GitHubRestClient.ParseExpiration(header));
    }

    [Fact]
    public async Task TheLatestRunnerRelease_IsReadWithoutTheToken_AndNamesTheLinuxPackageOnly()
    {
        const string Json = """
            {"tag_name":"v2.338.0","body":"notes","assets":[
              {"name":"actions-runner-linux-x64-2.338.0.tar.gz","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz"},
              {"name":"actions-runner-win-x64-2.338.0.zip","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-win-x64-2.338.0.zip"},
              {"name":"actions-runner-osx-x64-2.338.0.tar.gz","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/x"}]}
            """;
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, Json));
        var client = NewClient(handler);

        var release = await client.GetLatestRunnerReleaseAsync(CancellationToken.None);

        Assert.Equal("2.338.0", release.Version);
        Assert.Equal("notes", release.Notes);
        Assert.EndsWith("linux-x64-2.338.0.tar.gz", release.LinuxPackage.ToString(), StringComparison.Ordinal);
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("https://api.github.com/repos/actions/runner/releases/latest", handler.Requests[0].Url.ToString());
    }

    [Fact]
    public async Task ARunnerReleaseWithoutALinuxPackage_IsRefused()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"tag_name":"v2.338.0","body":"","assets":[]}"""));

        await Assert.ThrowsAsync<DownloadVerificationException>(() => NewClient(handler).GetLatestRunnerReleaseAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    public async Task ErrorsAreSortedIntoWorthRetryingAndNot(HttpStatusCode status, bool transient)
    {
        var handler = new StubHandler(_ => StubHandler.Json(status, """{"message":"nope"}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(handler).GetRepositoryAsync(CancellationToken.None));

        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal((int)status, ex.StatusCode);
    }

    [Fact]
    public async Task AForbiddenAnswerWithAnExhaustedRateLimit_IsWorthRetrying_AndAPermissionRefusalIsNot()
    {
        var limited = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Forbidden, "{}", ("x-ratelimit-remaining", "0")));
        var refused = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}"""));

        var first = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(limited).GetRepositoryAsync(CancellationToken.None));
        var second = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(refused).GetRepositoryAsync(CancellationToken.None));

        Assert.True(first.IsTransient);
        Assert.False(second.IsTransient);
        Assert.True(second.IsAuthenticationFailure);
    }

    [Fact]
    public async Task ANetworkFailure_IsTransient()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(handler).GetRepositoryAsync(CancellationToken.None));

        Assert.True(ex.IsTransient);
        Assert.Equal(0, ex.StatusCode);
    }

    [Fact]
    public async Task WithoutASavedToken_NoRequestIsSent()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{}"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(handler, token: null).ListRunnersAsync(CancellationToken.None));

        Assert.Empty(handler.Requests);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Dispatch_ReturnsTheRunIdWhenGitHubGivesIt_AndNullOtherwise()
    {
        var withId = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"workflow_run_id":123,"run_url":"u","html_url":"h"}"""));
        var without = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.Equal(123, await NewClient(withId).DispatchWorkflowAsync("ci-linux.yml", "main", CancellationToken.None));
        Assert.Null(await NewClient(without).DispatchWorkflowAsync("ci-linux.yml", "main", CancellationToken.None));
        Assert.EndsWith("/actions/workflows/ci-linux.yml/dispatches", withId.Requests[0].Url.ToString(), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(withId.Requests[0].Body!);
        Assert.Equal("main", body.RootElement.GetProperty("ref").GetString());
    }

    [Fact]
    public async Task Retry_WaitsLongerEachTime_ThenGivesUp_AndNeverRetriesAPermanentError()
    {
        var time = TestTime.New();
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var retry = new Retry(time, redactor, log, attempts: 3, firstWait: TimeSpan.FromSeconds(5));
        var calls = 0;

        var transient = retry.RunAsync<int>(
            _ =>
            {
                calls++;
                throw new GitHubApiException("down", 503, isTransient: true);
            },
            "Testing",
            CancellationToken.None);
        while (!transient.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Yield();
        }

        await Assert.ThrowsAsync<GitHubApiException>(() => transient);
        Assert.Equal(3, calls);

        var permanentCalls = 0;
        await Assert.ThrowsAsync<GitHubApiException>(() => retry.RunAsync<int>(
            _ =>
            {
                permanentCalls++;
                throw new GitHubApiException("no", 404, isTransient: false);
            },
            "Testing",
            CancellationToken.None));
        Assert.Equal(1, permanentCalls);
    }

    [Fact]
    public void TheApiHost_IsOnlyApiGitHubCom_OverHttps()
    {
        Assert.True(AllowedHosts.IsAllowedApi(new Uri("https://api.github.com/repos/x")));
        Assert.False(AllowedHosts.IsAllowedApi(new Uri("http://api.github.com/repos/x")));
        Assert.False(AllowedHosts.IsAllowedApi(new Uri("https://api.github.com.evil.example/repos/x")));
        Assert.False(AllowedHosts.IsAllowedApi(new Uri("https://api.github.com:8443/repos/x")));
        Assert.False(AllowedHosts.IsAllowedApi(new Uri("https://user:pw@api.github.com/repos/x")));
    }

    [Fact]
    public async Task ADownload_FollowsGitHubsRedirectToTheReleaseAssetsHost_ButNotToAnyOtherHost()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host switch
        {
            "github.com" => StubHandler.Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/1?sig=abc"),
            "release-assets.githubusercontent.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
            _ => throw new InvalidOperationException("unexpected host"),
        });
        var downloads = new HttpDownloads(new HttpClient(handler));
        using var folder = new TempFolder();
        var target = Path.Combine(folder.Path, "runner.zip");

        await downloads.DownloadToFileAsync(new Uri("https://github.com/actions/runner/releases/download/v2.338.0/a.zip"), target, 1024, CancellationToken.None);

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(["github.com", "release-assets.githubusercontent.com"], handler.Requests.Select(r => r.Url.Host).ToArray());

        var evil = new StubHandler(_ => StubHandler.Redirect("https://evil.example/payload"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new HttpDownloads(new HttpClient(evil)).DownloadToFileAsync(new Uri("https://github.com/actions/runner/releases/download/v2.338.0/x"), Path.Combine(folder.Path, "y"), 1024, CancellationToken.None));
        Assert.Single(evil.Requests);
    }

    [Theory]
    [InlineData("http://releases.ubuntu.com/noble/SHA256SUMS")]
    [InlineData("https://example.com/file")]
    [InlineData("https://releases.ubuntu.com.evil.example/noble/SHA256SUMS")]
    public async Task ADownloadFromAnUnapprovedAddress_IsRefusedBeforeAnyRequest(string address)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpDownloads(new HttpClient(handler)).GetStringAsync(new Uri(address), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheLatestRunnerRelease_CarriesGitHubsOwnDigestOfTheLinuxFile()
    {
        var linux = new string('A', 64);
        var json = $$"""
            {"tag_name":"v2.338.0","body":"notes","assets":[
              {"name":"actions-runner-linux-x64-2.338.0.tar.gz","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz","digest":"sha256:{{linux}}"},
              {"name":"actions-runner-win-x64-2.338.0.zip","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-win-x64-2.338.0.zip","digest":"sha256:{{new string('b', 64)}}"}]}
            """;

        var release = await NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, json))).GetLatestRunnerReleaseAsync(CancellationToken.None);

        Assert.Equal(linux.ToLowerInvariant(), release.LinuxDigest);
    }

    [Theory]
    [InlineData("md5:abcdef")]
    [InlineData("sha256:nothex")]
    [InlineData("sha512:" + "ab")]
    public async Task ADigestInAnUnknownForm_RefusesTheRelease_NotSkipsTheCheck(string digest)
    {
        var json = $$"""
            {"tag_name":"v2.338.0","body":"","assets":[
              {"name":"actions-runner-linux-x64-2.338.0.tar.gz","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz","digest":"{{digest}}"},
              {"name":"actions-runner-win-x64-2.338.0.zip","browser_download_url":"https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-win-x64-2.338.0.zip"}]}
            """;

        await Assert.ThrowsAsync<DownloadVerificationException>(() => NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, json))).GetLatestRunnerReleaseAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("v2.338")]
    [InlineData("nightly")]
    [InlineData("v2.338.0-rc1")]
    public async Task AReleaseWhoseVersionIsNotThreeNumbers_IsRefused(string tag)
    {
        var json = $$"""{"tag_name":"{{tag}}","body":"","assets":[]}""";

        await Assert.ThrowsAsync<DownloadVerificationException>(() => NewClient(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, json))).GetLatestRunnerReleaseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AVariable_IsReadWithItsValue_AndAMissingOneIsNull()
    {
        var present = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"name":"SECRETS_WINDOWS_RUNNER","value":"dbd-windows"}"""));
        var missing = new StubHandler(_ => StubHandler.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));

        Assert.Equal("dbd-windows", await NewClient(present).GetVariableAsync("SECRETS_WINDOWS_RUNNER", CancellationToken.None));
        Assert.Null(await NewClient(missing).GetVariableAsync("SECRETS_WINDOWS_RUNNER", CancellationToken.None));
        Assert.EndsWith("/actions/variables/SECRETS_WINDOWS_RUNNER", present.Requests[0].Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerLargerThanTheLimit_IsRefused_WhetherItAnnouncesItsSizeOrNot()
    {
        var announced = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) };
            response.Content.Headers.ContentLength = GitHubRestClient.MaximumAnswerBytes + 1;
            return response;
        });
        var silent = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new EndlessContent() });

        var first = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(announced).GetRepositoryAsync(CancellationToken.None));
        var second = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(silent).GetRepositoryAsync(CancellationToken.None));

        Assert.Contains("larger", first.Message, StringComparison.Ordinal);
        Assert.Contains("larger", second.Message, StringComparison.Ordinal);
        Assert.False(first.IsTransient);
    }

    /// <summary>An answer with no announced length that never ends.</summary>
    private sealed class EndlessContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new EndlessStream());
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'{', offset, count);
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-win-x64-2.338.0.zip", true)]
    [InlineData("https://releases.ubuntu.com/noble/ubuntu-24.04.5-wsl-amd64.wsl", true)]
    [InlineData("https://github.com/someone/else/releases/download/v1/x.zip", false)]
    [InlineData("https://github.com/actions/runner/archive/main.zip", false)]
    [InlineData("https://releases.ubuntu.com/jammy/x.iso", false)]
    [InlineData("https://releases.ubuntu.com/noble/x?evil=1", false)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1", false)]
    [InlineData("http://github.com/actions/runner/releases/download/v2.338.0/x.zip", false)]
    public void ADownloadMayStartOnlyAtAnOfficialRunnerFileOrCanonicalsFolder(string address, bool allowed)
    {
        Assert.Equal(allowed, AllowedHosts.IsAllowedDownloadStart(new Uri(address)));
    }

    [Fact]
    public async Task ADownloadThatStartsAtAnUnofficialAddress_IsRefusedBeforeAnyRequest()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new HttpDownloads(new HttpClient(handler)).GetStringAsync(new Uri("https://github.com/someone/else/raw/main/x.txt"), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ADownloadAnnouncedAsLargerThanTheLimit_IsNotStartedAndLeavesNoFile()
    {
        using var folder = new TempFolder();
        var target = Path.Combine(folder.Path, "big.zip");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) });

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() =>
            new HttpDownloads(new HttpClient(handler)).DownloadToFileAsync(new Uri("https://github.com/actions/runner/releases/download/v2.338.0/big.zip"), target, 1000, CancellationToken.None));

        Assert.Contains("nothing was downloaded", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ADownloadThatGrowsPastTheLimit_IsStoppedMidWay()
    {
        using var folder = new TempFolder();
        var target = Path.Combine(folder.Path, "endless.zip");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new EndlessContent() });

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() =>
            new HttpDownloads(new HttpClient(handler)).DownloadToFileAsync(new Uri("https://github.com/actions/runner/releases/download/v2.338.0/endless.zip"), target, 200_000, CancellationToken.None));

        Assert.Contains("stopped", ex.Message, StringComparison.Ordinal);
        Assert.True(new FileInfo(target).Length <= 200_000);
    }

    [Fact]
    public void EveryApprovedHost_IsListedForTheArchitectureTest()
    {
        Assert.Equal(
            ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "releases.ubuntu.com"],
            AllowedHosts.All.Order(StringComparer.Ordinal).ToArray());
    }
}
