using DbdPresence.Core.Composition;
using DbdPresence.Core.Images;
using DbdPresence.Core.Online;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Resolution;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Packs.Local;

/// <summary>Where a pack comes from.</summary>
public enum PackOrigin
{
    /// <summary>A pack the user imported on this PC (preview only).</summary>
    Imported,

    /// <summary>A pack synced from the owner's service (V2-S4).</summary>
    Synced,
}

/// <summary>A pack as the Packs page lists it.</summary>
/// <param name="Info">Metadata.</param>
/// <param name="Origin">Where it comes from.</param>
/// <param name="Entries">Its images.</param>
public sealed record PackCard(PackInfo Info, PackOrigin Origin, IReadOnlyList<PackEntry> Entries);

/// <summary>Where the UI loads an image from: always a normalised PNG file under the app data folder (nothing is bundled, D16).</summary>
/// <param name="Path">The file path.</param>
public sealed record PackImageLocation(string Path);

/// <summary>
/// An https address Discord was given that belongs to a pack or to the user's own uploaded pictures (D29).
/// </summary>
/// <param name="Location">The local, hash-verified file of the picture, or null when it is not on this PC (for example withdrawn or not downloaded yet).</param>
public sealed record PackReferenceMatch(PackImageLocation? Location);

/// <summary>The user's choice of pack per category (FR-PACK-06). An empty id means no pack is chosen for the category.</summary>
/// <param name="Killer">Pack for the Killer role icon.</param>
/// <param name="Survivor">Pack for the Survivor role icon.</param>
/// <param name="Characters">Pack for character images.</param>
/// <param name="Maps">Pack for map images.</param>
public sealed record PackChoices(string Killer, string Survivor, string Characters, string Maps)
{
    /// <summary>The id meaning "no pack chosen".</summary>
    public const string NoPackId = "";

    /// <summary>No pack chosen for any category: only the owner fallback pack, if any, applies.</summary>
    public static PackChoices None { get; } = new(NoPackId, NoPackId, NoPackId, NoPackId);
}

/// <summary>Packs delivered by sync (V2-S4): the verified index, the cache that holds their files, and where Discord loads them.</summary>
/// <param name="Index">The current signed, synced index.</param>
/// <param name="Cache">Tells which files are cached.</param>
/// <param name="UrlFor">The https URL of an entry on the owner's CDN.</param>
/// <param name="Withdrawn">Ids of entries to skip even if cached (licensed items withdrawn since the cache was filled); null for none.</param>
/// <param name="LocalPathFor">
/// The file the UI loads an entry's thumbnail from (a PNG that passed <c>StrictPng</c>), or null when there is none. For a PNG entry it is
/// the cache file; for a JPEG entry it is the helper's normalised PNG. Null for the delegate itself means no thumbnails.
/// </param>
public sealed record SyncedPacks(
    PackIndex Index,
    IPackContentCache Cache,
    Func<PackEntry, string?> UrlFor,
    IReadOnlySet<string>? Withdrawn = null,
    Func<PackEntry, string?>? LocalPathFor = null);

/// <summary>The outcome of storing a per-image override.</summary>
/// <param name="FileName">The stored file name (<c>&lt;sha256&gt;.png</c>) to keep in settings, or null on failure.</param>
/// <param name="Error">What went wrong, in plain words, when <paramref name="FileName"/> is null.</param>
public sealed record OverrideSaveResult(string? FileName, string? Error);

/// <summary>
/// The local image pack library (FR-PACK-01, FR-PACK-06, FR-PACK-07, FR-PACK-10): packs the
/// user imported, per-image overrides, and the resolution of what the preview and Discord show. Every image from the
/// user's PC is decoded by <see cref="ISafeImageLoader"/> (the image helper process) before it is stored; this class never
/// decodes an image itself. Nothing here uses the network.
/// </summary>
public sealed class PackLibrary
{
    /// <summary>Most images read from one import; the rest are listed as skipped.</summary>
    public const int MaxImportImages = 400;

    private const int ConcurrentLoads = 3;

    private readonly ILocalPackStorage _storage;
    private readonly ISafeImageLoader _loader;
    private readonly ImportedPackStore _imported;
    private readonly string _overridesRoot;
    private readonly string _picturesRoot;
    private readonly ImportArchiveReader _reader;
    private readonly ImportLimits _limits;
    private readonly Func<string, bool> _isLocalPath;
    private readonly Lock _gate = new();
    private IReadOnlyList<ImportedPack> _packs = [];
    private IReadOnlyList<string> _problems = [];
    private SyncedPacks? _synced;
    private Dictionary<string, string> _uploads = new(StringComparer.Ordinal);

    /// <summary>Creates the library and loads the imported packs.</summary>
    /// <param name="storage">File operations.</param>
    /// <param name="packsRoot">The <c>packs</c> folder under the app data folder.</param>
    /// <param name="loader">Loads untrusted images in the image helper process.</param>
    /// <param name="roster">The characters imported file names are matched against.</param>
    /// <param name="limits">Archive limits; the shipped ones by default.</param>
    /// <param name="isLocalPath">Decides whether a path the user chose may be opened at all; the default is <see cref="LocalPathGuard.IsAcceptable"/>.</param>
    public PackLibrary(ILocalPackStorage storage, string packsRoot, ISafeImageLoader loader, CharacterRoster roster, ImportLimits? limits = null, Func<string, bool>? isLocalPath = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrEmpty(packsRoot);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(roster);
        _storage = storage;
        _loader = loader;
        _imported = new ImportedPackStore(storage, Path.Combine(packsRoot, "imported"));
        _overridesRoot = Path.Combine(packsRoot, "overrides");
        _picturesRoot = Path.Combine(packsRoot, "pictures");
        _limits = limits ?? ImportLimits.Default;
        _isLocalPath = isLocalPath ?? LocalPathGuard.IsAcceptable;
        _reader = new ImportArchiveReader(roster, limits);
        Reload();
    }

    /// <summary>Gets the imported packs.</summary>
    public IReadOnlyList<ImportedPack> Imported
    {
        get
        {
            lock (_gate)
            {
                return _packs;
            }
        }
    }

    /// <summary>Gets what could not be loaded the last time the imported packs were read (shown on the Packs page).</summary>
    public IReadOnlyList<string> LoadProblems
    {
        get
        {
            lock (_gate)
            {
                return _problems;
            }
        }
    }

    /// <summary>Gets the packs delivered by sync (V2-S4), or null before the first successful sync and when nothing is cached.</summary>
    public SyncedPacks? Synced
    {
        get
        {
            lock (_gate)
            {
                return _synced;
            }
        }
    }

    /// <summary>Replaces the synced packs (called by the composition root when a sync finished or the cached index was loaded).</summary>
    /// <param name="synced">The new synced packs, or null for none.</param>
    public void SetSynced(SyncedPacks? synced)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_synced, synced))
            {
                return;
            }

            _synced = synced;
        }

        SyncedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised (on any thread) after <see cref="SetSynced"/> or <see cref="SetUploads"/>: what Discord and the preview show may have changed.</summary>
    public event EventHandler? SyncedChanged;

    /// <summary>
    /// Replaces the user's uploads (FR-ONL-09, D4, D12): the https URL of each uploaded image by the SHA-256 (lower-case hex) of its bytes. An override
    /// whose file was uploaded is no longer preview-only: Discord gets its unlisted upload URL (<see cref="ResolveDiscord"/>).
    /// </summary>
    /// <param name="urlBySha256">The uploads, or null for none (signed out).</param>
    /// <param name="uploadHost">The content host of this build's environment: only its exact <c>/u/</c> addresses are accepted
    /// (<see cref="UploadPolicy.IsUploadUrl"/>); anything else is ignored and never reaches Discord.</param>
    public void SetUploads(IReadOnlyDictionary<string, string>? urlBySha256, string? uploadHost)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sha, url) in urlBySha256 ?? new Dictionary<string, string>())
        {
            if (sha.Length == 64 && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && UploadPolicy.IsUploadUrl(url, uploadHost))
            {
                next[sha] = url;
            }
        }

        lock (_gate)
        {
            if (_uploads.Count == next.Count && next.All(p => _uploads.TryGetValue(p.Key, out var v) && v == p.Value))
            {
                return;
            }

            _uploads = next;
        }

        SyncedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The upload URL of an override file (<c>&lt;sha256&gt;.png</c>), or null when it was not uploaded.</summary>
    /// <param name="fileName">The override file name as stored in the settings.</param>
    /// <returns>The https URL, or null.</returns>
    public string? UploadUrlOf(string? fileName)
    {
        if (fileName is null || !IsOverrideFileName(fileName))
        {
            return null;
        }

        lock (_gate)
        {
            return _uploads.TryGetValue(fileName[..64], out var url) ? url : null;
        }
    }

    /// <summary>Reads the imported packs from disk again.</summary>
    public void Reload()
    {
        var packs = _imported.LoadAll(out var problems);
        lock (_gate)
        {
            _packs = packs;
            _problems = problems;
        }
    }

    /// <summary>
    /// The packs to list: the synced official packs by name (only their cached, not withdrawn images), then the imported packs by name.
    /// Empty when there are none of either.
    /// </summary>
    public IReadOnlyList<PackCard> Cards()
    {
        var cards = new List<PackCard>();
        if (Synced is { } synced)
        {
            foreach (var pack in synced.Index.Packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var entries = synced.Index.Entries
                    .Where(e => string.Equals(e.PackId, pack.Id, StringComparison.Ordinal) && IsUsable(synced, e))
                    .ToArray();
                if (entries.Length > 0)
                {
                    cards.Add(new PackCard(pack, PackOrigin.Synced, entries));
                }
            }
        }

        cards.AddRange(Imported.OrderBy(p => p.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pack => new PackCard(pack.Info, PackOrigin.Imported, pack.Entries)));
        return cards;
    }

    private static bool IsUsable(SyncedPacks synced, PackEntry entry) =>
        (synced.Withdrawn is null || !synced.Withdrawn.Contains(entry.Id)) && synced.Cache.Contains(entry);

    /// <summary>Where the UI loads the thumbnail of an entry from, or null when the file is missing.</summary>
    public PackImageLocation? LocationOf(PackCard card, PackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(entry);
        switch (card.Origin)
        {
            case PackOrigin.Imported:
                var pack = Imported.FirstOrDefault(p => p.Info.Id == card.Info.Id);
                return pack?.PathOf(entry) is { } path ? new PackImageLocation(path) : null;
            case PackOrigin.Synced:
                return SyncedLocation(entry);
            default:
                return null;
        }
    }

    /// <summary>Where the UI loads a resolved image from, or null when it has none or its file is missing.</summary>
    public PackImageLocation? LocationOf(ResolvedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        switch (image.Source)
        {
            case ResolvedImageSource.UserOverride when image.OverridePath is { } path:
                return new PackImageLocation(path);
            case ResolvedImageSource.ActivePack or ResolvedImageSource.FallbackPack when image.Entry is { } active:
                return Imported.FirstOrDefault(p => p.Info.Id == active.PackId)?.PathOf(active) is { } file
                    ? new PackImageLocation(file)
                    : SyncedLocation(active);
            default:
                return null;
        }
    }

    /// <summary>
    /// Finds the pack or upload picture behind an https address that is sent to Discord, so the preview can show the same picture from
    /// the local cache (D29, FR-ART-06). It never fetches anything: the answer holds only a local file path. Null means the address is
    /// not one of ours (a link the user typed, or an asset key), which the preview labels instead of showing.
    /// </summary>
    /// <param name="reference">The image reference of the activity.</param>
    public PackReferenceMatch? MatchReference(string? reference)
    {
        if (reference is null || !reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? uploadSha = null;
        lock (_gate)
        {
            foreach (var (sha, url) in _uploads)
            {
                if (string.Equals(url, reference, StringComparison.Ordinal))
                {
                    uploadSha = sha;
                    break;
                }
            }
        }

        if (uploadSha is not null)
        {
            return new PackReferenceMatch(OverridePath(uploadSha + ".png") is { } file ? new PackImageLocation(file) : null);
        }

        if (Synced is not { } synced)
        {
            return null;
        }

        foreach (var entry in synced.Index.Entries)
        {
            if (string.Equals(synced.UrlFor(entry), reference, StringComparison.Ordinal))
            {
                return new PackReferenceMatch(IsUsable(synced, entry) ? SyncedLocation(entry) : null);
            }
        }

        return null;
    }

    private PackImageLocation? SyncedLocation(PackEntry entry) =>
        Synced?.LocalPathFor?.Invoke(entry) is { Length: > 0 } path ? new PackImageLocation(path) : null;

    /// <summary>The stored file of an override, or null when the name is not a valid override file or the file is gone.</summary>
    public string? OverridePath(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!IsOverrideFileName(fileName))
        {
            return null;
        }

        // Own pictures (settings v3) live in packs/pictures; files kept by older versions are still in packs/overrides.
        foreach (var root in new[] { _picturesRoot, _overridesRoot })
        {
            var path = Path.Combine(root, fileName);
            if (_storage.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an archive and normalises each image in the image helper. Never throws for hostile content. Images are made one after the other (a few
    /// at a time) and each clean PNG is judged as it arrives: one that is too big is dropped at once, and the PNGs kept together stay under
    /// <see cref="ImportLimits.MaxResultBytes"/>, so a large archive cannot fill memory. Cancelling stops the work and throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="archivePath">The archive the user chose; a network path is refused before it is read.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <param name="progress">Told after each image how many of them are done.</param>
    public async Task<ImportAnalysis> AnalyseImportAsync(string archivePath, CancellationToken cancellationToken, IProgress<ImportProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        if (!_isLocalPath(archivePath))
        {
            return Rejected(LocalPathGuard.RefusalMessage);
        }

        byte[] bytes;
        try
        {
            if (!_storage.Exists(archivePath))
            {
                return Rejected("That file could not be found.");
            }

            if (_storage.Length(archivePath) > _limits.MaxArchiveBytes)
            {
                return Rejected($"The file is larger than {_limits.MaxArchiveBytes / (1024 * 1024)} MB.");
            }

            bytes = _storage.ReadAllBytes(archivePath);
        }
        catch (IOException ex)
        {
            return Rejected($"The file could not be read ({ex.GetType().Name}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Rejected($"The file could not be read ({ex.GetType().Name}).");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var read = _reader.Read(stream);
        if (read.IsRejected)
        {
            return new ImportAnalysis([], read.Skipped, read.RejectedReason);
        }

        var skipped = new List<SkippedEntry>(read.Skipped);
        var candidates = read.Candidates.Take(MaxImportImages).ToArray();
        foreach (var extra in read.Candidates.Skip(MaxImportImages))
        {
            skipped.Add(new SkippedEntry(extra.DisplayName, $"Only the first {MaxImportImages} images are read."));
        }

        var items = new List<ImportItem>();
        var keptBytes = 0L;
        var window = new Queue<Task<SafeImageResult>>();
        var next = 0;
        try
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (next < candidates.Length && window.Count < ConcurrentLoads)
                {
                    window.Enqueue(_loader.LoadBytesAsync(candidates[next++].Bytes, cancellationToken));
                }

                var candidate = candidates[i];
                var result = await window.Dequeue().ConfigureAwait(false);
                if (result.Status != SafeImageStatus.Ok || result.PngBytes is not { } png)
                {
                    skipped.Add(new SkippedEntry(candidate.DisplayName, result.UserMessage));
                }
                else if (png.Length > PackIndexParser.MaxFileBytes)
                {
                    skipped.Add(new SkippedEntry(candidate.DisplayName, "Still larger than 2 MB after conversion."));
                }
                else if (keptBytes + png.Length > _limits.MaxResultBytes)
                {
                    skipped.Add(new SkippedEntry(candidate.DisplayName, "Left out: the images of this archive together are too big to hold at once."));
                }
                else
                {
                    keptBytes += png.Length;
                    items.Add(new ImportItem(candidate.DisplayName, png, result.Width, result.Height, ImportedPackStore.Sha256Of(png), candidate.Proposal));
                }

                progress?.Report(new ImportProgress(i + 1, candidates.Length));
            }
        }
        finally
        {
            // Loads still running when the import was cancelled or failed are given time to end; their outcome is not used.
            await Task.WhenAny(Task.WhenAll(window)).ConfigureAwait(false);
        }

        return new ImportAnalysis(items, skipped, null);

        static ImportAnalysis Rejected(string reason) => new([], [], reason);
    }

    /// <summary>Saves the images the user confirmed as a new local pack and reloads the list.</summary>
    public SaveImportResult SaveImport(string name, string creator, string sourceName, IReadOnlyList<ConfirmedMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var taken = Imported.Select(p => p.Info.Id).Concat(Synced?.Index.Packs.Select(p => p.Id) ?? []).ToArray();
        var id = _imported.NewId(name, taken);
        var result = _imported.Save(id, name, creator, sourceName, mappings);
        if (result.Pack is not null)
        {
            Reload();
        }

        return result;
    }

    /// <summary>Removes an imported pack.</summary>
    public void RemoveImported(string id)
    {
        _imported.Remove(id);
        Reload();
    }

    /// <summary>
    /// Normalises the user's file in the image helper and stores the clean PNG as <c>overrides/&lt;sha256&gt;.png</c>
    /// (FR-PACK-06, FR-PACK-10). The original file is never kept or uploaded.
    /// </summary>
    public async Task<OverrideSaveResult> SaveOverrideAsync(string key, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!PackKey.TryParse(key, out _, out _))
        {
            return new OverrideSaveResult(null, "That is not a valid image slot.");
        }

        if (!_isLocalPath(path))
        {
            return new OverrideSaveResult(null, LocalPathGuard.RefusalMessage);
        }

        var result = await _loader.LoadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (result.Status != SafeImageStatus.Ok || result.PngBytes is not { } png)
        {
            return new OverrideSaveResult(null, result.UserMessage);
        }

        if (png.Length > PackIndexParser.MaxFileBytes)
        {
            return new OverrideSaveResult(null, "That image is still larger than 2 MB after conversion. Try a simpler picture.");
        }

        var fileName = ImportedPackStore.Sha256Of(png) + ".png";
        var target = Path.Combine(_overridesRoot, fileName);
        try
        {
            if (!_storage.Exists(target))
            {
                _storage.CreateDirectory(_overridesRoot);
                var temp = $"{target}.{Guid.NewGuid():N}.tmp";
                _storage.WriteAllBytesDurable(temp, png);
                _storage.Move(temp, target);
            }
        }
        catch (IOException ex)
        {
            return new OverrideSaveResult(null, $"The image could not be saved ({ex.GetType().Name}). Check free disk space and try again.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new OverrideSaveResult(null, $"The image could not be saved ({ex.GetType().Name}). The app data folder is not writable.");
        }

        return new OverrideSaveResult(fileName, null);
    }

    /// <summary>
    /// Reads the stored file of an override (the clean PNG the image helper made) so it can be uploaded (FR-ONL-09). Null when the name is not an
    /// override file name or the file cannot be read.
    /// </summary>
    /// <param name="fileName">The override file name as stored in the settings.</param>
    /// <returns>The bytes, or null.</returns>
    public byte[]? ReadOverrideFile(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (OverridePath(fileName) is not { } path || _storage.Length(path) > PackIndexParser.MaxFileBytes)
        {
            return null;
        }

        try
        {
            return _storage.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the stored file of an override that no setting refers to any more.</summary>
    public void DeleteOverrideFile(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (IsOverrideFileName(fileName))
        {
            _storage.Delete(Path.Combine(_overridesRoot, fileName));
        }
    }

    /// <summary>
    /// What the user's own preview shows: override, then the chosen pack for the category, then the owner fallback pack, then
    /// no image (D16). A picked character without an image shows the generic tile of its role when a pack has one.
    /// </summary>
    public ResolvedSelection ResolvePreview(
        ImageSelection selection, PackChoices choices, IReadOnlyDictionary<string, string> overrideFiles, SyncedPacks? synced = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(overrideFiles);
        return ResolveWith(selection, choices, OverridePaths(overrideFiles), synced ?? Synced);
    }

    /// <summary>
    /// What Discord is given (FR-PACK-06). Local images are preview-only, so Discord gets nothing for them and
    /// <see cref="DiscordImageRef.LocalOnly"/> tells the UI to say so, except an override the user uploaded (FR-ONL-09), which Discord gets as its
    /// unlisted upload URL. Synced packs, when present, give https URLs.
    /// </summary>
    public DiscordSelection ResolveDiscord(
        ImageSelection selection, PackChoices choices, IReadOnlyDictionary<string, string> overrideFiles, SyncedPacks? synced = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(overrideFiles);
        synced ??= Synced;
        var preview = ResolveWith(selection, choices, OverridePaths(overrideFiles), synced);

        // The Discord side sees no overrides and no local packs, only synced packs (the fallback pack included).
        var noOverrides = new Dictionary<string, string>();
        var remote = ResolveWith(selection, choices, noOverrides, synced, includeImported: false);
        var mark = PackImageResolver.ResolveKey(
            PackKey.Make(PackEntryKind.App, "mark"), Context(PackChoices.NoPackId, noOverrides, synced, includeImported: false));
        return new DiscordSelection(
            ToDiscord(preview.Role, remote.Role, synced),
            ToDiscord(preview.Character, remote.Character, synced),
            ToDiscord(preview.Map, remote.Map, synced),
            ToDiscord(mark, mark, synced));
    }

    /// <summary>
    /// What Discord is given for one of the nine reserved automatic pictures (FR-AUTO-02, <see cref="AutomaticIcons"/>): the first of the listed packs
    /// that has the <c>app:&lt;slug&gt;</c> picture in the cache, then the owner's fallback pack, then nothing. User overrides and imported packs never
    /// apply (these pictures are owner-pack-only, and Discord only gets synced https pictures), and a withdrawn entry is skipped.
    /// </summary>
    /// <param name="slug">One of the nine reserved slugs; any other slug gives no picture.</param>
    /// <param name="packIds">The packs to try first, in order: the active pack for the moment, then the owner's default pack. Empty ids are skipped.</param>
    /// <param name="synced">The synced packs; null uses the current ones.</param>
    public DiscordImageRef ResolveAutomaticPicture(string slug, IReadOnlyList<string?> packIds, SyncedPacks? synced = null)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(packIds);
        synced ??= Synced;
        if (synced is null || !AutomaticIcons.IsReserved(slug))
        {
            return DiscordImageRef.None;
        }

        var key = PackKey.Make(PackEntryKind.App, slug);
        var noOverrides = new Dictionary<string, string>();
        foreach (var packId in packIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal))
        {
            var image = PackImageResolver.ResolveKey(key, Context(packId!, noOverrides, synced, includeImported: false));
            if (image.Source == ResolvedImageSource.ActivePack)
            {
                return ToDiscord(image, image, synced);
            }
        }

        var fallback = PackImageResolver.ResolveKey(key, Context(PackChoices.NoPackId, noOverrides, synced, includeImported: false));
        return ToDiscord(fallback, fallback, synced);
    }

    private ResolvedSelection ResolveWith(
        ImageSelection selection, PackChoices choices, IReadOnlyDictionary<string, string> overrides, SyncedPacks? synced, bool includeImported = true)
    {
        var roleChoice = string.Equals(selection.Role, "survivor", StringComparison.Ordinal) ? choices.Survivor : choices.Killer;
        var role = PackImageResolver.Resolve(selection, Context(roleChoice, overrides, synced, includeImported)).Role;
        var charactersContext = Context(choices.Characters, overrides, synced, includeImported);
        var character = PackImageResolver.Resolve(selection, charactersContext).Character;
        var hasNoImage = character.Source == ResolvedImageSource.None;
        if (selection.Character is not null && hasNoImage && GenericCharacterKey(selection.Role) is { } genericKey)
        {
            character = PackImageResolver.ResolveKey(genericKey, charactersContext);
        }

        var map = PackImageResolver.Resolve(selection, Context(choices.Maps, overrides, synced, includeImported)).Map;
        return new ResolvedSelection(role, character, map);
    }

    private DiscordImageRef ToDiscord(ResolvedImage preview, ResolvedImage remote, SyncedPacks? synced)
    {
        if (preview.Source == ResolvedImageSource.UserOverride && UploadUrlOf(Path.GetFileName(preview.OverridePath)) is { } uploaded)
        {
            return new DiscordImageRef(uploaded, false);
        }

        var reference = DiscordImageMapper.Map(remote, synced?.UrlFor);
        return new DiscordImageRef(reference, IsLocal(preview), reference is not null && IsBehaviourArt(remote, synced));
    }

    private static bool IsBehaviourArt(ResolvedImage image, SyncedPacks? synced) =>
        image.Entry is { } entry && synced is not null
        && synced.Index.Packs.Any(p => p.BehaviourArt && string.Equals(p.Id, entry.PackId, StringComparison.Ordinal));

    private bool IsLocal(ResolvedImage image) => image.Source == ResolvedImageSource.UserOverride
        || (image.Source == ResolvedImageSource.ActivePack && image.Entry is { } entry && Imported.Any(p => p.Info.Id == entry.PackId));

    private static string? GenericCharacterKey(string? role) => role switch
    {
        "killer" => "character:generic-killer",
        "survivor" => "character:generic-survivor",
        _ => null,
    };

    private ResolverContext Context(string packId, IReadOnlyDictionary<string, string> overrides, SyncedPacks? synced, bool includeImported = true)
    {
        var importedPacks = includeImported ? Imported : [];
        var packs = new List<PackInfo>();
        var entries = new List<PackEntry>();
        foreach (var pack in importedPacks)
        {
            packs.Add(pack.Info);
            entries.AddRange(pack.Entries);
        }

        if (synced is not null)
        {
            packs.AddRange(synced.Index.Packs);
            entries.AddRange(synced.Index.Entries);
        }

        var active = string.IsNullOrEmpty(packId) ? [] : new[] { packId };
        return new ResolverContext(
            new PackIndex(packs, entries),
            active,
            overrides,
            new DelegateCache(entry => importedPacks.FirstOrDefault(p => p.Info.Id == entry.PackId)?.Cache.Contains(entry)
                ?? synced?.Cache.Contains(entry) ?? false),
            synced?.Withdrawn ?? new HashSet<string>(StringComparer.Ordinal));
    }

    private Dictionary<string, string> OverridePaths(IReadOnlyDictionary<string, string> files)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, file) in files)
        {
            if (OverridePath(file) is { } path)
            {
                paths[key] = path;
            }
        }

        return paths;
    }

    private static bool IsOverrideFileName(string fileName) =>
        fileName.Length == 68 && fileName.EndsWith(".png", StringComparison.Ordinal)
        && fileName[..64].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class DelegateCache(Func<PackEntry, bool> contains) : IPackContentCache
    {
        public bool Contains(PackEntry entry) => contains(entry);
    }
}
