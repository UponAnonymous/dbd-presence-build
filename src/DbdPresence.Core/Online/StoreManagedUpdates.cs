namespace DbdPresence.Core.Online;

/// <summary>
/// The updater of the Microsoft Store package (FR-ONL-10; Store policy: a Store app must not update itself). It contacts nothing and
/// installs nothing: it only reports <see cref="AppUpdateState.StoreManaged"/> so the Updates section can say where updates come from.
/// </summary>
public sealed class StoreManagedUpdates : IAppUpdates
{
    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add
        {
            // The state never changes, so there is nothing to raise.
        }
        remove
        {
            // See add: no subscription is kept.
        }
    }

    /// <inheritdoc />
    public AppUpdateStatus Current { get; } = new(AppUpdateState.StoreManaged, AppUpdateProblem.None, null, 0, null);

    /// <inheritdoc />
    public Task CheckNowAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void RestartToUpdate()
    {
        // Nothing to install: the Microsoft Store installs updates.
    }
}
