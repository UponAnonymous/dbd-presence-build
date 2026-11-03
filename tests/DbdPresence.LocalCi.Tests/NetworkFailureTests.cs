// Covers: QA M5, T11, T11b, T17, T17b (a connection that breaks in the middle of GitHub's answer, an answer of the wrong shape, and an answer or a download that starts and then goes quiet all end
// as the typed failure every caller expects, within a limit measured on the injected clock; a half-written download is not kept as a good file). docs/owner/free-ci.md.
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class NetworkFailureTests : IDisposable
{
    private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();

    public void Dispose() => _folder.Dispose();

    private sealed class BrokenContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"default_br"));
            throw new IOException("The response ended prematurely.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>A body that sends a little, then says it is waiting (the test advances the clock), and never sends anything more.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _first;
        private readonly TaskCompletionSource _stalled;
        private bool _sentFirst;

        public StallingStream(byte[] first, TaskCompletionSource stalled)
        {
            _first = first;
            _stalled = stalled;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            // A read-only stream has nothing to flush.
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }

            _stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private GitHubRestClient Client(StubHandler handler) =>
        new(new HttpClient(handler), () => new SecretText("t"), "o", "r", s_limit, _time);

    [Fact]
    public async Task ABrokenAnswerBody_IsAGitHubApiException_LikeEveryOtherNetworkFailure()
    {
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() }));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.ListRunnersAsync(CancellationToken.None));

        Assert.True(ex.IsTransient);
        Assert.Contains("broke off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerOfTheWrongShape_IsAGitHubApiException()
    {
        var client = Client(new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"total_count\":0}")));

        await Assert.ThrowsAsync<GitHubApiException>(() => client.ListRunnersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnAnswerThatStartsAndThenGoesQuiet_IsGivenUpAfterTheLimit_AsATransientFailure()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes("{\"total_c"), stalled)) }));
        var asking = client.ListRunnersAsync(CancellationToken.None);

        await stalled.Task.OrFail();
        _time.Advance(s_limit);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => asking.OrFail());
        Assert.True(ex.IsTransient);
        Assert.Contains("stopped sending its answer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryAnswer_IsReadToTheEnd()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"total_count\":0,\"runners\":[]}"));
        var client = Client(handler);

        var runners = await client.ListRunnersAsync(CancellationToken.None);

        Assert.Empty(runners);
    }

    [Fact]
    public async Task ADownloadThatStartsAndThenGoesQuiet_IsGivenUpAfterTheLimit_AndIsNotKeptAsAGoodFile()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(new byte[100], stalled)) });
        using var http = new HttpClient(handler);
        var downloads = new HttpDownloads(http, s_limit, _time);
        Directory.CreateDirectory(_folder.Paths.RunnerCache);
        var target = Path.Combine(_folder.Paths.RunnerCache, "x.part");
        var url = new Uri("https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz");
        var downloading = downloads.DownloadToFileAsync(url, target, 1_000_000, CancellationToken.None);

        await stalled.Task.OrFail();
        _time.Advance(s_limit);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => downloading.OrFail());
        Assert.Contains("stopped sending data", ex.Message, StringComparison.Ordinal);
        Assert.Equal(100, new FileInfo(target).Length);
    }

    [Fact]
    public async Task ATextDownloadThatGoesQuiet_IsGivenUpToo()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes("abc"), stalled)) });
        using var http = new HttpClient(handler);
        var downloads = new HttpDownloads(http, s_limit, _time);
        var url = new Uri("https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz");
        var reading = downloads.GetStringAsync(url, CancellationToken.None);

        await stalled.Task.OrFail();
        _time.Advance(s_limit);

        await Assert.ThrowsAsync<HttpRequestException>(() => reading.OrFail());
    }

    [Fact]
    public async Task TheCallersOwnCancel_StaysACancel()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes("{"), stalled)) }));
        using var cts = new CancellationTokenSource();
        var asking = client.ListRunnersAsync(cts.Token);
        await stalled.Task.OrFail();

        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => asking.OrFail());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }
}
