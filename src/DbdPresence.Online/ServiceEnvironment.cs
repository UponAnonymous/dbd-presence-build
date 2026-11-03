using System.Buffers.Text;
using System.Text.Json;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online;

/// <summary>
/// Which service this build talks to (ADR-0016): the one allowed API host and the trusted licence and config public keys.
/// The staging and production values are compiled in; a build uses exactly one set (constant <c>ONLINE_PRODUCTION</c>).
/// </summary>
public sealed class ServiceEnvironment
{
    /// <summary>The production API host.</summary>
    public const string ProductionHost = "api.arcavale.com";

    /// <summary>The staging API host.</summary>
    public const string StagingHost = "api-staging.arcavale.com";

    /// <summary>The production CDN host (pack files; GET of <c>/packs/&lt;sha256&gt;.png|jpg</c> only).</summary>
    public const string ProductionCdnHost = "cdn.arcavale.com";

    /// <summary>The staging CDN host.</summary>
    public const string StagingCdnHost = "cdn-staging.arcavale.com";

    private const string ResourceName = "TrustedServiceKeys.json";

    /// <summary>Initializes a new instance of the <see cref="ServiceEnvironment"/> class.</summary>
    /// <param name="name">Environment name for diagnostics.</param>
    /// <param name="apiHost">The API host.</param>
    /// <param name="cdnHost">The CDN host for pack files; the only other host requests may target.</param>
    /// <param name="keys">The trusted public keys by key id.</param>
    public ServiceEnvironment(string name, string apiHost, string cdnHost, IReadOnlyDictionary<string, TrustedKey> keys)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(apiHost);
        ArgumentException.ThrowIfNullOrEmpty(cdnHost);
        ArgumentNullException.ThrowIfNull(keys);
        Name = name;
        ApiHost = apiHost;
        CdnHost = cdnHost;
        Keys = keys;
    }

    /// <summary>Gets the environment of this build.</summary>
    public static ServiceEnvironment Current { get; } =
#if ONLINE_PRODUCTION
        Load("production", ProductionHost, ProductionCdnHost);
#else
        Load("staging", StagingHost, StagingCdnHost);
#endif

    /// <summary>Gets the environment name.</summary>
    public string Name { get; }

    /// <summary>Gets the allowed API host.</summary>
    public string ApiHost { get; }

    /// <summary>Gets the allowed CDN host (pack file downloads only).</summary>
    public string CdnHost { get; }

    /// <summary>Gets the trusted keys.</summary>
    public IReadOnlyDictionary<string, TrustedKey> Keys { get; }

    /// <summary>Gets the base address of the API.</summary>
    public Uri BaseAddress => new($"https://{ApiHost}/");

    /// <summary>Gets the base address of the pack files on the CDN, <c>https://&lt;cdn&gt;/packs/</c>.</summary>
    public Uri PackFilesBaseAddress => new($"https://{CdnHost}/packs/");

    /// <summary>Gets the base address of the signed update packages on the CDN, <c>https://&lt;cdn&gt;/releases/</c> (FR-ONL-10).</summary>
    public Uri ReleasePackagesBaseAddress => new($"https://{CdnHost}/releases/");

    /// <summary>Builds the trusted key set for the signed-document verifier.</summary>
    /// <returns>The key set.</returns>
    public TrustedKeySet CreateTrustedKeySet() => new(Keys);

    /// <summary>Reads one key set from the embedded <c>TrustedServiceKeys.json</c>.</summary>
    /// <param name="set">"staging" or "production".</param>
    /// <returns>The keys by id.</returns>
    public static IReadOnlyDictionary<string, TrustedKey> LoadKeys(string set)
    {
        using var stream = typeof(ServiceEnvironment).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded service key list is missing.");
        using var json = JsonDocument.Parse(stream);
        var keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var item in json.RootElement.GetProperty(set).EnumerateArray())
        {
            if (item.TryGetProperty("awaitingKey", out var awaiting) && awaiting.ValueKind == JsonValueKind.True)
            {
                // A reserved entry (awaitingKey) whose public key has not been issued yet: nothing is trusted for it, so documents of that purpose fail
                // verification (UnknownKey) until the real key replaces the entry. build/check-online-keys.py --release refuses it.
                continue;
            }

            var purposes = item.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!).ToArray();
            keys[item.GetProperty("kid").GetString()!] = new TrustedKey(Base64Url.DecodeFromChars(item.GetProperty("spki").GetString()!), purposes);
        }

        return keys;
    }

    private static ServiceEnvironment Load(string set, string host, string cdnHost) => new(set, host, cdnHost, LoadKeys(set));
}
