using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Core.Presence;
using DbdPresence.Discord;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Theming;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>In-memory settings with the real validator, so tests see the same corrections the app does.</summary>
internal sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
{
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    public bool IsReadOnly => false;

    public AppSettings Current { get; private set; } = SettingsValidator.Validate(initial ?? new AppSettings()).Settings;

    public int UpdateCount { get; private set; }

    public IReadOnlyList<SettingsIssue> Update(Func<AppSettings, AppSettings> change)
    {
        var previous = Current;
        var (next, issues) = SettingsValidator.Validate(change(previous));
        Current = next;
        UpdateCount++;
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
        return issues;
    }

    public void RaiseSaveFailed(Exception error) => SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(error));
}

internal sealed class FakeLifecycleSource : IGameLifecycleSource
{
    public event EventHandler<GameLifecycle>? LifecycleChanged;

    public GameLifecycle Current { get; private set; } = GameLifecycle.Initial;

    public void Set(GameLifecycle lifecycle)
    {
        Current = lifecycle;
        LifecycleChanged?.Invoke(this, lifecycle);
    }

    public void SetRunning(DateTimeOffset started) =>
        Set(new GameLifecycle
        {
            State = GameState.Running,
            Process = new GameProcessInfo(4321, "DeadByDaylight-Win64-Shipping.exe", started),
            StartedUtc = started,
        });

    public int SubscriberCount => LifecycleChanged?.GetInvocationList().Length ?? 0;
}

internal sealed class FakeSink : IPresenceSink
{
    public event EventHandler<RpcHealth>? HealthChanged;

    public RpcHealth Health { get; private set; } = new(RpcStatus.NotConfigured, null, null, null, 0, null);

    public List<ValidatedActivity?> Published { get; } = [];

    public List<string?> ApplicationIds { get; } = [];

    public ValidatedActivity? Last => Published.Count == 0 ? null : Published[^1];

    public Exception? ThrowOnPublish { get; set; }

    /// <summary>Gets how many times Publish was called, including identical requests (the lock tests need "never called").</summary>
    public int PublishCalls { get; private set; }

    public void Publish(ValidatedActivity? activity)
    {
        PublishCalls++;
        if (ThrowOnPublish is { } failure)
        {
            throw failure;
        }

        // Mirrors PresencePublisher: an identical request is a no-op.
        if (Published.Count > 0 && Equals(Published[^1], activity))
        {
            return;
        }

        Published.Add(activity);
    }

    public void SetApplicationId(string? applicationId) => ApplicationIds.Add(applicationId);

    public void SetHealth(RpcHealth health)
    {
        Health = health;
        HealthChanged?.Invoke(this, health);
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeAutostart : IAutostart
{
    public bool IsEnabled { get; private set; }

    public string? FailWith { get; set; }

    public string? SetEnabled(bool enabled)
    {
        if (FailWith is not null)
        {
            return FailWith;
        }

        IsEnabled = enabled;
        return null;
    }
}

internal sealed class FakeThemeService : IThemeService
{
    public event EventHandler? ThemeChanged;

    public ResolvedTheme Current { get; private set; } =
        ThemeResolver.Resolve(new AppearanceSettings(), new SystemThemeState(AppsUseDarkTheme: true, HighContrast: false, AnimationsEnabled: true));

    public void Apply(AppearanceSettings appearance)
    {
        Current = ThemeResolver.Resolve(appearance, new SystemThemeState(AppsUseDarkTheme: true, HighContrast: false, AnimationsEnabled: true));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal static class TestIds
{
    public const string ApplicationId = "123456789012345678";
}
