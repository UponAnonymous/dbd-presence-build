using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>A coordinator wired to fakes, with a fake clock.</summary>
internal sealed class Harness : IDisposable
{
    /// <param name="settings">The settings to start with.</param>
    /// <param name="detectedRole">A role the (scripted) detection has confirmed; None means detection adds nothing. Nothing is declared by hand any more (D30).</param>
    public Harness(AppSettings? settings = null, Role detectedRole = Role.None)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.Zero));
        Settings = new FakeSettingsService(settings);
        Game = new FakeLifecycleSource();
        Sink = new FakeSink();
        Feed = detectedRole == Role.None ? null : StubFeed.With(Time.GetUtcNow(), detectedRole);
        Coordinator = new PresenceCoordinator(Settings, Catalog, Game, Sink, Time, detection: Feed);
    }

    /// <summary>Gets the scripted detection feed, or null when no role was given.</summary>
    public StubFeed? Feed { get; }

    /// <summary>Confirms another role through the scripted feed.</summary>
    /// <param name="role">The role.</param>
    public void DetectRole(Role role) => (Feed ?? throw new InvalidOperationException("This harness has no detection feed.")).SetRole(Time.GetUtcNow(), role);

    public BundledApplicationCatalog Catalog { get; } = new();

    public FakeTimeProvider Time { get; }

    public FakeSettingsService Settings { get; }

    public FakeLifecycleSource Game { get; }

    public FakeSink Sink { get; }

    public PresenceCoordinator Coordinator { get; }

    public InlineDispatcher Dispatcher { get; } = new();

    /// <summary>Settings that use the user's own application with a valid ID (Advanced option).</summary>
    public static AppSettings Configured(AppSettings? settings = null) =>
        (settings ?? new AppSettings()) with { Discord = new DiscordSettings { UseOwnApplication = true, OwnApplicationId = TestIds.ApplicationId } };

    /// <summary>Settings that choose the own-application option without entering an ID: the only "not configured" state left.</summary>
    public static AppSettings Unconfigured(AppSettings? settings = null) =>
        (settings ?? new AppSettings()) with { Discord = new DiscordSettings { UseOwnApplication = true } };

    public PresenceControlsViewModel Controls() => new(Coordinator, Settings, Dispatcher, Time);

    public void Dispose() => Coordinator.Dispose();
}
