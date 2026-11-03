using System.Security.Cryptography;
using System.Text.Json;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Index;

namespace DbdPresence.Packs.Local;

/// <summary>A pack the user imported. Its files are normalised PNGs stored under the app data folder (FR-PACK-07).</summary>
/// <param name="Info">Name, creator text and credit.</param>
/// <param name="Entries">The confirmed mappings.</param>
/// <param name="Cache">The pack's own content-addressed file store.</param>
public sealed record ImportedPack(PackInfo Info, IReadOnlyList<PackEntry> Entries, PackCache Cache)
{
    /// <summary>The file of an entry, or null when it is missing.</summary>
    public string? PathOf(PackEntry entry) => Cache.Contains(entry) ? Cache.PathOf(entry) : null;
}

/// <summary>An image of an import after the image helper normalised it, waiting for the user's confirmation.</summary>
/// <param name="DisplayName">Sanitised source file name; never a path.</param>
/// <param name="Png">The clean PNG returned by the image helper.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of <paramref name="Png"/>.</param>
/// <param name="Proposal">A suggested mapping, never applied without the user.</param>
public sealed record ImportItem(string DisplayName, byte[] Png, int Width, int Height, string Sha256, MappingProposal? Proposal);

/// <summary>How far an import has come.</summary>
/// <param name="Done">Images finished (made clean or skipped).</param>
/// <param name="Total">Images to do.</param>
public readonly record struct ImportProgress(int Done, int Total);

/// <summary>What reading and normalising an archive produced.</summary>
/// <param name="Items">Images ready for mapping.</param>
/// <param name="Skipped">Entries left out, with reasons in plain words.</param>
/// <param name="RejectedReason">Set when the whole archive was refused.</param>
public sealed record ImportAnalysis(IReadOnlyList<ImportItem> Items, IReadOnlyList<SkippedEntry> Skipped, string? RejectedReason)
{
    /// <summary>True when the whole archive was refused.</summary>
    public bool IsRejected => RejectedReason is not null;
}

/// <summary>One image the user confirmed for a mapping key such as <c>character:the-trapper</c>.</summary>
/// <param name="Item">The normalised image.</param>
/// <param name="Key">The confirmed mapping key.</param>
public sealed record ConfirmedMapping(ImportItem Item, string Key);

/// <summary>The outcome of saving an imported pack.</summary>
/// <param name="Pack">The saved pack, or null on failure.</param>
/// <param name="Error">What went wrong, in plain words, when <paramref name="Pack"/> is null.</param>
public sealed record SaveImportResult(ImportedPack? Pack, string? Error);

/// <summary>
/// Stores imported packs as <c>packs/imported/&lt;id&gt;/pack.json</c> (the FR-PACK-01 index format, unsigned: the user made
/// it on this PC) plus <c>files/&lt;sha256&gt;.png</c>. Nothing here is ever uploaded.
/// </summary>
public sealed class ImportedPackStore
{
    /// <summary>The file name of a pack's index.</summary>
    public const string IndexFileName = "pack.json";

    /// <summary>Most characters of a pack name.</summary>
    public const int MaxNameLength = 60;

    /// <summary>Most characters of the creator text.</summary>
    public const int MaxCreatorLength = 80;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly ILocalPackStorage _storage;
    private readonly string _root;

    /// <summary>Creates the store.</summary>
    /// <param name="storage">File operations.</param>
    /// <param name="root">The <c>packs/imported</c> folder.</param>
    public ImportedPackStore(ILocalPackStorage storage, string root)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrEmpty(root);
        _storage = storage;
        _root = root;
    }

    /// <summary>Loads every readable imported pack. A damaged pack is reported in <paramref name="problems"/> and skipped.</summary>
    public IReadOnlyList<ImportedPack> LoadAll(out IReadOnlyList<string> problems)
    {
        var packs = new List<ImportedPack>();
        var issues = new List<string>();
        foreach (var directory in _storage.ListDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(directory);
            var indexPath = Path.Combine(directory, IndexFileName);
            if (!_storage.Exists(indexPath))
            {
                issues.Add($"The imported pack folder '{id}' has no index and was ignored.");
                continue;
            }

            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(_storage.ReadAllBytes(indexPath));
            }
            catch (IOException ex)
            {
                issues.Add($"The imported pack '{id}' could not be read ({ex.GetType().Name}).");
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                issues.Add($"The imported pack '{id}' could not be read ({ex.GetType().Name}).");
                continue;
            }

            var parsed = PackIndexParser.Parse(json);
            if (parsed.Index is not { Packs.Count: 1 } index || index.Packs[0].Id != id)
            {
                issues.Add($"The imported pack '{id}' has a damaged index and was ignored.");
                continue;
            }

            packs.Add(new ImportedPack(index.Packs[0], index.Entries, new PackCache(_storage, directory)));
        }

        problems = issues;
        return packs;
    }

    /// <summary>Picks a pack id for <paramref name="name"/> that no pack uses yet.</summary>
    public string NewId(string name, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var slug = MappingProposer.Slugify(name);
        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-');
        }

        if (slug.Length == 0)
        {
            slug = "imported";
        }

        var id = slug;
        for (var n = 2; taken.Contains(id) || _storage.Exists(Path.Combine(_root, id, IndexFileName)); n++)
        {
            id = $"{slug}-{n}";
        }

        return id;
    }

    /// <summary>
    /// Saves a confirmed import. Every mapping key must be valid and unique, the total must stay within the pack size
    /// limit, and the index must pass the same validation as any pack index. The index file is written last, so a crash
    /// leaves no half-visible pack.
    /// </summary>
    public SaveImportResult Save(string id, string name, string creator, string sourceName, IReadOnlyList<ConfirmedMapping> mappings)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count == 0)
        {
            return new SaveImportResult(null, "Confirm at least one image before saving.");
        }

        var cleanName = Clean(name, MaxNameLength);
        if (cleanName.Length == 0)
        {
            return new SaveImportResult(null, "Give the pack a name.");
        }

        var cleanCreator = Clean(creator, MaxCreatorLength);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<PackEntry>();
        foreach (var mapping in mappings)
        {
            if (!PackKey.TryParse(mapping.Key, out var kind, out _))
            {
                return new SaveImportResult(null, $"'{mapping.Item.DisplayName}' is mapped to something that is not a valid image slot.");
            }

            if (!seen.Add(mapping.Key))
            {
                return new SaveImportResult(null, $"Two images are mapped to {mapping.Key}. Each slot takes one image.");
            }

            var entryId = EntryId(id, mapping.Key);
            entries.Add(new PackEntry(
                entryId, id, kind, mapping.Key, mapping.Item.Sha256, mapping.Item.Png.Length, mapping.Item.Width, mapping.Item.Height,
                PackImageFormat.Png, null));
        }

        var info = new PackInfo(
            id, cleanName, 1, cleanCreator.Length == 0 ? "Unknown creator" : cleanCreator,
            "Imported by you for your own preview. Never uploaded; Discord does not show it.",
            Clean($"Source: {sourceName}", 200));
        var index = new PackIndex([info], entries);
        var errors = PackIndexParser.Validate(index);
        if (errors.Count > 0)
        {
            return new SaveImportResult(null, errors[0]);
        }

        var directory = Path.Combine(_root, id);
        var cache = new PackCache(_storage, directory);
        try
        {
            foreach (var mapping in mappings)
            {
                var entry = entries.First(e => e.Key == mapping.Key);
                var added = cache.Add(entry, mapping.Item.Png);
                if (added is not (PackFileAddResult.Stored or PackFileAddResult.AlreadyPresent))
                {
                    _storage.DeleteDirectory(directory);
                    return new SaveImportResult(null, "An image could not be stored safely. Nothing was saved.");
                }
            }

            WriteIndex(directory, index);
        }
        catch (IOException ex)
        {
            _storage.DeleteDirectory(directory);
            return new SaveImportResult(null, $"The pack could not be saved ({ex.GetType().Name}). Check free disk space and try again.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _storage.DeleteDirectory(directory);
            return new SaveImportResult(null, $"The pack could not be saved ({ex.GetType().Name}). The app data folder is not writable.");
        }

        return new SaveImportResult(new ImportedPack(info, entries, cache), null);
    }

    /// <summary>Deletes an imported pack and its files.</summary>
    public void Remove(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!PackKey.IsSlug(id))
        {
            throw new ArgumentException("Not a pack id.", nameof(id));
        }

        _storage.DeleteDirectory(Path.Combine(_root, id));
    }

    /// <summary>Computes the lowercase hex SHA-256 of <paramref name="data"/>.</summary>
    public static string Sha256Of(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private void WriteIndex(string directory, PackIndex index)
    {
        var document = new
        {
            packs = index.Packs.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                version = p.Version,
                author = p.Author,
                licenceNote = p.LicenceNote,
                credit = p.Credit,
            }),
            entries = index.Entries.Select(e => new
            {
                id = e.Id,
                pack = e.PackId,
                kind = PackKey.KindTag(e.Kind),
                key = e.Key,
                sha256 = e.Sha256,
                size = e.Size,
                width = e.Width,
                height = e.Height,
                format = "png",
                licensed = (object?)null,
            }),
        };
        var path = Path.Combine(directory, IndexFileName);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        _storage.WriteAllBytesDurable(temp, JsonSerializer.SerializeToUtf8Bytes(document, s_json));
        _storage.Move(temp, path);
    }

    private static string EntryId(string packId, string key)
    {
        var id = $"{packId}-{key.Replace(':', '-')}";
        return id.Length <= PackKey.MaxSlugLength ? id : id[..PackKey.MaxSlugLength].TrimEnd('-');
    }

    private static string Clean(string value, int max)
    {
        var chars = (value ?? string.Empty).Where(c => !char.IsControl(c)).ToArray();
        var text = new string(chars).Trim();
        return text.Length <= max ? text : text[..max].TrimEnd();
    }
}
