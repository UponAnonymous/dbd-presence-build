// Covers: FR-RPC-02 (backoff resets only after a healthy session), FR-RPC-01 (next pipe within the same attempt), FR-RPC-08 (rejection forgotten
// per connection and application id; sanitised, quoted Discord text), FR-RPC-03 and NFR-REL-02 (shutdown clear within 5 per 20 s), NFR-REL-01 (subscriber isolation).
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Publishing;

public sealed class PresencePublisherResilienceTests
{
    private const double Tolerance = 0.25;

    private static double GapSeconds(IReadOnlyList<DateTimeOffset> attempts, int index) =>
        (attempts[index] - attempts[index - 1]).TotalSeconds;

    private static void AssertGap(IReadOnlyList<DateTimeOffset> attempts, int index, double nominalSeconds)
    {
        var low = Math.Max(nominalSeconds * 0.8, 1) - Tolerance;
        var high = Math.Min(nominalSeconds * 1.2, 60) + Tolerance;
        Assert.InRange(GapSeconds(attempts, index), low, high);
    }

    // ---- Q-M1: the backoff resets only when a session proved healthy -------------------------------------

    [Fact]
    public async Task DiscordThatSendsReadyThenClose_IsRetriedWithGrowingDelays()
    {
        await using var fixture = new PublisherFixture(seed: 6);
        fixture.Server.OnHandshake = (connection, _) =>
        {
            connection.SendReady();
            connection.SendClose(1000, "Going away");
        };

        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 7, 300, 0.25);

        var attempts = fixture.Server.ConnectAttempts;
        for (var k = 1; k < 7; k++)
        {
            AssertGap(attempts, k, Math.Pow(2, k - 1));
        }

        Assert.True(GapSeconds(attempts, 6) > GapSeconds(attempts, 2) * 3);
    }

    [Fact]
    public async Task AnAcknowledgedUpdate_ProvesTheSessionHealthy_AndResetsTheBackoff()
    {
        await using var fixture = new PublisherFixture(seed: 7);
        var closeAfterReady = true;
        fixture.Server.OnHandshake = (connection, _) =>
        {
            connection.SendReady();
            if (closeAfterReady)
            {
                connection.SendClose(1000, "Going away");
            }
        };
        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 4, 60, 0.25);
        closeAfterReady = false;
        await fixture.AdvanceUntilAsync(() => fixture.Publisher.Health.Status == RpcStatus.Connected, 60, 0.25);

        fixture.Publisher.Publish(TestActivities.Simple("Healthy"));
        await fixture.AdvanceSecondsAsync(2);
        Assert.NotNull(fixture.Publisher.Health.LastAckUtc);
        var attemptsBefore = fixture.Server.ConnectAttempts.Count;
        var droppedAt = fixture.Time.GetUtcNow();
        fixture.Server.Latest.Drop();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count > attemptsBefore, 20, 0.25);

        Assert.InRange((fixture.Server.ConnectAttempts[attemptsBefore] - droppedAt).TotalSeconds, 1 - Tolerance, 1.2 + Tolerance);
    }

    [Fact]
    public async Task ASessionThatLastsThirtySeconds_ResetsTheBackoffWithoutAnyUpdate()
    {
        await using var fixture = new PublisherFixture(seed: 8);
        var closeAfterReady = true;
        fixture.Server.OnHandshake = (connection, _) =>
        {
            connection.SendReady();
            if (closeAfterReady)
            {
                connection.SendClose(1000, "Going away");
            }
        };
        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 4, 60, 0.25);
        closeAfterReady = false;
        await fixture.AdvanceUntilAsync(() => fixture.Publisher.Health.Status == RpcStatus.Connected, 60, 0.25);
        await fixture.AdvanceSecondsAsync(31, 0.5);

        var attemptsBefore = fixture.Server.ConnectAttempts.Count;
        var droppedAt = fixture.Time.GetUtcNow();
        fixture.Server.Latest.Drop();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count > attemptsBefore, 20, 0.25);

        Assert.InRange((fixture.Server.ConnectAttempts[attemptsBefore] - droppedAt).TotalSeconds, 1 - Tolerance, 1.2 + Tolerance);
    }

    [Fact]
    public async Task ASessionShorterThanThirtySecondsWithoutAnUpdate_KeepsTheBackoff()
    {
        await using var fixture = new PublisherFixture(new PresencePublisherOptions { ProcessId = 1, BackoffJitter = 0 });
        fixture.Server.OnHandshake = (connection, _) =>
        {
            connection.SendReady();
            connection.SendClose(1000, "Going away");
        };

        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 5, 60, 0.25);

        var attempts = fixture.Server.ConnectAttempts;
        double[] expected = [1, 2, 4, 8];
        for (var k = 0; k < expected.Length; k++)
        {
            Assert.InRange(GapSeconds(attempts, k + 1), expected[k], expected[k] + Tolerance);
        }
    }

    // ---- Q-M2: rejections do not outlive the connection or the application id ----------------------------

    [Fact]
    public async Task ARejectedPayload_IsSentAgainOnTheNextConnection()
    {
        await using var fixture = new PublisherFixture(seed: 10);
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Error(4000, "Transient trouble");
        fixture.Publisher.Publish(TestActivities.Simple("Retry me"));
        await fixture.AdvanceSecondsAsync(2);
        Assert.Single(fixture.Server.SetActivityFrames);

        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;
        fixture.Server.Latest.Drop();
        await fixture.AdvanceUntilAsync(() => fixture.Server.Connections.Count == 2, 20, 0.25);
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal("Retry me", Assert.Single(fixture.Server.Connections[1].SetActivityFrames).Details);
        Assert.NotNull(fixture.Publisher.Health.LastAckUtc);
    }

    [Fact]
    public async Task ARejectedPayload_IsSentAgainAfterTheApplicationIdChanges()
    {
        await using var fixture = new PublisherFixture(seed: 11);
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Error(4000, "Unknown asset");
        fixture.Publisher.Publish(TestActivities.Simple("Needs assets"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;
        fixture.Publisher.SetApplicationId("987654321098765432");
        await fixture.AdvanceSecondsAsync(3);

        Assert.Equal(2, fixture.Server.Connections.Count);
        Assert.Equal("Needs assets", Assert.Single(fixture.Server.Connections[1].SetActivityFrames).Details);
    }

    // ---- Q-M3: a throwing HealthChanged subscriber never breaks publishing -------------------------------

    [Fact]
    public async Task AThrowingHealthSubscriber_DoesNotAffectPublishingOrShutdown()
    {
        await using var fixture = new PublisherFixture();
        var thrown = 0;
        var laterSubscriberEvents = 0;
        fixture.Publisher.HealthChanged += (_, _) =>
        {
            thrown++;
            throw new InvalidOperationException("subscriber bug at C:\\Users\\private-name");
        };
        fixture.Publisher.HealthChanged += (_, _) => laterSubscriberEvents++;

        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Still published"));
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal(RpcStatus.Connected, fixture.Publisher.Health.Status);
        Assert.Equal("Still published", Assert.Single(fixture.Server.SetActivityFrames).Details);
        Assert.NotNull(fixture.Publisher.Health.LastAckUtc);
        Assert.True(thrown >= 2);
        Assert.Equal(thrown, laterSubscriberEvents);
        Assert.Equal(thrown, fixture.Publisher.Health.HandlerFailures);
        Assert.Equal(nameof(InvalidOperationException), fixture.Publisher.Health.LastHandlerFailureType);

        await fixture.Publisher.ShutdownAsync().Guard();

        Assert.True(fixture.Server.Latest.Received[^1].IsClear);
        Assert.True(fixture.Server.Latest.ClosedByClient);
    }

    [Fact]
    public async Task ASubscriberFailure_IsShownAsTheErrorWhenNothingElseIsSet()
    {
        await using var fixture = new PublisherFixture();
        fixture.Publisher.HealthChanged += (_, health) =>
        {
            if (health.Status == RpcStatus.Connected)
            {
                throw new InvalidOperationException("only when connected");
            }
        };

        fixture.Publisher.SetApplicationId(TestActivities.ApplicationId);
        await fixture.SettleAsync();

        Assert.Equal("A health subscriber threw InvalidOperationException.", fixture.Publisher.Health.LastError);
        Assert.Equal(1, fixture.Publisher.Health.HandlerFailures);
    }

    // ---- Q-L1: the shutdown clear stays within Discord's documented 5 updates per 20 s -------------------

    [Fact]
    public async Task ShutdownClear_KeepsEveryTwentySecondWindowAtFiveSendsOrFewer()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        for (var i = 0; i < 4; i++)
        {
            fixture.Publisher.Publish(TestActivities.Simple($"update {i}"));
            await fixture.AdvanceSecondsAsync(1.5);
        }

        // The limiter has no slot left; the clear is the deliberate extra send.
        fixture.Publisher.Publish(TestActivities.Simple("update 4"));
        await fixture.AdvanceSecondsAsync(1.5);
        await fixture.Publisher.ShutdownAsync().Guard();

        var sends = fixture.Server.SetActivityFrames;
        Assert.Equal(5, sends.Count);
        Assert.True(sends[^1].IsClear);
        for (var i = 0; i + 5 < sends.Count; i++)
        {
            Assert.True(sends[i + 5].At - sends[i].At >= TimeSpan.FromSeconds(20));
        }

        Assert.True(sends.Count <= 5);
    }

    // ---- S-L2: continue with the next pipe in the same attempt -------------------------------------------

    [Fact]
    public async Task AHandshakeThatFailsOnOnePipe_ContinuesWithTheNextPipeInTheSameAttempt()
    {
        await using var fixture = new PublisherFixture(seed: 12);
        fixture.Server.EndpointCount = 3;
        fixture.Server.OnHandshake = (connection, frame) =>
        {
            switch (connection.EndpointIndex)
            {
                case 0:
                    break;
                case 1:
                    connection.SendClose(4000, "Not this one");
                    break;
                default:
                    connection.SendReady();
                    break;
            }
        };

        await fixture.ConfigureAsync();
        await fixture.AdvanceSecondsAsync(6, 0.25);

        Assert.Equal(RpcStatus.Connected, fixture.Publisher.Health.Status);
        Assert.Equal("discord-ipc-2", fixture.Publisher.Health.Endpoint);
        Assert.Equal(["discord-ipc-0", "discord-ipc-1", "discord-ipc-2"], fixture.Server.Connections.Select(c => c.Endpoint));
        var attempts = fixture.Server.ConnectAttempts;
        Assert.Equal(3, attempts.Count);
        Assert.InRange((attempts[1] - attempts[0]).TotalSeconds, 5, 5 + Tolerance);
        Assert.Equal(attempts[1], attempts[2]);
    }

    [Fact]
    public async Task WhenEveryPipeFailsTheHandshake_TheBackoffStartsAndTheNextAttemptBeginsAtPipeZero()
    {
        await using var fixture = new PublisherFixture(new PresencePublisherOptions { ProcessId = 1, BackoffJitter = 0 });
        fixture.Server.EndpointCount = 3;
        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Close(4000, "Nope");

        await fixture.ConfigureAsync();
        Assert.Equal(3, fixture.Server.ConnectAttempts.Count);
        Assert.Equal(RpcStatus.Disconnected, fixture.Publisher.Health.Status);
        Assert.NotNull(fixture.Publisher.Health.NextRetryUtc);
        await fixture.AdvanceSecondsAsync(1.2, 0.1);

        Assert.Equal(6, fixture.Server.ConnectAttempts.Count);
        Assert.Equal("discord-ipc-0", fixture.Server.Connections[3].Endpoint);
        Assert.Contains("Nope", fixture.Publisher.Health.LastError, StringComparison.Ordinal);
    }

    // ---- S-M2 / S-L1 through the publisher ---------------------------------------------------------------

    [Fact]
    public async Task HostileDiscordText_ReachesHealthSanitisedCappedAndQuoted()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = (connection, frame) =>
            connection.SendError(frame.Nonce, 4000, "line one\\nline\\u202Etwo " + new string('z', 10_000));

        fixture.Publisher.Publish(TestActivities.Simple("Bad"));
        await fixture.AdvanceSecondsAsync(2);

        var error = fixture.Publisher.Health.LastError!;
        Assert.StartsWith("Discord rejected the activity (error 4000). Discord said: \"line one linetwo z", error, StringComparison.Ordinal);
        Assert.EndsWith("…\"", error, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', error);
        Assert.DoesNotContain('\u202E', error);
        Assert.True(error.Length < 300);
    }

    [Fact]
    public async Task RejectedHandshakeText_ReachesHealthQuotedAsDiscordSpeech()
    {
        await using var fixture = new PublisherFixture(seed: 3);
        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Close(4000, "Invalid Client ID");

        await fixture.ConfigureAsync();

        Assert.Equal(
            "Discord rejected the connection (code 4000). Discord said: \"Invalid Client ID\"",
            fixture.Publisher.Health.LastError);
    }
}
