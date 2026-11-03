using System.IO;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Releases;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace DbdPresence.App.Updates;

/// <summary>A package that was not the one the verified release feed offered, or that did not arrive intact.</summary>
internal sealed class UpdatePackageRefusedException : IOException
{
    public UpdatePackageRefusedException()
    {
    }

    public UpdatePackageRefusedException(string message)
        : base(message)
    {
    }

    public UpdatePackageRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the download outcome, when the download itself was refused.</summary>
    public UpdateDownloadOutcome? Outcome { get; init; }
}

/// <summary>
/// The custom Velopack update source (FR-ONL-10, PLAN_V2 §6.5, ADR-0012, ADR-0016). Velopack never makes a request of its own: the feed comes
/// from <see cref="UpdateService"/> (the release key's signature checked before parsing, channel bound, expiry and rollback enforced) and
/// holds at most the one full package that is newer than the running version. The package is downloaded by <see cref="UpdateService"/>
/// through the app's only HTTP client (the CDN host, an exact size and SHA-256 from the signed feed); Velopack then checks the size and
/// SHA-256 once more before it keeps the file. No delta packages are offered, so no other file is ever fetched.
/// </summary>
internal sealed class SignedUpdateSource : IUpdateSource
{
    private readonly UpdateService _service;
    private readonly ReleaseVersion _current;

    /// <summary>Initializes a new instance of the <see cref="SignedUpdateSource"/> class.</summary>
    /// <param name="service">The signed-feed service.</param>
    /// <param name="current">The running version.</param>
    public SignedUpdateSource(UpdateService service, ReleaseVersion current)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(current);
        _service = service;
        _current = current;
    }

    /// <summary>Gets the last check (why the feed was empty, for the UI).</summary>
    public UpdateCheck? LastCheck => _service.LastCheck;

    /// <inheritdoc />
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        // The staging id (a random id Velopack keeps for staged roll-outs) is never sent anywhere.
        if (!string.Equals(appId, ReleaseFeed.ExpectedPackageId, StringComparison.Ordinal) || !string.Equals(channel, _service.Channel, StringComparison.Ordinal))
        {
            logger?.Warn($"Update check refused: the app id or channel is not {ReleaseFeed.ExpectedPackageId}/{_service.Channel}.");
            return new VelopackAssetFeed();
        }

        var check = await _service.CheckAsync(_current, CancellationToken.None).ConfigureAwait(false);
        return check.Candidate is { } candidate ? new VelopackAssetFeed { Assets = [ToAsset(candidate)] } : new VelopackAssetFeed();
    }

    /// <inheritdoc />
    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseEntry);
        ArgumentException.ThrowIfNullOrEmpty(localFile);
        var candidate = _service.LastCheck?.Candidate;
        if (candidate is null || !Matches(releaseEntry, candidate))
        {
            throw new UpdatePackageRefusedException("Velopack asked for a package the verified release feed did not offer.");
        }

        var outcome = await _service.DownloadAsync(candidate, localFile, progress, cancelToken).ConfigureAwait(false);
        if (outcome != UpdateDownloadOutcome.Downloaded)
        {
            throw new UpdatePackageRefusedException($"The update package was not downloaded ({outcome}).") { Outcome = outcome };
        }
    }

    /// <summary>The Velopack asset for a verified package: a full package with the signed size and SHA-256.</summary>
    /// <param name="package">The package.</param>
    /// <returns>The asset.</returns>
    internal static VelopackAsset ToAsset(ReleasePackage package) => new()
    {
        PackageId = ReleaseFeed.ExpectedPackageId,
        Version = SemanticVersion.Parse(package.Version.Text),
        Type = VelopackAssetType.Full,
        FileName = package.FileName,
        Size = package.Size,

        // Velopack prefers SHA-256 when it is set and then ignores SHA-1, which the signed feed does not carry. It compares its own upper-case hex
        // ordinally, so the feed's lower-case hash is handed over in upper case (found by the update test).
        SHA256 = package.Sha256.ToUpperInvariant(),
        SHA1 = string.Empty,
        NotesMarkdown = string.Empty,
        NotesHTML = string.Empty,
    };

    private static bool Matches(VelopackAsset asset, ReleasePackage package) =>
        asset.Type == VelopackAssetType.Full
        && string.Equals(asset.PackageId, ReleaseFeed.ExpectedPackageId, StringComparison.Ordinal)
        && string.Equals(asset.FileName, package.FileName, StringComparison.Ordinal)
        && string.Equals(asset.SHA256, package.Sha256.ToUpperInvariant(), StringComparison.Ordinal)
        && asset.Size == package.Size
        && asset.Version is not null
        && string.Equals(asset.Version.ToNormalizedString(), package.Version.Text, StringComparison.Ordinal);
}
