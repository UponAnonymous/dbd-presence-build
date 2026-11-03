using System.Text.Json;

namespace DbdPresence.LocalCi;

/// <summary>A cached file does not match the checksum recorded when it was verified, or was never recorded. It is not used.</summary>
public sealed class IntegrityException : Exception
{
    public IntegrityException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The checksums of the files the loop will start from (runner packages, the Linux base image), kept in a file inside the data folder that only
/// Administrators and the system can write (security review 2026-10-30, H1). A file is recorded when it has been verified against the publisher's checksum
/// (or built by this program); <see cref="OpenVerifiedAsync"/> hashes it again immediately before every use and refuses it when anything differs.
/// </summary>
public sealed class IntegrityStore
{
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    private readonly string _file;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Length, DateTime Written, string Recorded, bool Intact)> _verdicts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="stateFile">The JSON file that holds the checksums (inside the data folder).</param>
    /// <param name="root">The data folder; recorded paths are kept relative to it.</param>
    public IntegrityStore(string stateFile, string root)
    {
        _file = stateFile;
        _root = PathTrust.Normalize(root);
    }

    /// <summary>Remembers that <paramref name="path"/> has the SHA-256 <paramref name="sha256"/>.</summary>
    public void Record(string path, string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        var key = KeyFor(path);
        lock (_gate)
        {
            var files = ReadLocked();
            files[key] = sha256.ToLowerInvariant();
            WriteLocked(files);
        }
    }

    /// <summary>The recorded SHA-256 of <paramref name="path"/>, or null when it was never recorded.</summary>
    public string? Recorded(string path)
    {
        var key = KeyFor(path);
        lock (_gate)
        {
            return ReadLocked().TryGetValue(key, out var hash) ? hash : null;
        }
    }

    /// <summary>Drops the record of <paramref name="path"/> (the file is gone or no longer trusted).</summary>
    public void Forget(string path)
    {
        var key = KeyFor(path);
        lock (_gate)
        {
            var files = ReadLocked();
            if (files.Remove(key))
            {
                WriteLocked(files);
            }
        }
    }

    /// <summary>Drops the records of everything inside <paramref name="directory"/> (the folder was deleted).</summary>
    public void ForgetUnder(string directory)
    {
        var prefix = KeyFor(directory) + "/";
        lock (_gate)
        {
            var files = ReadLocked();
            var gone = files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (gone.Count == 0)
            {
                return;
            }

            foreach (var key in gone)
            {
                files.Remove(key);
            }

            WriteLocked(files);
        }
    }

    /// <summary>
    /// Opens the file for reading with other writers locked out, hashes it, compares with the record and returns the same open stream, rewound.
    /// Whoever uses the returned stream reads exactly the bytes that were checked.
    /// </summary>
    /// <exception cref="IntegrityException">The file was never recorded or differs from its record.</exception>
    public async Task<FileStream> OpenVerifiedAsync(string path, CancellationToken cancellationToken)
    {
        var expected = Recorded(path) ?? throw new IntegrityException($"{Path.GetFileName(path)} has no recorded checksum, so it is not used.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var actual = await Hashing.Sha256OfStreamAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!Hashing.SameHash(actual, expected))
            {
                throw new IntegrityException($"{Path.GetFileName(path)} does not match the checksum recorded when it was checked. It was not used.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// True when the file exists and its SHA-256 equals its record. Hashing a file of a gigabyte takes seconds, so the verdict is remembered for as long as the file's size, time stamp and record
    /// stay the same; every use of the file still hashes it again (<see cref="OpenVerifiedAsync"/>), so this is only for deciding early that something is not ready or is due to be made again.
    /// </summary>
    public bool IsIntact(string path)
    {
        if (!File.Exists(path) || Recorded(path) is not { } recorded)
        {
            return false;
        }

        var info = new FileInfo(path);
        var key = KeyFor(path);
        lock (_gate)
        {
            if (_verdicts.TryGetValue(key, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc && string.Equals(known.Recorded, recorded, StringComparison.Ordinal))
            {
                return known.Intact;
            }
        }

        string actual;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
        {
            actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }

        var intact = Hashing.SameHash(actual, recorded);
        lock (_gate)
        {
            _verdicts[key] = (info.Length, info.LastWriteTimeUtc, recorded, intact);
        }

        return intact;
    }

    /// <summary>True when the file exists and still matches its record (hashes the whole file).</summary>
    public async Task<bool> IsIntactAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || Recorded(path) is not { } expected)
        {
            return false;
        }

        var actual = await Hashing.Sha256OfFileAsync(path, cancellationToken).ConfigureAwait(false);
        return Hashing.SameHash(actual, expected);
    }

    private string KeyFor(string path)
    {
        var full = PathTrust.Normalize(path);
        if (!PathTrust.IsSameOrInside(_root, full))
        {
            throw new IntegrityException($"{full} is outside the data folder, so no checksum is kept for it.");
        }

        return Path.GetRelativePath(_root, full).Replace('\\', '/');
    }

    private Dictionary<string, string> ReadLocked()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_file))
        {
            return files;
        }

        try
        {
            var document = JsonSerializer.Deserialize<IntegrityDocument>(File.ReadAllText(_file));
            foreach (var (key, value) in document?.Files ?? [])
            {
                files[key] = value;
            }
        }
        catch (JsonException)
        {
            // A damaged record keeps nothing: every file then counts as unverified and is downloaded again.
            files.Clear();
        }

        return files;
    }

    private void WriteLocked(Dictionary<string, string> files)
    {
        DataFolder.RequireExists(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new IntegrityDocument { Files = files }, s_options));
        File.Move(temporary, _file, overwrite: true);
    }

    private sealed class IntegrityDocument
    {
        public Dictionary<string, string> Files { get; set; } = [];
    }
}
