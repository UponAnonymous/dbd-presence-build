// Covers: FR-ONL-07 / FR-PACK-03 (synced official packs feed the library as cards and as Discord https URLs), FR-PACK-06 (local images stay
// preview-only), FR-RPC-12 (the signed behaviourArt flag decides the Behaviour label, never a guess), D16 (the owner fallback pack).
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Resolution;

namespace DbdPresence.Packs.Tests;

public sealed class SyncedPackLibraryTests
{
    private const string Root = "/data/packs";
    private static readonly IReadOnlyDictionary<string, string> s_noOverrides = new Dictionary<string, string>();

    private readonly InMemoryLocalStorage _storage = new();
    private readonly PackLibrary _library;

    public SyncedPackLibraryTests() => _library = new PackLibrary(_storage, Root, new FakeImageLoader(_storage), TestRoster.Embedded);

    private static ImageSelection Pick(string? role = "killer", string? character = null) => new(role, character, null, false);

    private static SyncedPacks Synced(
        PackInfo[] packs, PackEntry[] entries, HashSet<string>? uncached = null, HashSet<string>? withdrawn = null, Func<PackEntry, string?>? localPath = null) =>
        new(
            new PackIndex(packs, entries),
            new Cached(uncached ?? []),
            e => $"https://cdn.example.org/packs/{e.CacheFileName}",
            withdrawn,
            localPath ?? (e => $"/cache/{e.CacheFileName}"));

    private static PackEntry Killer(string pack = "official", string marker = "k") => PackData.Entry($"{pack}-role-killer", pack, "role:killer", marker);

    [Fact]
    public void Cards_ListSyncedPacksFirst_WithOnlyTheirCachedAndNotWithdrawnImages()
    {
        var kept = Killer();
        var missing = PackData.Entry("official-role-survivor", "official", "role:survivor", "s");
        var withdrawn = PackData.Entry("official-char-x", "official", "character:x", "x", new LicensedInfo("Holder"));
        var empty = PackData.Entry("empty-role-killer", "empty", "role:killer", "e");
        _library.SetSynced(Synced(
            [PackData.Pack("official"), PackData.Pack("empty")],
            [kept, missing, withdrawn, empty],
            uncached: [missing.Sha256, empty.Sha256],
            withdrawn: [withdrawn.Id]));

        var cards = _library.Cards();

        var card = Assert.Single(cards);
        Assert.Equal(PackOrigin.Synced, card.Origin);
        Assert.Equal("official", card.Info.Id);
        Assert.Equal([kept.Id], card.Entries.Select(e => e.Id));
    }

    [Fact]
    public void SyncedCardTiles_UseTheLocalThumbnail_AndNoneWhenThereIsNone()
    {
        var png = Killer();
        var jpeg = PackData.Entry("official-map-hawkins", "official", "map:hawkins", "m", format: PackImageFormat.Jpeg);
        _library.SetSynced(Synced([PackData.Pack("official")], [png, jpeg], localPath: e => e.Format == PackImageFormat.Png ? $"/cache/{e.CacheFileName}" : null));
        var card = _library.Cards().Single();

        Assert.Equal($"/cache/{png.CacheFileName}", _library.LocationOf(card, png)!.Path);
        Assert.Null(_library.LocationOf(card, jpeg));
    }

    [Fact]
    public void Preview_ShowsTheSyncedImage_ForTheChosenPack_AndTheFallbackPackOtherwise()
    {
        var killer = Killer();
        var fallback = PackData.Entry("backup-role-survivor", "backup", "role:survivor", "b");
        _library.SetSynced(Synced([PackData.Pack("official"), PackData.Pack("backup") with { Fallback = true }], [killer, fallback]));
        var choices = PackChoices.None with { Killer = "official" };

        var killerPreview = _library.ResolvePreview(Pick("killer"), choices, s_noOverrides).Role;
        var survivorPreview = _library.ResolvePreview(Pick("survivor"), choices, s_noOverrides).Role;
        var unchosen = _library.ResolvePreview(Pick("killer"), PackChoices.None, s_noOverrides).Role;

        Assert.Equal(ResolvedImageSource.ActivePack, killerPreview.Source);
        Assert.Equal($"/cache/{killer.CacheFileName}", _library.LocationOf(killerPreview)!.Path);
        Assert.Equal(ResolvedImageSource.FallbackPack, survivorPreview.Source);
        Assert.Equal($"/cache/{fallback.CacheFileName}", _library.LocationOf(survivorPreview)!.Path);
        Assert.Equal(ResolvedImageSource.None, unchosen.Source);
    }

    [Fact]
    public void Discord_GetsTheHttpsCdnUrl_OfASyncedImage_WithoutPassingSyncedPacksExplicitly()
    {
        var killer = Killer();
        _library.SetSynced(Synced([PackData.Pack("official")], [killer]));

        var discord = _library.ResolveDiscord(Pick("killer"), PackChoices.None with { Killer = "official" }, s_noOverrides);

        Assert.Equal($"https://cdn.example.org/packs/{killer.CacheFileName}", discord.Role.Reference);
        Assert.StartsWith("https://", discord.Role.Reference, StringComparison.Ordinal);
        Assert.False(discord.Role.LocalOnly);
        Assert.False(discord.Role.BehaviourArt);
    }

    [Fact]
    public async Task Discord_NeverGetsLocalImages_EvenWhileSyncedPacksExist()
    {
        var killer = Killer();
        _library.SetSynced(Synced([PackData.Pack("official")], [killer]));
        _storage.Files["/in/a.zip"] = ZipBuilder.Build(("art.png", ZipBuilder.Png("one")));
        var analysis = await _library.AnalyseImportAsync("/in/a.zip", CancellationToken.None);
        var saved = _library.SaveImport("Mine", "Me", "a.zip", [new ConfirmedMapping(analysis.Items[0], "role:killer")]);
        var choices = PackChoices.None with { Killer = saved.Pack!.Info.Id };

        var preview = _library.ResolvePreview(Pick("killer"), choices, s_noOverrides).Role;
        var discord = _library.ResolveDiscord(Pick("killer"), choices, s_noOverrides).Role;

        Assert.Equal(ResolvedImageSource.ActivePack, preview.Source);
        Assert.True(discord.LocalOnly);
        Assert.Null(discord.Reference); // the chosen import has no URL; the unchosen synced pack is not substituted
    }

    [Fact]
    public void BehaviourArt_IsReportedOnlyForImagesOfAPackTheIndexFlags()
    {
        var behaviour = Killer("behaviour");
        var plain = PackData.Entry("plain-role-survivor", "plain", "role:survivor", "p");
        _library.SetSynced(Synced([PackData.Pack("behaviour") with { BehaviourArt = true }, PackData.Pack("plain")], [behaviour, plain]));
        var choices = PackChoices.None with { Killer = "behaviour", Survivor = "plain" };

        var killer = _library.ResolveDiscord(Pick("killer"), choices, s_noOverrides).Role;
        var survivor = _library.ResolveDiscord(Pick("survivor"), choices, s_noOverrides).Role;
        var nothing = _library.ResolveDiscord(Pick("killer"), PackChoices.None, s_noOverrides).Role;

        Assert.True(killer.BehaviourArt);
        Assert.NotNull(killer.Reference);
        Assert.False(survivor.BehaviourArt);
        Assert.False(nothing.BehaviourArt); // no image, so nothing to label
        Assert.Null(nothing.Reference);
    }

    [Fact]
    public void BehaviourArt_AppliesToTheFallbackPackAndToTheAppMark()
    {
        var mark = PackData.Entry("backup-app-mark", "backup", "app:mark", "m");
        var killer = PackData.Entry("backup-role-killer", "backup", "role:killer", "k");
        _library.SetSynced(Synced([PackData.Pack("backup") with { Fallback = true, BehaviourArt = true }], [mark, killer]));

        var discord = _library.ResolveDiscord(Pick("killer"), PackChoices.None, s_noOverrides);

        Assert.True(discord.Role.BehaviourArt);
        Assert.True(discord.AppMark.BehaviourArt);
        Assert.NotNull(discord.AppMark.Reference);
    }

    [Fact]
    public void WithdrawnLicensedImage_IsNotSent_AndFallsBackToTheFallbackPack()
    {
        var licensed = PackData.Entry("official-char-ghostface", "official", "character:ghostface", "g", new LicensedInfo("Holder"));
        var backup = PackData.Entry("backup-char-ghostface", "backup", "character:ghostface", "b");
        var packs = new[] { PackData.Pack("official"), PackData.Pack("backup") with { Fallback = true } };
        var choices = PackChoices.None with { Characters = "official" };

        _library.SetSynced(Synced(packs, [licensed, backup]));
        var before = _library.ResolveDiscord(Pick("killer", "ghostface"), choices, s_noOverrides).Character.Reference;

        _library.SetSynced(Synced(packs, [licensed, backup], withdrawn: [licensed.Id]));
        var after = _library.ResolveDiscord(Pick("killer", "ghostface"), choices, s_noOverrides).Character.Reference;

        Assert.Contains(licensed.Sha256, before, StringComparison.Ordinal);
        Assert.Contains(backup.Sha256, after, StringComparison.Ordinal);
    }

    [Fact]
    public void SetSynced_RaisesTheChangedEventOnlyWhenThePacksChange()
    {
        var synced = Synced([PackData.Pack("official")], [Killer()]);
        var raised = 0;
        _library.SyncedChanged += (_, _) => raised++;

        _library.SetSynced(synced);
        _library.SetSynced(synced);
        _library.SetSynced(null);

        Assert.Equal(2, raised);
        Assert.Null(_library.Synced);
        Assert.Empty(_library.Cards());
    }

    [Fact]
    public async Task ImportedPackIds_NeverCollideWithSyncedPackIds()
    {
        _library.SetSynced(Synced([PackData.Pack("mine")], [Killer("mine")]));
        _storage.Files["/in/a.zip"] = ZipBuilder.Build(("art.png", ZipBuilder.Png("one")));
        var analysis = await _library.AnalyseImportAsync("/in/a.zip", CancellationToken.None);

        var saved = _library.SaveImport("Mine", "Me", "a.zip", [new ConfirmedMapping(analysis.Items[0], "role:killer")]);

        Assert.NotEqual("mine", saved.Pack!.Info.Id);
    }

    private sealed class Cached(HashSet<string> uncachedHashes) : IPackContentCache
    {
        public bool Contains(PackEntry entry) => !uncachedHashes.Contains(entry.Sha256);
    }
}
