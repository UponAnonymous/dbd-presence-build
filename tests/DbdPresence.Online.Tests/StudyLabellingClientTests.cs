// Covers: FR-LAB-02 (the relay client of the labeller), ADR-0018 (the relay contract: bearer, JSON body with a known length, a long wait), project rule 3 (only the
// compiled-in API host, through the existing client and allow-list).
using System.Text;
using DbdPresence.Online.Api;
using DbdPresence.Online.Http;
using DbdPresence.Online.Study;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class StudyLabellingClientTests
{
    private const string Ok = """{"schemaVersion":1,"text":"{\"segments\":[]}","stopReason":"end_turn","inputTokens":1234,"outputTokens":56}""";
    private static readonly byte[] s_body = Encoding.UTF8.GetBytes("""{"model":"reviewer","messages":[]}""");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Capture : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? ContentType { get; private set; }

        public long? ContentLength { get; private set; }

        public byte[] Body { get; private set; } = [];

        public Func<HttpResponseMessage> Respond { get; set; } = () => FakeService.Json(200, Ok);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.ToString();
            ContentLength = request.Content?.Headers.ContentLength;
            Body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Started.TrySetResult();
            if (Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Respond();
        }
    }

    private static (ServiceClient Api, Capture Handler, FakeTimeProvider Time) Client()
    {
        var handler = new Capture();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        var http = ServiceHttp.CreateClient(new AllowListHandler(new HashSet<string>(["api.arcavale.com"], StringComparer.OrdinalIgnoreCase), handler), new Uri("https://api.arcavale.com/"));
        return (new ServiceClient(http, time), handler, time);
    }

    [Fact]
    public async Task ThePost_CarriesTheBearer_AJsonBodyOfKnownLength_AndGivesBackTheAnswerAndCounts()
    {
        var (api, handler, _) = Client();

        var result = await api.LabelAsync("at_token", s_body, Ct);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.arcavale.com/v1/study/label", handler.Uri!.ToString());
        Assert.Equal("Bearer at_token", handler.Authorization);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal(s_body.Length, handler.ContentLength);
        Assert.Equal(s_body, handler.Body);
        Assert.True(result.IsOk);
        Assert.Equal(new LabellerAnswer("{\"segments\":[]}", "end_turn", 1234, 56), result.Value);
    }

    [Fact]
    public async Task TheCall_WaitsTenMinutes_NotTwentySeconds()
    {
        var (api, handler, time) = Client();
        handler.Hang = true;

        var call = api.LabelAsync("at_token", s_body, Ct);
        await handler.Started.Task;
        time.Advance(ServiceClient.RequestTimeout + TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(call.IsCompleted, "the usual 20 second timeout must not apply");
        time.Advance(TimeSpan.FromMinutes(10) - ServiceClient.RequestTimeout - TimeSpan.FromSeconds(2));
        await Task.Yield();
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        var result = await call;
        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(10), ServiceClient.StudyLabelTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30 * 1024 * 1024 + 1)]
    public async Task ABodyOutsideTheRelaysLimit_IsNeverSent(int length)
    {
        var (api, handler, _) = Client();

        await Assert.ThrowsAsync<ArgumentException>(() => api.LabelAsync("at_token", new byte[length], Ct));
        Assert.Null(handler.Method);
    }

    [Theory]
    [InlineData(200, Ok, null, LabellingStatus.Ok)]
    [InlineData(404, """{"schemaVersion":1,"error":"not_found"}""", null, LabellingStatus.NotAllowed)]
    [InlineData(401, """{"schemaVersion":1,"error":"unauthenticated"}""", null, LabellingStatus.NotAllowed)]
    [InlineData(429, """{"schemaVersion":1,"error":"rate_limited"}""", 3600, LabellingStatus.DailyCapReached)]
    [InlineData(503, """{"schemaVersion":1,"error":"labelling_not_set_up"}""", null, LabellingStatus.NotSetUp)]
    [InlineData(503, """{"schemaVersion":1,"error":"labeller_busy"}""", 20, LabellingStatus.Busy)]
    [InlineData(502, """{"schemaVersion":1,"error":"labeller_refused","reason":"invalid_request_error"}""", null, LabellingStatus.Refused)]
    [InlineData(502, """{"schemaVersion":1,"error":"labeller_answer_unreadable"}""", null, LabellingStatus.Refused)]
    [InlineData(502, """{"schemaVersion":1,"error":"labeller_unreachable"}""", null, LabellingStatus.Failed)]
    [InlineData(400, """{"schemaVersion":1,"error":"invalid_request"}""", null, LabellingStatus.Failed)]
    [InlineData(413, """{"schemaVersion":1,"error":"too_large"}""", null, LabellingStatus.Failed)]
    public async Task EveryStatus_IsMappedToTheOutcomeTheRunActsOn(int status, string body, int? retrySeconds, LabellingStatus expected)
    {
        var (api, handler, _) = Client();
        handler.Respond = () => FakeService.Json(status, body, retrySeconds is { } s ? TimeSpan.FromSeconds(s) : null);

        var response = StudyLabellingClient.Map(await api.LabelAsync("at_token", s_body, Ct));

        Assert.Equal(expected, response.Status);
        if (expected == LabellingStatus.Ok)
        {
            Assert.Equal((1234, 56, "end_turn"), (response.InputTokens, response.OutputTokens, response.StopReason));
        }

        if (expected == LabellingStatus.Busy)
        {
            Assert.Equal(TimeSpan.FromSeconds(20), response.RetryAfter);
        }
    }

    [Fact]
    public async Task AnUnreadableOrOfflineAnswer_IsFailed()
    {
        var (api, handler, _) = Client();
        handler.Respond = () => FakeService.Json(200, """{"schemaVersion":1,"text":5}""");
        Assert.Equal(LabellingStatus.Failed, StudyLabellingClient.Map(await api.LabelAsync("at_token", s_body, Ct)).Status);

        handler.Respond = () => throw new HttpRequestException("offline");
        Assert.Equal(LabellingStatus.Failed, StudyLabellingClient.Map(await api.LabelAsync("at_token", s_body, Ct)).Status);
    }

    [Fact]
    public async Task ThroughTheAccount_TheSignedInTokenIsUsed_AndSignedOutIsNotAllowed()
    {
        using var h = new AccountHarness();
        var client = new StudyLabellingClient(h.Account, h.Api);
        Assert.Equal(LabellingStatus.NotAllowed, (await client.LabelAsync(s_body, Ct)).Status);
        Assert.DoesNotContain(h.Service.Requests, r => r.Path == "/v1/study/label");

        Assert.True((await h.Account.SignInAsync(Ct)).Succeeded);
        h.Service.Handler = r => r.Path == "/v1/study/label" ? FakeService.Json(200, Ok) : h.DefaultHandler(r);

        var response = await client.LabelAsync(s_body, Ct);

        Assert.Equal(LabellingStatus.Ok, response.Status);
        var sent = Assert.Single(h.Service.Requests, r => r.Path == "/v1/study/label");
        Assert.StartsWith("Bearer at_token", sent.Authorization, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(s_body), sent.Body);
        Assert.Equal(HttpMethod.Post, sent.Method);
    }

    [Fact]
    public async Task AnotherHost_IsRefusedByTheAllowList_SoNothingLeavesThePc()
    {
        var handler = new Capture();
        var http = ServiceHttp.CreateClient(new AllowListHandler(new HashSet<string>(["api.arcavale.com"], StringComparer.OrdinalIgnoreCase), handler), new Uri("https://elsewhere.example/"));
        var api = new ServiceClient(http, new FakeTimeProvider());

        var result = await api.LabelAsync("at_token", s_body, Ct);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Null(handler.Method);
    }
}
