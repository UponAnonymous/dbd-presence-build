namespace DbdPresence.Core.Online;

/// <summary>Where the app's signed updater is (FR-ONL-10).</summary>
public enum AppUpdateState
{
    /// <summary>This copy was not installed with the installer (a development build), so it cannot update itself.</summary>
    NotInstalled,

    /// <summary>This copy is the Microsoft Store package: the Store delivers updates, so the app never checks or installs by itself.</summary>
    StoreManaged,

    /// <summary>No check has run yet.</summary>
    Idle,

    /// <summary>Reading and verifying the release feed.</summary>
    Checking,

    /// <summary>The verified feed offers nothing newer.</summary>
    UpToDate,

    /// <summary>Downloading and verifying a newer version.</summary>
    Downloading,

    /// <summary>A verified newer version is downloaded; it is installed on "Restart to update", after it is checked once more.</summary>
    ReadyToRestart,

    /// <summary>The last check or download did not succeed (<see cref="AppUpdateStatus.Problem"/>).</summary>
    Problem,
}

/// <summary>Why the last update check or download did not succeed.</summary>
public enum AppUpdateProblem
{
    /// <summary>Nothing went wrong.</summary>
    None,

    /// <summary>Online features are off (signed out, no current licence, or switched off by the owner): nothing was contacted.</summary>
    OnlineFeaturesOff,

    /// <summary>The service could not be reached, or asked the app to wait.</summary>
    Unreachable,

    /// <summary>No release is published yet.</summary>
    NothingPublished,

    /// <summary>The release feed was refused: not signed by the release key, expired, older than one already seen, or malformed.</summary>
    FeedRefused,

    /// <summary>A downloaded package did not match the signed feed and was deleted.</summary>
    PackageRefused,

    /// <summary>Anything else (for example the disk was full); the log has the type of failure.</summary>
    Failed,
}

/// <summary>The updater's state for the UI.</summary>
/// <param name="State">Where it is.</param>
/// <param name="Problem">Why the last attempt failed, when <see cref="AppUpdateState.Problem"/>.</param>
/// <param name="Version">The newer version being downloaded or ready, or null.</param>
/// <param name="Progress">Download progress from 0 to 100.</param>
/// <param name="LastChecked">When the last check finished, or null.</param>
public sealed record AppUpdateStatus(AppUpdateState State, AppUpdateProblem Problem, string? Version, int Progress, DateTimeOffset? LastChecked)
{
    /// <summary>Gets the state before anything ran.</summary>
    public static AppUpdateStatus Initial { get; } = new(AppUpdateState.Idle, AppUpdateProblem.None, null, 0, null);
}

/// <summary>The signed updater as the UI uses it (implemented by the app over Velopack and <c>DbdPresence.Online</c>).</summary>
public interface IAppUpdates
{
    /// <summary>Raised (on any thread) after <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the current state.</summary>
    AppUpdateStatus Current { get; }

    /// <summary>Checks now; downloads and verifies a newer version when the signed feed offers one.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the check (and any download) ended.</returns>
    Task CheckNowAsync(CancellationToken cancellationToken);

    /// <summary>Closes the app and installs the downloaded version, then starts it again. Does nothing unless <see cref="AppUpdateState.ReadyToRestart"/>.</summary>
    void RestartToUpdate();
}
