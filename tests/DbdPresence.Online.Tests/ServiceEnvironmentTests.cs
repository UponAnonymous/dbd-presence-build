// Covers: ADR-0016 (host allow-list per environment), ADR-0012 (key scoping per purpose), build/check-online-keys.py (production keys differ from staging).
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Tests;

public sealed class ServiceEnvironmentTests
{
    [Fact]
    public void StagingKeys_AreRealP256Keys_ScopedToOnePurposeEach()
    {
        var keys = ServiceEnvironment.LoadKeys("staging");

        // The service keys and, since 2026-11-03, the owner's offline release and recovery keys (docs/owner/release-keys.md).
        Assert.Equal(["config-staging-1", "licence-staging-1", "recovery-staging-1", "release-staging-1"], keys.Keys.Where(k => !k.StartsWith("pack-index-", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        var set = new TrustedKeySet(keys); // throws unless every key is a valid P-256 SPKI
        Assert.True(set.IsAllowedFor("licence-staging-1", SignedPurposes.Licence));
        Assert.False(set.IsAllowedFor("licence-staging-1", SignedPurposes.Config));
        Assert.True(set.IsAllowedFor("config-staging-1", SignedPurposes.Config));
        Assert.False(set.IsAllowedFor("config-staging-1", SignedPurposes.Releases));
        Assert.True(set.IsAllowedFor("release-staging-1", SignedPurposes.Releases));
        Assert.False(set.IsAllowedFor("release-staging-1", SignedPurposes.Config));
        Assert.False(set.IsAllowedFor("recovery-staging-1", SignedPurposes.Releases));
    }

    [Fact]
    public void ProductionKeys_ShareNothingWithStaging_AndAreNeverTheStagingSet()
    {
        var staging = ServiceEnvironment.LoadKeys("staging");
        var production = ServiceEnvironment.LoadKeys("production");

        Assert.Empty(production.Keys.Intersect(staging.Keys, StringComparer.Ordinal));
        Assert.DoesNotContain(production.Values, p => staging.Values.Any(s => s.Spki.SequenceEqual(p.Spki)));
    }

    [Fact]
    public void Current_MatchesTheBuildEnvironment()
    {
        var current = ServiceEnvironment.Current;
#if ONLINE_PRODUCTION
        Assert.Equal(ServiceEnvironment.ProductionHost, current.ApiHost);
        Assert.NotEmpty(current.Keys); // a release build with an empty production key set must not exist
        Assert.NotEqual(ServiceEnvironment.LoadKeys("staging").Keys.Order(StringComparer.Ordinal), current.Keys.Keys.Order(StringComparer.Ordinal));
#else
        Assert.Equal(ServiceEnvironment.StagingHost, current.ApiHost);
        Assert.Equal("staging", current.Name);
#endif
        Assert.Equal($"https://{current.ApiHost}/", current.BaseAddress.AbsoluteUri);
        Assert.True(current.CreateTrustedKeySet().Count > 0);
    }

    [Fact]
    public void Hosts_AreTheOwnersTwoApiHostsOnly()
    {
        Assert.Equal("api.arcavale.com", ServiceEnvironment.ProductionHost);
        Assert.Equal("api-staging.arcavale.com", ServiceEnvironment.StagingHost);
    }

    [Fact]
    public void CdnHosts_AreTheOwnersTwoCdnHosts_AndTheFilesFolderIsPacksOnly()
    {
        Assert.Equal("cdn.arcavale.com", ServiceEnvironment.ProductionCdnHost);
        Assert.Equal("cdn-staging.arcavale.com", ServiceEnvironment.StagingCdnHost);
#if ONLINE_PRODUCTION
        Assert.Equal(ServiceEnvironment.ProductionCdnHost, ServiceEnvironment.Current.CdnHost);
#else
        Assert.Equal(ServiceEnvironment.StagingCdnHost, ServiceEnvironment.Current.CdnHost);
#endif
        Assert.Equal($"https://{ServiceEnvironment.Current.CdnHost}/packs/", ServiceEnvironment.Current.PackFilesBaseAddress.AbsoluteUri);
    }

    [Fact]
    public void PackIndexKey_IsDeclaredForTheStagingSet_AndAnEntryAwaitingItsKeyTrustsNothing()
    {
        using var json = System.Text.Json.JsonDocument.Parse(
            typeof(ServiceEnvironment).Assembly.GetManifestResourceStream("TrustedServiceKeys.json")!);
        var declared = json.RootElement.GetProperty("staging").EnumerateArray()
            .Where(k => k.GetProperty("purposes").EnumerateArray().Any(p => p.GetString() == SignedPurposes.PackIndex))
            .ToArray();
        Assert.Single(declared);

        var trusted = new TrustedKeySet(ServiceEnvironment.LoadKeys("staging"));
        var isAwaitingKey = declared[0].TryGetProperty("awaitingKey", out var flag) && flag.GetBoolean();
        var kid = declared[0].GetProperty("kid").GetString()!;

        // An entry awaiting its key has no key, so nothing signed "by" it can verify; a real entry is scoped to the pack-index purpose only.
        Assert.Equal(!isAwaitingKey, trusted.IsAllowedFor(kid, SignedPurposes.PackIndex));
        Assert.False(trusted.IsAllowedFor(kid, SignedPurposes.Config));
        Assert.False(trusted.IsAllowedFor("licence-staging-1", SignedPurposes.PackIndex));
        Assert.False(trusted.IsAllowedFor("config-staging-1", SignedPurposes.PackIndex));
    }
}
