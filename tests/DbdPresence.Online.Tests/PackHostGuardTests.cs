// Covers: ADR-0016 (CDN host used only for GET of /packs/<64 hex>.png|jpg, path check in the handler as well as the host check, no redirects,
// a non-allow-listed host is never contacted), FR-ONL-07 (file response size cap).
using DbdPresence.Online.Api;
using DbdPresence.Online.Http;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class PackHostGuardTests
{
    private const string Cdn = "cdn-staging.arcavale.com";
    private static readonly string s_hash = new('a', 64);
    private static readonly HashSet<string> s_hosts = new(["api-staging.arcavale.com", Cdn], StringComparer.OrdinalIgnoreCase);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/packs/{h}.png", true)]
    [InlineData("/packs/{h}.jpg", true)]
    [InlineData("/packs/{h}.jpeg", false)]
    [InlineData("/packs/{h}.gif", false)]
    [InlineData("/packs/{h}", false)]
    [InlineData("/packs/{h}.png?x=1", false)]
    [InlineData("/packs/{h}.png#top", false)]
    [InlineData("/packs/../v1/config", false)]
    [InlineData("/packs/{h}/extra.png", false)]
    [InlineData("/u/{h}.png", false)]
    [InlineData("/app/{h}.png", false)]
    [InlineData("/v1/config", false)]
    [InlineData("/", false)]
    [InlineData("/packs/", false)]
    [InlineData("/packs/{H}.png", false)]
    [InlineData("/packs/{short}.png", false)]
    public void IsPackFilePath_AcceptsOnlyTheContentAddressedNames(string template, bool expected)
    {
        var path = template.Replace("{h}", s_hash, StringComparison.Ordinal).Replace("{H}", s_hash.ToUpperInvariant(), StringComparison.Ordinal).Replace("{short}", new string('a', 63), StringComparison.Ordinal);

        Assert.Equal(expected, AllowListHandler.IsPackFilePath(new Uri($"https://{Cdn}{path}")));
    }

    [Fact]
    public async Task CdnHost_AllowsGetOfPackFiles_AndNothingElse()
    {
        var inner = new FakeService { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) } };
        using var client = new HttpClient(new AllowListHandler(s_hosts, inner, Cdn));

        using var ok = await client.GetAsync(new Uri($"https://{Cdn}/packs/{s_hash}.png"), Ct);
        Assert.Single(inner.Requests);

        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri($"https://{Cdn}/v1/config"), Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri($"https://{Cdn}/packs/{s_hash}.png?download=1"), Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.PostAsync(new Uri($"https://{Cdn}/packs/{s_hash}.png"), new StringContent("x"), Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.DeleteAsync(new Uri($"https://{Cdn}/packs/{s_hash}.png"), Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri($"https://{Cdn}/packs/{s_hash}.png")), Ct));
        Assert.Single(inner.Requests); // every refused request stopped before the network
    }

    [Fact]
    public async Task ApiHost_IsNotRestrictedToPackPaths_ButTheCdnHostIsNotAnApiHost()
    {
        var inner = new FakeService { Handler = _ => FakeService.Json(200, """{"schemaVersion":1,"document":"x"}""") };
        using var client = new HttpClient(new AllowListHandler(s_hosts, inner, Cdn));

        using var api = await client.GetAsync(new Uri("https://api-staging.arcavale.com/v1/packs?channel=stable"), Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, api.StatusCode);
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri($"https://{Cdn}/v1/packs?channel=stable"), Ct));
    }

    [Theory]
    [InlineData("https://evil.example.com/packs/{h}.png")]
    [InlineData("https://cdn.arcavale.com/packs/{h}.png")] // the production CDN host is not allowed in a staging build
    [InlineData("https://cdn-staging.arcavale.com.evil.example/packs/{h}.png")]
    [InlineData("http://cdn-staging.arcavale.com/packs/{h}.png")]
    [InlineData("https://cdn-staging.arcavale.com:8443/packs/{h}.png")]
    [InlineData("https://127.0.0.1/packs/{h}.png")]
    public async Task DownloadFromAHostOffTheList_IsUnreachable_AndNothingIsSent(string template)
    {
        var inner = new FakeService { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1]) } };
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner, Cdn), new Uri("https://api-staging.arcavale.com/"));
        var api = new ServiceClient(http, new FakeTimeProvider());

        var result = await api.DownloadPackFileAsync(new Uri(template.Replace("{h}", s_hash, StringComparison.Ordinal)), 100, Ct);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task DownloadThatRedirects_IsRefusedAndNeverFollowed()
    {
        var inner = new FakeService
        {
            Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.com/x.png") } },
        };
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner, Cdn), new Uri("https://api-staging.arcavale.com/"));
        var api = new ServiceClient(http, new FakeTimeProvider());

        var result = await api.DownloadPackFileAsync(new Uri($"https://{Cdn}/packs/{s_hash}.png"), 100, Ct);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Download_ReadsAtMostTheDeclaredSize()
    {
        var inner = new FakeService { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[101]) } };
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner, Cdn), new Uri("https://api-staging.arcavale.com/"));
        var api = new ServiceClient(http, new FakeTimeProvider());
        var url = new Uri($"https://{Cdn}/packs/{s_hash}.png");

        var tooBig = await api.DownloadPackFileAsync(url, 100, Ct);
        var exact = await api.DownloadPackFileAsync(url, 101, Ct);

        Assert.Equal(ApiOutcome.Rejected, tooBig.Outcome);
        Assert.Equal(ServiceClient.FileTooLargeCode, tooBig.ErrorCode);
        Assert.Equal(101, exact.Value!.Length);
    }

    [Fact]
    public async Task Download429_SetsABackoff_ThatCoversTheNextDownload()
    {
        var time = new FakeTimeProvider();
        var inner = new FakeService { Handler = _ => FakeService.Json(429, "{}", TimeSpan.FromSeconds(40)) };
        using var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner, Cdn), new Uri("https://api-staging.arcavale.com/"));
        var api = new ServiceClient(http, time);
        var url = new Uri($"https://{Cdn}/packs/{s_hash}.png");

        var first = await api.DownloadPackFileAsync(url, 100, Ct);
        var second = await api.DownloadPackFileAsync(url, 100, Ct);

        Assert.Equal(ApiOutcome.RateLimited, first.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(40), first.RetryAfter);
        Assert.Equal(ApiOutcome.RateLimited, second.Outcome);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task RealHandlerChain_RefusesAThirdHost_BeforeAnyConnection()
    {
        using var client = new HttpClient(ServiceHttp.CreateHandler("api-staging.arcavale.com", new System.Net.WebProxy(), Cdn));

        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri($"https://evil.example.com/packs/{s_hash}.png"), Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri($"https://{Cdn}/v1/config"), Ct));
    }
}
