// Covers: FR-PACK-13 (context from the pack library: withdrawn and unsynced entries left out, imported packs preview-only, default pack by id),
// FR-PACK-16 (imported packs become own pictures: up to 3, the characters pack sets the picture in use, generic keys named, files copied before the
// packs may go, repeatable), D29 (own picture files are found for the Discord-address preview from the pictures folder).
using DbdPresence.Core.Game;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class PictureLibraryIntegrationTests
{
    private const string Root = "/data/packs";
    private readonly InMemoryLocalStorage _storage = new();
    private readonly PackLibrary _library;
    private readonly PictureStore _store;

    public PictureLibraryIntegrationTests()
    {
        _library = new PackLibrary(_storage, Root, new FakeImageLoader(_storage), TestRoster.Embedded);
        _store = new PictureStore(_storage, Root, new FakeFitter(edge => FakePng.Make(edge, edge, 2000)), new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)));
    }

    private async Task<ImportedPack> ImportPack(string name, params (string File, string Key, string Marker)[] files)
    {
        _storage.Files["/in/pack.zip"] = ZipBuilder.Build([.. files.Select(f => (f.File, ZipBuilder.Png(f.Marker)))]);
        var analysis = await _library.AnalyseImportAsync("/in/pack.zip", CancellationToken.None);
        var mappings = analysis.Items.Select((item, i) => new ConfirmedMapping(item, files[i].Key)).ToArray();
        var saved = _library.SaveImport(name, "Creator", "pack.zip", mappings);
        Assert.NotNull(saved.Pack);
        return saved.Pack;
    }

    private static SyncedPacks Synced(PackInfo[] packs, PackEntry[] entries, HashSet<string>? uncached = null, HashSet<string>? withdrawn = null) =>
        new(new PackIndex(packs, entries), new Cached(uncached ?? []), e => $"https://cdn.example.org/{e.CacheFileName}", withdrawn, e => $"/cache/{e.CacheFileName}");

    [Fact]
    public void Build_ListsUsableSyncedEntries_WithAddresses_AndTheDefaultPackById()
    {
        var onryo = PackData.Entry("def-onryo", "def", "character:the-onryo", "o");
        var gone = PackData.Entry("def-nurse", "def", "character:the-nurse", "n", new LicensedInfo("Holder"));
        var unsynced = PackData.Entry("def-wraith", "def", "character:the-wraith", "w");
        _library.SetSynced(Synced(
            [PackData.Pack("def") with { BehaviourArt = true }, PackData.Pack("back") with { Fallback = true }],
            [onryo, gone, unsynced, PackData.Entry("back-role-killer", "back", "role:killer", "k")],
            uncached: [unsynced.Sha256],
            withdrawn: [gone.Id]));

        var context = PictureContextBuilder.Build(_library, _store, null, "def", new Dictionary<string, string> { [onryo.Id] = new string('a', 64) }, _ => new PictureOnline(PictureOnlineStatus.Saving));

        Assert.Equal(["back", "def"], context.Packs.Select(p => p.Id).Order(StringComparer.Ordinal));
        Assert.True(context.Packs.Single(p => p.Id == "def").IsDefault);
        Assert.True(context.Packs.Single(p => p.Id == "def").BehaviourArt);
        Assert.True(context.Packs.Single(p => p.Id == "back").IsFallback);
        Assert.DoesNotContain(context.Pieces, p => p.EntryId is "def-nurse" or "def-wraith");
        var piece = context.Pieces.Single(p => p.EntryId == "def-onryo");
        Assert.Equal($"https://cdn.example.org/{onryo.CacheFileName}", piece.Url);
        Assert.Equal(new string('a', 64), piece.SourceSha256);
        var resolution = PictureResolver.Resolve("character:the-onryo", Role.Killer, null, context);
        Assert.Equal(DiscordSource.DefaultPack, resolution.Discord.Source);
        Assert.True(resolution.Discord.BehaviourArt);
    }

    [Fact]
    public async Task Build_ImportedPackIsPreviewOnly_AndNoDefaultWithoutAnId()
    {
        var pack = await ImportPack("Fan", ("K27_TheOnryo.png", "character:the-onryo", "one"));
        _library.SetSynced(Synced([PackData.Pack("def")], [PackData.Entry("def-onryo", "def", "character:the-onryo", "o")]));

        var context = PictureContextBuilder.Build(_library, _store, pack.Info.Id, null, null, _ => new PictureOnline(PictureOnlineStatus.Saving));
        var result = PictureResolver.Resolve("character:the-onryo", Role.Killer, null, context);

        Assert.DoesNotContain(context.Packs, p => p.IsDefault);
        Assert.True(context.Packs.Single(p => p.Id == pack.Info.Id).IsImported);
        Assert.Null(context.Pieces.Single(p => p.PackId == pack.Info.Id).Url);
        Assert.Equal(PictureSource.CharactersPack, result.Preview.Source);
        Assert.True(result.Preview.LocalOnly);
        Assert.Null(result.Discord.Reference);
    }

    [Fact]
    public async Task OwnPictureFiles_AreFoundByTheAddressPreview_FromThePicturesFolder()
    {
        var item = (await _store.AddFileAsync("/in/me.png", 512, CancellationToken.None)).Item!;
        _library.SetUploads(new Dictionary<string, string> { [item.Sha256] = "https://cdn.example.org/u/0123456789abcdef0123456789abcdef.png" }, "cdn.example.org");

        var match = _library.MatchReference("https://cdn.example.org/u/0123456789abcdef0123456789abcdef.png");

        Assert.Equal($"{Root}/pictures/{item.File}", match!.Location!.Path.Replace('\\', '/'));
    }

    [Fact]
    public async Task Migrate_ImportedPackPictures_BecomeOwnPictures_AndTheChosenPackIsInUse()
    {
        var pack = await ImportPack(
            "Fan",
            ("a.png", "character:the-onryo", "one"),
            ("b.png", "character:the-trapper", "two"),
            ("c.png", "role:killer", "three"),
            ("d.png", "character:generic-survivor", "four"));

        var result = ImportedPackMigration.Migrate([pack], new Dictionary<string, CharacterPictures>(), pack.Info.Id, _store);

        Assert.True(result.AllCopied);
        Assert.Equal(2, result.Migrated);
        Assert.Equal(0, result.OverLimit);
        Assert.Equal(["character:generic-survivor", "role:killer"], result.GenericKeys);
        var onryo = result.Pictures["character:the-onryo"];
        var item = Assert.Single(onryo.Items);
        Assert.Equal(PictureOrigin.Import, item.Origin);
        Assert.Equal(PictureUse.Personal(item.Sha256), onryo.Use);
        Assert.NotNull(_store.PathOf(item));
        Assert.True(PictureSlots.IsValid(item));
    }

    [Fact]
    public async Task Migrate_WhenPackNotChosen_KeepsTheDefaultPortraitInUse()
    {
        var pack = await ImportPack("Fan", ("a.png", "character:the-onryo", "one"));

        var result = ImportedPackMigration.Migrate([pack], new Dictionary<string, CharacterPictures>(), null, _store);

        Assert.Null(result.Pictures["character:the-onryo"].Use);
        Assert.Single(result.Pictures["character:the-onryo"].Items);
    }

    [Fact]
    public async Task Migrate_PicturesBeyondThree_AreCounted_NotAdded()
    {
        var first = await ImportPack("One", ("a.png", "character:the-onryo", "p1"));
        _storage.Files.Remove("/in/pack.zip");
        var second = await ImportPack("Two", ("a.png", "character:the-onryo", "p2"));
        var third = await ImportPack("Three", ("a.png", "character:the-onryo", "p3"));
        var fourth = await ImportPack("Four", ("a.png", "character:the-onryo", "p4"));

        var result = ImportedPackMigration.Migrate([first, second, third, fourth], new Dictionary<string, CharacterPictures>(), null, _store);

        Assert.Equal(3, result.Pictures["character:the-onryo"].Items.Count);
        Assert.Equal(3, result.Migrated);
        Assert.Equal(1, result.OverLimit);
    }

    [Fact]
    public async Task Migrate_IsRepeatable_AndMissingFilesStopTheCleanUp()
    {
        var pack = await ImportPack("Fan", ("a.png", "character:the-onryo", "one"));
        var once = ImportedPackMigration.Migrate([pack], new Dictionary<string, CharacterPictures>(), pack.Info.Id, _store);

        var twice = ImportedPackMigration.Migrate([pack], once.Pictures, pack.Info.Id, _store);
        foreach (var key in _storage.Files.Keys.Where(k => k.Replace('\\', '/').Contains("/imported/", StringComparison.Ordinal) && k.EndsWith(".png", StringComparison.Ordinal)).ToArray())
        {
            _storage.Files.Remove(key);
        }

        var missing = ImportedPackMigration.Migrate([pack], new Dictionary<string, CharacterPictures>(), null, _store);

        Assert.Equal(once.Pictures["character:the-onryo"], twice.Pictures["character:the-onryo"]);
        Assert.Equal(0, twice.Migrated);
        Assert.False(missing.AllCopied);
        Assert.Empty(missing.Pictures);
    }

    private sealed class Cached(HashSet<string> uncachedHashes) : IPackContentCache
    {
        public bool Contains(PackEntry entry) => !uncachedHashes.Contains(entry.Sha256);
    }
}
