using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// Makes the Linux base image that every Linux job starts from: the pinned Ubuntu 24.04 WSL image (<see cref="UbuntuImage.Pinned"/>, downloaded once, checked against its pinned SHA-256, hashed again
/// before each import), plus the tools the workflows call, a non-root <c>runner</c> user with no administrator rights, and no Windows drives, Windows programs or GPU inside (automount, interop and GPU
/// off). It is rebuilt weekly from the same pinned image so the packages are current: the rebuild runs <c>apt-get upgrade</c> and nothing else changes (APT checks the signed Ubuntu archive itself).
/// </summary>
/// <remarks>
/// The build runs as the dedicated Windows account inside a <see cref="WslSession"/> (its own virtual machine, its own settings file). The pinned image goes in through standard input from the
/// verified, held-open file and the finished archive comes out through standard output into a file of the closed data folder, so the account never opens a file of that folder. The new
/// <c>/etc/wsl.conf</c> (no Windows drives, no Windows programs) is written first and the machine is stopped, so the next start applies it; only then does anything touch the network (red-team F10).
/// </remarks>
public sealed class BaseImageBuilder
{
    /// <summary>Free room the build needs: the image, the imported disk and the exported archive.</summary>
    private const long BuildFreeBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Run first, as root, in the new distribution: no Windows drives and no Windows programs from now on. It takes effect when the machine is started again, which is why the build stops it before
    /// the second script.
    /// </summary>
    public const string WslConfScript = """
        set -eu
        R=""
        mkdir -p "$R/etc"
        printf '[automount]\nenabled=false\nmountFsTab=false\n\n[interop]\nenabled=false\nappendWindowsPath=false\n\n[gpu]\nenabled=false\n' > "$R/etc/wsl.conf"
        grep -q '^enabled=false' "$R/etc/wsl.conf" || { echo "the settings file was not written" >&2; exit 1; }
        """;

    /// <summary>
    /// Run second, as root, after the machine was stopped and started again: it refuses to go on unless the first script's settings are in force (no Windows program interface, no mounted drive), then
    /// installs the tools, makes the <c>runner</c> user without any administrator right, writes the final settings and checks that the runner cannot use sudo.
    /// </summary>
    public const string SetupScript = """
        set -eu
        R=""
        export DEBIAN_FRONTEND=noninteractive
        if [ -e "$R/proc/sys/fs/binfmt_misc/WSLInterop" ] || [ -e "$R/proc/sys/fs/binfmt_misc/WSLInterop-late" ]; then echo "the Windows program interface is still on; nothing is installed" >&2; exit 1; fi
        for d in "$R"/mnt/[A-Za-z]; do
          if [ -d "$d" ] && { grep -Fq " ${d#"$R"} " "$R/proc/mounts" 2>/dev/null || [ -n "$(ls -A "$d" 2>/dev/null)" ]; }; then echo "a Windows drive is still mounted at ${d#"$R"}; nothing is installed" >&2; exit 1; fi
        done
        apt-get update
        apt-get install -y python3 git curl unzip libicu74 ca-certificates
        apt-get upgrade -y -o Dpkg::Options::=--force-confold
        apt-get clean
        command -v setpriv >/dev/null 2>&1 || { echo "setpriv (util-linux) is not in the image; the runner could not be started without new privileges" >&2; exit 1; }
        id -u runner >/dev/null 2>&1 || useradd --create-home --shell /bin/bash runner
        passwd -l runner
        passwd -l root
        for group in sudo admin; do gpasswd -d runner "$group" >/dev/null 2>&1 || true; done
        rm -f "$R"/etc/sudoers.d/*runner* 2>/dev/null || true
        mkdir -p "$R/home/runner/actions-runner"
        chown runner:runner "$R/home/runner/actions-runner"
        printf '[automount]\nenabled=false\nmountFsTab=false\n\n[interop]\nenabled=false\nappendWindowsPath=false\n\n[gpu]\nenabled=false\n\n[user]\ndefault=runner\n' > "$R/etc/wsl.conf"
        if su runner -s /bin/bash -c 'command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1'; then echo "the runner user has administrator rights" >&2; exit 1; fi
        rm -f "$R"/tmp/dbd-*.sh
        """;

    private readonly IDownloads _downloads;
    private readonly IWsl _wsl;
    private readonly WslSandbox _sandbox;
    private readonly LocalCiPaths _paths;
    private readonly ICiLog _log;
    private readonly Retry _retry;
    private readonly IntegrityStore _integrity;
    private readonly IDiskSpace _disk;
    private readonly ResidueRecorder _residue;
    private readonly TimeProvider _time;
    private readonly UbuntuImage _image;
    private readonly TimeSpan _buildLimit;

    /// <param name="downloads">Where the pinned image is fetched.</param>
    /// <param name="wsl">WSL, as the dedicated account.</param>
    /// <param name="sandbox">The session of the account's Linux machine (settings, fresh machine).</param>
    /// <param name="paths">The program's folders.</param>
    /// <param name="log">The log.</param>
    /// <param name="retry">Retry rules for downloads.</param>
    /// <param name="integrity">The recorded checksums.</param>
    /// <param name="disk">Free room of the drive.</param>
    /// <param name="residue">What a clean-up could not remove.</param>
    /// <param name="time">The clock (names the folder of one build).</param>
    /// <param name="image">The image to start from; the pinned one unless a test supplies another.</param>
    /// <param name="buildLimit">How long a whole build may take (default <see cref="WslLimits.ImageBuild"/>, 90 minutes); when it runs out the build is ended and its clean-up still runs.</param>
    public BaseImageBuilder(IDownloads downloads, IWsl wsl, WslSandbox sandbox, LocalCiPaths paths, ICiLog log, Retry retry, IntegrityStore integrity, IDiskSpace disk, ResidueRecorder residue, TimeProvider time, UbuntuImage? image = null, TimeSpan? buildLimit = null)
    {
        _image = image ?? UbuntuImage.Pinned;
        _buildLimit = buildLimit ?? WslLimits.ImageBuild;
        _downloads = downloads;
        _wsl = wsl;
        _sandbox = sandbox;
        _paths = paths;
        _log = log;
        _retry = retry;
        _integrity = integrity;
        _disk = disk;
        _residue = residue;
        _time = time;
    }

    /// <summary>True when a base image is there, its checksum is on record and the file still matches it (the hash is remembered while the file does not change; every job hashes it again).</summary>
    public bool Exists => File.Exists(_paths.BaseArchive) && _integrity.IsIntact(_paths.BaseArchive);

    /// <summary>
    /// Names what the image is made from: the pinned Ubuntu file and the two setup scripts. An image built from another recipe is out of date, so a re-run of set-up for a new token keeps an image
    /// that is built from this recipe and not older than a week (local CI round 2, QA M2) and builds another one otherwise.
    /// </summary>
    public string RecipeId => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_image.Sha256 + "\n" + WslConfScript + "\n" + SetupScript)))[..16];

    /// <summary>True when the image exists, still matches its record, was made from the current recipe and is younger than the weekly limit.</summary>
    public bool IsCurrent(CiState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Exists
               && string.Equals(state.BaseImageRecipe, RecipeId, StringComparison.Ordinal)
               && state.LastBaseImageBuild is { } built
               && now - built < LocalCiDefaults.BaseImageMaximumAge;
    }

    /// <summary>Builds the image. The whole build has a time limit of its own (round 3, L2): when it runs out the build is ended, its Linux machine is removed as after any failure, and a <see cref="TimeoutException"/> says so.</summary>
    public Task BuildAsync(CancellationToken cancellationToken) =>
        TimeBox.RunAsync(_buildLimit, _time, "Building the Linux base image", BuildCoreAsync, cancellationToken);

    private async Task BuildCoreAsync(CancellationToken cancellationToken)
    {
        if (_disk.FreeBytes(_paths.BaseDirectory) < BuildFreeBytes)
        {
            throw new IOException("There is not enough free disk space to build the Linux base image (about 10 GB are needed).");
        }

        DataFolder.CreateChild(_paths.BaseDirectory);
        var image = _image;
        var downloaded = Path.Combine(_paths.BaseDirectory, image.FileName + ".tar.gz");
        if (!await HasPinnedImageAsync(downloaded, image, cancellationToken).ConfigureAwait(false))
        {
            await DownloadVerifiedAsync(image, downloaded, cancellationToken).ConfigureAwait(false);
        }

        _log.Info($"Building the Linux base image from {image.FileName}.");
        var temporaryArchive = _paths.BaseArchive + ".part";
        SafeTree.Delete(temporaryArchive);
        var installDirectory = _paths.WslInstallDirectory(LocalCiDefaults.BuildFolderFor(_time.GetUtcNow()));
        try
        {
            var session = await _sandbox.BeginAsync(cancellationToken).ConfigureAwait(false);
            await using (session.ConfigureAwait(false))
            {
                try
                {
                    await RemoveLeftoverBuildDistributionAsync(cancellationToken).ConfigureAwait(false);

                    // The image is hashed again now and kept open (others may only read it) while WSL reads it from standard input, so the bytes WSL gets are the bytes that were checked.
                    var verified = await _integrity.OpenVerifiedAsync(downloaded, cancellationToken).ConfigureAwait(false);
                    await using (verified.ConfigureAwait(false))
                    {
                        await _wsl.ImportAsync(LocalCiDefaults.BaseDistribution, installDirectory, verified, cancellationToken).ConfigureAwait(false);
                    }

                    await RunAsRootAsync(WslConfScript, "writing the Windows-free settings", WslLimits.BaseSettings, cancellationToken).ConfigureAwait(false);
                    await _wsl.TerminateAsync(LocalCiDefaults.BaseDistribution, cancellationToken).ConfigureAwait(false);
                    await RunAsRootAsync(SetupScript, "setting up the Linux base image", WslLimits.BaseSetup, cancellationToken).ConfigureAwait(false);
                    await _wsl.TerminateAsync(LocalCiDefaults.BaseDistribution, cancellationToken).ConfigureAwait(false);

                    var archive = new FileStream(temporaryArchive, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await using (archive.ConfigureAwait(false))
                    {
                        await _wsl.ExportAsync(LocalCiDefaults.BaseDistribution, archive, cancellationToken).ConfigureAwait(false);
                    }

                    if (new FileInfo(temporaryArchive).Length == 0)
                    {
                        throw new InvalidOperationException("Exporting the Linux base image produced an empty file.");
                    }
                }
                finally
                {
                    var removed = await _sandbox.RemoveDistributionAsync(LocalCiDefaults.BaseDistribution).ConfigureAwait(false);
                    if (!removed)
                    {
                        _residue.Add(ResidueKinds.LinuxMachine, LocalCiDefaults.BaseDistribution, $"the Linux machine {LocalCiDefaults.BaseDistribution} of a base image build could not be removed");
                    }
                    else
                    {
                        _residue.Resolve(ResidueKinds.LinuxMachine, LocalCiDefaults.BaseDistribution);
                        if (!_sandbox.RemoveFolderIfEmpty(installDirectory))
                        {
                            _residue.Add(ResidueKinds.Folder, installDirectory, $"the folder {installDirectory} could not be removed");
                        }
                        else
                        {
                            _residue.Resolve(ResidueKinds.Folder, installDirectory);
                        }
                    }
                }
            }

            var archiveHash = await Hashing.Sha256OfFileAsync(temporaryArchive, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryArchive, _paths.BaseArchive, overwrite: true);
            _integrity.Record(_paths.BaseArchive, archiveHash);
            foreach (var old in Directory.EnumerateFiles(_paths.BaseDirectory, "ubuntu-*.tar.gz").Where(f => !string.Equals(f, downloaded, StringComparison.Ordinal)))
            {
                _integrity.Forget(old);
                File.Delete(old);
            }
        }
        finally
        {
            // A half-written export is never kept (it can be gigabytes); after a good build the file has been moved and this removes nothing.
            SafeTree.Delete(temporaryArchive);
        }

        _log.Info("The Linux base image is ready.");
    }

    /// <summary>A Linux machine of this name that is still registered is what a crashed build left: it is removed first, and the build does not start if that fails.</summary>
    private async Task RemoveLeftoverBuildDistributionAsync(CancellationToken cancellationToken)
    {
        var present = await _wsl.ListDistributionsAsync(cancellationToken).ConfigureAwait(false);
        if (!present.Contains(LocalCiDefaults.BaseDistribution, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _log.Warn($"The Linux machine {LocalCiDefaults.BaseDistribution} of an earlier build is still there; removing it.");
        if (!await _sandbox.RemoveDistributionAsync(LocalCiDefaults.BaseDistribution).ConfigureAwait(false))
        {
            throw new IOException($"The Linux machine {LocalCiDefaults.BaseDistribution} of an earlier build could not be removed, so no new one was built.");
        }

        _residue.Resolve(ResidueKinds.LinuxMachine, LocalCiDefaults.BaseDistribution);
    }

    private async Task RunAsRootAsync(string script, string what, TimeSpan limit, CancellationToken cancellationToken)
    {
        var exit = await _wsl.ExecuteAsync(LocalCiDefaults.BaseDistribution, "root", script, null, line => _log.Info("base: " + line), limit, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException($"{char.ToUpperInvariant(what[0])}{what[1..]} failed (exit code {exit}). See the log for the last lines.");
        }
    }

    private async Task<bool> HasPinnedImageAsync(string downloaded, UbuntuImage image, CancellationToken cancellationToken) =>
        File.Exists(downloaded)
        && _integrity.Recorded(downloaded) is { } recorded
        && Hashing.SameHash(recorded, image.Sha256)
        && await _integrity.IsIntactAsync(downloaded, cancellationToken).ConfigureAwait(false);

    private async Task DownloadVerifiedAsync(UbuntuImage image, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            _integrity.Forget(destination);
            SafeTree.Delete(destination);
        }

        var partial = destination + ".part";
        try
        {
            await _retry.RunAsync(
                ct => _downloads.DownloadToFileAsync(image.Address, partial, image.SizeBytes, ct),
                $"Downloading {image.FileName}",
                cancellationToken).ConfigureAwait(false);
            var actual = await Hashing.Sha256OfFileAsync(partial, cancellationToken).ConfigureAwait(false);
            if (!Hashing.SameHash(actual, image.Sha256))
            {
                throw new DownloadVerificationException($"{image.FileName} does not match the checksum this program was built with; it was deleted and not used.");
            }

            File.Move(partial, destination, overwrite: true);
            _integrity.Record(destination, actual);
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
