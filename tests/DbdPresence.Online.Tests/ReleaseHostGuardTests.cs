// Covers: FR-ONL-10 (update packages come only from GET https://<cdn>/releases/<sha256>.nupkg, exactly the signed size and SHA-256, no redirect),
// ADR-0016 (the CDN host serves nothing else; Velopack's own downloader is never used), PLAN_V2 §6.5.
using System.Security.Cryptography;
using DbdPresence.Online.Api;
using DbdPresence.Online.Http;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class ReleaseHostGuardTests
{
    private const string Cdn = "cdn-staging.arcavale.com";
    private static readonly string s_hash = new('b', 64);
    private static readonly HashSet<string> s_hosts = new(["api-staging.arcavale.com", Cdn], StringComparer.OrdinalIgnoreCase);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/releases/{h}.nupkg", true)]
    [InlineData("/releases/{h}.zip", false)]
    [InlineData("/releases/{h}.nupkg?x=1", false)]
    [InlineData("/releases/{h}.nupkg#x", false)]
    [InlineData("/releases/{H}.nupkg", false)]
    [InlineData("/releases/{short}.nupkg", false)]
    [InlineData("/releases/../v1/config", false)]
    [InlineData("/releases/{h}/x.nupkg", false)]
    [InlineData("/releases/DbdPresence-1.0.0-stable-full.nupkg", false)]
    [InlineData("/packs/{h}.nupkg", false)]
    [InlineData("/RELEASES", false)]
    public void IsReleasePackagePath_AcceptsOnlyTheContentAddressedNames(string template, bool expected)
    {
        var path = template.Replace("{h}", s_hash, StringComparison.Ordinal).Replace("{H}", s_hash.ToUpperInvariant(), StringComparison.Ordinal).Replace("{short}", new string('b', 63), StringComparison.Ordinal);

        Assert.Equal(expected, AllowListHandler.IsReleasePackagePath(new Uri($"https://{Cdn}{path}")));
    }

    [Fact]
    public async Task CdnHost_AllowsGetOfReleasePackages_ButNoOtherMethodOrPath()
    {
        var inner = new FakeService { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1]) } };
        using var client = new HttpClient(new AllowListHandler(s_hosts, inner, Cdn));

        using var ok = await client.GetAsync(new Uri($"https://{Cdn}/releases/{s_hash}.nupkg"), Ct);

        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.PostAsync(new Uri($"https://{Cdn}/releases/{s_hash}.nupkg"), new StringContent("x"), Ct));
        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(new Uri($"https://{Cdn}/releases/feed.json"), Ct));
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Download_AcceptsExactlyTheSignedSizeAndHash()
    {
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var inner = new FakeService { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } };
        var api = Client(inner);
        using var destination = new MemoryStream();
        var progress = new List<int>();

        var result = await api.DownloadReleasePackageAsync(new Uri($"https://{Cdn}/releases/{sha}.nupkg"), bytes.Length, sha, destination, progress.Add, Ct);

        Assert.True(result.IsOk);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(100, progress[^1]);
    }

    [Theory]
    [InlineData("other-hash")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task Download_RefusesAnyOtherBytes(string change)
    {
        var bytes = RandomNumberGenerator.GetBytes(1000);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var served = change switch
        {
            "other-hash" => [.. bytes[..^1], (byte)(bytes[^1] ^ 1)],
            "short" => bytes[..^1],
            _ => [.. bytes, 0],
        };
        var inner = new FakeService { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(served) } };
        using var destination = new MemoryStream();

        var result = await Client(inner).DownloadReleasePackageAsync(new Uri($"https://{Cdn}/releases/{sha}.nupkg"), bytes.Length, sha, destination, null, Ct);

        Assert.Equal(ApiOutcome.Rejected, result.Outcome);
        Assert.Equal(ServiceClient.PackageMismatchCode, result.ErrorCode);
        Assert.True(destination.Length <= bytes.Length, "No more bytes than the signed size are ever written.");
    }

    [Fact]
    public async Task Download_ThatRedirects_IsRefusedAndNeverFollowed()
    {
        var inner = new FakeService
        {
            Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.com/x.nupkg") } },
        };
        using var destination = new MemoryStream();

        var result = await Client(inner).DownloadReleasePackageAsync(new Uri($"https://{Cdn}/releases/{s_hash}.nupkg"), 10, s_hash, destination, null, Ct);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Single(inner.Requests);
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData("https://evil.example.com/releases/{h}.nupkg")]
    [InlineData("https://cdn.arcavale.com/releases/{h}.nupkg")]
    [InlineData("http://cdn-staging.arcavale.com/releases/{h}.nupkg")]
    public async Task Download_FromAHostOffTheList_IsUnreachable_AndNothingIsSent(string template)
    {
        var inner = new FakeService { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1]) } };
        using var destination = new MemoryStream();

        var result = await Client(inner).DownloadReleasePackageAsync(new Uri(template.Replace("{h}", s_hash, StringComparison.Ordinal)), 1, s_hash, destination, null, Ct);

        Assert.Equal(ApiOutcome.Unreachable, result.Outcome);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task ReleaseFeedAndKeyReplacement_AreReadFromTheApiHost()
    {
        var inner = new FakeService { Handler = r => FakeService.Json(200, $$"""{"schemaVersion":1,"document":"doc:{{r.Path}}"}""") };
        var api = Client(inner);

        var feed = await api.GetReleaseFeedAsync("stable", Ct);
        var keys = await api.GetKeyReplacementAsync(Ct);

        Assert.Equal("doc:/v1/releases/stable", feed.Value);
        Assert.Equal("doc:/v1/keys", keys.Value);
        Assert.All(inner.Requests, r => Assert.Equal("api-staging.arcavale.com", r.Host));
        Assert.Throws<ArgumentException>(() => { _ = api.GetReleaseFeedAsync("../stable", Ct); });
    }

    private static ServiceClient Client(FakeService inner)
    {
        var http = ServiceHttp.CreateClient(new AllowListHandler(s_hosts, inner, Cdn), new Uri("https://api-staging.arcavale.com/"));
        return new ServiceClient(http, new FakeTimeProvider());
    }
}
