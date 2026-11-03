using DbdPresence.Online.Api;
using DbdPresence.Packs.Releases;

namespace DbdPresence.Online.Updates;

/// <summary>Where the release feed, the key replacement and the update packages come from (the service in the real app; a fake in tests).</summary>
public interface IReleaseTransport
{
    /// <summary>Reads the signed release feed of a channel (unverified text).</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text when <see cref="ApiOutcome.Ok"/>.</returns>
    Task<ApiResult<string>> GetFeedAsync(string channel, CancellationToken cancellationToken);

    /// <summary>Reads the newest key-replacement document (unverified text).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text when <see cref="ApiOutcome.Ok"/>.</returns>
    Task<ApiResult<string>> GetKeyReplacementAsync(CancellationToken cancellationToken);

    /// <summary>Downloads one package into <paramref name="destination"/>, refusing any byte count or hash other than the feed's.</summary>
    /// <param name="package">The package as the verified feed lists it.</param>
    /// <param name="destination">Where the bytes go.</param>
    /// <param name="progress">Progress from 0 to 100, or null.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>True when every byte arrived and matched.</returns>
    Task<ApiResult<bool>> DownloadAsync(ReleasePackage package, Stream destination, Action<int>? progress, CancellationToken cancellationToken);
}

/// <summary>The real transport: the API host for the documents and the CDN host for the packages (ADR-0016; nothing else is contacted).</summary>
public sealed class HttpReleaseTransport : IReleaseTransport
{
    private readonly ServiceClient _api;
    private readonly Uri _packagesBase;

    /// <summary>Initializes a new instance of the <see cref="HttpReleaseTransport"/> class.</summary>
    /// <param name="api">The service client (its handler allows only the API and CDN hosts).</param>
    /// <param name="packagesBase">The package folder on the CDN, <c>https://&lt;cdn&gt;/releases/</c>.</param>
    public HttpReleaseTransport(ServiceClient api, Uri packagesBase)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(packagesBase);
        _api = api;
        _packagesBase = packagesBase;
    }

    /// <inheritdoc />
    public Task<ApiResult<string>> GetFeedAsync(string channel, CancellationToken cancellationToken) => _api.GetReleaseFeedAsync(channel, cancellationToken);

    /// <inheritdoc />
    public Task<ApiResult<string>> GetKeyReplacementAsync(CancellationToken cancellationToken) => _api.GetKeyReplacementAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ApiResult<bool>> DownloadAsync(ReleasePackage package, Stream destination, Action<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!ReleaseFeed.IsSha256(package.Sha256))
        {
            throw new ArgumentException("The package hash is not in the feed's form.", nameof(package));
        }

        return _api.DownloadReleasePackageAsync(new Uri(_packagesBase, package.Sha256 + ".nupkg"), package.Size, package.Sha256, destination, progress, cancellationToken);
    }
}
