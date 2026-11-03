using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Http;

namespace DbdPresence.Online.Api;

/// <summary>How a call to the service ended, reduced to what the app acts on.</summary>
public enum ApiOutcome
{
    /// <summary>2xx with a readable body.</summary>
    Ok,

    /// <summary>No usable answer: offline, timeout, refused host, redirect, oversize or unreadable body.</summary>
    Unreachable,

    /// <summary>401: the session or token is not accepted (see <see cref="ApiResult{T}.ErrorCode"/>).</summary>
    Unauthorized,

    /// <summary>403. A revocation travels only in <see cref="ApiResult{T}.RevocationDocument"/>; the status alone proves nothing.</summary>
    Forbidden,

    /// <summary>429.</summary>
    RateLimited,

    /// <summary>Any other 4xx.</summary>
    Rejected,

    /// <summary>5xx.</summary>
    ServerError,
}

/// <summary>A typed service answer. Tokens inside <typeparamref name="T"/> are never logged.</summary>
/// <typeparam name="T">The success payload.</typeparam>
/// <param name="Outcome">How it ended.</param>
/// <param name="Value">The payload when <see cref="ApiOutcome.Ok"/>.</param>
/// <param name="ErrorCode">The service's error code (for example <c>invite_invalid</c>), or null.</param>
/// <param name="RevocationDocument">The signed document text of a 403 <c>revoked</c> answer, unverified; null otherwise.</param>
/// <param name="RetryAfter">The <c>Retry-After</c> delay of a 429, or null.</param>
/// <param name="ErrorReason">The service's <c>reason</c> member next to the error code (which rule an upload broke), or null.</param>
public sealed record ApiResult<T>(ApiOutcome Outcome, T? Value, string? ErrorCode, string? RevocationDocument, TimeSpan? RetryAfter, string? ErrorReason = null)
{
    /// <summary>Gets a value indicating whether the call succeeded.</summary>
    public bool IsOk => Outcome == ApiOutcome.Ok;
}

/// <summary>Tokens issued by sign-in or refresh.</summary>
/// <param name="AccessToken">Bearer token (1 hour).</param>
/// <param name="AccessExpiresAt">When it lapses.</param>
/// <param name="RefreshToken">Rotating refresh token.</param>
/// <param name="HasAccess">False when the service wants an invite code.</param>
/// <param name="LicenceDocument">The signed licence text (unverified), or null without access.</param>
/// <param name="UserId">The service's user id (sign-in only).</param>
/// <param name="DisplayName">The Discord display name (sign-in only).</param>
public sealed record SessionGrant(string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, bool HasAccess, string? LicenceDocument, string? UserId, string? DisplayName);

/// <summary>The invite redemption answer.</summary>
/// <param name="LicenceDocument">The signed licence text (unverified).</param>
public sealed record RedeemGrant(string? LicenceDocument);

/// <summary>The upload part of <c>GET /v1/me</c> (service contract, <c>tests/fixtures/uploads/contract.json</c>).</summary>
/// <param name="Items">The uploads.</param>
/// <param name="Limit">The per-user limit.</param>
/// <param name="BlockedReason">Why new uploads are refused, or null.</param>
/// <param name="Usage">The per-person quota and its use (D28), or null from a service that does not report it.</param>
/// <param name="RosterReporter">Whether the same answer marks this account as the owner's game account (<c>user.rosterReporter</c>), or null when it does not say.</param>
public sealed record UploadList(IReadOnlyList<UploadInfo> Items, int Limit, string? BlockedReason, UploadUsage? Usage = null, bool? RosterReporter = null);

/// <summary>The answer of <c>POST /v1/uploads</c>.</summary>
/// <param name="Upload">The stored upload (an existing one when the same bytes were uploaded before).</param>
/// <param name="Existing">True when the service already held these bytes for this user.</param>
public sealed record UploadGrant(UploadInfo Upload, bool Existing);

/// <summary>The answer of a delete that also removes stored files.</summary>
/// <param name="Deleted">True when the service reports it deleted.</param>
/// <param name="FilesRemoved">False when a stored file could not be deleted yet (its address already answers 404; the service retries daily).</param>
public sealed record DeletionReport(bool Deleted, bool FilesRemoved);

/// <summary>One sighting as <c>POST /v1/roster/sightings</c> carries it (D35).</summary>
/// <param name="FoldedName">The folded name (<c>DbdPresence.Core.Roster.RosterNameFold</c>): 2 to 40 characters of a–z, 0–9 and single spaces.</param>
/// <param name="Role"><c>killer</c> or <c>survivor</c>.</param>
/// <param name="MatchId">32 lower-case hex characters, random per match.</param>
public sealed record CharacterSightingReport(string FoldedName, string Role, string MatchId)
{
    /// <summary>Most sightings in one report (the service's limit).</summary>
    public const int MaxPerReport = 4;
}

/// <summary>The answer of <c>POST /v1/study/label</c>: the answer text and the counts the Study page shows. No model name is in it.</summary>
/// <param name="Text">The answer text (a JSON document in the format the request asked for).</param>
/// <param name="StopReason">Why the answer ended; only <c>end_turn</c> is a complete answer.</param>
/// <param name="InputTokens">Tokens read.</param>
/// <param name="OutputTokens">Tokens written.</param>
public sealed record LabellerAnswer(string Text, string StopReason, int InputTokens, int OutputTokens);

/// <summary>The device-signed refresh proof (service contract, <c>service/src/auth/device.ts</c>).</summary>
/// <param name="Timestamp">Unix seconds.</param>
/// <param name="Nonce">16 to 64 base64url characters, never reused.</param>
/// <param name="Signature">Unpadded base64url of the 64-byte P1363 signature.</param>
public sealed record RefreshProof(long Timestamp, string Nonce, string Signature);

/// <summary>
/// The service API as the app uses it (PLAN_V2 §5). One call per method; bodies are read with a hard size cap and parsed with
/// <see cref="JsonDocument"/> (no polymorphic deserialisation). Every call has its own timeout.
/// </summary>
public sealed class ServiceClient
{
    /// <summary>Per-call timeout.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The wait assumed when a 429 carries no usable <c>Retry-After</c> (the service always sends one).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The longest wait honoured, however large the header.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    /// <summary>The wait after a first 429 even when the service asked for less; it doubles with each further consecutive 429 on the same route.</summary>
    public static readonly TimeSpan BackoffFloor = TimeSpan.FromSeconds(15);

    /// <summary>The longest wait the backoff floor alone reaches.</summary>
    public static readonly TimeSpan BackoffCeiling = TimeSpan.FromMinutes(10);

    /// <summary>The <see cref="ApiResult{T}.ErrorCode"/> of a pack file longer than the size the signed index declares.</summary>
    public const string FileTooLargeCode = "file_too_large";

    /// <summary>The error code of an update package whose size or SHA-256 differs from the signed feed.</summary>
    public const string PackageMismatchCode = "package_mismatch";

    /// <summary>The longest an update package download may take.</summary>
    public static readonly TimeSpan PackageDownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The wait for one labelling call: the model reads a few hundred pictures before it answers.</summary>
    public static readonly TimeSpan StudyLabelTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The largest labelling request body (the relay refuses more).</summary>
    public const int MaxLabelBodyBytes = 30 * 1024 * 1024;

    private const string RefreshProofPrefix = "dbdp-refresh-proof-v1\n";
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly string? _uploadHost;
    private readonly Lock _gateLock = new();
    private readonly Dictionary<string, RateGate> _gates = [];

    /// <summary>Initializes a new instance of the <see cref="ServiceClient"/> class.</summary>
    /// <param name="http">The client built by <see cref="ServiceHttp"/> (or a test client over the allow-list handler).</param>
    /// <param name="time">The clock for timeouts.</param>
    /// <param name="uploadHost">The content host whose <c>/u/</c> addresses are accepted for uploads, or null when this client does not use uploads.</param>
    public ServiceClient(HttpClient http, TimeProvider time, string? uploadHost = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        _http = http;
        _time = time;
        _uploadHost = uploadHost;
    }

    /// <summary>Gets the content host whose upload addresses are accepted, or null.</summary>
    public string? UploadHost => _uploadHost;

    /// <summary>Builds the exact bytes the device key signs for a refresh.</summary>
    /// <param name="refreshToken">The refresh token (hashed here; never sent in the proof).</param>
    /// <param name="timestamp">Unix seconds.</param>
    /// <param name="nonce">The nonce.</param>
    /// <returns>The UTF-8 message.</returns>
    public static byte[] ProofMessage(string refreshToken, long timestamp, string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        var tokenHash = Base64Url.EncodeToString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        return Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{RefreshProofPrefix}{tokenHash}\n{timestamp}\n{nonce}"));
    }

    /// <summary><c>POST /v1/auth/discord</c>.</summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="codeVerifier">The PKCE verifier.</param>
    /// <param name="redirectUri">The loopback redirect used in the authorize URL.</param>
    /// <param name="device">The device public key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant.</returns>
    public Task<ApiResult<SessionGrant>> SignInAsync(string code, string codeVerifier, string redirectUri, DevicePublicKey device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["code"] = code,
            ["codeVerifier"] = codeVerifier,
            ["redirectUri"] = redirectUri,
            ["devicePublicKeyJwk"] = new Dictionary<string, string> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = device.X, ["y"] = device.Y },
        });
        return SendAsync(HttpMethod.Post, "v1/auth/discord", body, null, ParseGrant, cancellationToken);
    }

    /// <summary><c>POST /v1/auth/refresh</c>.</summary>
    /// <param name="refreshToken">The current refresh token.</param>
    /// <param name="proof">The device-signed proof.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new grant.</returns>
    public Task<ApiResult<SessionGrant>> RefreshAsync(string refreshToken, RefreshProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["refreshToken"] = refreshToken,
            ["proof"] = new Dictionary<string, object> { ["timestamp"] = proof.Timestamp, ["nonce"] = proof.Nonce, ["signature"] = proof.Signature },
        });
        return SendAsync(HttpMethod.Post, "v1/auth/refresh", body, null, ParseGrant, cancellationToken);
    }

    /// <summary><c>POST /v1/auth/logout</c>.</summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public Task<ApiResult<bool>> LogoutAsync(string accessToken, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "v1/auth/logout", "{}", accessToken, _ => true, cancellationToken);

    /// <summary><c>POST /v1/invites/redeem</c>.</summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="code">The code as normalised for display.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The grant.</returns>
    public Task<ApiResult<RedeemGrant>> RedeemInviteAsync(string accessToken, string code, CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            "v1/invites/redeem",
            JsonSerializer.Serialize(new Dictionary<string, string> { ["code"] = code }),
            accessToken,
            root => new RedeemGrant(OptionalString(root, "licence")),
            cancellationToken);

    /// <summary><c>DELETE /v1/me</c>.</summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result; <see cref="ApiResult{T}.Value"/> is true when the service reports the account deleted.</returns>
    public Task<ApiResult<DeletionReport>> DeleteAccountAsync(string accessToken, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, "v1/me", null, accessToken, root => ParseDeletion(root, "filesRemoved"), cancellationToken);

    /// <summary>Reads <c>deleted</c> and the files-removed flag of a delete answer; a service without the flag means the files are gone.</summary>
    private static DeletionReport ParseDeletion(JsonElement root, string filesMember) =>
        new(
            root.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True,
            !(root.TryGetProperty(filesMember, out var f) && f.ValueKind == JsonValueKind.False));

    /// <summary><c>GET /v1/me</c>: the user's uploads, the limit and any block (FR-ONL-09).</summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The upload list.</returns>
    public Task<ApiResult<UploadList>> GetUploadsAsync(string accessToken, CancellationToken cancellationToken) =>
        SendContentAsync(HttpMethod.Get, "v1/me", null, accessToken, root => ParseUploadList(root.GetProperty("uploads"), _uploadHost) with { RosterReporter = ParseRosterReporter(root) }, cancellationToken);

    /// <summary>
    /// <c>POST /v1/uploads</c>: one <c>multipart/form-data</c> part named <c>file</c> (FR-ONL-09). The body is built byte for byte in the one shape the
    /// service's strict reader accepts (<c>service/src/lib/multipart.ts</c>): no preamble, Content-Disposition and Content-Type, the bytes, the
    /// closing delimiter and one CRLF.
    /// </summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="image">The image bytes.</param>
    /// <param name="contentType"><c>image/png</c> or <c>image/jpeg</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored upload.</returns>
    public Task<ApiResult<UploadGrant>> UploadAsync(string accessToken, ReadOnlyMemory<byte> image, string contentType, CancellationToken cancellationToken)
    {
        if (contentType is not ("image/png" or "image/jpeg"))
        {
            throw new ArgumentException("Only image/png and image/jpeg are uploaded.", nameof(contentType));
        }

        var boundary = "dbdp-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var content = new ByteArrayContent(MultipartBody(boundary, image.Span, contentType));
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        return SendContentAsync(
            HttpMethod.Post,
            "v1/uploads",
            content,
            accessToken,
            root => new UploadGrant(ParseUpload(root.GetProperty("upload"), _uploadHost), root.TryGetProperty("existing", out var e) && e.ValueKind == JsonValueKind.True),
            cancellationToken);
    }

    /// <summary>
    /// <c>POST /v1/study/label</c> (owner-only labelling relay, ADR-0018). The caller builds the whole JSON body (it must start with the exact prefix the relay checks);
    /// it is sent as it is with its length known. The wait is <see cref="StudyLabelTimeout"/>, not the usual call timeout.
    /// </summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="body">The request body, at most 30 MB.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer text and counts.</returns>
    public Task<ApiResult<LabellerAnswer>> LabelAsync(string accessToken, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (body.Length == 0 || body.Length > MaxLabelBodyBytes)
        {
            throw new ArgumentException("A labelling request is between 1 byte and 30 MB.", nameof(body));
        }

        var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return SendContentAsync(
            HttpMethod.Post,
            "v1/study/label",
            content,
            accessToken,
            root => new LabellerAnswer(
                root.GetProperty("text").GetString() ?? throw new JsonException("text"),
                root.GetProperty("stopReason").GetString() ?? throw new JsonException("stopReason"),
                root.GetProperty("inputTokens").GetInt32(),
                root.GetProperty("outputTokens").GetInt32()),
            cancellationToken,
            StudyLabelTimeout);
    }

    /// <summary>The exact multipart body <see cref="UploadAsync"/> sends.</summary>
    /// <param name="boundary">The boundary (letters, digits and hyphens).</param>
    /// <param name="image">The file bytes.</param>
    /// <param name="contentType">The part's type.</param>
    /// <returns>The body.</returns>
    public static byte[] MultipartBody(string boundary, ReadOnlySpan<byte> image, string contentType)
    {
        ArgumentException.ThrowIfNullOrEmpty(boundary);
        var head = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"image\"\r\nContent-Type: {contentType}\r\n\r\n");
        var tail = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        var body = new byte[head.Length + image.Length + tail.Length];
        head.CopyTo(body, 0);
        image.CopyTo(body.AsSpan(head.Length));
        tail.CopyTo(body, head.Length + image.Length);
        return body;
    }

    /// <summary><c>DELETE /v1/uploads/{id}</c>.</summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="id">The upload id (a UUID).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was deleted, and whether the stored file is gone too.</returns>
    public Task<ApiResult<DeletionReport>> DeleteUploadAsync(string accessToken, string id, CancellationToken cancellationToken)
    {
        if (!UploadPolicy.IsUploadId(id))
        {
            throw new ArgumentException("An upload id is a lower-case UUID.", nameof(id));
        }

        return SendContentAsync(HttpMethod.Delete, $"v1/uploads/{id}", null, accessToken, root => ParseDeletion(root, "fileRemoved"), cancellationToken);
    }

    /// <summary>Reads <c>user.rosterReporter</c> of <c>GET /v1/me</c>: true or false when the member is a boolean, otherwise null (an older service).</summary>
    /// <param name="root">The answer.</param>
    /// <returns>The flag, or null.</returns>
    public static bool? ParseRosterReporter(JsonElement root) =>
        root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object && user.TryGetProperty("rosterReporter", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.ValueKind == JsonValueKind.True
            : null;

    /// <summary>Reads the <c>uploads</c> member of <c>GET /v1/me</c>.</summary>
    /// <param name="uploads">The member.</param>
    /// <param name="uploadHost">The content host the addresses must be on.</param>
    /// <returns>The list.</returns>
    public static UploadList ParseUploadList(JsonElement uploads, string? uploadHost)
    {
        var items = uploads.GetProperty("items").EnumerateArray().Select(u => ParseUpload(u, uploadHost)).ToList();
        var blocked = uploads.GetProperty("blocked");
        return new UploadList(
            items,
            uploads.GetProperty("limit").GetInt32(),
            blocked.ValueKind == JsonValueKind.String ? blocked.GetString() : blocked.ValueKind == JsonValueKind.Null ? null : throw new JsonException("blocked"),
            ParseUsage(uploads));
    }

    /// <summary>
    /// Reads the quota members of the <c>uploads</c> member (S5b-6): all four present and non-negative, or none (an older service), else the
    /// answer is refused.
    /// </summary>
    private static UploadUsage? ParseUsage(JsonElement uploads)
    {
        string[] names = ["usedBytes", "usedFiles", "limitBytes", "limitFiles"];
        var present = names.Count(n => uploads.TryGetProperty(n, out _));
        if (present == 0)
        {
            return null;
        }

        if (present != names.Length)
        {
            throw new JsonException("usage");
        }

        var usage = new UploadUsage(
            uploads.GetProperty("usedBytes").GetInt64(),
            uploads.GetProperty("usedFiles").GetInt32(),
            uploads.GetProperty("limitBytes").GetInt64(),
            uploads.GetProperty("limitFiles").GetInt32());
        return usage is { UsedBytes: >= 0, UsedFiles: >= 0, LimitBytes: >= 0, LimitFiles: >= 0 } ? usage : throw new JsonException("usage");
    }

    /// <summary>Reads one upload. The URL must be exactly an upload address on <paramref name="uploadHost"/> (<see cref="UploadPolicy.IsUploadUrl"/>).</summary>
    /// <param name="upload">The object.</param>
    /// <param name="uploadHost">The content host the address must be on.</param>
    /// <returns>The upload.</returns>
    public static UploadInfo ParseUpload(JsonElement upload, string? uploadHost)
    {
        var url = upload.GetProperty("url").GetString() ?? throw new JsonException("url");
        if (!UploadPolicy.IsUploadUrl(url, uploadHost))
        {
            throw new JsonException("url");
        }

        var sha = upload.GetProperty("sha256").GetString() ?? throw new JsonException("sha256");
        if (sha.Length != 64 || !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new JsonException("sha256");
        }

        var format = upload.GetProperty("format").GetString();
        var id = upload.GetProperty("id").GetString();
        return new UploadInfo(
            UploadPolicy.IsUploadId(id) ? id! : throw new JsonException("id"),
            url,
            sha,
            format is "png" or "jpeg" ? format : throw new JsonException("format"),
            upload.GetProperty("size").GetInt64(),
            upload.GetProperty("width").GetInt32(),
            upload.GetProperty("height").GetInt32(),
            DateTimeOffset.Parse(upload.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
    }

    /// <summary><c>GET /v1/config</c>: the signed configuration document text (unverified).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text.</returns>
    public Task<ApiResult<string>> GetConfigAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "v1/config", null, null, root => OptionalString(root, "document") ?? throw new JsonException("no document"), cancellationToken);

    /// <summary><c>GET /v1/packs?channel=…</c>: the signed pack index document text (unverified; FR-ONL-07).</summary>
    /// <param name="channel">The channel, for example <c>stable</c>; letters, digits and hyphens only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text. The service wraps it as <c>{ "schemaVersion": 1, "document": "…" }</c> like the configuration.</returns>
    public Task<ApiResult<string>> GetPackIndexAsync(string channel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        if (!channel.All(c => c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
        {
            throw new ArgumentException("A channel is lower-case letters, digits and hyphens.", nameof(channel));
        }

        return SendAsync(
            HttpMethod.Get,
            $"v1/packs?channel={channel}",
            null,
            null,
            root => OptionalString(root, "document") ?? throw new JsonException("no document"),
            cancellationToken);
    }

    /// <summary><c>GET /v1/roster?channel=…</c>: the signed character roster document text (unverified; PLAN_V2_S5B §2.3).</summary>
    /// <param name="channel">The channel, for example <c>stable</c>; letters, digits and hyphens only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text, unwrapped from <c>{ "schemaVersion": 1, "document": "…" }</c>.</returns>
    public Task<ApiResult<string>> GetRosterAsync(string channel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        if (!channel.All(c => c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
        {
            throw new ArgumentException("A channel is lower-case letters, digits and hyphens.", nameof(channel));
        }

        return SendAsync(HttpMethod.Get, $"v1/roster?channel={channel}", null, null, root => OptionalString(root, "document") ?? throw new JsonException("no document"), cancellationToken);
    }

    /// <summary>
    /// <c>GET /v1/me</c>, reading only <c>user.rosterReporter</c>: whether the owner marked this account as their own game account, whose app
    /// reports unknown character names (D35). A service without the member means no.
    /// </summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True for the owner's account.</returns>
    public Task<ApiResult<bool>> GetRosterReporterAsync(string accessToken, CancellationToken cancellationToken) =>
        SendContentAsync(
            HttpMethod.Get,
            "v1/me",
            null,
            accessToken,
            root => root.GetProperty("user").TryGetProperty("rosterReporter", out var flag) && flag.ValueKind == JsonValueKind.True,
            cancellationToken);

    /// <summary>
    /// <c>POST /v1/roster/sightings</c> (D35): character names read on screen that are not in the vocabulary. Each carries only the folded name,
    /// the role and a random id of the match; the body also names the app version. No player name and no image is ever sent.
    /// </summary>
    /// <param name="accessToken">The bearer token.</param>
    /// <param name="appVersion">The app version, <c>major.minor.patch</c>.</param>
    /// <param name="sightings">At most 4 sightings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many characters the service added because of this report.</returns>
    public Task<ApiResult<int>> ReportSightingsAsync(string accessToken, string appVersion, IReadOnlyList<CharacterSightingReport> sightings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(appVersion);
        ArgumentNullException.ThrowIfNull(sightings);
        if (sightings.Count is < 1 or > CharacterSightingReport.MaxPerReport)
        {
            throw new ArgumentException("A report carries 1 to 4 sightings.", nameof(sightings));
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["appVersion"] = appVersion,
            ["sightings"] = sightings.Select(s => new Dictionary<string, string> { ["name"] = s.FoldedName, ["role"] = s.Role, ["match"] = s.MatchId }).ToArray(),
        });
        return SendAsync(
            HttpMethod.Post,
            "v1/roster/sightings",
            body,
            accessToken,
            root => root.TryGetProperty("added", out var added) && added.ValueKind == JsonValueKind.Array ? added.GetArrayLength() : 0,
            cancellationToken);
    }

    /// <summary><c>GET /v1/releases/{channel}</c>: the signed release feed document text (unverified; FR-ONL-10).</summary>
    /// <param name="channel">The channel, <c>stable</c> or <c>beta</c>; letters, digits and hyphens only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text, unwrapped from <c>{ "schemaVersion": 1, "document": "…" }</c>. A 404 <c>no_release</c> means nothing is published.</returns>
    public Task<ApiResult<string>> GetReleaseFeedAsync(string channel, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        if (!channel.All(c => c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
        {
            throw new ArgumentException("A channel is lower-case letters, digits and hyphens.", nameof(channel));
        }

        return SendAsync(HttpMethod.Get, $"v1/releases/{channel}", null, null, root => OptionalString(root, "document") ?? throw new JsonException("no document"), cancellationToken);
    }

    /// <summary><c>GET /v1/keys</c>: the newest key-replacement document text (unverified; PLAN_V2 §6.2). A 404 <c>no_key_replacement</c> means there is none.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text, unwrapped from <c>{ "schemaVersion": 1, "document": "…" }</c>.</returns>
    public Task<ApiResult<string>> GetKeyReplacementAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "v1/keys", null, null, root => OptionalString(root, "document") ?? throw new JsonException("no document"), cancellationToken);

    /// <summary>
    /// <c>GET</c> of one update package from the CDN into <paramref name="destination"/> (FR-ONL-10). The body must be exactly
    /// <paramref name="size"/> bytes with exactly the SHA-256 <paramref name="sha256"/> from the signed feed; anything else is refused
    /// (<see cref="PackageMismatchCode"/>) and the caller discards what was written.
    /// </summary>
    /// <param name="url">The package URL; the allow-list handler accepts only <c>https://&lt;cdn host&gt;/releases/&lt;sha256&gt;.nupkg</c>.</param>
    /// <param name="size">The size the signed feed declares.</param>
    /// <param name="sha256">The lower-case hex SHA-256 the signed feed declares.</param>
    /// <param name="destination">Where the bytes go.</param>
    /// <param name="progress">Called with 0–100 while the body is read, or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when <see cref="ApiOutcome.Ok"/>: every byte arrived and matched.</returns>
    public async Task<ApiResult<bool>> DownloadReleasePackageAsync(Uri url, long size, string sha256, Stream destination, Action<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        ArgumentNullException.ThrowIfNull(destination);
        const string GateKey = "cdn:releases";
        if (WaitRemaining(GateKey) is { } remaining)
        {
            return new ApiResult<bool>(ApiOutcome.RateLimited, false, "rate_limited", null, remaining);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeout = new CancellationTokenSource(PackageDownloadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            ApiResult<bool> result;
            if (status is >= 200 and < 300)
            {
                var matched = await CopyVerifiedAsync(response, size, sha256, destination, progress, linked.Token).ConfigureAwait(false);
                result = matched
                    ? new ApiResult<bool>(ApiOutcome.Ok, true, null, null, null)
                    : new ApiResult<bool>(ApiOutcome.Rejected, false, PackageMismatchCode, null, null);
            }
            else
            {
                var outcome = status switch
                {
                    429 => ApiOutcome.RateLimited,
                    >= 500 => ApiOutcome.ServerError,
                    _ => ApiOutcome.Rejected,
                };
                result = new ApiResult<bool>(outcome, false, null, null, response.Headers.RetryAfter?.Delta);
            }

            return NoteAnswer(GateKey, response, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable<bool>();
        }
        catch (HttpRequestException)
        {
            return Unreachable<bool>();
        }
        catch (HostNotAllowedException)
        {
            return Unreachable<bool>();
        }
        catch (RedirectRefusedException)
        {
            return Unreachable<bool>();
        }
        catch (InvalidDataException)
        {
            // Longer than the size the signed feed declares: refused, not retried as an outage.
            return new ApiResult<bool>(ApiOutcome.Rejected, false, PackageMismatchCode, null, null);
        }
    }

    /// <summary>
    /// <c>GET</c> of one pack file from the CDN (FR-ONL-07). The caller passes the exact size the signed index declares; a longer body is refused
    /// while it is read, so no more than that many bytes are ever held. The bytes are untrusted until the caller has checked their SHA-256.
    /// </summary>
    /// <param name="url">The file URL; the allow-list handler accepts only <c>https://&lt;cdn host&gt;/packs/&lt;sha256&gt;.png|jpg</c>.</param>
    /// <param name="maxBytes">The most bytes to accept.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The body when <see cref="ApiOutcome.Ok"/>.</returns>
    public async Task<ApiResult<byte[]>> DownloadPackFileAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        const string GateKey = "cdn:packs";
        if (WaitRemaining(GateKey) is { } remaining)
        {
            return new ApiResult<byte[]>(ApiOutcome.RateLimited, default, "rate_limited", null, remaining);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            ApiResult<byte[]> result;
            if (status is >= 200 and < 300)
            {
                var body = await ReadBoundedBytesAsync(response, maxBytes, linked.Token).ConfigureAwait(false);
                result = new ApiResult<byte[]>(ApiOutcome.Ok, body, null, null, null);
            }
            else
            {
                var outcome = status switch
                {
                    429 => ApiOutcome.RateLimited,
                    >= 500 => ApiOutcome.ServerError,
                    _ => ApiOutcome.Rejected,
                };
                result = new ApiResult<byte[]>(outcome, default, null, null, response.Headers.RetryAfter?.Delta);
            }

            return NoteAnswer(GateKey, response, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable<byte[]>();
        }
        catch (HttpRequestException)
        {
            return Unreachable<byte[]>();
        }
        catch (HostNotAllowedException)
        {
            return Unreachable<byte[]>();
        }
        catch (RedirectRefusedException)
        {
            return Unreachable<byte[]>();
        }
        catch (InvalidDataException)
        {
            // Larger than the size the signed index declares: the file is refused, not retried as an outage.
            return new ApiResult<byte[]>(ApiOutcome.Rejected, default, FileTooLargeCode, null, null);
        }
    }

    private static SessionGrant ParseGrant(JsonElement root)
    {
        var access = root.GetProperty("access").GetString();
        var user = root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : (JsonElement?)null;
        return new SessionGrant(
            root.GetProperty("accessToken").GetString()!,
            DateTimeOffset.Parse(root.GetProperty("accessTokenExpiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            root.GetProperty("refreshToken").GetString()!,
            access == "granted",
            OptionalString(root, "licence"),
            user is { } uu ? OptionalString(uu, "id") : null,
            user is { } un ? OptionalString(un, "displayName") : null);
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, string? json, string? bearer, Func<JsonElement, T> parse, CancellationToken cancellationToken) =>
        SendContentAsync(method, path, json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"), bearer, parse, cancellationToken);

    private async Task<ApiResult<T>> SendContentAsync<T>(HttpMethod method, string path, HttpContent? content, string? bearer, Func<JsonElement, T> parse, CancellationToken cancellationToken, TimeSpan? timeoutOverride = null)
    {
        // A route that answered 429 is not contacted again until its wait has passed, so no caller can spin on it.
        if (WaitRemaining(path) is { } remaining)
        {
            content?.Dispose();
            return new ApiResult<T>(ApiOutcome.RateLimited, default, "rate_limited", null, remaining);
        }

        using var request = new HttpRequestMessage(method, new Uri(_http.BaseAddress!, path));
        request.Content = content;

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var timeout = new CancellationTokenSource(timeoutOverride ?? RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var text = await ReadBoundedAsync(response, linked.Token).ConfigureAwait(false);
            var result = Interpret(response, text, parse);
            return NoteAnswer(path, response, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable<T>();
        }
        catch (HttpRequestException)
        {
            return Unreachable<T>();
        }
        catch (HostNotAllowedException)
        {
            return Unreachable<T>();
        }
        catch (RedirectRefusedException)
        {
            return Unreachable<T>();
        }
        catch (InvalidDataException)
        {
            return Unreachable<T>();
        }
    }

    /// <summary>The time left on the route's rate-limit wait, or null when it may be called.</summary>
    private TimeSpan? WaitRemaining(string path)
    {
        lock (_gateLock)
        {
            if (_gates.TryGetValue(path, out var gate) && gate.NotBefore - _time.GetUtcNow() is { } left && left > TimeSpan.Zero)
            {
                return left;
            }

            return null;
        }
    }

    /// <summary>Starts the wait after a 429 (Retry-After, floored by an exponential backoff on consecutive 429s) and clears it after any other answer.</summary>
    private ApiResult<T> NoteAnswer<T>(string path, HttpResponseMessage response, ApiResult<T> result)
    {
        lock (_gateLock)
        {
            if (result.Outcome != ApiOutcome.RateLimited)
            {
                _gates.Remove(path);
                return result;
            }

            var consecutive = _gates.TryGetValue(path, out var previous) ? previous.Consecutive + 1 : 1;
            var now = _time.GetUtcNow();
            var asked = response.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - now,
                _ => DefaultRetryAfter,
            };
            var floor = TimeSpan.FromTicks(Math.Min(BackoffFloor.Ticks << Math.Min(consecutive - 1, 16), BackoffCeiling.Ticks));
            var wait = TimeSpan.FromTicks(Math.Min(Math.Max(Math.Max(asked.Ticks, floor.Ticks), TimeSpan.FromSeconds(1).Ticks), MaxRetryAfter.Ticks));
            _gates[path] = new RateGate(consecutive, now + wait);
            return result with { RetryAfter = wait };
        }
    }

    private sealed record RateGate(int Consecutive, DateTimeOffset NotBefore);

    private static ApiResult<T> Unreachable<T>() => new(ApiOutcome.Unreachable, default, null, null, null);

    private static ApiResult<T> Interpret<T>(HttpResponseMessage response, string text, Func<JsonElement, T> parse)
    {
        var status = (int)response.StatusCode;
        JsonDocument? document = null;
        try
        {
            try
            {
                document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            }
            catch (JsonException)
            {
                // Not JSON (for example an edge error page): handled below by status alone, never trusted.
                document = null;
            }

            var root = document?.RootElement;
            var error = root is { ValueKind: JsonValueKind.Object } r ? OptionalString(r, "error") : null;
            if (status is >= 200 and < 300)
            {
                if (root is not { ValueKind: JsonValueKind.Object } ok)
                {
                    return Unreachable<T>();
                }

                try
                {
                    return new ApiResult<T>(ApiOutcome.Ok, parse(ok), null, null, null);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    // A 2xx answer with the wrong shape is treated as no usable answer.
                    return Unreachable<T>();
                }
            }

            var outcome = status switch
            {
                401 => ApiOutcome.Unauthorized,
                403 => ApiOutcome.Forbidden,
                429 => ApiOutcome.RateLimited,
                >= 500 => ApiOutcome.ServerError,
                _ => ApiOutcome.Rejected,
            };
            var revocation = status == 403 && error == "revoked" && root is { ValueKind: JsonValueKind.Object } body ? OptionalString(body, "revocation") : null;
            TimeSpan? retry = response.Headers.RetryAfter?.Delta;
            // `reason` says which rule a refused image broke; `limit` says which cap 409 upload_limit hit (bytes or files).
            var reason = root is { ValueKind: JsonValueKind.Object } detail ? OptionalString(detail, "reason") ?? OptionalString(detail, "limit") : null;
            return new ApiResult<T>(outcome, default, error, revocation, retry, reason);
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException("The file is larger than the size in the index.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("The file is larger than the size in the index.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<bool> CopyVerifiedAsync(HttpResponseMessage response, long size, string sha256, Stream destination, Action<int>? progress, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared != size)
        {
            return false;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var chunk = new byte[64 * 1024];
        long total = 0;
        var reported = -1;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (total + read > size)
            {
                throw new InvalidDataException("The package is larger than the size in the signed feed.");
            }

            hash.AppendData(chunk, 0, read);
            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            var percent = (int)(total * 100 / size);
            if (percent != reported)
            {
                reported = percent;
                progress?.Invoke(percent);
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return total == size && string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.Ordinal);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > ServiceHttp.MaxResponseBytes)
        {
            throw new InvalidDataException("The response is larger than the cap.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > ServiceHttp.MaxResponseBytes)
            {
                throw new InvalidDataException("The response is larger than the cap.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
