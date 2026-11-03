namespace DbdPresence.LocalCi;

/// <summary>
/// Keeps the newest actions/runner packages (linux-x64 tar.gz; decision D41: no Windows package) in <c>ProgramData\DbdLocalCi\runner\&lt;version&gt;</c>.
/// Each package is checked before it is moved into place: its address must be the official release file of that version, its SHA-256 must equal the one in the release
/// notes and, when GitHub gives one, the release asset's own digest; a mismatch or a missing marker refuses it. The verified checksum is recorded in the data folder's
/// integrity file, and the package is hashed again before every use. A version older than one already cached is refused (no downgrade). Only the newest two are kept.
/// Entries that are not owned by Administrators or the system, or that are links, are ignored (security review 2026-10-30, H1, M6).
/// </summary>
public sealed class RunnerCache
{
    private const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    private readonly IGitHubApi _api;
    private readonly IDownloads _downloads;
    private readonly LocalCiPaths _paths;
    private readonly ICiLog _log;
    private readonly Retry _retry;
    private readonly IntegrityStore _integrity;
    private readonly IProtectedStorage _storage;
    private readonly IDiskSpace _disk;
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    public RunnerCache(IGitHubApi api, IDownloads downloads, LocalCiPaths paths, ICiLog log, Retry retry, IntegrityStore integrity, IProtectedStorage storage, IDiskSpace disk)
    {
        _api = api;
        _downloads = downloads;
        _paths = paths;
        _log = log;
        _retry = retry;
        _integrity = integrity;
        _storage = storage;
        _disk = disk;
    }

    /// <summary>Fetches the latest release if it is not cached yet; returns its version.</summary>
    public async Task<string> RefreshAsync(CancellationToken cancellationToken)
    {
        var release = await _retry.RunAsync(ct => _api.GetLatestRunnerReleaseAsync(ct), "Reading the latest runner release", cancellationToken).ConfigureAwait(false);
        var linuxSha = RunnerReleaseNotes.ShaFor(release.Notes, RunnerReleaseNotes.LinuxPlatform);
        var version = RunnerAssetPolicy.ParseVersion(release.Version)
                      ?? throw new DownloadVerificationException("The runner release has a version that is not three numbers; refusing to use it.");
        var problems = RunnerAssetPolicy.Problems(release, linuxSha);
        if (problems.Count > 0)
        {
            throw new DownloadVerificationException("Refusing the runner release: " + string.Join("; ", problems) + ".");
        }

        if (CachedVersions().FirstOrDefault() is { } newest && version < newest)
        {
            throw new DownloadVerificationException($"GitHub's latest runner release ({release.Version}) is older than the one already cached ({newest}); refusing to go back to an older runner.");
        }

        if (_disk.FreeBytes(_paths.RunnerCache) < MinimumFreeBytes)
        {
            throw new IOException("There is not enough free disk space to download the runner program (about 2 GB are needed).");
        }

        DataFolder.CreateChild(_paths.RunnerCache);
        DataFolder.CreateChild(_paths.RunnerVersionDirectory(release.Version));
        await EnsureAsync(release.LinuxPackage, _paths.LinuxPackage(release.Version), linuxSha, cancellationToken).ConfigureAwait(false);
        Prune();
        return release.Version;
    }

    /// <summary>The newest cached Linux package that can be trusted, or null when there is none yet.</summary>
    public string? LatestPackage()
    {
        foreach (var version in CachedVersions())
        {
            var path = _paths.LinuxPackage(version.ToString());
            if (!File.Exists(path))
            {
                continue;
            }

            if (Untrusted(path) is { } reason)
            {
                WarnOnce(path, $"Ignoring {path}: {reason}");
                continue;
            }

            return path;
        }

        return null;
    }

    public void Prune()
    {
        foreach (var old in CachedVersions().Skip(LocalCiDefaults.RunnerVersionsKept))
        {
            var directory = _paths.RunnerVersionDirectory(old.ToString());
            try
            {
                SafeTree.Delete(directory);
                _integrity.ForgetUnder(directory);
                _log.Info($"Removed the old runner version {old}.");
            }
            catch (IOException ex)
            {
                _log.Warn($"Could not remove the old runner version {old}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn($"Could not remove the old runner version {old}: {ex.Message}");
            }
        }
    }

    /// <summary>The reason a cached file is not used, or null when it can be trusted.</summary>
    private string? Untrusted(string path)
    {
        var problems = _storage.CheckInsideDataFolder(path);
        if (problems.Count > 0)
        {
            return problems[0];
        }

        return _integrity.Recorded(path) is null ? "no checksum was recorded for it" : null;
    }

    /// <summary>The cached versions that can be trusted, newest first. A folder that is a link, has another owner or is not a version number is left out and never deleted.</summary>
    private List<Version> CachedVersions()
    {
        if (!Directory.Exists(_paths.RunnerCache))
        {
            return [];
        }

        var versions = new List<Version>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.RunnerCache))
        {
            if (RunnerAssetPolicy.ParseVersion(Path.GetFileName(directory)) is not { } version)
            {
                continue;
            }

            var problems = _storage.CheckInsideDataFolder(directory);
            if (problems.Count > 0)
            {
                WarnOnce(directory, $"Ignoring {directory}: {problems[0]}");
                continue;
            }

            versions.Add(version);
        }

        return [.. versions.OrderByDescending(v => v)];
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key))
        {
            _log.Warn(message);
        }
    }

    private async Task EnsureAsync(Uri address, string finalPath, string expectedSha, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(finalPath);
        if (File.Exists(finalPath))
        {
            if (_integrity.Recorded(finalPath) is { } recorded && Hashing.SameHash(recorded, expectedSha)
                && await _integrity.IsIntactAsync(finalPath, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            _log.Warn($"{name} in the cache does not match its checksum; it is downloaded again.");
            _integrity.Forget(finalPath);
            SafeTree.Delete(finalPath);
        }

        var partial = finalPath + ".part";
        try
        {
            await _retry.RunAsync(
                ct => _downloads.DownloadToFileAsync(address, partial, LocalCiDefaults.MaximumRunnerPackageBytes, ct),
                $"Downloading {name}",
                cancellationToken).ConfigureAwait(false);

            var actual = await Hashing.Sha256OfFileAsync(partial, cancellationToken).ConfigureAwait(false);
            if (!Hashing.SameHash(actual, expectedSha))
            {
                throw new DownloadVerificationException($"{name} does not match the checksum in the release notes; it was deleted and not used.");
            }

            File.Move(partial, finalPath, overwrite: true);
            _integrity.Record(finalPath, actual);
            _log.Info($"Runner package {name} downloaded and verified.");
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}
