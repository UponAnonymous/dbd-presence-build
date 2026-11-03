// Covers: FR-PACK-02 (D16: owner fallback pack, else no image), FR-PACK-04 (licensed withdrawal falls back to the fallback
// pack, else none), FR-PACK-06 (role, manual character, map only when Confirmed).
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Resolution;

namespace DbdPresence.Packs.Tests;

public sealed class PackImageResolverTests
{
    private static readonly PackInfo[] s_syncedPacks =
        [PackData.Pack("original"), PackData.Pack("custom"), PackData.Pack("backup") with { Fallback = true }];

    private static readonly PackEntry s_backupKiller = PackData.Entry("b-killer", "backup", "role:killer", "b-killer");
    private static readonly PackEntry s_backupSurvivor = PackData.Entry("b-survivor", "backup", "role:survivor", "b-survivor");
    private static readonly PackEntry s_backupGhostface = PackData.Entry("b-ghostface", "backup", "character:ghostface", "b-ghostface");
    private static readonly PackEntry s_backupMapGeneric = PackData.Entry("b-map-generic", "backup", "map:generic", "b-map-generic");

    private static readonly PackEntry s_originalKiller = PackData.Entry("o-killer", "original", "role:killer", "o-killer");
    private static readonly PackEntry s_originalTrapper = PackData.Entry("o-trapper", "original", "character:the-trapper", "o-trapper");
    private static readonly PackEntry s_originalGhostface = PackData.Entry("o-ghostface", "original", "character:ghostface", "o-ghostface", new LicensedInfo("Holder"));
    private static readonly PackEntry s_originalHawkins = PackData.Entry("o-hawkins", "original", "map:hawkins", "o-hawkins", new LicensedInfo("Holder"));
    private static readonly PackEntry s_customKiller = PackData.Entry("x-killer", "custom", "role:killer", "x-killer");

    private static readonly PackIndex s_synced = PackData.Index(s_syncedPacks, s_originalKiller, s_originalTrapper, s_originalGhostface, s_originalHawkins, s_customKiller, s_backupKiller, s_backupSurvivor, s_backupGhostface, s_backupMapGeneric);

    private static readonly PackEntry[] s_allEntries =
    [
        s_originalKiller, s_originalTrapper, s_originalGhostface, s_originalHawkins, s_customKiller,
        s_backupKiller, s_backupSurvivor, s_backupGhostface, s_backupMapGeneric,
    ];

    private static ResolverContext Context(
        IEnumerable<PackEntry>? cached = null,
        string[]? active = null,
        Dictionary<string, string>? overrides = null,
        PackIndex? synced = null,
        string[]? withdrawn = null) =>
        new(
            synced ?? s_synced,
            active ?? ["original"],
            overrides ?? [],
            new FakeCache([.. (cached ?? s_allEntries).Select(e => e.Sha256)]),
            new HashSet<string>(withdrawn ?? [], StringComparer.Ordinal));

    [Fact]
    public void ActivePackEntry_IsUsedWhenCached()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context());

        Assert.Equal(ResolvedImageSource.ActivePack, image.Source);
        Assert.Equal("o-killer", image.Entry!.Id);
    }

    [Fact]
    public void UserOverride_BeatsEverything()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context(overrides: new() { ["role:killer"] = "/home/me/k.png" }));

        Assert.Equal(ResolvedImageSource.UserOverride, image.Source);
        Assert.Equal("/home/me/k.png", image.OverridePath);
        Assert.Null(image.Entry);
    }

    [Fact]
    public void BlankOverride_IsIgnored()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context(overrides: new() { ["role:killer"] = "  " }));

        Assert.Equal(ResolvedImageSource.ActivePack, image.Source);
    }

    [Fact]
    public void OverrideForOneKey_DoesNotAffectOthers()
    {
        var image = PackImageResolver.ResolveKey("character:the-trapper", Context(overrides: new() { ["role:killer"] = "/x.png" }));

        Assert.Equal(ResolvedImageSource.ActivePack, image.Source);
    }

    [Fact]
    public void EntryNotYetInTheCache_FallsBackToTheFallbackPack()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context(cached: [s_originalTrapper, s_backupKiller]));

        Assert.Equal(ResolvedImageSource.FallbackPack, image.Source);
        Assert.Equal("b-killer", image.Entry!.Id);
    }

    [Fact]
    public void ActivePackLacksTheKey_TheFallbackPackSuppliesIt()
    {
        var image = PackImageResolver.ResolveKey("role:survivor", Context());

        Assert.Equal(ResolvedImageSource.FallbackPack, image.Source);
        Assert.Equal("b-survivor", image.Entry!.Id);
    }

    [Fact]
    public void ActivePackEntry_BeatsTheFallbackPack()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context());

        Assert.Equal(ResolvedImageSource.ActivePack, image.Source);
        Assert.Equal("o-killer", image.Entry!.Id);
    }

    [Fact]
    public void UserOverride_BeatsTheFallbackPack()
    {
        var image = PackImageResolver.ResolveKey("role:survivor", Context(overrides: new() { ["role:survivor"] = "/x.png" }));

        Assert.Equal(ResolvedImageSource.UserOverride, image.Source);
    }

    [Fact]
    public void NothingResolves_TheResultIsNone()
    {
        var image = PackImageResolver.ResolveKey("character:the-trapper", Context(active: [], cached: [], synced: PackData.Index(s_syncedPacks)));

        Assert.Equal(ResolvedImageSource.None, image.Source);
        Assert.Null(image.Entry);
        Assert.Null(image.OverridePath);
        Assert.Equal("character:the-trapper", image.Key);
    }

    [Fact]
    public void PackWithoutTheFallbackFlag_IsNeverUsedAsFallback()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context(active: [], cached: [s_originalKiller, s_customKiller]));

        Assert.Equal(ResolvedImageSource.None, image.Source);
    }

    [Fact]
    public void FallbackPackEntryNotInTheCache_ResolvesToNone()
    {
        var image = PackImageResolver.ResolveKey("role:survivor", Context(cached: [s_originalKiller]));

        Assert.Equal(ResolvedImageSource.None, image.Source);
    }

    [Fact]
    public void WithdrawnLicensedEntry_FallsBackToTheFallbackPackEntryForTheSameKey()
    {
        var withdrawnIndex = PackData.Index(s_syncedPacks, s_originalKiller, s_originalTrapper, s_backupGhostface);

        var image = PackImageResolver.ResolveKey("character:ghostface", Context(synced: withdrawnIndex));

        Assert.Equal(ResolvedImageSource.FallbackPack, image.Source);
        Assert.Equal("b-ghostface", image.Entry!.Id);
    }

    [Fact]
    public void WithdrawnLicensedEntry_WithoutAFallbackEntry_ResolvesToNone()
    {
        var withdrawnIndex = PackData.Index(s_syncedPacks, s_originalKiller, s_originalTrapper);

        var image = PackImageResolver.ResolveKey("character:ghostface", Context(synced: withdrawnIndex));

        Assert.Equal(ResolvedImageSource.None, image.Source);
    }

    [Fact]
    public void WithdrawnSet_SkipsAStillCachedFile_WithoutTouchingTheRestOfThePack()
    {
        var context = Context(withdrawn: ["o-ghostface"]);

        var ghostface = PackImageResolver.ResolveKey("character:ghostface", context);
        Assert.Equal(ResolvedImageSource.FallbackPack, ghostface.Source);
        Assert.Equal("b-ghostface", ghostface.Entry!.Id);
        Assert.Equal(ResolvedImageSource.ActivePack, PackImageResolver.ResolveKey("character:the-trapper", context).Source);
        Assert.Equal(ResolvedImageSource.ActivePack, PackImageResolver.ResolveKey("role:killer", context).Source);
    }

    [Fact]
    public void WithdrawnSet_AlsoSkipsAFallbackPackEntry_SoTheResultIsNone()
    {
        var context = Context(withdrawn: ["o-ghostface", "b-ghostface"]);

        Assert.Equal(ResolvedImageSource.None, PackImageResolver.ResolveKey("character:ghostface", context).Source);
    }

    [Fact]
    public void BeforeTheFirstSync_NothingResolves()
    {
        var context = new ResolverContext(null, [], new Dictionary<string, string>(), new FakeCache([]), new HashSet<string>());

        Assert.Equal(ResolvedImageSource.None, PackImageResolver.ResolveKey("role:survivor", context).Source);
        Assert.Equal(ResolvedImageSource.None, PackImageResolver.ResolveKey("character:unknown-one", context).Source);
    }

    [Fact]
    public void InactivePack_IsNotUsed()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context(active: ["custom"]));

        Assert.Equal("x-killer", image.Entry!.Id);
        Assert.Equal(ResolvedImageSource.ActivePack, image.Source);
        Assert.Equal(ResolvedImageSource.FallbackPack, PackImageResolver.ResolveKey("role:killer", Context(active: [])).Source);
    }

    [Fact]
    public void ActivePackOrder_DecidesPriority()
    {
        Assert.Equal("o-killer", PackImageResolver.ResolveKey("role:killer", Context(active: ["original", "custom"])).Entry!.Id);
        Assert.Equal("x-killer", PackImageResolver.ResolveKey("role:killer", Context(active: ["custom", "original"])).Entry!.Id);
    }

    [Fact]
    public void HigherPriorityPackMissingFromCache_FallsThroughToTheNextActivePack()
    {
        var image = PackImageResolver.ResolveKey("role:killer", Context(active: ["custom", "original"], cached: [s_originalKiller]));

        Assert.Equal("o-killer", image.Entry!.Id);
    }

    [Fact]
    public void NotAKey_ResolvesToNone()
    {
        Assert.Equal(ResolvedImageSource.None, PackImageResolver.ResolveKey("not a key", Context()).Source);
    }

    [Fact]
    public void Map_IsNoneUnlessConfirmed_EvenWithAnOverrideOrCachedImage()
    {
        var context = Context(overrides: new() { ["map:hawkins"] = "/m.png" });

        var unconfirmed = PackImageResolver.Resolve(new ImageSelection("killer", "the-trapper", "hawkins", MapConfirmed: false), context);
        var confirmed = PackImageResolver.Resolve(new ImageSelection("killer", "the-trapper", "hawkins", MapConfirmed: true), context);

        Assert.Equal(ResolvedImageSource.None, unconfirmed.Map.Source);
        Assert.Equal(ResolvedImageSource.UserOverride, confirmed.Map.Source);
    }

    [Fact]
    public void ConfirmedMap_UsesThePackAndOtherwiseNone()
    {
        var context = Context();

        Assert.Equal("o-hawkins", PackImageResolver.Resolve(new ImageSelection(null, null, "hawkins", true), context).Map.Entry!.Id);
        Assert.Equal(ResolvedImageSource.None, PackImageResolver.Resolve(new ImageSelection(null, null, "somewhere-new", true), context).Map.Source);
    }

    [Fact]
    public void ConfirmedButUnknownMap_IsNoneWhenNothingIsSelected()
    {
        Assert.Equal(ResolvedImageSource.None, PackImageResolver.Resolve(new ImageSelection(null, null, null, true), Context()).Map.Source);
    }

    [Fact]
    public void Selection_ResolvesRoleAndCharacterIndependently()
    {
        var result = PackImageResolver.Resolve(new ImageSelection("survivor", "ghostface", null, false), Context());

        Assert.Equal(ResolvedImageSource.FallbackPack, result.Role.Source);
        Assert.Equal("b-survivor", result.Role.Entry!.Id);
        Assert.Equal("o-ghostface", result.Character.Entry!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bad Slug")]
    public void MissingOrInvalidSelections_ResolveToNone(string? slug)
    {
        var result = PackImageResolver.Resolve(new ImageSelection(slug, slug, slug, true), Context());

        Assert.Equal(ResolvedImageSource.None, result.Role.Source);
        Assert.Equal(ResolvedImageSource.None, result.Character.Source);
        Assert.Equal(ResolvedImageSource.None, result.Map.Source);
    }

    [Fact]
    public void EveryResolvedImage_ReportsTheRequestedKey()
    {
        var result = PackImageResolver.Resolve(new ImageSelection("killer", "nobody-yet", "hawkins", true), Context());

        Assert.Equal("role:killer", result.Role.Key);
        Assert.Equal("character:nobody-yet", result.Character.Key);
        Assert.Equal("map:hawkins", result.Map.Key);
    }

    private sealed class FakeCache(HashSet<string> hashes) : IPackContentCache
    {
        public bool Contains(PackEntry entry) => hashes.Contains(entry.Sha256);
    }
}
