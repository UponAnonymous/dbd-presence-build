using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Core.Online;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// "Updates" in Settings, Start and close (FR-ONL-10; replaces the FR-LIFE-05 "no update channel" sentence): what the signed updater is doing, a
/// "Check for updates" button and, once a verified newer version is downloaded, "Restart to update". Every sentence is honest about what
/// happened: an update whose signature or hash does not check out is never installed, and the page says so.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject, IDisposable
{
    /// <summary>The sentence before the first check.</summary>
    public const string IdleText = "DBD Presence checks for updates while you're signed in. Every update must carry the developer's signature before it is installed.";

    /// <summary>A copy that was not installed with the installer.</summary>
    public const string NotInstalledText = "This copy was not installed with DBD-Presence-Setup.exe, so it can't update itself. Install it with the setup file to get updates.";

    /// <summary>The Microsoft Store package: the Store delivers updates.</summary>
    public const string StoreManagedText = "Updates come from the Microsoft Store.";

    /// <summary>While the feed is read and verified.</summary>
    public const string CheckingText = "Checking for updates…";

    /// <summary>Online features are off.</summary>
    public const string OnlineOffText = "Updates need online features: sign in with access on the Account page. Your Discord status keeps working without them.";

    /// <summary>The service could not be reached.</summary>
    public const string UnreachableText = "Couldn't reach the update service. DBD Presence tries again later.";

    /// <summary>Nothing published yet.</summary>
    public const string NothingPublishedText = "No update has been published yet.";

    /// <summary>The feed did not verify.</summary>
    public const string FeedRefusedText = "An update was offered, but its signature didn't check out, so it was ignored. Nothing was installed.";

    /// <summary>The package did not match the feed.</summary>
    public const string PackageRefusedText = "A downloaded update didn't match its signature and was deleted. Nothing was installed.";

    /// <summary>Anything else.</summary>
    public const string FailedText = "The update check didn't finish. The log in the data folder says why.";

    private readonly IAppUpdates _updates;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>Initializes a new instance of the <see cref="UpdatesViewModel"/> class.</summary>
    /// <param name="updates">The updater.</param>
    /// <param name="dispatcher">Runs UI updates on the UI thread.</param>
    public UpdatesViewModel(IAppUpdates updates, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _updates = updates;
        _dispatcher = dispatcher;
        _updates.Changed += OnChanged;
        Rebuild();
    }

    /// <summary>Gets what the updater is doing, in one sentence.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = IdleText;

    /// <summary>Gets a value indicating whether a check may be started now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool CanCheck { get; private set; }

    /// <summary>Gets a value indicating whether a verified newer version waits for a restart.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    public partial bool IsReadyToRestart { get; private set; }

    /// <summary>Gets a value indicating whether a check or download is running.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets the download progress from 0 to 100.</summary>
    [ObservableProperty]
    public partial int Progress { get; private set; }

    /// <summary>Gets the restart button's label.</summary>
    [ObservableProperty]
    public partial string RestartLabel { get; private set; } = "Restart to update";

    /// <summary>The sentence for a status.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The sentence.</returns>
    public static string Describe(AppUpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            AppUpdateState.NotInstalled => NotInstalledText,
            AppUpdateState.StoreManaged => StoreManagedText,
            AppUpdateState.Idle => IdleText,
            AppUpdateState.Checking => CheckingText,
            AppUpdateState.UpToDate => status.LastChecked is { } at
                ? $"DBD Presence is up to date (checked {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})."
                : "DBD Presence is up to date.",
            AppUpdateState.Downloading => $"Downloading version {status.Version}… {status.Progress}%",
            AppUpdateState.ReadyToRestart => $"Version {status.Version} is downloaded and its signature checked. Restart to install it; it is checked once more first. If you restart later, it is checked again before it is offered.",
            _ => status.Problem switch
            {
                AppUpdateProblem.OnlineFeaturesOff => OnlineOffText,
                AppUpdateProblem.Unreachable => UnreachableText,
                AppUpdateProblem.NothingPublished => NothingPublishedText,
                AppUpdateProblem.FeedRefused => FeedRefusedText,
                AppUpdateProblem.PackageRefused => PackageRefusedText,
                _ => FailedText,
            },
        };
    }

    /// <inheritdoc />
    public void Dispose() => _updates.Changed -= OnChanged;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => _updates.CheckNowAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(IsReadyToRestart))]
    private void Restart() => _updates.RestartToUpdate();

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.Post(Rebuild);

    private void Rebuild()
    {
        var status = _updates.Current;
        StatusText = Describe(status);
        IsBusy = status.State is AppUpdateState.Checking or AppUpdateState.Downloading;
        CanCheck = status.State is not (AppUpdateState.NotInstalled or AppUpdateState.StoreManaged or AppUpdateState.Checking or AppUpdateState.Downloading or AppUpdateState.ReadyToRestart);
        IsReadyToRestart = status.State == AppUpdateState.ReadyToRestart;
        Progress = status.Progress;
        RestartLabel = status.Version is { } version ? $"Restart to install {version}" : "Restart to update";
    }
}
