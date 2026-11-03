using System.Text.Json;
using DbdPresence.Online.Api;
using DbdPresence.Packs.Index;

namespace DbdPresence.Online.Packs;

/// <summary>Where the sync gets the signed index and the pack files from (the service and the CDN; a local folder in UI tests only).</summary>
public interface IPackSyncTransport
{
    /// <summary>Fetches the signed index document text (unverified).</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text.</returns>
    Task<ApiResult<string>> GetIndexAsync(string channel, CancellationToken cancellationToken);

    /// <summary>Fetches one pack file; at most <see cref="PackEntry.Size"/> bytes are accepted.</summary>
    /// <param name="entry">The index entry that names the file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The untrusted bytes.</returns>
    Task<ApiResult<byte[]>> GetFileAsync(PackEntry entry, CancellationToken cancellationToken);

    /// <summary>The https URL Discord loads the entry's image from.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The URL, or null when this transport has no public URL for it.</returns>
    string? PublicUrlOf(PackEntry entry);
}

/// <summary>The real transport: the signed index from the API host, the files from the CDN host (both on the compiled-in allow-list).</summary>
public sealed class HttpPackSyncTransport : IPackSyncTransport
{
    private readonly ServiceClient _api;
    private readonly Uri _filesBase;

    /// <summary>Initializes a new instance of the <see cref="HttpPackSyncTransport"/> class.</summary>
    /// <param name="api">The service client (shares its 429 backoff).</param>
    /// <param name="filesBase">The CDN folder of pack files, <c>https://&lt;cdn&gt;/packs/</c>.</param>
    public HttpPackSyncTransport(ServiceClient api, Uri filesBase)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(filesBase);
        _api = api;
        _filesBase = filesBase;
    }

    /// <inheritdoc />
    public Task<ApiResult<string>> GetIndexAsync(string channel, CancellationToken cancellationToken) => _api.GetPackIndexAsync(channel, cancellationToken);

    /// <inheritdoc />
    public Task<ApiResult<byte[]>> GetFileAsync(PackEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _api.DownloadPackFileAsync(new Uri(_filesBase, entry.CacheFileName), entry.Size, cancellationToken);
    }

    /// <inheritdoc />
    public string? PublicUrlOf(PackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new Uri(_filesBase, entry.CacheFileName).AbsoluteUri;
    }
}

/// <summary>
/// UI-test transport: reads <c>packs.json</c> (the same <c>{ "schemaVersion": 1, "document": "…" }</c> body the service sends) and
/// <c>cdn/&lt;sha256&gt;.png|jpg</c> from a local folder. It has no network code; the composition root creates it only when the test data
/// root variable is set. Files are not public, so no Discord URL is offered for them.
/// </summary>
public sealed class DirectoryPackSyncTransport : IPackSyncTransport
{
    private static readonly byte[] s_bom = [0xEF, 0xBB, 0xBF];
    private readonly string _directory;

    /// <summary>Initializes a new instance of the <see cref="DirectoryPackSyncTransport"/> class.</summary>
    /// <param name="directory">The folder with <c>packs.json</c> and <c>cdn/</c>.</param>
    public DirectoryPackSyncTransport(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    /// <summary>Parses a JSON file that a person or a test wrote, tolerating a UTF-8 byte order mark.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The document; the caller disposes it.</returns>
    internal static JsonDocument ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(s_bom) ? s_bom.Length : 0));
    }

    /// <inheritdoc />
    public Task<ApiResult<string>> GetIndexAsync(string channel, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "packs.json");
        if (!File.Exists(path))
        {
            return Task.FromResult(new ApiResult<string>(ApiOutcome.Unreachable, null, null, null, null));
        }

        try
        {
            using var json = ParseFile(path);
            var document = json.RootElement.TryGetProperty("document", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return Task.FromResult(document is null
                ? new ApiResult<string>(ApiOutcome.Unreachable, null, null, null, null)
                : new ApiResult<string>(ApiOutcome.Ok, document, null, null, null));
        }
        catch (JsonException)
        {
            // An unreadable test fixture is "no usable answer", exactly like a garbled network answer.
            return Task.FromResult(new ApiResult<string>(ApiOutcome.Unreachable, null, null, null, null));
        }
    }

    /// <inheritdoc />
    public Task<ApiResult<byte[]>> GetFileAsync(PackEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = Path.Combine(_directory, "cdn", entry.CacheFileName);
        if (!File.Exists(path))
        {
            return Task.FromResult(new ApiResult<byte[]>(ApiOutcome.Rejected, null, "not_found", null, null));
        }

        var length = new FileInfo(path).Length;
        return Task.FromResult(length > entry.Size
            ? new ApiResult<byte[]>(ApiOutcome.Rejected, null, ServiceClient.FileTooLargeCode, null, null)
            : new ApiResult<byte[]>(ApiOutcome.Ok, File.ReadAllBytes(path), null, null, null));
    }

    /// <inheritdoc />
    public string? PublicUrlOf(PackEntry entry) => null;
}
