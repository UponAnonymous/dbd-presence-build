// Covers: FR-AUTO-02 (the nine reserved app:status-* / app:result-* slots: owner packs only, order active pack, default pack, fallback pack, never an
// override or an imported pack), FR-RPC-12 (the signed behaviourArt flag), FR-PACK-04 (withdrawn and uncached entries are skipped),
// FR-PACK-01 (an index that carries the slots still parses: they use the existing app kind).
using System.Text.Json.Nodes;
using DbdPresence.Core.Composition;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;

namespace DbdPresence.Packs.Tests;

public sealed class AutomaticPictureLibraryTests
{
    private const string Root = "/data/packs";

    private readonly InMemoryLocalStorage _storage = new();
    private readonly PackLibrary _library;

    public AutomaticPictureLibraryTests() => _library = new PackLibrary(_storage, Root, new FakeImageLoader(_storage), TestRoster.Embedded);

    private static SyncedPacks Synced(PackInfo[] packs, PackEntry[] entries, HashSet<string>? uncached = null, HashSet<string>? withdrawn = null) =>
        new(new PackIndex(packs, entries), new Cached(uncached ?? []), e => $"https://cdn.example.org/packs/{e.CacheFileName}", withdrawn, null);

    private static PackEntry Hooked(string pack, string marker = "h") => PackData.Entry($"{pack}-app-status-hooked", pack, "app:status-hooked", marker);

    private string? Resolve(string slug, params string?[] packs) => _library.ResolveAutomaticPicture(slug, packs).Reference;

    [Fact]
    public void TheActivePack_WinsOverTheDefaultPack_WhichWinsOverTheFallbackPack()
    {
        var active = Hooked("mine", "a");
        var owner = Hooked("owner", "o");
        var backup = Hooked("backup", "b");
        _library.SetSynced(Synced([PackData.Pack("mine"), PackData.Pack("owner") with { Default = true }, PackData.Pack("backup") with { Fallback = true }], [active, owner, backup]));

        Assert.EndsWith(active.CacheFileName, Resolve(AutomaticIcons.StatusHooked, "mine", "owner"), StringComparison.Ordinal);
        Assert.EndsWith(owner.CacheFileName, Resolve(AutomaticIcons.StatusHooked, "unknown", "owner"), StringComparison.Ordinal);
        Assert.EndsWith(backup.CacheFileName, Resolve(AutomaticIcons.StatusHooked, "unknown", null), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoPackThatHasIt_TheAnswerIsNoPicture_NothingIsBundled()
    {
        _library.SetSynced(Synced([PackData.Pack("owner")], [PackData.Entry("owner-role-killer", "owner", "role:killer", "k")]));

        Assert.Null(Resolve(AutomaticIcons.StatusHooked, "owner"));
        Assert.Null(new PackLibrary(_storage, Root, new FakeImageLoader(_storage), TestRoster.Embedded).ResolveAutomaticPicture(AutomaticIcons.StatusHooked, ["owner"]).Reference);
    }

    [Fact]
    public void OnlyTheNineReservedSlugs_ResolveThroughThisPath()
    {
        var other = PackData.Entry("owner-app-mark", "owner", "app:mark", "m");
        _library.SetSynced(Synced([PackData.Pack("owner") with { Fallback = true }], [other, Hooked("owner")]));

        Assert.Null(Resolve("mark", "owner"));
        Assert.Null(Resolve("status-unknown", "owner"));
        Assert.NotNull(Resolve(AutomaticIcons.StatusHooked, "owner"));
    }

    [Fact]
    public void AnImportedPack_NeverReachesDiscord_ForTheseSlugs()
    {
        // The same key exists only in a pack the user imported on this PC; Discord must get nothing for it.
        _library.SetSynced(Synced([PackData.Pack("owner")], [PackData.Entry("owner-role-killer", "owner", "role:killer", "k")]));

        Assert.Null(Resolve(AutomaticIcons.StatusHooked, "my-imported-pack"));
    }

    [Fact]
    public void WithdrawnAndUncachedEntries_AreSkipped_SoTheNextPackIsUsed()
    {
        var withdrawn = Hooked("mine", "w");
        var uncached = PackData.Entry("owner-app-status-dying", "owner", "app:status-dying", "u");
        var backup = Hooked("backup", "b");
        var backupDying = PackData.Entry("backup-app-status-dying", "backup", "app:status-dying", "bd");
        _library.SetSynced(Synced(
            [PackData.Pack("mine"), PackData.Pack("owner"), PackData.Pack("backup") with { Fallback = true }],
            [withdrawn, uncached, backup, backupDying],
            uncached: [uncached.Sha256],
            withdrawn: [withdrawn.Id]));

        Assert.EndsWith(backup.CacheFileName, Resolve(AutomaticIcons.StatusHooked, "mine"), StringComparison.Ordinal);
        Assert.EndsWith(backupDying.CacheFileName, Resolve(AutomaticIcons.StatusDying, "owner"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBehaviourFlag_ComesFromTheSignedIndexOnly()
    {
        _library.SetSynced(Synced([PackData.Pack("flagged") with { BehaviourArt = true }, PackData.Pack("plain")], [Hooked("flagged"), PackData.Entry("plain-app-status-dying", "plain", "app:status-dying", "p")]));

        Assert.True(_library.ResolveAutomaticPicture(AutomaticIcons.StatusHooked, ["flagged"]).BehaviourArt);
        Assert.False(_library.ResolveAutomaticPicture(AutomaticIcons.StatusDying, ["plain"]).BehaviourArt);
    }

    [Fact]
    public void AnIndexCarryingTheNineSlots_StillParses_BecauseTheyUseTheExistingAppKind()
    {
        var sha = new string('a', 64);
        var entries = new JsonArray();
        foreach (var slug in AutomaticIcons.AllSlugs)
        {
            entries.Add(new JsonObject
            {
                ["id"] = "p1-app-" + slug,
                ["pack"] = "p1",
                ["kind"] = "app",
                ["key"] = "app:" + slug,
                ["sha256"] = sha,
                ["size"] = 1000,
                ["width"] = 1024,
                ["height"] = 1024,
                ["format"] = "png",
                ["licensed"] = null,
            });
        }

        var doc = new JsonObject
        {
            ["packs"] = new JsonArray(new JsonObject { ["id"] = "p1", ["name"] = "Pack", ["version"] = 1, ["author"] = "Owner", ["licenceNote"] = "Note" }),
            ["entries"] = entries,
        };

        var result = PackIndexParser.Parse(doc.ToJsonString());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(9, result.Index!.Entries.Count(e => e.Kind == PackEntryKind.App));
    }

    private sealed class Cached(HashSet<string> uncachedHashes) : IPackContentCache
    {
        public bool Contains(PackEntry entry) => !uncachedHashes.Contains(entry.Sha256);
    }
}
