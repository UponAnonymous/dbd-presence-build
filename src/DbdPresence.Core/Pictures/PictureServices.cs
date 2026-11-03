using DbdPresence.Core.Images;

namespace DbdPresence.Core.Pictures;

/// <summary>
/// Makes an image smaller for saving (ADR-0013). Like <see cref="ISafeImageLoader"/> it runs in the separate image helper process, never in
/// the app: the input is untrusted. The result is a clean PNG fitted within <c>maxEdge</c> pixels (never enlarged), saved with the best resize
/// filter and the strongest lossless PNG compression.
/// </summary>
public interface IPictureFitter
{
    /// <summary>Fits an image held in memory.</summary>
    /// <param name="data">The image bytes (PNG, JPEG or WebP); a clean PNG from <see cref="ISafeImageLoader"/> is also accepted.</param>
    /// <param name="maxEdge">The longest side in pixels, 512 or 1024.</param>
    /// <param name="cancellationToken">Cancels the work and stops the helper.</param>
    Task<SafeImageResult> FitBytesAsync(ReadOnlyMemory<byte> data, int maxEdge, CancellationToken cancellationToken);

    /// <summary>Fits an image file the user chose.</summary>
    /// <param name="path">The local file path.</param>
    /// <param name="maxEdge">The longest side in pixels, 512 or 1024.</param>
    /// <param name="cancellationToken">Cancels the work and stops the helper.</param>
    Task<SafeImageResult> FitFileAsync(string path, int maxEdge, CancellationToken cancellationToken);
}

/// <summary>An own picture that has been saved on this PC and may be saved online (D28, S5b-6).</summary>
/// <param name="Key">The character key, <c>character:&lt;id&gt;</c>.</param>
/// <param name="Sha256">The picture's hash.</param>
/// <param name="Bytes">The size of the stored file.</param>
/// <param name="InUse">True when the character uses this picture now, so it goes first.</param>
public sealed record PictureUploadRequest(string Key, string Sha256, long Bytes, bool InUse);

/// <summary>Why an own picture handed to the saving-online queue is not online yet (PLAN_V2_S5B §2.6).</summary>
public enum PictureSaveState
{
    /// <summary>Waiting its turn, or being saved now.</summary>
    Waiting,

    /// <summary>The person's online storage is full (60 MB or the picture backstop): it stays in the preview until space is freed.</summary>
    StorageFull,

    /// <summary>Saving online is paused for everyone (the service's storage stop): it is saved once the service allows it again.</summary>
    Paused,

    /// <summary>The service owner turned saving off for this account or for everyone.</summary>
    Blocked,

    /// <summary>Today's saving limit is reached: it is saved tomorrow.</summary>
    DailyLimit,

    /// <summary>Saving it failed three times today, or the service refused the file.</summary>
    Failed,

    /// <summary>The service cannot be reached: it is tried again later.</summary>
    Offline,
}

/// <summary>
/// The place where the saving-online queue (S5b-6, <c>PictureUploader</c> in <c>DbdPresence.Online</c>) plugs in. The picture model tells it
/// what was saved or removed; it decides whether, when and how to upload. A publish of a status never calls it (FR-PACK-13).
/// </summary>
public interface IPictureUploadSink
{
    /// <summary>A picture file was saved on this PC (added or imported).</summary>
    void Enqueue(PictureUploadRequest request);

    /// <summary>A picture was removed from every character; its online copy, if any, should be deleted.</summary>
    void Remove(string sha256);
}
