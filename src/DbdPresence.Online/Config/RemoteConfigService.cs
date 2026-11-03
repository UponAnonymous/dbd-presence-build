using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Config;

/// <summary>
/// Fetches, verifies, stores and merges the signed configuration (FR-ONL-08, ADR-0012, SAFETY_AND_PRIVACY §2.3).
/// Only <b>disable-only</b> flags, the application list and the access mode (which can only skip the invite step, D32) are taken from it; every other member is ignored. After <c>expiresAt</c>
/// (14 days) the flags and the emergency switch fall back to <see cref="RemoteConfigSnapshot.SafeDefaults"/> while the application
/// list and default keep the last good signed copy (D8).
/// </summary>
public sealed partial class RemoteConfigService : IRemoteConfigSource
{
    /// <summary>The channel the configuration is published on.</summary>
    public const string Channel = "stable";

    private const int MaxApplications = 10;
    private const int MaxBannerLength = 280;
    private readonly ServiceClient _api;
    private readonly SignedDocumentVerifier _verifier;
    private readonly IPackFileStore _files;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly IOnlineLog _log;
    private readonly Lock _gate = new();
    private SignedEnvelope? _envelope;
    private readonly KeyReplacementService? _keys;
    private ConfigPayload? _payload;

    /// <summary>Initializes a new instance of the <see cref="RemoteConfigService"/> class and loads the stored copy, if any.</summary>
    /// <param name="api">The service client.</param>
    /// <param name="verifier">The signed-document verifier (trusts the config key for purpose <c>config</c>).</param>
    /// <param name="files">File access for the stored copy.</param>
    /// <param name="path">Where the last good document is kept.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">Diagnostics.</param>
    /// <param name="keys">When set, a newer signed key replacement is fetched and applied before each refresh, so a revoked config, content or licence key
    /// is replaced without waiting for an update check (TM-04); the same verified code path as the update check.</param>
    public RemoteConfigService(ServiceClient api, SignedDocumentVerifier verifier, IPackFileStore files, string path, TimeProvider time, IOnlineLog log, KeyReplacementService? keys = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _api = api;
        _verifier = verifier;
        _files = files;
        _path = path;
        _time = time;
        _log = log;
        _keys = keys;
        LoadStored();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public RemoteConfigSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return Merge(_envelope, _payload, _time.GetUtcNow());
            }
        }
    }

    /// <summary>Gets the newest server time seen in a verified configuration (for the clock rule), or null.</summary>
    public DateTimeOffset? NewestIssuedAt
    {
        get
        {
            lock (_gate)
            {
                return _envelope?.IssuedAt;
            }
        }
    }

    /// <summary>Fetches the configuration, verifies it before parsing, and keeps it when it is newer than the stored one.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a new valid document was accepted.</returns>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_keys is not null)
        {
            await _keys.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await _api.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(result.Value!);
        var verified = _verifier.Verify(bytes, SignedPurposes.Config, Channel);
        if (!verified.IsValid)
        {
            _log.Warning("Configuration document refused: " + verified.Outcome);
            return false;
        }

        var payload = ConfigPayload.TryParse(verified.Envelope!.PayloadJson);
        if (payload is null)
        {
            _log.Warning("Configuration document refused: payload has the wrong shape.");
            return false;
        }

        lock (_gate)
        {
            _files.WriteAllBytesDurable(_path + ".tmp", bytes);
            _files.Move(_path + ".tmp", _path);
            _verifier.CommitVersion(verified.Envelope);
            _envelope = verified.Envelope;
            _payload = payload;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Merges a verified envelope and payload as of <paramref name="now"/> (pure; used by tests).</summary>
    /// <param name="envelope">The envelope, or null when there is none.</param>
    /// <param name="payload">The parsed payload, or null.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The effective configuration.</returns>
    internal static RemoteConfigSnapshot Merge(SignedEnvelope? envelope, ConfigPayload? payload, DateTimeOffset now)
    {
        if (envelope is null || payload is null)
        {
            return RemoteConfigSnapshot.SafeDefaults;
        }

        if (now >= envelope.ExpiresAt)
        {
            return RemoteConfigSnapshot.SafeDefaults with { Applications = payload.Applications, DefaultApplicationKey = payload.DefaultApplication };
        }

        return new RemoteConfigSnapshot(
            payload.DisableCapture,
            payload.DisableDetection,
            !payload.EmergencyStop,
            payload.Applications,
            payload.DefaultApplication,
            IsFresh: true,
            payload.Banner,
            payload.MinimumVersion,
            payload.AccessPublic);
    }

    private void LoadStored()
    {
        try
        {
            if (!_files.Exists(_path) || _files.Length(_path) > SignedDocumentVerifier.MaxDocumentBytes)
            {
                return;
            }

            var verified = _verifier.VerifyStored(_files.ReadAllBytes(_path), SignedPurposes.Config, Channel);
            if (!verified.IsValid || ConfigPayload.TryParse(verified.Envelope!.PayloadJson) is not { } payload)
            {
                _log.Warning("The stored configuration was ignored: " + verified.Outcome);
                return;
            }

            _envelope = verified.Envelope;
            _payload = payload;
        }
        catch (IOException ex)
        {
            _log.Warning("The stored configuration could not be read: " + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warning("The stored configuration could not be read: " + ex.GetType().Name);
        }
    }

    /// <summary>The members of the config payload this app reads. Everything else is ignored.</summary>
    internal sealed partial record ConfigPayload(
        bool DisableCapture,
        bool DisableDetection,
        bool EmergencyStop,
        IReadOnlyList<RemoteApplication> Applications,
        string? DefaultApplication,
        string? Banner = null,
        string? MinimumVersion = null,
        bool AccessPublic = false)
    {
        public static ConfigPayload? TryParse(string payloadJson)
        {
            try
            {
                using var json = JsonDocument.Parse(payloadJson, new JsonDocumentOptions { MaxDepth = 12 });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1
                    || !root.TryGetProperty("disable", out var disable) || disable.ValueKind != JsonValueKind.Object
                    || !Bool(disable, "capture", out var capture) || !Bool(disable, "detection", out var detection)
                    || !Bool(root, "emergencyStop", out var emergency))
                {
                    return null;
                }

                var applications = ParseApplications(root);
                var defaultKey = root.TryGetProperty("defaultApplication", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (applications is null)
                {
                    return null;
                }

                if (!OptionalBanner(root, out var banner) || !OptionalMinimumVersion(root, out var minimum))
                {
                    return null;
                }

                // D32: only the exact signed string "public" opens the invite step; absent, "invite-only" or anything else keeps the lock.
                var accessPublic = root.TryGetProperty("accessMode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "public";
                return new ConfigPayload(
                    capture, detection, emergency, applications, defaultKey is not null && applications.Any(a => a.Key == defaultKey) ? defaultKey : null, banner, minimum, accessPublic);
            }
            catch (JsonException)
            {
                // Signed but not parseable: refused as a whole, the last good copy stays.
                return null;
            }
        }

        /// <summary>The announcement: absent or null for none, otherwise plain text of 1 to 280 characters without control characters. Anything else refuses the document.</summary>
        private static bool OptionalBanner(JsonElement root, out string? banner)
        {
            banner = null;
            if (!root.TryGetProperty("banner", out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = element.GetString()!.Trim();
            if (text.Length > MaxBannerLength || text.Any(char.IsControl))
            {
                return false;
            }

            banner = text.Length == 0 ? null : text;
            return true;
        }

        /// <summary>The minimum version: absent or null for none, otherwise <c>major.minor.patch</c> (up to 5 digits each).</summary>
        private static bool OptionalMinimumVersion(JsonElement root, out string? minimum)
        {
            minimum = null;
            if (!root.TryGetProperty("minimumVersion", out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (element.ValueKind != JsonValueKind.String || !VersionPattern().IsMatch(element.GetString()!))
            {
                return false;
            }

            minimum = element.GetString();
            return true;
        }

        private static List<RemoteApplication>? ParseApplications(JsonElement root)
        {
            if (!root.TryGetProperty("applications", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxApplications)
            {
                return null;
            }

            var result = new List<RemoteApplication>();
            foreach (var item in list.EnumerateArray())
            {
                var key = Str(item, "key");
                var id = Str(item, "applicationId");
                var name = Str(item, "name");
                var description = Str(item, "description");
                if (key is null || id is null || name is null || description is null
                    || !KeyPattern().IsMatch(key) || !IdPattern().IsMatch(id)
                    || name.Length is 0 or > 128 || description.Length > 256
                    || name.Any(char.IsControl) || description.Any(char.IsControl)
                    || result.Any(a => a.Key == key))
                {
                    return null;
                }

                result.Add(new RemoteApplication(key, id, name, description));
            }

            return result;
        }

        private static string? Str(JsonElement item, string name) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement parent, string name, out bool value)
        {
            value = false;
            if (!parent.TryGetProperty(name, out var element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            value = element.GetBoolean();
            return true;
        }

        [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$")]
        private static partial Regex VersionPattern();

        [GeneratedRegex("^[a-z0-9-]{1,32}$")]
        private static partial Regex KeyPattern();

        [GeneratedRegex(@"^\d{15,22}$")]
        private static partial Regex IdPattern();
    }
}
