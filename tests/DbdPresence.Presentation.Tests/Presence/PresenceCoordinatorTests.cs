// Covers: FR-RPC-07 (application id drives the publisher), FR-VIS-03 + FR-RPC-05 (pause clears or goes generic),
// FR-VIS-02 (streamer mode), FR-RPC-05 (hide full activity), FR-RPC-06 (not running clears by default),
// FR-PROC-05 (session timer), FR-TPL-03 (tokens only from available values), and the coordinator contract in
// ARCHITECTURE §5 (single writer, nothing published before Start).
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Discord;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class PresenceCoordinatorTests
{
    [Fact]
    public void BeforeStart_NothingIsPublishedOrConfigured()
    {
        using var h = new Harness(Harness.Configured());

        Assert.Empty(h.Sink.Published);
        Assert.Empty(h.Sink.ApplicationIds);
        Assert.True(h.Coordinator.Snapshot.IsConfigured);
    }

    [Fact]
    public void FreshInstall_IsConfiguredWithTheCatalogApplication()
    {
        using var h = new Harness();
        h.Coordinator.Start();

        Assert.Equal(["1563291325107937340"], h.Sink.ApplicationIds);
        Assert.True(h.Coordinator.Snapshot.IsConfigured);
        Assert.Equal("Dead by Daylight", h.Coordinator.Application.Name);
        Assert.False(h.Coordinator.Application.IsOwn);
    }

    [Fact]
    public void DisplayName_IsPublishedAsTheActivityName_AndChangesRepublish()
    {
        using var h = new Harness(new AppSettings { Discord = new DiscordSettings { DisplayName = "Fog Watcher" } });
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());
        Assert.Equal("Fog Watcher", h.Sink.Last?.NameOverride);

        h.Settings.Update(s => s with { Discord = s.Discord with { DisplayName = "DBD" } });
        Assert.Equal("DBD", h.Sink.Last?.NameOverride);

        h.Settings.Update(s => s with { Discord = s.Discord with { DisplayName = null } });
        Assert.Null(h.Sink.Last?.NameOverride);
    }

    [Fact]
    public void Start_OwnApplicationWithoutId_LeavesThePublisherIdle()
    {
        using var h = new Harness(Harness.Unconfigured());
        h.Coordinator.Start();

        Assert.Equal([null], h.Sink.ApplicationIds);
        Assert.False(h.Coordinator.Snapshot.IsConfigured);
        Assert.Null(h.Sink.Last);
    }

    [Fact]
    public void GameNotRunning_ClearsByDefault()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();

        Assert.Equal([TestIds.ApplicationId], h.Sink.ApplicationIds);
        Assert.Equal(PresenceState.NotRunning, h.Coordinator.Snapshot.State);
        Assert.Null(h.Sink.Last);
    }

    [Fact]
    public void GameRunning_PublishesTheRunningTemplate_WithTheSessionStart()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        var started = h.Time.GetUtcNow().AddMinutes(-5);

        h.Game.SetRunning(started);

        var activity = h.Sink.Last;
        Assert.NotNull(activity);
        Assert.Equal("Playing Dead by Daylight", activity.Details);
        Assert.Equal(started, activity.StartTimestamp);
        Assert.Equal(PresenceState.Running, h.Coordinator.Snapshot.Composition.TemplateState);
    }

    [Fact]
    public void ADeclaredRoleFromAnOlderSettingsFile_IsIgnored_BecauseNothingIsDeclaredByHandAnyMore()
    {
        // D30: the role comes only from detection. An older file may still carry a declared role; it is read, ignored and cleared.
#pragma warning disable CS0618 // The point of the test.
        var settings = Harness.Configured() with
        {
            Profiles = [BuiltInProfiles.Default with { Manual = new ManualDetails { Role = Role.Killer } }],
        };
#pragma warning restore CS0618
        using var h = new Harness(settings);
        h.Coordinator.Start();

        h.Game.SetRunning(h.Time.GetUtcNow());

        Assert.Null(h.Sink.Last?.State);
        Assert.Equal("Playing Dead by Daylight", h.Sink.Last?.Details);
    }

    [Fact]
    public void ADetectedRole_AppearsInTheLobbyText_AndTheSnapshotCarriesTheInputsUsed()
    {
        using var h = new Harness(Harness.Configured(), Role.Killer);
        h.Coordinator.Start();

        h.Game.SetRunning(h.Time.GetUtcNow());

        Assert.Equal("Killer", h.Sink.Last?.State);
        Assert.Equal(Role.Killer, h.Coordinator.Snapshot.Inputs?.DetectedRole);
    }

    [Fact]
    public void Pause_ClearsByDefault_AndResumeRestores()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());
        var running = h.Sink.Last;

        h.Coordinator.SetPaused(true);
        Assert.Null(h.Sink.Last);
        Assert.True(h.Coordinator.Snapshot.Paused);

        h.Coordinator.SetPaused(false);
        Assert.Equal(running, h.Sink.Last);
    }

    [Fact]
    public void PauseWithGenericBehaviour_KeepsAGenericActivity()
    {
        var settings = Harness.Configured() with { Profiles = [BuiltInProfiles.Default with { WhenPaused = PauseBehavior.Generic }] };
        using var h = new Harness(settings);
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());

        h.Coordinator.SetPaused(true);

        Assert.Equal("Playing Dead by Daylight", h.Sink.Last?.Details);
    }

    [Fact]
    public void StreamerMode_UsesTheStreamerTemplates()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow().AddMinutes(-3));
        Assert.NotNull(h.Sink.Last?.StartTimestamp);

        h.Settings.Update(s => s with { Privacy = s.Privacy with { StreamerMode = true } });

        // The streamer-safe Running template has no timer.
        Assert.Null(h.Sink.Last?.StartTimestamp);
        Assert.True(h.Coordinator.Snapshot.StreamerMode);
    }

    [Fact]
    public void HideFullActivity_ClearsEvenWhileRunning()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());

        h.Settings.Update(s => s with { Privacy = s.Privacy with { HideFullActivity = true } });

        Assert.Null(h.Sink.Last);
        Assert.Contains(h.Coordinator.Snapshot.Composition.Notes, n => n.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChangingTheApplicationId_ReconfiguresThePublisher_AndClearingItStopsIt()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();

        h.Settings.Update(s => s with { Discord = s.Discord with { OwnApplicationId = "987654321098765432" } });
        h.Settings.Update(s => s with { Discord = s.Discord with { OwnApplicationId = string.Empty } });
        h.Settings.Update(s => s with { Privacy = s.Privacy with { StreamerMode = true } });
        Assert.False(h.Coordinator.Snapshot.IsConfigured);

        h.Settings.Update(s => s with { Discord = s.Discord with { UseOwnApplication = false } });

        Assert.Equal([TestIds.ApplicationId, "987654321098765432", null, "1563291325107937340"], h.Sink.ApplicationIds);
        Assert.True(h.Coordinator.Snapshot.IsConfigured);
    }

    [Fact]
    public void RefreshTimer_KeepsTheDurationTokenCurrent()
    {
        var template = new PresenceTemplate { Details = "Playing for {duration}" };
        var profile = BuiltInProfiles.Default with
        {
            Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Running] = template } },
        };
        using var h = new Harness(Harness.Configured() with { Profiles = [profile] });
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());
        Assert.Equal("Playing for 0 min", h.Sink.Last?.Details);

        h.Time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal("Playing for 1 min", h.Sink.Last?.Details);
    }

    [Fact]
    public void ConnectionTest_RequiresAnApplicationId_WhenUsingOwnApplication()
    {
        using var h = new Harness(Harness.Unconfigured());
        h.Coordinator.Start();

        Assert.Equal(ConnectionTestStart.NotConfigured, h.Coordinator.StartConnectionTest());
        Assert.Null(h.Coordinator.Snapshot.ConnectionTest);
    }

    [Fact]
    public void ConnectionTest_ShowsATemporaryActivity_ReportsTheAck_AndRestores()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        var start = h.Time.GetUtcNow();

        Assert.Equal(ConnectionTestStart.Started, h.Coordinator.StartConnectionTest());
        Assert.Equal("Testing DBD Presence", h.Sink.Last?.Details);
        Assert.False(h.Coordinator.Snapshot.ConnectionTest!.Succeeded);

        h.Time.Advance(TimeSpan.FromSeconds(2));
        h.Sink.SetHealth(new RpcHealth(RpcStatus.Connected, "discord-ipc-0", h.Time.GetUtcNow(), null, 1, null));
        Assert.True(h.Coordinator.Snapshot.ConnectionTest!.Succeeded);

        h.Time.Advance(PresenceCoordinator.TestDuration);
        Assert.Null(h.Sink.Last);
        Assert.True(h.Coordinator.Snapshot.ConnectionTest!.Succeeded);
        Assert.Equal(start, h.Coordinator.Snapshot.ConnectionTest.StartedUtc);
    }

    [Fact]
    public void ConnectionTest_AnAckFromBeforeTheTest_DoesNotCount()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        h.Sink.SetHealth(new RpcHealth(RpcStatus.Connected, "discord-ipc-0", h.Time.GetUtcNow(), null, 1, null));
        h.Time.Advance(TimeSpan.FromSeconds(1));

        h.Coordinator.StartConnectionTest();
        h.Sink.SetHealth(h.Sink.Health with { UpdatesInWindow = 2 });

        Assert.False(h.Coordinator.Snapshot.ConnectionTest!.Succeeded);
    }

    [Fact]
    public void HealthChanges_AreReflectedInTheSnapshot()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        var raised = 0;
        h.Coordinator.SnapshotChanged += (_, _) => raised++;

        h.Sink.SetHealth(new RpcHealth(RpcStatus.Connected, "discord-ipc-1", null, null, 0, null));

        Assert.Equal(1, raised);
        Assert.Equal(RpcStatus.Connected, h.Coordinator.Snapshot.Connection.Status);
    }

    [Fact]
    public void Dispose_StopsReactingToInputs()
    {
        var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        h.Coordinator.Dispose();

        h.Game.SetRunning(h.Time.GetUtcNow());
        h.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(h.Sink.Last);
        Assert.Equal(0, h.Game.SubscriberCount);
    }

    [Theory]
    [InlineData(GameState.NotRunning, PresenceState.NotRunning)]
    [InlineData(GameState.Launching, PresenceState.Launching)]
    [InlineData(GameState.Running, PresenceState.Running)]
    public void ProcessStates_MapToPresenceStates(GameState game, PresenceState expected) =>
        Assert.Equal(expected, PresenceCoordinator.StateFor(game));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ConnectionTest_IsRefused_WhilePausedOrHidden(bool paused, bool hidden)
    {
        using var h = new Harness(Harness.Configured() with { Privacy = new PrivacySettings { HideFullActivity = hidden } });
        h.Coordinator.Start();
        h.Coordinator.SetPaused(paused);

        Assert.Equal(ConnectionTestStart.ActivityHidden, h.Coordinator.StartConnectionTest());
        Assert.Null(h.Sink.Last);
    }

    [Fact]
    public void ConnectionTest_AnAckWithinTheDebounceGrace_DoesNotCount()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        h.Coordinator.StartConnectionTest();

        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        h.Sink.SetHealth(new RpcHealth(RpcStatus.Connected, "discord-ipc-0", h.Time.GetUtcNow(), null, 1, null));

        Assert.False(h.Coordinator.Snapshot.ConnectionTest!.Succeeded);
    }

    [Fact]
    public void AFailureInABackgroundTrigger_IsCountedAndReported_NeverThrown()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        var reported = new List<Exception>();
        h.Coordinator.RecomputeFailed += (_, e) => reported.Add(e);
        h.Sink.ThrowOnPublish = new InvalidOperationException("sink broke");

        h.Game.SetRunning(h.Time.GetUtcNow());
        h.Time.Advance(PresenceCoordinator.RefreshInterval);

        Assert.Equal(2, h.Coordinator.FailureCount);
        Assert.All(reported, e => Assert.Equal("sink broke", e.Message));

        h.Sink.ThrowOnPublish = null;
        h.Time.Advance(PresenceCoordinator.RefreshInterval);
        Assert.Equal("Playing Dead by Daylight", h.Sink.Last?.Details);
    }
}
