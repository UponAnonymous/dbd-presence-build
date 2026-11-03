using System.Text;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Packs.Cache;

namespace DbdPresence.Online.Updates;

/// <summary>
/// The highest release feed version this install has verified, per channel (FR-ONL-10, ADR-0012 rule 3). It is raised as soon as a feed's
/// signature and payload check out, whether or not a package is then downloaded, so a later, validly signed but older feed can never be
/// accepted (rollback). It is kept apart from the general version store so that losing that store cannot lower it.
/// Each floor remembers the key that signed the feed, and <see cref="Forget"/> drops the floors of revoked keys (TM-01), so a floor set by a stolen
/// release key does not outlive the key.
/// </summary>
public interface IReleaseFloor
{
    /// <summary>Gets a value indicating whether the stored floor could not be read; updates then stay off (fail closed).</summary>
    bool IsDamaged { get; }

    /// <summary>The highest verified feed version for <paramref name="channel"/>, or null when none was seen.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The floor.</returns>
    long? Highest(string channel);

    /// <summary>Raises the floor for <paramref name="channel"/> to <paramref name="version"/> and keeps it on disk (atomically).</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="version">The verified feed version.</param>
    /// <param name="kid">The key that signed the feed.</param>
    /// <returns>False when it could not be kept; the caller then refuses the feed (fail closed).</returns>
    bool Raise(string channel, long version, string? kid);

    /// <summary>Drops the floors that were raised by one of <paramref name="revokedKids"/>; the highest remaining floor applies. Idempotent.</summary>
    /// <param name="revokedKids">Key ids that a key replacement removed.</param>
    void Forget(IReadOnlyCollection<string> revokedKids);
}

/// <summary>
/// The floor in one small JSON file, replaced atomically: <c>{ "schemaVersion": 1, "channels": { "stable": 123 }, "kids": [ { "channel", "kid", "version" } ] }</c>.
/// <c>channels</c> holds floors of unknown origin (files written before TM-01), which are never dropped; <c>kids</c> is optional when reading.
/// </summary>
public sealed class FileReleaseFloor : IReleaseFloor
{
    private const int MaxFileBytes = 16 * 1024;
    private readonly IPackFileStore _files;
    private readonly string _path;
    private readonly IOnlineLog _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Channel, string Kid), long> _highest = [];

    /// <summary>Initializes a new instance of the <see cref="FileReleaseFloor"/> class and reads the stored floor.</summary>
    /// <param name="files">File access.</param>
    /// <param name="path">The floor file.</param>
    /// <param name="log">Diagnostics.</param>
    public FileReleaseFloor(IPackFileStore files, string path, IOnlineLog log)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(log);
        _files = files;
        _path = path;
        _log = log;
        Load();
    }

    /// <inheritdoc />
    public bool IsDamaged { get; private set; }

    /// <inheritdoc />
    public long? Highest(string channel)
    {
        lock (_lock)
        {
            return HighestOf(_highest, channel);
        }
    }

    private static long? HighestOf(Dictionary<(string Channel, string Kid), long> all, string channel)
    {
        long? best = null;
        foreach (var (key, value) in all)
        {
            if (key.Channel == channel && (best is null || value > best))
            {
                best = value;
            }
        }

        return best;
    }

    /// <inheritdoc />
    public bool Raise(string channel, long version, string? kid)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        lock (_lock)
        {
            if (IsDamaged)
            {
                return false;
            }

            var key = (channel, kid ?? string.Empty);
            if (_highest.TryGetValue(key, out var current) && current >= version)
            {
                return true;
            }

            var next = new Dictionary<(string Channel, string Kid), long>(_highest) { [key] = version };
            try
            {
                _files.WriteAllBytesDurable(_path + ".tmp", Serialize(next));
                _files.Move(_path + ".tmp", _path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning("The update version floor could not be saved (" + ex.GetType().Name + "); the update is not used.");
                return false;
            }

            _highest[key] = version;
            return true;
        }
    }

    /// <inheritdoc />
    public void Forget(IReadOnlyCollection<string> revokedKids)
    {
        ArgumentNullException.ThrowIfNull(revokedKids);
        lock (_lock)
        {
            var next = _highest.Where(e => e.Key.Kid.Length == 0 || !revokedKids.Contains(e.Key.Kid)).ToDictionary(e => e.Key, e => e.Value);
            if (next.Count == _highest.Count)
            {
                return;
            }

            // Dropped in memory first; if the file cannot be written the next start drops them again (the key set is replaced before this runs).
            _highest.Clear();
            foreach (var (key, value) in next)
            {
                _highest[key] = value;
            }

            try
            {
                _files.WriteAllBytesDurable(_path + ".tmp", Serialize(next));
                _files.Move(_path + ".tmp", _path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning("The update version floor of a revoked key could not be removed from the file (" + ex.GetType().Name + "); it is ignored until the next start.");
            }
        }
    }

    private static byte[] Serialize(Dictionary<(string Channel, string Kid), long> versions)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartObject("channels");
            foreach (var (key, version) in versions.Where(v => v.Key.Kid.Length == 0).OrderBy(v => v.Key.Channel, StringComparer.Ordinal))
            {
                writer.WriteNumber(key.Channel, version);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("kids");
            foreach (var (key, version) in versions.Where(v => v.Key.Kid.Length > 0).OrderBy(v => v.Key.Channel, StringComparer.Ordinal).ThenBy(v => v.Key.Kid, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("channel", key.Channel);
                writer.WriteString("kid", key.Kid);
                writer.WriteNumber("version", version);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private void Load()
    {
        try
        {
            if (!_files.Exists(_path))
            {
                return;
            }

            if (_files.Length(_path) > MaxFileBytes)
            {
                Damaged("it is too large");
                return;
            }

            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(_files.ReadAllBytes(_path)), new JsonDocumentOptions { MaxDepth = 5, AllowDuplicateProperties = false });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1
                || !root.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Object)
            {
                Damaged("it has the wrong shape");
                return;
            }

            foreach (var channel in channels.EnumerateObject())
            {
                if (channel.Value.ValueKind != JsonValueKind.Number || !channel.Value.TryGetInt64(out var version) || version < 0)
                {
                    Damaged("a version is not a whole number");
                    return;
                }

                _highest[(channel.Name, string.Empty)] = version;
            }

            if (root.TryGetProperty("kids", out var kids))
            {
                if (kids.ValueKind != JsonValueKind.Array)
                {
                    Damaged("kids is not a list");
                    return;
                }

                foreach (var item in kids.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("channel", out var kidChannel) || kidChannel.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(kid.GetString())
                        || !item.TryGetProperty("version", out var kidVersion) || kidVersion.ValueKind != JsonValueKind.Number || !kidVersion.TryGetInt64(out var value) || value < 0)
                    {
                        Damaged("a kids entry has the wrong shape");
                        return;
                    }

                    var key = (kidChannel.GetString()!, kid.GetString()!);
                    _highest[key] = _highest.TryGetValue(key, out var seen) && seen > value ? seen : value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            Damaged("it could not be read (" + ex.GetType().Name + ")");
        }
    }

    private void Damaged(string why)
    {
        _highest.Clear();
        IsDamaged = true;
        _log.Warning($"The update version floor file is damaged: {why}. Updates stay off so that an older release cannot be installed. Deleting release-floor.json in the app's online data folder resets it.");
    }
}
