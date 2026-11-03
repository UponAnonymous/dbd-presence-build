using System.Text.Json;
using DbdPresence.Packs.Cache;

namespace DbdPresence.Packs.Signing;

/// <summary>
/// Persistent <see cref="IVersionStore"/> (ADR-0012 rollback protection across restarts). The highest version per (purpose, channel) is kept
/// in one small JSON file, replaced atomically (write a temporary file, then move it over the old one) through <see cref="IPackFileStore"/>.
/// An unreadable file is treated as empty and reported through <see cref="LoadFailure"/>; the caller then re-seeds the floor from the cached
/// signed index (<see cref="SignedDocumentVerifier.SeedFromCachedDocument"/>).
/// Each floor remembers the <c>kid</c> that signed it (threat-model review 2026-10-25, TM-01), so a key replacement that revokes the key can
/// drop the floors that key set. Entries written before this change have no <c>kid</c>; they stay as they are and are never dropped.
/// </summary>
public sealed class FileVersionStore : IVersionStore
{
    private const int MaxFileBytes = 64 * 1024;
    private const string TempSuffix = ".tmp";

    private readonly IPackFileStore _files;
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Purpose, string Channel, string Kid), long> _highest = [];

    /// <summary>Opens (or starts) the store at <paramref name="path"/>.</summary>
    public FileVersionStore(IPackFileStore files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _files = files;
        _path = path;
        LoadFailure = Load();
    }

    /// <summary>Null when the file loaded (or did not exist); otherwise the exception type name of why it could not be read.</summary>
    public string? LoadFailure { get; }

    /// <inheritdoc />
    public long? GetHighest(string purpose, string channel)
    {
        lock (_gate)
        {
            return HighestOf(_highest, purpose, channel);
        }
    }

    /// <inheritdoc />
    public void RecordHighest(string purpose, string channel, long version, string? kid)
    {
        var key = (purpose, channel, kid ?? string.Empty);
        lock (_gate)
        {
            if (_highest.TryGetValue(key, out var current) && version <= current)
            {
                return;
            }

            var next = new Dictionary<(string Purpose, string Channel, string Kid), long>(_highest) { [key] = version };
            Persist(next);
            _highest[key] = version;
        }
    }

    /// <inheritdoc />
    public void Forget(IReadOnlyCollection<string> revokedKids)
    {
        ArgumentNullException.ThrowIfNull(revokedKids);
        lock (_gate)
        {
            var next = _highest.Where(e => e.Key.Kid.Length == 0 || !revokedKids.Contains(e.Key.Kid)).ToDictionary(e => e.Key, e => e.Value);
            if (next.Count == _highest.Count)
            {
                return;
            }

            // Dropped in memory first, so the revoked floor stops counting even when the file cannot be written; the next start drops it again.
            _highest.Clear();
            foreach (var (key, value) in next)
            {
                _highest[key] = value;
            }

            Persist(next);
        }
    }

    private static long? HighestOf(Dictionary<(string Purpose, string Channel, string Kid), long> all, string purpose, string channel)
    {
        long? best = null;
        foreach (var (key, value) in all)
        {
            if (key.Purpose == purpose && key.Channel == channel && (best is null || value > best))
            {
                best = value;
            }
        }

        return best;
    }

    private void Persist(Dictionary<(string Purpose, string Channel, string Kid), long> versions)
    {
        var entries = versions
            .OrderBy(e => e.Key.Purpose, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Channel, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Kid, StringComparer.Ordinal)
            .Select(e => ToEntry(e.Key, e.Value));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["versions"] = entries });
        var temp = _path + TempSuffix;
        _files.WriteAllBytesDurable(temp, bytes);
        _files.Move(temp, _path);
    }

    private static Dictionary<string, object> ToEntry((string Purpose, string Channel, string Kid) key, long version)
    {
        var entry = new Dictionary<string, object> { ["purpose"] = key.Purpose, ["channel"] = key.Channel, ["version"] = version };
        if (key.Kid.Length > 0)
        {
            entry["kid"] = key.Kid;
        }

        return entry;
    }

    private string? Load()
    {
        if (!_files.Exists(_path))
        {
            return null;
        }

        try
        {
            if (_files.Length(_path) > MaxFileBytes)
            {
                return "TooLarge";
            }

            using var json = JsonDocument.Parse(_files.ReadAllBytes(_path));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("versions", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return "BadShape";
            }

            var loaded = new Dictionary<(string Purpose, string Channel, string Kid), long>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("purpose", out var purpose) || purpose.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("version", out var version) || !version.TryGetInt64(out var value) || value < 0)
                {
                    return "BadShape";
                }

                // "kid" is optional: files written before TM-01 have none, and such an entry is kept as a floor that no revocation drops.
                var kid = string.Empty;
                if (item.TryGetProperty("kid", out var kidElement))
                {
                    if (kidElement.ValueKind != JsonValueKind.String)
                    {
                        return "BadShape";
                    }

                    kid = kidElement.GetString()!;
                }

                var key = (purpose.GetString()!, channel.GetString()!, kid);
                loaded[key] = loaded.TryGetValue(key, out var seen) && seen > value ? seen : value;
            }

            foreach (var (key, value) in loaded)
            {
                _highest[key] = value;
            }

            return null;
        }
        catch (JsonException ex)
        {
            return ex.GetType().Name;
        }
        catch (InvalidOperationException ex)
        {
            return ex.GetType().Name;
        }
        catch (IOException ex)
        {
            return ex.GetType().Name;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ex.GetType().Name;
        }
    }
}
