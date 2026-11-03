// Covers: ADR-0016 / PLAN_V2 §4.1 runtime enforcement: https only, compiled-in host allow-list (a refused request never reaches the
// network), no redirects, the connect-time check when no proxy is used, and the system proxy being honoured.
using System.Net;
using DbdPresence.Online.Api;
using DbdPresence.Online.Http;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class HttpBoundaryTests
{
    private static readonly HashSet<string> s_hosts = new(["api-staging.arcavale.com"], StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("https://api-staging.arcavale.com/v1/config", true)]
    [InlineData("https://API-STAGING.arcavale.com/v1/config", true)]
    [InlineData("http://api-staging.arcavale.com/v1/config", false)]
    [InlineData("https://api.arcavale.com/v1/config", false)]
    [InlineData("https://api-staging.arcavale.com.evil.example/v1/config", false)]
    [InlineData("https://evil.api-staging.arcavale.com/v1/config", false)]
    [InlineData("https://api-staging.arcavale.com:8443/v1/config", false)]
    [InlineData("https://user:pw@api-staging.arcavale.com/v1/config", false)]
    [InlineData("https://127.0.0.1/v1/config", false)]
    [InlineData("ftp://api-staging.arcavale.com/x", false)]
    public void IsAllowed_AcceptsOnlyHttpsOnTheListedHostAndDefaultPort(string url, bool expected) =>
        Assert.Equal(expected, AllowListHandler.IsAllowed(new Uri(url), s_hosts));

    [Fact]
    public async Task NonAllowedHost_IsRefusedAndTheInnerHandlerIsNeverContacted()
    {
        var inner = new FakeService();
        using var client = new HttpClient(new AllowListHandler(s_hosts, inner));

        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri("https://evil.example.com/v1/config"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri("http://api-staging.arcavale.com/v1/config"), TestContext.Current.CancellationToken));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task ServiceClient_ReportsARefusedHostAsUnreachable_AndSendsNothing()
    {
        var inner = new FakeService();
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner), new Uri("https://evil.example.com/"));
        var api = new ServiceClient(http, new FakeTimeProvider());

        var result = await api.GetConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Empty(inner.Requests);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    public async Task Redirects_AreRefused_NotFollowed(int status)
    {
        var inner = new FakeService { Handler = _ => RedirectTo(status) };
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner), new Uri("https://api-staging.arcavale.com/"));
        var api = new ServiceClient(http, new FakeTimeProvider());

        var result = await api.GetConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Single(inner.Requests);
        Assert.DoesNotContain(inner.Requests, r => r.Host == "evil.example.com");
    }

    private static HttpResponseMessage RedirectTo(int status)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new Uri("https://evil.example.com/steal");
        return response;
    }

    [Fact]
    public void InnerHandler_NeverFollowsRedirectsOrUsesCookies_AndChecksTheConnectionWhenNoProxyIsUsed()
    {
        using var direct = ServiceHttp.CreateInnerHandler("api-staging.arcavale.com", new WebProxy());

        Assert.False(direct.AllowAutoRedirect);
        Assert.False(direct.UseCookies);
        Assert.NotNull(direct.ConnectCallback);
        Assert.Null(direct.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void InnerHandler_HonoursTheSystemProxy_SoTheTargetCheckStaysInTheAllowListHandler()
    {
        var proxy = new WebProxy(new Uri("http://corp-proxy.example:3128"));

        using var viaProxy = ServiceHttp.CreateInnerHandler("api-staging.arcavale.com", proxy);

        Assert.True(ServiceHttp.UsesProxyFor(proxy, "api-staging.arcavale.com"));
        Assert.Same(proxy, viaProxy.Proxy);
        Assert.Null(viaProxy.ConnectCallback);
        Assert.False(ServiceHttp.UsesProxyFor(new WebProxy(), "api-staging.arcavale.com"));
    }

    [Fact]
    public async Task ProxiedRequest_StillCarriesTheRealTargetThroughTheHandlerCheck()
    {
        // With a proxy the allow-list handler is the only gate, and it sees the request's real target, not the proxy's address.
        var inner = new FakeService { Handler = _ => FakeService.Json(200, """{"schemaVersion":1,"document":"x"}""") };
        using var client = new HttpClient(new AllowListHandler(s_hosts, inner));

        using var ok = await client.GetAsync(new Uri("https://api-staging.arcavale.com/v1/config"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri("https://corp-proxy.example/v1/config"), TestContext.Current.CancellationToken));

        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData("evil.example.com", 443)]
    [InlineData("api-staging.arcavale.com", 8443)]
    [InlineData("127.0.0.1", 443)]
    public async Task ConnectCheck_RefusesAnyTargetOffTheListBeforeOpeningASocket(string host, int port)
    {
        await Assert.ThrowsAsync<HostNotAllowedException>(async () =>
            await ServiceHttp.ConnectAsync(new DnsEndPoint(host, port), s_hosts, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizeResponse_IsRefused()
    {
        var big = new string('a', ServiceHttp.MaxResponseBytes + 10);
        var inner = new FakeService { Handler = _ => FakeService.Json(200, $$"""{"schemaVersion":1,"document":"{{big}}"}""") };
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner), new Uri("https://api-staging.arcavale.com/"));
        var api = new ServiceClient(http, new FakeTimeProvider());

        var result = await api.GetConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task RequestTimeout_IsEnforcedWithTheInjectedClock()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource();
        var inner = new HangingHandler(entered);
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner), new Uri("https://api-staging.arcavale.com/"));
        var api = new ServiceClient(http, time);

        var pending = api.GetConfigAsync(TestContext.Current.CancellationToken);
        await entered.Task;
        time.Advance(ServiceClient.RequestTimeout + TimeSpan.FromSeconds(1));
        var result = await pending;

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public void ServiceClient_ErrorMapping_KeepsCodesAndRetryAfter()
    {
        // The mapping is exercised end to end in AccountManagerTests; this pins the typed result shape.
        var result = new ApiResult<bool>(ApiOutcome.RateLimited, false, "rate_limited", null, TimeSpan.FromMinutes(3));

        Assert.False(result.IsOk);
        Assert.Equal(TimeSpan.FromMinutes(3), result.RetryAfter);
    }

    private sealed class HangingHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
