using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Online;
using DbdPresence.Packs.Local;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// The user's uploads (FR-ONL-09, FR-ONL-11; UI_SPEC Account page): the upload policy shown before the first upload, the list of uploaded images
/// with what they are used for and a local preview (the UI never loads a remote image: only the matching file on this PC is shown, when there is
/// one), and delete with a confirmation step. Uploads are unlisted, not private, and Discord may keep showing a deleted image for a while; the page
/// says both. Uploading itself starts from an own-image row on the Packs page (<see cref="PacksViewModel"/>).
/// </summary>
public sealed partial class UploadsViewModel : ObservableObject, IDisposable
{
    private readonly IUploadService _uploads;
    private readonly IAccountService _account;
    private readonly ISettingsService _settings;
    private readonly PackLibrary _library;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>Initializes a new instance of the <see cref="UploadsViewModel"/> class.</summary>
    /// <param name="uploads">The upload service.</param>
    /// <param name="account">The account (uploads need online features).</param>
    /// <param name="settings">The settings (which own-image slots use an upload).</param>
    /// <param name="library">The pack library (local previews of uploaded images).</param>
    /// <param name="dispatcher">Runs UI updates on the UI thread.</param>
    public UploadsViewModel(IUploadService uploads, IAccountService account, ISettingsService settings, PackLibrary library, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _uploads = uploads;
        _account = account;
        _settings = settings;
        _library = library;
        _dispatcher = dispatcher;
        _uploads.Changed += OnChanged;
        _account.Changed += OnChanged;
        _settings.Changed += OnSettingsChanged;
        Rebuild();
    }

    /// <summary>Gets the upload policy, one rule per line.</summary>
    public IReadOnlyList<string> PolicyRules { get; } = UploadPolicy.Rules;

    /// <summary>Gets what "unlisted" means.</summary>
    public string UnlistedText { get; } = UploadPolicy.Unlisted;

    /// <summary>Gets the note that Discord may keep a deleted image for a while.</summary>
    public string DiscordCacheText { get; } = UploadPolicy.DiscordCache;

    /// <summary>Gets the rule for files.</summary>
    public string FileRulesText { get; } = UploadPolicy.FileRules;

    /// <summary>Gets a value indicating whether the user is signed in with online features available, so uploads can be used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPolicy), nameof(CanUpload), nameof(UnavailableText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(AcceptPolicyCommand))]
    public partial bool IsAvailable { get; private set; }

    /// <summary>Gets a value indicating whether the user is signed in at all (the section shows only then).</summary>
    [ObservableProperty]
    public partial bool IsSignedIn { get; private set; }

    /// <summary>Gets a value indicating whether the upload policy was accepted on this PC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPolicy), nameof(CanUpload))]
    public partial bool PolicyAccepted { get; private set; }

    /// <summary>Gets a value indicating whether the policy and its Accept button show (available, not yet accepted).</summary>
    public bool ShowPolicy => IsAvailable && !PolicyAccepted;

    /// <summary>Gets a value indicating whether an upload may be started now (available, policy accepted, not blocked, under the limit).</summary>
    public bool CanUpload => IsAvailable && PolicyAccepted && BlockedText is null && !IsAtLimit && !IsBusy;

    /// <summary>Gets why uploads cannot be used now, or null.</summary>
    public string? UnavailableText => IsAvailable ? null : "Uploading needs an active account with online features. Your own images still work in your preview.";

    /// <summary>Gets the uploads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(HasNoItems))]
    public partial IReadOnlyList<UploadItemViewModel> Items { get; private set; } = [];

    /// <summary>Gets a value indicating whether there is at least one upload.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Gets a value indicating whether there are no uploads.</summary>
    public bool HasNoItems => Items.Count == 0;

    /// <summary>Gets "2 of 20 images uploaded", or an empty string before the list was read.</summary>
    [ObservableProperty]
    public partial string QuotaText { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the limit is reached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpload))]
    public partial bool IsAtLimit { get; private set; }

    /// <summary>Gets why new uploads are refused (blocked by the owner, online features paused), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpload), nameof(HasBlockedText))]
    public partial string? BlockedText { get; private set; }

    /// <summary>Gets a value indicating whether <see cref="BlockedText"/> has text.</summary>
    public bool HasBlockedText => BlockedText is not null;

    /// <summary>Gets the result of the last action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether <see cref="Message"/> has text.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>Gets a value indicating whether an action is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpload))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Stops listening.</summary>
    public void Dispose()
    {
        _uploads.Changed -= OnChanged;
        _account.Changed -= OnChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Uploads one own-image file (already a clean PNG made by the image helper). Used by the Packs page.</summary>
    /// <param name="png">The PNG bytes.</param>
    /// <returns>The outcome.</returns>
    public async Task<UploadResult> UploadAsync(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        IsBusy = true;
        try
        {
            var result = await _uploads.UploadAsync(png, CancellationToken.None).ConfigureAwait(true);
            Message = result.Message;
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Records that the user accepted the upload policy.</summary>
    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private void AcceptPolicy()
    {
        _uploads.AcceptPolicy();
        Message = "Upload policy accepted. You can now upload your own images from the Characters page.";
    }

    /// <summary>Reads the list from the service again.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Message = (await _uploads.RefreshAsync(CancellationToken.None).ConfigureAwait(true)).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => IsAvailable && !IsBusy;

    private async Task DeleteAsync(UploadItemViewModel item)
    {
        IsBusy = true;
        try
        {
            var result = await _uploads.DeleteAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
            Message = result.Message;
            if (!result.Succeeded)
            {
                item.CancelDeleteCommand.Execute(null);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.Post(Rebuild);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _dispatcher.Post(Rebuild);

    private void Rebuild()
    {
        var account = _account.Current;
        var snapshot = _uploads.Current;
        IsSignedIn = account.IsSignedIn;
        IsAvailable = account.IsSignedIn && account.OnlineFeaturesAllowed;
        PolicyAccepted = snapshot.PolicyAccepted;
        var overrides = _settings.Current.Packs.Overrides;
        Items = [.. snapshot.Items.Select(u => new UploadItemViewModel(u, UsedFor(overrides, u.Sha256), _library.OverridePath(u.Sha256 + ".png"), DeleteAsync))];
        // The per-person quota (D28): bytes and a picture backstop when the service reports them, else the older image count.
        IsAtLimit = snapshot.IsKnown && (snapshot.Usage is { } usage
            ? usage.UsedFiles >= usage.LimitFiles || usage.UsedBytes >= usage.LimitBytes
            : snapshot.Items.Count >= snapshot.Limit);
        var quota = !snapshot.IsKnown
            ? string.Empty
            : snapshot.Usage is { } used
                ? string.Create(CultureInfo.InvariantCulture, $"{Characters.PictureTexts.StorageUsed(used.UsedBytes, used.LimitBytes)} · {used.UsedFiles} pictures.")
                : string.Create(CultureInfo.InvariantCulture, $"{snapshot.Items.Count} of {snapshot.Limit} images uploaded.");
        // The saving-online queue's sentence (storage full, paused, tomorrow, unreachable) follows the figures.
        QuotaText = snapshot.SavingMessage is { } saving && quota.Length > 0 ? $"{quota} {saving}" : quota;
        BlockedText = snapshot.BlockedReason switch
        {
            "uploads_blocked" => "Uploads are turned off for your account by the service owner. Images you already uploaded stay until you delete them.",
            "online_features_off" => "Online features are paused by the service owner, so nothing can be uploaded now.",
            "upload_storage_full" => "Saving pictures online is paused for everyone for now because the service's storage is nearly full. Images you already uploaded keep working.",
            null => null,
            _ => "The service is not accepting uploads right now.",
        };
    }

    private static string UsedFor(IReadOnlyDictionary<string, string> overrides, string sha256)
    {
        var slots = overrides.Where(o => string.Equals(o.Value, sha256 + ".png", StringComparison.Ordinal)).Select(o => PackLabels.ForKey(o.Key)).Order(StringComparer.Ordinal).ToList();
        return slots.Count == 0 ? "Not used for any slot on this PC" : "Used for " + string.Join(", ", slots);
    }
}

/// <summary>One uploaded image in the list.</summary>
public sealed partial class UploadItemViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="UploadItemViewModel"/> class.</summary>
    /// <param name="upload">The upload.</param>
    /// <param name="usedFor">The slots it is used for, in words.</param>
    /// <param name="localPreviewPath">The matching own-image file on this PC, or null.</param>
    /// <param name="delete">Deletes it.</param>
    public UploadItemViewModel(UploadInfo upload, string usedFor, string? localPreviewPath, Func<UploadItemViewModel, Task> delete)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(delete);
        Id = upload.Id;
        UsedFor = usedFor;
        Preview = localPreviewPath is null ? null : new PackImageLocation(localPreviewPath);
        Details = string.Create(
            CultureInfo.InvariantCulture,
            $"{(upload.Format == "png" ? "PNG" : "JPEG")} · {upload.Width} × {upload.Height} · {Math.Max(1, (upload.Size + 512) / 1024)} KB · uploaded {upload.CreatedAt.ToLocalTime():d MMM yyyy}");
        AutomationName = $"Uploaded image, {usedFor}";
        DeleteCommand = new RelayCommand(() => IsConfirming = true);
        CancelDeleteCommand = new RelayCommand(() => IsConfirming = false);
        ConfirmDeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    /// <summary>Gets the upload id.</summary>
    public string Id { get; }

    /// <summary>Gets which slots use it.</summary>
    public string UsedFor { get; }

    /// <summary>Gets format, size and date.</summary>
    public string Details { get; }

    /// <summary>Gets the local file shown as its preview, or null when this PC has no copy.</summary>
    public PackImageLocation? Preview { get; }

    /// <summary>Gets a value indicating whether there is no local preview (a labelled tile shows instead).</summary>
    public bool HasNoPreview => Preview is null;

    /// <summary>Gets the accessible name of the row.</summary>
    public string AutomationName { get; }

    /// <summary>Gets a value indicating whether the delete confirmation shows.</summary>
    [ObservableProperty]
    public partial bool IsConfirming { get; private set; }

    /// <summary>Gets the command that asks for confirmation.</summary>
    public IRelayCommand DeleteCommand { get; }

    /// <summary>Gets the command that keeps the upload.</summary>
    public IRelayCommand CancelDeleteCommand { get; }

    /// <summary>Gets the command that deletes it.</summary>
    public IAsyncRelayCommand ConfirmDeleteCommand { get; }
}
