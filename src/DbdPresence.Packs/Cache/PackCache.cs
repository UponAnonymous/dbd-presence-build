using System.Security.Cryptography;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Packs.Cache;

/// <summary>Answers whether a pack file is available locally.</summary>
public interface IPackContentCache
{
    /// <summary>True when the entry's file is stored with the expected size.</summary>
    bool Contains(PackEntry entry);
}

/// <summary>Result of offering a downloaded file to the cache.</summary>
public enum PackFileAddResult
{
    /// <summary>The file was verified and stored.</summary>
    Stored,

    /// <summary>A verified copy was already stored.</summary>
    AlreadyPresent,

    /// <summary>Storing the file would take the cache over <see cref="PackCache.MaxCacheBytes"/>; nothing was stored.</summary>
    CacheFull,

    /// <summary>The byte count differs from the signed index; nothing was stored.</summary>
    SizeMismatch,

    /// <summary>The SHA-256 differs from the signed index; nothing was stored.</summary>
    HashMismatch,
}

/// <summary>
/// Content-addressed pack file cache (FR-PACK-01, PLAN_V2 §6.3). Layout under the root:
/// <c>files/&lt;sha256&gt;.&lt;ext&gt;</c> and <c>index.json</c> (the last accepted signed index document).
/// Every file is verified for size and SHA-256 before it is accepted; the index is replaced atomically
/// (write a temporary file, then move it over the old one); files no longer referenced are removed after the swap.
/// </summary>
public sealed class PackCache : IPackContentCache
{
    /// <summary>Largest total size of cached pack files in bytes (ADR-0013).</summary>
    public const long MaxCacheBytes = 200L * 1024 * 1024;

    private const string TempSuffix = ".tmp";

    private readonly IPackFileStore _store;
    private readonly string _filesDirectory;
    private readonly string _indexPath;
    private readonly long _maxCacheBytes;

    /// <summary>Creates a cache rooted at <paramref name="root"/>.</summary>
    /// <param name="store">File operations.</param>
    /// <param name="root">Cache root directory.</param>
    /// <param name="maxCacheBytes">Total size cap; the default is <see cref="MaxCacheBytes"/>. Lower values exist for tests.</param>
    public PackCache(IPackFileStore store, string root, long maxCacheBytes = MaxCacheBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCacheBytes, 1);
        _maxCacheBytes = maxCacheBytes;
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(root);
        _store = store;
        _filesDirectory = Path.Combine(root, "files");
        _indexPath = Path.Combine(root, "index.json");
        _store.CreateDirectory(_filesDirectory);
    }

    /// <summary>
    /// The hashes (with one entry each) that <paramref name="next"/> needs and <paramref name="previous"/> did not
    /// already list: the download delta between two indexes. Pure; does not look at the disk.
    /// </summary>
    public static IReadOnlyList<PackEntry> Delta(PackIndex? previous, PackIndex next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var known = previous is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : previous.Entries.Select(e => e.Sha256).ToHashSet(StringComparer.Ordinal);
        return DistinctByHash(next.Entries.Where(e => !known.Contains(e.Sha256)));
    }

    /// <summary>
    /// Licensed entries that <paramref name="previous"/> offered and <paramref name="next"/> no longer lists
    /// (FR-PACK-04): these have been withdrawn and fall back to the owner's fallback pack, or to no image.
    /// </summary>
    public static IReadOnlyList<PackEntry> WithdrawnLicensed(PackIndex previous, PackIndex next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        var remaining = next.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return [.. previous.Entries.Where(e => e.Licensed is not null && !remaining.Contains(e.Id))];
    }

    /// <summary>The entries of <paramref name="index"/> whose files are not (correctly) in the cache, one per hash.</summary>
    /// <exception cref="PackCacheLimitException">The index as a whole needs more than <see cref="MaxCacheBytes"/>.</exception>
    public IReadOnlyList<PackEntry> Missing(PackIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!Fits(index))
        {
            throw new PackCacheLimitException(_maxCacheBytes);
        }

        return DistinctByHash(index.Entries.Where(e => !Contains(e)));
    }

    /// <summary>True when every distinct file of <paramref name="index"/> together fits under <see cref="MaxCacheBytes"/>.</summary>
    public bool Fits(PackIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return DistinctByHash(index.Entries).Sum(e => e.Size) <= _maxCacheBytes;
    }

    /// <inheritdoc />
    public bool Contains(PackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = PathOf(entry);
        return _store.Exists(path) && _store.Length(path) == entry.Size;
    }

    /// <summary>Full integrity check: re-hashes the stored file.</summary>
    public bool IsIntact(PackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = PathOf(entry);
        return _store.Exists(path) && Matches(entry, _store.ReadAllBytes(path)) == PackFileAddResult.Stored;
    }

    /// <summary>The cache path of an entry's file (it may not exist).</summary>
    public string PathOf(PackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsCacheFileName(entry.CacheFileName))
        {
            throw new ArgumentException("The entry does not map to a content-addressed cache file name.", nameof(entry));
        }

        return Path.Combine(_filesDirectory, entry.CacheFileName);
    }

    /// <summary>Verifies <paramref name="content"/> against the entry's size and SHA-256, then stores it atomically.</summary>
    public PackFileAddResult Add(PackEntry entry, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = PathOf(entry);
        var verdict = Matches(entry, content);
        if (verdict != PackFileAddResult.Stored)
        {
            return verdict;
        }

        if (_store.Exists(path) && _store.Length(path) == entry.Size)
        {
            return PackFileAddResult.AlreadyPresent;
        }

        if (CachedBytes() + content.Length > _maxCacheBytes)
        {
            return PackFileAddResult.CacheFull;
        }

        var temp = $"{path}.{Guid.NewGuid():N}{TempSuffix}";
        _store.WriteAllBytesDurable(temp, content);
        _store.Move(temp, path);
        return PackFileAddResult.Stored;
    }

    /// <summary>The last committed signed index document bytes, or null if none.</summary>
    public byte[]? ReadIndexDocument() => _store.Exists(_indexPath) ? _store.ReadAllBytes(_indexPath) : null;

    /// <summary>
    /// Atomically replaces the stored index document, removes cached files that <paramref name="index"/> no longer references, and only
    /// then records the envelope's version as the new rollback floor. Callers verify the document, parse the payload and download every
    /// missing file first, so a failure earlier keeps the previous index, files and version floor untouched.
    /// </summary>
    /// <returns>The number of files removed.</returns>
    public int CommitIndex(ReadOnlySpan<byte> signedDocument, PackIndex index, SignedDocumentVerifier verifier, SignedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(envelope);
        var temp = $"{_indexPath}.{Guid.NewGuid():N}{TempSuffix}";
        _store.WriteAllBytesDurable(temp, signedDocument);
        _store.Move(temp, _indexPath);
        verifier.CommitVersion(envelope);
        return CollectGarbage(index);
    }

    /// <summary>
    /// Startup: raises the rollback floor from the index document already on disk (if any), so a missing or damaged version file cannot
    /// let an older signed index back in.
    /// </summary>
    /// <returns>Null when there is no cached index; otherwise the outcome of reading it.</returns>
    public VerificationOutcome? SeedVersionFloor(SignedDocumentVerifier verifier, string channel)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentException.ThrowIfNullOrEmpty(channel);
        return ReadIndexDocument() is { } document ? verifier.SeedFromCachedDocument(document, SignedPurposes.PackIndex, channel) : null;
    }

    /// <summary>
    /// Deletes leftover temporary files from interrupted writes. Call once at startup, before any write: garbage collection never touches
    /// <c>.tmp</c> files because one may belong to a write in progress.
    /// </summary>
    /// <returns>The number of files removed.</returns>
    public int RemoveStaleTempFiles()
    {
        var removed = 0;
        foreach (var file in _store.ListFiles(_filesDirectory))
        {
            if (IsTempFileName(Path.GetFileName(file)))
            {
                _store.Delete(file);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Deletes cache files (never temporary files; see <see cref="RemoveStaleTempFiles"/>) whose hash is not referenced by <paramref name="index"/>.</summary>
    /// <returns>The number of files removed.</returns>
    public int CollectGarbage(PackIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var wanted = index.Entries.Select(e => e.CacheFileName).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var file in _store.ListFiles(_filesDirectory))
        {
            var name = Path.GetFileName(file);
            if (wanted.Contains(name) || !IsCacheFileName(name) || name.EndsWith(TempSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            _store.Delete(file);
            removed++;
        }

        return removed;
    }

    private static PackFileAddResult Matches(PackEntry entry, ReadOnlySpan<byte> content)
    {
        if (content.Length != entry.Size)
        {
            return PackFileAddResult.SizeMismatch;
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        return string.Equals(actual, entry.Sha256, StringComparison.Ordinal) ? PackFileAddResult.Stored : PackFileAddResult.HashMismatch;
    }

    private long CachedBytes() => _store.ListFiles(_filesDirectory).Sum(f => _store.Length(f));

    /// <summary>True for <c>&lt;64 hex&gt;.png|jpg</c>; other files are never touched.</summary>
    private static bool IsCacheFileName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot != 64 || name[(dot + 1)..] is not ("png" or "jpg"))
        {
            return false;
        }

        return IsLowerHex(name.AsSpan(0, 64));
    }

    /// <summary>True for <c>&lt;cache file name&gt;.&lt;32 hex&gt;.tmp</c>, the per-write temporary names.</summary>
    private static bool IsTempFileName(string name)
    {
        if (!name.EndsWith(TempSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var core = name[..^TempSuffix.Length];
        var dot = core.LastIndexOf('.');
        return dot > 0 && IsLowerHex(core.AsSpan(dot + 1)) && core.Length - dot - 1 == 32 && IsCacheFileName(core[..dot]);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static List<PackEntry> DistinctByHash(IEnumerable<PackEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. entries.Where(e => seen.Add(e.Sha256))];
    }
}

/// <summary>Raised when a signed index asks for more cache space than <see cref="PackCache.MaxCacheBytes"/>.</summary>
public sealed class PackCacheLimitException : InvalidOperationException
{
    /// <summary>Creates the exception for the given limit.</summary>
    public PackCacheLimitException(long limitBytes)
        : base($"The pack index needs more than {limitBytes} bytes of cache.")
    {
        LimitBytes = limitBytes;
    }

    /// <summary>The cache limit in bytes.</summary>
    public long LimitBytes { get; }
}
