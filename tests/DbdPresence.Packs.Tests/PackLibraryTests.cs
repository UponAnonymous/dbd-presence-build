// Covers: FR-PACK-06 (preview and Discord resolution per category; local images are preview-only; map only when Confirmed),
// FR-PACK-07 (import analysis, confirmation, local pack storage and removal), FR-PACK-10 (every image goes through the
// image loader; nothing is decoded here), FR-ART-04 and D16 (override, active pack, owner fallback pack, else no image).
using System.Text;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Resolution;

namespace DbdPresence.Packs.Tests;

public sealed class PackLibraryTests
{
    private const string Root = "/data/packs";
    private static readonly IReadOnlyDictionary<string, string> s_noOverrides = new Dictionary<string, string>();

    private readonly InMemoryLocalStorage _storage = new();
    private readonly FakeImageLoader _loader;
    private readonly PackLibrary _library;

    public PackLibraryTests()
    {
        _loader = new FakeImageLoader(_storage);
        _library = new PackLibrary(_storage, Root, _loader, TestRoster.Embedded);
    }

    private static ImageSelection Pick(string? role = "killer", string? character = null, string? map = null, bool mapConfirmed = false) =>
        new(role, character, map, mapConfirmed);

    private async Task<ImportedPack> ImportOne(string key = "role:killer", string name = "My pack")
    {
        _storage.Files["/in/a.zip"] = ZipBuilder.Build(("art.png", ZipBuilder.Png("one")));
        var analysis = await _library.AnalyseImportAsync("/in/a.zip", CancellationToken.None);
        var saved = _library.SaveImport(name, "Some Creator", "a.zip", [new ConfirmedMapping(analysis.Items[0], key)]);
        Assert.NotNull(saved.Pack);
        return saved.Pack;
    }

    [Fact]
    public void Cards_AreEmptyUntilAPackIsImported()
    {
        Assert.Empty(_library.Cards());
        Assert.Empty(_library.Imported);
        Assert.Empty(_library.LoadProblems);
    }

    [Fact]
    public async Task Analyse_NormalisesEveryCandidateThroughTheLoaderAndProposesMappings()
    {
        _storage.Files["/in/pack.zip"] = ZipBuilder.Build(
            ("the-trapper.png", ZipBuilder.Png("a")), ("killer.png", ZipBuilder.Png("b")), ("broken.png", ZipBuilder.Png("BAD")), ("notes.txt", Encoding.ASCII.GetBytes("hello")));

        var analysis = await _library.AnalyseImportAsync("/in/pack.zip", CancellationToken.None);

        Assert.False(analysis.IsRejected);
        Assert.Equal(["the-trapper.png", "killer.png"], analysis.Items.Select(i => i.DisplayName));
        Assert.Equal("character:the-trapper", analysis.Items[0].Proposal!.Key);
        Assert.Equal("role:killer", analysis.Items[1].Proposal!.Key);
        Assert.Equal(3, _loader.Loaded.Count);
        Assert.Contains(analysis.Skipped, s => s.Name == "broken.png" && s.Reason.Contains("could not be opened", StringComparison.Ordinal));
        Assert.Contains(analysis.Skipped, s => s.Name == "notes.txt");
        Assert.All(analysis.Items, i => Assert.Equal(ImportedPackStore.Sha256Of(i.Png), i.Sha256));
        Assert.All(analysis.Items, i => Assert.True(DbdPresence.Core.Images.StrictPng.TryValidate(i.Png, out _, out _, out _)));
    }

    [Fact]
    public async Task Analyse_ReportsAMissingFileAsRejected()
    {
        var analysis = await _library.AnalyseImportAsync("/in/none.zip", CancellationToken.None);

        Assert.True(analysis.IsRejected);
        Assert.Empty(analysis.Items);
    }

    [Fact]
    public async Task Analyse_RejectsANonArchive()
    {
        _storage.Files["/in/x.zip"] = Encoding.ASCII.GetBytes("this is not a zip file at all");

        var analysis = await _library.AnalyseImportAsync("/in/x.zip", CancellationToken.None);

        Assert.True(analysis.IsRejected);
    }

    [Fact]
    public async Task Analyse_ReadsOnlyTheFirstFourHundredImages()
    {
        var entries = Enumerable.Range(0, PackLibrary.MaxImportImages + 3).Select(i => ($"img{i:000}.png", ZipBuilder.Png(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        _storage.Files["/in/big.zip"] = ZipBuilder.Build(entries);

        var analysis = await _library.AnalyseImportAsync("/in/big.zip", CancellationToken.None);

        Assert.Equal(PackLibrary.MaxImportImages, analysis.Items.Count);
        Assert.Equal(3, analysis.Skipped.Count(s => s.Reason.Contains("first 400", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Save_StoresIndexAndHashNamedFilesAndTheLibraryListsThePack()
    {
        var pack = await ImportOne("character:the-trapper");

        Assert.Equal("my-pack", pack.Info.Id);
        Assert.Equal("Some Creator", pack.Info.Author);
        Assert.Contains("a.zip", pack.Info.Credit, StringComparison.Ordinal);
        Assert.True(_storage.Exists(Path.Combine(Root, "imported", "my-pack", "pack.json")));
        var entry = Assert.Single(pack.Entries);
        Assert.True(_storage.Exists(Path.Combine(Root, "imported", "my-pack", "files", $"{entry.Sha256}.png")));
        var parsed = PackIndexParser.Parse(Encoding.UTF8.GetString(_storage.Files[Path.Combine(Root, "imported", "my-pack", "pack.json")]));
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        var cards = _library.Cards();
        var card = Assert.Single(cards);
        Assert.Equal(PackOrigin.Imported, card.Origin);
        Assert.True(_storage.Exists(_library.LocationOf(card, card.Entries[0])!.Path));
    }

    [Fact]
    public async Task Save_SurvivesARestart()
    {
        await ImportOne();

        var again = new PackLibrary(_storage, Root, _loader, TestRoster.Embedded);

        Assert.Equal(["my-pack"], again.Imported.Select(p => p.Info.Id));
    }

    [Fact]
    public async Task Save_GivesASecondPackWithTheSameNameANewId()
    {
        await ImportOne();
        var second = await ImportOne(name: "My pack");

        Assert.Equal("my-pack-2", second.Info.Id);
    }

    [Fact]
    public async Task Save_AllowsAnyNameNoIdIsReserved()
    {
        var pack = await ImportOne(name: "Classic");

        Assert.Equal("classic", pack.Info.Id);
    }

    [Fact]
    public async Task Save_RefusesTwoImagesForOneSlotAndKeepsNothing()
    {
        _storage.Files["/in/a.zip"] = ZipBuilder.Build(("a.png", ZipBuilder.Png("a")), ("b.png", ZipBuilder.Png("b")));
        var analysis = await _library.AnalyseImportAsync("/in/a.zip", CancellationToken.None);

        var result = _library.SaveImport("X", "me", "a.zip", [new ConfirmedMapping(analysis.Items[0], "role:killer"), new ConfirmedMapping(analysis.Items[1], "role:killer")]);

        Assert.Null(result.Pack);
        Assert.Contains("role:killer", result.Error, StringComparison.Ordinal);
        Assert.Empty(_library.Imported);
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("character:UPPER")]
    [InlineData("../x")]
    public async Task Save_RefusesInvalidKeys(string key)
    {
        _storage.Files["/in/a.zip"] = ZipBuilder.Build(("a.png", ZipBuilder.Png("a")));
        var analysis = await _library.AnalyseImportAsync("/in/a.zip", CancellationToken.None);

        var result = _library.SaveImport("X", "me", "a.zip", [new ConfirmedMapping(analysis.Items[0], key)]);

        Assert.Null(result.Pack);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Save_RefusesAnEmptyConfirmationAndAnEmptyName()
    {
        Assert.NotNull(_library.SaveImport("X", "me", "a.zip", []).Error);
    }

    [Fact]
    public async Task Save_UsesAPlaceholderCreatorWhenNoneIsGiven()
    {
        _storage.Files["/in/a.zip"] = ZipBuilder.Build(("a.png", ZipBuilder.Png("a")));
        var analysis = await _library.AnalyseImportAsync("/in/a.zip", CancellationToken.None);

        var result = _library.SaveImport("Named", "   ", "a.zip", [new ConfirmedMapping(analysis.Items[0], "role:survivor")]);

        Assert.Equal("Unknown creator", result.Pack!.Info.Author);
    }

    [Fact]
    public async Task Remove_DeletesThePackAndItsFiles()
    {
        var pack = await ImportOne();

        _library.RemoveImported(pack.Info.Id);

        Assert.Empty(_library.Imported);
        Assert.DoesNotContain(_storage.Files.Keys, k => VirtualPaths.IsUnder(k, Path.Combine(Root, "imported", "my-pack"), out _));
    }

    [Fact]
    public void Remove_RefusesAnIdThatIsNotASlug()
    {
        Assert.Throws<ArgumentException>(() => _library.RemoveImported("../settings"));
    }

    [Fact]
    public void Load_ReportsADamagedPackAndKeepsTheRest()
    {
        _storage.Files[Path.Combine(Root, "imported", "broken", "pack.json")] = Encoding.ASCII.GetBytes("{not json");
        _storage.Files[Path.Combine(Root, "imported", "empty", "files", "x")] = [1];

        _library.Reload();

        Assert.Empty(_library.Imported);
        Assert.Equal(2, _library.LoadProblems.Count);
    }

    [Fact]
    public async Task Override_StoresTheNormalisedCopyUnderItsHash()
    {
        _storage.Files["/pics/mine.png"] = ZipBuilder.Png("mine");

        var result = await _library.SaveOverrideAsync("role:killer", "/pics/mine.png", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Matches("^[0-9a-f]{64}\\.png$", result.FileName);
        var stored = _library.OverridePath(result.FileName!);
        Assert.NotNull(stored);
        Assert.True(VirtualPaths.IsUnder(stored, Path.Combine(Root, "overrides"), out _), $"'{stored}' is not under the overrides folder");
        Assert.True(DbdPresence.Core.Images.StrictPng.TryValidate(_storage.Files[stored], out _, out _, out _));
        Assert.Equal(["/pics/mine.png"], _loader.Loaded);
    }

    [Fact]
    public async Task Override_ShowsTheLoadersMessageAndStoresNothingOnFailure()
    {
        _storage.Files["/pics/bad.png"] = ZipBuilder.Png("BAD");

        var result = await _library.SaveOverrideAsync("role:killer", "/pics/bad.png", CancellationToken.None);

        Assert.Null(result.FileName);
        Assert.Contains("could not be opened", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_storage.Files.Keys, k => VirtualPaths.IsUnder(k, Path.Combine(Root, "overrides"), out _));
    }

    [Fact]
    public async Task Override_RefusesAnInvalidSlotBeforeTouchingTheFile()
    {
        var result = await _library.SaveOverrideAsync("nonsense", "/pics/mine.png", CancellationToken.None);

        Assert.Null(result.FileName);
        Assert.Empty(_loader.Loaded);
    }

    [Theory]
    [InlineData("../../settings.json")]
    [InlineData("abc.png")]
    [InlineData("")]
    public void OverridePath_RejectsNamesThatAreNotStoredHashes(string name)
    {
        Assert.Null(_library.OverridePath(name));
    }

    [Fact]
    public async Task DeleteOverrideFile_RemovesOnlyValidOverrideFiles()
    {
        _storage.Files["/pics/mine.png"] = ZipBuilder.Png("mine");
        var saved = await _library.SaveOverrideAsync("role:killer", "/pics/mine.png", CancellationToken.None);
        var settings = Path.Combine("/data", "settings.json");
        _storage.Files[settings] = [1];

        _library.DeleteOverrideFile(saved.FileName!);
        _library.DeleteOverrideFile("../../settings.json");

        Assert.Null(_library.OverridePath(saved.FileName!));
        Assert.True(_storage.Exists(settings));
    }

    [Fact]
    public void Resolve_WithNoPacksAndNoFallbackPack_NothingResolvesAndDiscordGetsNoImages()
    {
        var selection = Pick("survivor", "the-trapper", "hawkins", mapConfirmed: true);

        var preview = _library.ResolvePreview(selection, PackChoices.None, s_noOverrides);
        var discord = _library.ResolveDiscord(selection, PackChoices.None, s_noOverrides);

        Assert.Equal(ResolvedImageSource.None, preview.Role.Source);
        Assert.Equal(ResolvedImageSource.None, preview.Character.Source);
        Assert.Equal(ResolvedImageSource.None, preview.Map.Source);
        Assert.Equal(DiscordImageRef.None, discord.Role);
        Assert.Equal(DiscordImageRef.None, discord.Character);
        Assert.Equal(DiscordImageRef.None, discord.Map);
        Assert.Equal(DiscordImageRef.None, discord.AppMark);
    }

    [Fact]
    public void Resolve_NoRoleAndNoCharacterGivesNoImages()
    {
        var discord = _library.ResolveDiscord(Pick(role: null), PackChoices.None, s_noOverrides);

        Assert.Equal(DiscordImageRef.None, discord.Role);
        Assert.Equal(DiscordImageRef.None, discord.Character);
        Assert.Equal(DiscordImageRef.None, discord.Map);
    }

    [Fact]
    public void Resolve_FallbackPackFillsWhatTheActivePackLacks()
    {
        var synced = Official(
            fallback: true, PackData.Entry("official-role-killer", "official", "role:killer", "k"),
            PackData.Entry("official-char-killer", "official", "character:generic-killer", "g"),
            PackData.Entry("official-app-mark", "official", "app:mark", "m"));

        var discord = _library.ResolveDiscord(Pick("killer", "the-trapper"), PackChoices.None, s_noOverrides, synced);

        Assert.Equal(Url(synced, "official-role-killer"), discord.Role.Reference);
        Assert.Equal(Url(synced, "official-char-killer"), discord.Character.Reference);
        Assert.Equal(Url(synced, "official-app-mark"), discord.AppMark.Reference);
        Assert.Null(discord.Map.Reference);
    }

    [Fact]
    public async Task Resolve_FallbackPackIsUsedWhenTheActivePackLacksTheKey()
    {
        await ImportOne("character:the-trapper");
        var synced = Official(fallback: true, PackData.Entry("official-role-killer", "official", "role:killer", "k"));
        var choices = PackChoices.None with { Killer = "my-pack", Characters = "my-pack" };

        var preview = _library.ResolvePreview(Pick("killer", "the-trapper"), choices, s_noOverrides, synced);
        var discord = _library.ResolveDiscord(Pick("killer", "the-trapper"), choices, s_noOverrides, synced);

        Assert.Equal(ResolvedImageSource.FallbackPack, preview.Role.Source);
        Assert.Equal("official-role-killer", preview.Role.Entry!.Id);
        Assert.Equal(ResolvedImageSource.ActivePack, preview.Character.Source);
        Assert.Equal(new DiscordImageRef(Url(synced, "official-role-killer"), false), discord.Role);
        Assert.Equal(new DiscordImageRef(null, true), discord.Character);
    }

    [Fact]
    public void Resolve_AWithdrawnLicensedItemFallsBackToTheFallbackPackThenToNone()
    {
        var licensed = PackData.Entry("official-ghostface", "official", "character:ghostface", "licensed", new LicensedInfo("Holder"));
        var backup = PackData.Entry("backup-ghostface", "backup", "character:ghostface", "backup");
        var index = new PackIndex(
            [new PackInfo("official", "Official", 1, "owner", "note", null), new PackInfo("backup", "Backup", 1, "owner", "note", null, Fallback: true)],
            [licensed, backup]);
        var choices = PackChoices.None with { Characters = "official" };

        SyncedPacks Synced(params string[] withdrawn) =>
            new(index, new AlwaysCached(), e => $"https://cdn.example.org/{e.Id}.png", new HashSet<string>(withdrawn, StringComparer.Ordinal));

        var live = _library.ResolveDiscord(Pick("killer", "ghostface"), choices, s_noOverrides, Synced());
        var licensedWithdrawn = _library.ResolveDiscord(Pick("killer", "ghostface"), choices, s_noOverrides, Synced("official-ghostface"));
        var bothWithdrawn = _library.ResolveDiscord(Pick("killer", "ghostface"), choices, s_noOverrides, Synced("official-ghostface", "backup-ghostface"));

        Assert.Equal("https://cdn.example.org/official-ghostface.png", live.Character.Reference);
        Assert.Equal("https://cdn.example.org/backup-ghostface.png", licensedWithdrawn.Character.Reference);
        Assert.Null(bothWithdrawn.Character.Reference);
    }

    [Fact]
    public void Resolve_MapIsOfferedOnlyWhenConfirmed()
    {
        var synced = Official(fallback: true, PackData.Entry("official-map-hawkins", "official", "map:hawkins", "map"));

        var unconfirmed = _library.ResolveDiscord(Pick(map: "hawkins", mapConfirmed: false), PackChoices.None, s_noOverrides, synced);
        var confirmed = _library.ResolveDiscord(Pick(map: "hawkins", mapConfirmed: true), PackChoices.None, s_noOverrides, synced);

        Assert.Null(unconfirmed.Map.Reference);
        Assert.Equal(Url(synced, "official-map-hawkins"), confirmed.Map.Reference);
    }

    [Fact]
    public async Task Resolve_AnOverrideWinsInThePreviewButDiscordGetsNoImage()
    {
        _storage.Files["/pics/mine.png"] = ZipBuilder.Png("mine");
        var saved = await _library.SaveOverrideAsync("role:killer", "/pics/mine.png", CancellationToken.None);
        var overrides = new Dictionary<string, string> { ["role:killer"] = saved.FileName! };

        var preview = _library.ResolvePreview(Pick(), PackChoices.None, overrides);
        var discord = _library.ResolveDiscord(Pick(), PackChoices.None, overrides);

        Assert.Equal(ResolvedImageSource.UserOverride, preview.Role.Source);
        Assert.Equal(_library.OverridePath(saved.FileName!), preview.Role.OverridePath);
        Assert.Equal(new DiscordImageRef(null, true), discord.Role);
    }

    [Fact]
    public async Task Resolve_AnUploadedOverride_GoesToDiscordAtItsUploadUrl_AndBackToNoneWhenTheUploadIsGone()
    {
        // FR-ONL-09: once the user uploaded their own image, Discord gets its unlisted https URL; signing out (no uploads) makes it preview-only again.
        _storage.Files["/pics/mine.png"] = ZipBuilder.Png("mine");
        var saved = await _library.SaveOverrideAsync("role:killer", "/pics/mine.png", CancellationToken.None);
        var overrides = new Dictionary<string, string> { ["role:killer"] = saved.FileName! };
        var sha = saved.FileName![..64];
        var url = "https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png";
        var raised = 0;
        _library.SyncedChanged += (_, _) => raised++;

        _library.SetUploads(new Dictionary<string, string> { [sha] = url }, Host);
        Assert.Equal(new DiscordImageRef(url, false), _library.ResolveDiscord(Pick(), PackChoices.None, overrides).Role);
        Assert.Equal(url, _library.UploadUrlOf(saved.FileName));
        Assert.Equal(1, raised);

        // The same list again changes nothing and raises nothing.
        _library.SetUploads(new Dictionary<string, string> { [sha] = url }, Host);
        Assert.Equal(1, raised);

        _library.SetUploads(null, Host);
        Assert.Equal(new DiscordImageRef(null, true), _library.ResolveDiscord(Pick(), PackChoices.None, overrides).Role);
        Assert.Equal(2, raised);
    }

    private const string Host = "cdn.arcavale.com";

    [Theory]
    [InlineData("http://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://evil.example/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com.evil.example/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn-staging.arcavale.com/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com:8443/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://user@cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png?x=1")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png#a")]
    [InlineData("https://cdn.arcavale.com/u/0123456789ABCDEF0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.gif")]
    [InlineData("https://cdn.arcavale.com/packs/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://CDN.arcavale.com/u/0123456789abcdef0123456789abcdef.png")]
    public void SetUploads_IgnoresAnythingButAnUploadAddressOnTheContentHost(string url)
    {
        var sha = new string('b', 64);
        _library.SetUploads(new Dictionary<string, string> { [sha] = url }, Host);
        Assert.Null(_library.UploadUrlOf(sha + ".png"));
    }

    [Fact]
    public void SetUploads_IgnoresBadHashesAndNeedsAHost()
    {
        var url = "https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png";
        _library.SetUploads(new Dictionary<string, string> { [new string('B', 64)] = url, ["short"] = url }, Host);
        Assert.Null(_library.UploadUrlOf(new string('b', 64) + ".png"));
        _library.SetUploads(new Dictionary<string, string> { [new string('b', 64)] = url }, null);
        Assert.Null(_library.UploadUrlOf(new string('b', 64) + ".png"));
        Assert.Null(_library.UploadUrlOf("not-a-file"));
        _library.SetUploads(new Dictionary<string, string> { [new string('b', 64)] = url }, Host);
        Assert.Equal(url, _library.UploadUrlOf(new string('b', 64) + ".png"));
    }

    [Fact]
    public async Task ReadOverrideFile_ReturnsTheStoredCleanPng_OrNull()
    {
        _storage.Files["/pics/mine.png"] = ZipBuilder.Png("mine");
        var saved = await _library.SaveOverrideAsync("role:killer", "/pics/mine.png", CancellationToken.None);
        Assert.NotNull(_library.ReadOverrideFile(saved.FileName!));
        Assert.Null(_library.ReadOverrideFile(new string('c', 64) + ".png"));
        Assert.Null(_library.ReadOverrideFile("../escape.png"));
    }

    [Fact]
    public void Resolve_AMissingOverrideFileIsIgnored()
    {
        var overrides = new Dictionary<string, string> { ["role:killer"] = new string('a', 64) + ".png" };

        var preview = _library.ResolvePreview(Pick(), PackChoices.None, overrides);
        var discord = _library.ResolveDiscord(Pick(), PackChoices.None, overrides);

        Assert.Equal(ResolvedImageSource.None, preview.Role.Source);
        Assert.False(discord.Role.LocalOnly);
    }

    [Fact]
    public async Task Resolve_AnImportedPackChosenForACategoryShowsInThePreviewOnly()
    {
        await ImportOne("role:killer");
        var choices = PackChoices.None with { Killer = "my-pack" };

        var preview = _library.ResolvePreview(Pick("killer"), choices, s_noOverrides);
        var discord = _library.ResolveDiscord(Pick("killer"), choices, s_noOverrides);
        var survivor = _library.ResolveDiscord(Pick("survivor"), choices, s_noOverrides);

        Assert.Equal(ResolvedImageSource.ActivePack, preview.Role.Source);
        Assert.Equal("my-pack", preview.Role.Entry!.PackId);
        Assert.Equal(new DiscordImageRef(null, true), discord.Role);
        Assert.Equal(DiscordImageRef.None, survivor.Role);
    }

    [Fact]
    public async Task Resolve_AnImportedCharacterImageIsUsedForThePickedCharacter()
    {
        await ImportOne("character:the-trapper");
        var choices = PackChoices.None with { Characters = "my-pack" };

        var preview = _library.ResolvePreview(Pick("killer", "the-trapper"), choices, s_noOverrides);
        var other = _library.ResolvePreview(Pick("killer", "the-wraith"), choices, s_noOverrides);

        Assert.Equal("character:the-trapper", preview.Character.Key);
        Assert.Equal(ResolvedImageSource.ActivePack, preview.Character.Source);
        Assert.Equal(ResolvedImageSource.None, other.Character.Source);
    }

    [Fact]
    public async Task Resolve_APackThatNoLongerExistsResolvesToNone()
    {
        var pack = await ImportOne("role:killer");
        var choices = PackChoices.None with { Killer = pack.Info.Id };
        _library.RemoveImported(pack.Info.Id);

        var preview = _library.ResolvePreview(Pick("killer"), choices, s_noOverrides);

        Assert.Equal(ResolvedImageSource.None, preview.Role.Source);
    }

    [Fact]
    public void Resolve_ASyncedPackGivesDiscordAnHttpsUrl()
    {
        var entry = new PackEntry("official-role-killer", "official", PackEntryKind.Role, "role:killer", new string('b', 64), 10, 1024, 1024, PackImageFormat.Png, null);
        var synced = new SyncedPacks(
            new PackIndex([new PackInfo("official", "Official", 1, "owner", "note", null)], [entry]),
            new AlwaysCached(),
            e => $"https://cdn.example.org/packs/{e.CacheFileName}");
        var choices = PackChoices.None with { Killer = "official" };

        var discord = _library.ResolveDiscord(Pick("killer"), choices, s_noOverrides, synced);

        Assert.Equal(new DiscordImageRef($"https://cdn.example.org/packs/{entry.CacheFileName}", false), discord.Role);
    }

    [Fact]
    public void Resolve_ASyncedEntryWithANonHttpsUrlIsNeverGivenToDiscord()
    {
        var entry = new PackEntry("official-role-killer", "official", PackEntryKind.Role, "role:killer", new string('b', 64), 10, 1024, 1024, PackImageFormat.Png, null);
        var synced = new SyncedPacks(
            new PackIndex([new PackInfo("official", "Official", 1, "owner", "note", null)], [entry]), new AlwaysCached(), _ => "http://insecure.example.org/a.png");

        var discord = _library.ResolveDiscord(Pick("killer"), PackChoices.None with { Killer = "official" }, s_noOverrides, synced);

        Assert.Null(discord.Role.Reference);
    }

    [Fact]
    public async Task Load_FindsPacksWhenTheStorageSpellsPathsWithBackslashes()
    {
        // The library joins paths with Path.Combine (a backslash on Windows). The storage fake must not care which separator
        // a path uses, so this runs the same on Linux and Windows (the CI failures of run 40034529719 were this mismatch).
        await ImportOne();
        var original = _storage.Files.ToArray();
        _storage.Files.Clear();
        foreach (var (path, bytes) in original)
        {
            _storage.Files[path.Replace('/', '\\')] = bytes;
        }

        var again = new PackLibrary(_storage, Root, _loader, TestRoster.Embedded);

        Assert.Equal(["my-pack"], again.Imported.Select(p => p.Info.Id));
        Assert.Empty(again.LoadProblems);
    }

    private static SyncedPacks Official(bool fallback, params PackEntry[] entries) =>
        new(
            new PackIndex([new PackInfo("official", "Official", 1, "owner", "note", null, fallback)], entries),
            new AlwaysCached(),
            e => $"https://cdn.example.org/{e.Id}.png");

    private static string Url(SyncedPacks synced, string entryId) => synced.UrlFor(synced.Index.Entries.Single(e => e.Id == entryId))!;

    private sealed class AlwaysCached : IPackContentCache
    {
        public bool Contains(PackEntry entry) => true;
    }
}
