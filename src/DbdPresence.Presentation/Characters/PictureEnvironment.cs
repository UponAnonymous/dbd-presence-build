using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Presentation.Characters;

/// <summary>
/// What the Characters page learns from the account, the sync and the saving-online service (S5b-4 and S5b-6 supply it). Reading it never
/// starts a network call.
/// </summary>
public interface IPictureEnvironment
{
    /// <summary>Raised after any value changed; the page reloads.</summary>
    event EventHandler? Changed;

    /// <summary>Gets a value indicating whether the user is signed in with online features available.</summary>
    bool SignedIn { get; }

    /// <summary>Gets a value indicating whether the owner blocked saving pictures online for this account.</summary>
    bool UploadsBlocked { get; }

    /// <summary>Gets the unlisted https address of each picture saved online, by SHA-256.</summary>
    IReadOnlyDictionary<string, string> SavedUrlBySha256 { get; }

    /// <summary>Gets the bytes used online (from the service).</summary>
    long UsedBytes { get; }

    /// <summary>Gets the most bytes the service lets this user store.</summary>
    long LimitBytes { get; }

    /// <summary>Gets the id of the owner's default pack, or null when there is none.</summary>
    string? DefaultPackId { get; }

    /// <summary>
    /// Why an own picture handed to the saving-online queue is not online yet (storage full, paused, tomorrow, failed, service unreachable), or
    /// null when the queue has nothing to say about it (it is then "saving" until the upload list names it).
    /// </summary>
    /// <param name="sha256">The picture's hash.</param>
    /// <returns>The status, or null.</returns>
    PictureOnlineStatus? SavingStatusOf(string sha256);

    /// <summary>Gets the <c>sourceSha256</c> of owner entries by entry id, or null for none.</summary>
    IReadOnlyDictionary<string, string>? SourceShaByEntryId { get; }
}

/// <summary>The environment of a build without online features: signed out, nothing saved, the default 60 MB limit.</summary>
public sealed class OfflinePictureEnvironment : IPictureEnvironment
{
    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool SignedIn => false;

    /// <inheritdoc />
    public bool UploadsBlocked => false;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> SavedUrlBySha256 { get; } = new Dictionary<string, string>();

    /// <inheritdoc />
    public long UsedBytes => 0;

    /// <inheritdoc />
    public long LimitBytes => PictureLimits.OnlineQuotaBytes;

    /// <inheritdoc />
    public string? DefaultPackId => null;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? SourceShaByEntryId => null;

    /// <inheritdoc />
    public PictureOnlineStatus? SavingStatusOf(string sha256) => null;
}
