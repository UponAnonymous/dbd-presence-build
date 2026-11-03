// Covers: FR-ONL-07 / FR-PACK-03 (a synced image reaches Discord as its https CDN URL), FR-RPC-12 (the hover text carries
// "Unofficial · © Behaviour Interactive" exactly when the signed index flags the pack behaviourArt), FR-PACK-06 (imports and overrides stay
// preview-only), D16 (fallback pack), FR-PACK-04 (withdrawn items stop being sent).
using DbdPresence.Configuration;
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class SyncedArtworkTests : IDisposable
{
    private const string Cdn = "https://cdn.example.org/packs/";
    private readonly Harness _host = new();
    private readonly PackHarness _packs;
    private readonly FakeSink _sink = new();
    private readonly FakeLifecycleSource _game = new();
    private readonly PresenceCoordinator _coordinator;
    private readonly StubFeed _feed = new();

    public SyncedArtworkTests()
    {
        _packs = new PackHarness(_host);
        _coordinator = new PresenceCoordinator(
            _host.Settings, _host.Catalog, _game, _sink, new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero)), _packs.Provider, detection: _feed);
        _coordinator.Start();
        _game.SetRunning(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _host.Dispose();
    }

    private ArtworkSettings Artwork => _coordinator.Snapshot.Artwork ?? throw new InvalidOperationException("The coordinator published no artwork.");

    private static PackEntry Entry(string id, string pack, string key, string marker, LicensedInfo? licensed = null)
    {
        var content = System.Text.Encoding.UTF8.GetBytes("image-" + marker);
        return new PackEntry(
            id, pack, Enum.Parse<PackEntryKind>(key.Split(':')[0], ignoreCase: true), key, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
            content.Length, 1024, 1024, PackImageFormat.Png, licensed);
    }

    private static SyncedPacks Synced(PackInfo[] packs, PackEntry[] entries, HashSet<string>? withdrawn = null) =>
        new(new PackIndex(packs, entries), new AllCached(), e => Cdn + e.CacheFileName, withdrawn, null);

    private static PackInfo Pack(string id, bool behaviour = false, bool fallback = false) =>
        new(id, id, 1, "DBD Presence", "Fan content shown as a courtesy.", null, fallback, behaviour);

    private void PickKiller()
    {
        _host.Settings.Update(s => s with { Packs = new PackSettings { KillerPack = "official" } });
        _feed.SetRole(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero), Role.Killer); // D30: the role comes from (scripted) detection
    }

    [Fact]
    public void BehaviourFlaggedPack_SendsItsHttpsUrl_AndLabelsTheHoverText()
    {
        var mark = Entry("official-app-mark", "official", "app:mark", "mark");
        var killer = Entry("official-role-killer", "official", "role:killer", "killer");
        _packs.Library.SetSynced(Synced([Pack("official", behaviour: true, fallback: true)], [mark, killer]));
        PickKiller();

        var activity = _sink.Last!;

        Assert.StartsWith(Cdn, activity.LargeImage, StringComparison.Ordinal);
        Assert.Contains(PresenceComposer.BehaviourLabel, activity.LargeText, StringComparison.Ordinal);
        Assert.True(Artwork.IsBehaviourArtwork);
    }

    [Fact]
    public void APackReference_WinsOverTheShippedDefaultKeys()
    {
        var mark = Entry("official-app-mark", "official", "app:mark", "mark");
        var killer = Entry("official-role-killer", "official", "role:killer", "killer");
        _packs.Library.SetSynced(Synced([Pack("official", fallback: true)], [mark, killer]));
        PickKiller();

        var artwork = Artwork;

        Assert.StartsWith(Cdn, artwork.RoleKiller, StringComparison.Ordinal);
        Assert.StartsWith(Cdn, artwork.AppLogo, StringComparison.Ordinal);
        Assert.Equal("role_survivor", artwork.RoleSurvivor);
    }

    [Fact]
    public void AnOrdinaryPack_IsSentWithoutTheBehaviourLabel()
    {
        var mark = Entry("official-app-mark", "official", "app:mark", "mark");
        var killer = Entry("official-role-killer", "official", "role:killer", "killer");
        _packs.Library.SetSynced(Synced([Pack("official", behaviour: false, fallback: true)], [mark, killer]));
        PickKiller();

        var activity = _sink.Last!;

        Assert.StartsWith(Cdn, activity.LargeImage, StringComparison.Ordinal);
        Assert.DoesNotContain("Behaviour", activity.LargeText ?? string.Empty, StringComparison.Ordinal);
        Assert.False(Artwork.IsBehaviourArtwork);
    }

    [Fact]
    public void TheLabelIsDecidedByTheSignedFlagOnly_NotByNamesOrKeys()
    {
        // A pack named like Behaviour art, with Behaviour-sounding keys, but not flagged: no label. The reverse (a flagged pack with a bland name) is labelled.
        var named = Pack("behaviour-interactive-official-art") with { Name = "Behaviour Interactive Art", Credit = "© Behaviour Interactive" };
        var mark = Entry("a-app-mark", "behaviour-interactive-official-art", "app:mark", "m");
        _packs.Library.SetSynced(Synced([named with { Fallback = true }], [mark]));

        Assert.DoesNotContain("Behaviour", _sink.Last!.LargeText ?? string.Empty, StringComparison.Ordinal);

        _packs.Library.SetSynced(Synced([Pack("plain", behaviour: true, fallback: true)], [Entry("plain-app-mark", "plain", "app:mark", "p")]));

        Assert.Contains(PresenceComposer.BehaviourLabel, _sink.Last!.LargeText, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalImportsAndOverrides_NeverReachDiscord_EvenWhenAnOfficialPackExists()
    {
        var killer = Entry("official-role-killer", "official", "role:killer", "killer");
        _packs.Library.SetSynced(Synced([Pack("official")], [killer]));
        _packs.Storage.Files[Path.Combine(PackFixtures.Root, "overrides", new string('c', 64) + ".png")] = PackFixtures.Png("o");
        _host.Settings.Update(s => s with
        {
            Packs = new PackSettings { KillerPack = "my-pack", Overrides = new Dictionary<string, string> { ["role:killer"] = new string('c', 64) + ".png" } },
        });
        _feed.SetRole(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero), Role.Killer);

        var artwork = Artwork;

        Assert.DoesNotContain(Cdn, artwork.RoleKiller, StringComparison.Ordinal);
        Assert.Equal("role_killer", artwork.RoleKiller);
    }

    [Fact]
    public void WhenThePacksChange_TheActivityFollows_AndAWithdrawnImageStopsBeingSent()
    {
        var mark = Entry("official-app-mark", "official", "app:mark", "mark");
        var killer = Entry("official-role-killer", "official", "role:killer", "killer", new LicensedInfo("Holder"));
        var packs = new[] { Pack("official", behaviour: true, fallback: true) };
        _packs.Library.SetSynced(Synced(packs, [mark, killer]));
        PickKiller();
        Assert.NotNull(_sink.Last!.LargeImage);

        _packs.Library.SetSynced(Synced(packs, [mark, killer], withdrawn: [mark.Id, killer.Id]));

        Assert.Equal("role_killer", _sink.Last!.LargeImage);
        Assert.False(Artwork.IsBehaviourArtwork);

        _packs.Library.SetSynced(null);

        Assert.Equal("role_killer", _sink.Last!.LargeImage);
    }

    private sealed class AllCached : IPackContentCache
    {
        public bool Contains(PackEntry entry) => true;
    }
}
