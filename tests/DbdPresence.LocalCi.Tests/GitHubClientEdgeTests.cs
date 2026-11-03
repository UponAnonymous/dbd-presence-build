// Covers: FR-CI-04 (the 8 MB limit on GitHub's answers, exact), FR-CI-10 (what is transient and what is not: the line between success and failure, the 500 edge, a timeout, a missing token), FR-CI-01
// (a runner that is already gone is not an error when it is removed). Written for the survivors of the round 3 mutation run (docs/reviews/2026-10-30-local-ci-round3-qa.md, Resolution). No real
// network: every request goes to a stub handler. docs/owner/free-ci.md.
using System.Net;
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class GitHubClientEdgeTests
{
    private const string RepositoryJson = "{\"default_branch\":\"main\",\"private\":true}";

    private static GitHubRestClient NewClient(StubHandler handler, string? token = "github_pat_SECRETSECRETSECRET") =>
        new(new HttpClient(handler), () => token is null ? null : new SecretText(token), "UponAnonymous", "dbd-presence");

    /// <summary>The repository answer followed by spaces, exactly <paramref name="length"/> bytes long.</summary>
    private static byte[] Padded(int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        Encoding.UTF8.GetBytes(RepositoryJson).CopyTo(bytes, 0);
        return bytes;
    }

    private static HttpResponseMessage Unannounced(byte[] data) => new(HttpStatusCode.OK) { Content = new UnannouncedContent(data) };

    /// <summary>A body whose length is not announced (so the program has to count it while it reads).</summary>
    private sealed class UnannouncedContent : HttpContent
    {
        private readonly byte[] _data;

        public UnannouncedContent(byte[] data)
        {
            _data = data;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new ForwardOnlyStream(_data));
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public ForwardOnlyStream(byte[] data)
        {
            _data = data;
        }

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
            var n = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AnAnswerOfExactlyTheLimit_IsRead_AndOneByteMoreIsRefused_WhenItsLengthIsNotAnnounced()
    {
        Assert.Equal(8L * 1024 * 1024, GitHubRestClient.MaximumAnswerBytes); // 8 MB, as FR-CI-04 says
        var limit = (int)GitHubRestClient.MaximumAnswerBytes;

        var exact = await NewClient(new StubHandler(_ => Unannounced(Padded(limit)))).GetRepositoryAsync(CancellationToken.None);
        var tooLong = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(new StubHandler(_ => Unannounced(Padded(limit + 1)))).GetRepositoryAsync(CancellationToken.None));

        Assert.Equal("main", exact.DefaultBranch);
        Assert.Contains("larger", tooLong.Message, StringComparison.Ordinal);
        Assert.False(tooLong.IsTransient);
    }

    [Fact]
    public async Task ARunnerThatIsAlreadyGone_IsNotAnErrorWhenItIsRemoved_ButARefusalIs()
    {
        var gone = new StubHandler(_ => StubHandler.Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        var refused = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}"""));

        await NewClient(gone).DeleteRunnerAsync(42, CancellationToken.None);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(refused).DeleteRunnerAsync(42, CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task WithoutASavedToken_AnAuthenticatedCallFailsAtOnce_AndIsNotTransient()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{}"));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(handler, token: null).ListRunnersAsync(CancellationToken.None));

        Assert.Equal(401, error.StatusCode);
        Assert.False(error.IsTransient);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnAnswerThatTimesOutByItself_IsTransient_ButTheCallersOwnCancelIsNot()
    {
        var slow = new StubHandler(_ => throw new TaskCanceledException("the call timed out"));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(slow).GetRepositoryAsync(CancellationToken.None));

        Assert.True(error.IsTransient);
        Assert.Equal(0, error.StatusCode);
        Assert.Contains("did not answer in time", error.Message, StringComparison.Ordinal);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = new StubHandler(_ => throw new TaskCanceledException("cancelled", null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewClient(cancelled).GetRepositoryAsync(cts.Token));
    }

    [Fact]
    public async Task AGoodStatusWithNothingInIt_IsAFailureThatIsNotTransient()
    {
        var empty = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) });

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => NewClient(empty).GetRepositoryAsync(CancellationToken.None));

        Assert.Contains("without data", error.Message, StringComparison.Ordinal);
        Assert.False(error.IsTransient);
    }

    [Theory]
    [InlineData(200, true, false, "")]
    [InlineData(299, true, false, "")]
    [InlineData(300, false, false, "GitHub answered 300.")]
    [InlineData(404, false, false, "GitHub could not find it")]
    [InlineData(405, false, false, "GitHub answered 405.")]
    [InlineData(499, false, false, "GitHub answered 499.")]
    [InlineData(500, false, true, "GitHub answered 500.")]
    [InlineData(503, false, true, "GitHub answered 503.")]
    public async Task TheLineBetweenSuccessAndFailure_AndTheLineForATransientOne_AreWhereTheyShouldBe(int status, bool success, bool transient, string reason)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") });
        var client = NewClient(handler);

        if (success)
        {
            await client.CancelWorkflowRunAsync(1, CancellationToken.None);
            return;
        }

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CancelWorkflowRunAsync(1, CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(transient, error.IsTransient);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobWhoseLabelsAreNotAList_OrMissing_IsListedWithoutLabels()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            HttpStatusCode.OK,
            """{"jobs":[{"id":1,"name":"a","status":"queued","labels":"dbd-linux"},{"id":2,"name":"b","status":"queued"},{"id":3,"name":"c","status":"queued","labels":["self-hosted",7,"dbd-linux"]}]}"""));

        var jobs = await NewClient(handler).ListWorkflowJobsAsync(5, CancellationToken.None);

        Assert.Equal(3, jobs.Count);
        Assert.Empty(jobs[0].Labels ?? []);
        Assert.Empty(jobs[1].Labels ?? []);
        Assert.Equal(["self-hosted", "dbd-linux"], jobs[2].Labels ?? []);
    }
}
