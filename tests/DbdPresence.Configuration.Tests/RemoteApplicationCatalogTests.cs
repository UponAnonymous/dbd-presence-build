// Covers: FR-ONL-08 (the signed application list replaces the bundled one; the bundled list stays the fallback), D8, ADR-0012.
using DbdPresence.Core.Online;

namespace DbdPresence.Configuration.Tests;

public sealed class RemoteApplicationCatalogTests
{
    private sealed class FakeRemote(RemoteConfigSnapshot snapshot) : IRemoteConfigSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public RemoteConfigSnapshot Current { get; set; } = snapshot;
    }

    private static readonly RemoteApplication s_a = new("dbd", "1563291325107937340", "Dead by Daylight", "Unofficial");
    private static readonly RemoteApplication s_b = new("alt", "1563291325107937341", "Fog Presence", "Unofficial");

    [Fact]
    public void NoRemoteList_UsesTheBundledCatalog()
    {
        var catalog = new RemoteApplicationCatalog(new BundledApplicationCatalog(), new FakeRemote(RemoteConfigSnapshot.SafeDefaults));

        Assert.Equal(["dbd"], catalog.Applications.Select(a => a.Key));
        Assert.Equal("1563291325107937340", catalog.Resolve(null).ApplicationId);
    }

    [Fact]
    public void RemoteList_ReplacesTheBundledOne_WithTheRemoteDefaultFirst()
    {
        var remote = new FakeRemote(RemoteConfigSnapshot.SafeDefaults with { Applications = [s_a, s_b], DefaultApplicationKey = "alt" });
        var catalog = new RemoteApplicationCatalog(new BundledApplicationCatalog(), remote);

        Assert.Equal(["alt", "dbd"], catalog.Applications.Select(a => a.Key));
        Assert.Equal("Fog Presence", catalog.Resolve("unknown-key").Name);
        Assert.Equal("Dead by Daylight", catalog.Resolve("dbd").Name);
    }

    [Fact]
    public void AnExpiredConfig_StillOffersItsLastGoodApplications()
    {
        var expired = RemoteConfigSnapshot.SafeDefaults with { Applications = [s_a, s_b], IsFresh = false };
        var catalog = new RemoteApplicationCatalog(new BundledApplicationCatalog(), new FakeRemote(expired));

        Assert.Equal(2, catalog.Applications.Count);
    }
}
