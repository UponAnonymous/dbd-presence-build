using DbdPresence.Core.Pictures;

namespace DbdPresence.Core.Online;

/// <summary>One image the user uploaded (FR-ONL-09, D4, D12). The address is unlisted, not private: anyone who has it can open the image.</summary>
/// <param name="Id">The service's id, used to delete it.</param>
/// <param name="Url">The unlisted https address on the content host that Discord is given.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the stored bytes (the same as the local override file's name).</param>
/// <param name="Format"><c>png</c> or <c>jpeg</c>.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Width">Pixels.</param>
/// <param name="Height">Pixels.</param>
/// <param name="CreatedAt">When it was uploaded.</param>
public sealed record UploadInfo(string Id, string Url, string Sha256, string Format, long Size, int Width, int Height, DateTimeOffset CreatedAt);

/// <summary>How much of the per-person online quota the user's pictures use (D28, FR-ONL-15; from <c>GET /v1/me</c>).</summary>
/// <param name="UsedBytes">Bytes stored online.</param>
/// <param name="UsedFiles">Pictures stored online.</param>
/// <param name="LimitBytes">The most bytes the service lets this user store (60 MB by default).</param>
/// <param name="LimitFiles">The most pictures the service lets this user store (300 by default).</param>
public sealed record UploadUsage(long UsedBytes, int UsedFiles, long LimitBytes, int LimitFiles)
{
    /// <summary>Whether a picture of <paramref name="bytes"/> bytes fits in both limits now.</summary>
    /// <param name="bytes">The picture's size.</param>
    /// <returns>True when it fits.</returns>
    public bool Fits(long bytes) => UsedFiles < LimitFiles && UsedBytes + bytes <= LimitBytes;
}

/// <summary>The user's uploads as last read from the service, for the UI and for presence.</summary>
/// <param name="Items">The uploads, oldest first.</param>
/// <param name="Limit">The most images the service lets this user store.</param>
/// <param name="BlockedReason">
/// Why new uploads are refused now (<c>uploads_blocked</c>, <c>online_features_off</c>, or <c>upload_storage_full</c> while the service-wide
/// upload stop is reached), or null.
/// </param>
/// <param name="PolicyAccepted">Whether the user accepted the upload policy on this PC (shown before the first upload).</param>
/// <param name="IsKnown">False until the list was read from the service at least once since sign-in.</param>
/// <param name="Usage">The quota and its use as the service last reported it, or null when it did not report one.</param>
/// <param name="Saving">The own pictures waiting to be saved online, by SHA-256, and why each one waits (the saving-online queue, S5b-6), or null.</param>
/// <param name="SavingMessage">A plain sentence about the saving-online queue while it is stopped or paused, or null.</param>
public sealed record UploadsSnapshot(
    IReadOnlyList<UploadInfo> Items,
    int Limit,
    string? BlockedReason,
    bool PolicyAccepted,
    bool IsKnown,
    UploadUsage? Usage = null,
    IReadOnlyDictionary<string, PictureSaveState>? Saving = null,
    string? SavingMessage = null)
{
    /// <summary>Gets the state before anything is known.</summary>
    public static UploadsSnapshot Empty { get; } = new([], 0, null, false, false);

    /// <summary>Gets the bytes used online: the service's figure, or the sum of the listed uploads when it reported none.</summary>
    public long UsedBytes => Usage?.UsedBytes ?? Items.Sum(i => i.Size);

    /// <summary>Gets the upload URL by SHA-256, for the pack resolver.</summary>
    public IReadOnlyDictionary<string, string> UrlBySha256 =>
        Items.GroupBy(i => i.Sha256, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Url, StringComparer.Ordinal);
}

/// <summary>The result of an upload action, with a message safe to show.</summary>
/// <param name="Failure">What went wrong, or <see cref="AccountFailure.None"/>.</param>
/// <param name="Message">A plain-language message.</param>
/// <param name="Upload">The stored upload after a successful upload; otherwise null.</param>
/// <param name="RetryAfter">The wait after a rate-limited call; otherwise null.</param>
/// <param name="Code">The service's error code when it refused the call (for example <c>upload_limit</c>), otherwise null.</param>
public sealed record UploadResult(AccountFailure Failure, string Message, UploadInfo? Upload = null, TimeSpan? RetryAfter = null, string? Code = null)
{
    /// <summary>Gets a value indicating whether the action succeeded.</summary>
    public bool Succeeded => Failure == AccountFailure.None;
}

/// <summary>The words of the upload policy and the notices the UI shows around uploads (PLAN_V2 §3.5, ADR-0015).</summary>
public static class UploadPolicy
{
    /// <summary>
    /// Whether <paramref name="url"/> is exactly an upload address on the content host: <c>https://&lt;host&gt;/u/&lt;32 lower-case hex&gt;.png</c> or
    /// <c>.jpg</c>, with no port, user info, query or fragment. Nothing else is ever accepted from the service or handed to Discord as an upload.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="host">The content host of this build's environment.</param>
    /// <returns>True for an upload address.</returns>
    public static bool IsUploadUrl(string? url, string? host)
    {
        const string Prefix = "https://";
        if (url is null || string.IsNullOrEmpty(host) || !url.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = url.AsSpan(Prefix.Length);
        if (!rest.StartsWith(host, StringComparison.Ordinal) || !rest[host.Length..].StartsWith("/u/", StringComparison.Ordinal))
        {
            return false;
        }

        var name = rest[(host.Length + 3)..];
        if (name.Length != 36 || !(name.EndsWith(".png", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (var c in name[..32])
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return host.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    }

    /// <summary>
    /// Whether <paramref name="id"/> is exactly an upload id as the service issues it: a UUID in the lower-case "D" form. Every id the app reads
    /// (a service answer, a stored list or the saving-online queue's file) and every id it sends in <c>DELETE /v1/uploads/{id}</c> passes this one check.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True for an upload id.</returns>
    public static bool IsUploadId(string? id) =>
        id is { Length: 36 } && Guid.TryParseExact(id, "D", out var parsed) && string.Equals(parsed.ToString("D"), id, StringComparison.Ordinal);

    /// <summary>The policy the user accepts before the first upload.</summary>
    public static readonly IReadOnlyList<string> Rules =
    [
        "Upload only images you made yourself or have the rights to use.",
        "Nothing offensive, hateful, sexual or illegal.",
        "The service owner may remove any upload, at any time, without notice.",
    ];

    /// <summary>What "unlisted" means.</summary>
    public const string Unlisted = "Uploads are unlisted, not private: they are not listed or shared by the service, but anyone who sees your Discord status or has the address can open the image.";

    /// <summary>What deleting does not undo.</summary>
    public const string DiscordCache = "After you delete an image, Discord may keep showing its cached copy for a while.";

    /// <summary>The file rules the app enforces before upload (the service checks them again).</summary>
    public const string FileRules = "Images are converted on this PC to a clean PNG of at most 1024 by 1024 pixels and 2 MB before they are uploaded.";
}

/// <summary>Uploads as the UI uses them (implemented by <c>DbdPresence.Online</c>).</summary>
public interface IUploadService
{
    /// <summary>Raised (on any thread) after <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the current uploads view.</summary>
    UploadsSnapshot Current { get; }

    /// <summary>Records that the user accepted <see cref="UploadPolicy.Rules"/> on this PC.</summary>
    void AcceptPolicy();

    /// <summary>Reads the user's uploads from the service.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<UploadResult> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Uploads one image that the image helper already converted to a clean PNG.</summary>
    /// <param name="png">The PNG bytes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome; on success <see cref="UploadResult.Upload"/> is set.</returns>
    Task<UploadResult> UploadAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken);

    /// <summary>Deletes one of the user's uploads.</summary>
    /// <param name="id">The upload id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<UploadResult> DeleteAsync(string id, CancellationToken cancellationToken);
}
