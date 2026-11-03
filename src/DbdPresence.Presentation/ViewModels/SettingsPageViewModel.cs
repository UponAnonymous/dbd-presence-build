using CommunityToolkit.Mvvm.ComponentModel;
using DbdPresence.Configuration;
using DbdPresence.Core.Settings;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// Base for pages bound to settings: <see cref="Load"/> copies settings into properties (without writing back),
/// and <see cref="Apply"/> writes a change through the validating settings service. External changes (tray,
/// another page, backup restore) reload the page on the UI thread.
/// </summary>
public abstract partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="SettingsPageViewModel"/> class.</summary>
    protected SettingsPageViewModel(ISettingsService settings, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        Settings = settings;
        Dispatcher = dispatcher;
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Gets a message describing the last rejected or corrected change, if any.</summary>
    [ObservableProperty]
    public partial string? ValidationMessage { get; private set; }

    /// <summary>Gets the settings service.</summary>
    protected ISettingsService Settings { get; }

    /// <summary>Gets the UI dispatcher.</summary>
    protected IUiDispatcher Dispatcher { get; }

    /// <summary>Gets a value indicating whether properties are being loaded (changes must not be written back).</summary>
    protected bool IsLoading { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Replaces the active profile using <paramref name="change"/>.</summary>
    protected static AppSettings WithActiveProfile(AppSettings settings, Func<PresenceProfile, PresenceProfile> change)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(change);
        var active = settings.ActiveProfile;
        return settings with { Profiles = [.. settings.Profiles.Select(p => ReferenceEquals(p, active) ? change(p) : p)] };
    }

    /// <summary>Loads properties from settings. Call from the derived constructor once fields are ready.</summary>
    protected void Reload()
    {
        IsLoading = true;
        try
        {
            Load(Settings.Current);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Copies settings into properties.</summary>
    protected abstract void Load(AppSettings settings);

    /// <summary>Writes a change unless loading. Validation corrections are surfaced in <see cref="ValidationMessage"/>.</summary>
    protected void Apply(Func<AppSettings, AppSettings> change)
    {
        if (IsLoading)
        {
            return;
        }

        var issues = Settings.Update(change);
        ValidationMessage = issues.Count == 0 ? null : string.Join(" ", issues.Select(i => i.Message));
    }

    /// <summary>Releases subscriptions. Overrides must call the base implementation.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            Settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => Dispatcher.Post(() =>
    {
        if (!_disposed)
        {
            Reload();
        }
    });
}
