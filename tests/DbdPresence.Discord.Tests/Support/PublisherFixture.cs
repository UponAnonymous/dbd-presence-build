using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Discord.Tests.Support;

/// <summary>
/// A publisher wired to a fake Discord and a fake clock. Time moves only when a test says so;
/// <see cref="SettleAsync"/> lets the background loop finish whatever the last stimulus triggered before
/// time moves again, so tests are deterministic without sleeping.
/// </summary>
internal sealed class PublisherFixture : IAsyncDisposable
{
    private static readonly TimeSpan s_defaultStep = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_hangLimit = TimeSpan.FromSeconds(20);

    public PublisherFixture(PresencePublisherOptions? options = null, int seed = 1, IDiscordTransportFactory? factory = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        Server = new FakeDiscordServer(Time);
        Options = options ?? new PresencePublisherOptions { ProcessId = 4242 };
        Publisher = new PresencePublisher(factory ?? Server, Time, Options, new Random(seed));
        Publisher.HealthChanged += (_, health) => HealthEvents.Add(health);
    }

    public FakeTimeProvider Time { get; }

    public FakeDiscordServer Server { get; }

    public PresencePublisherOptions Options { get; }

    public PresencePublisher Publisher { get; }

    public List<RpcHealth> HealthEvents { get; } = [];

    /// <summary>Configures the application id and lets the publisher connect.</summary>
    public async Task ConfigureAsync(string applicationId = TestActivities.ApplicationId)
    {
        Publisher.SetApplicationId(applicationId);
        await SettleAsync();
    }

    /// <summary>
    /// Waits until the publisher's loop has nothing left to do without a new stimulus (see
    /// <see cref="PresencePublisher.IsQuiescent"/>). This yields the processor but never sleeps; the
    /// wall-clock limit only turns a hung loop into a failed test.
    /// </summary>
    public async Task SettleAsync()
    {
        var started = Stopwatch.StartNew();
        var quiet = 0;
        while (quiet < 3)
        {
            if (started.Elapsed > s_hangLimit)
            {
                Assert.Fail($"The publisher did not become idle within {s_hangLimit.TotalSeconds} s. Health: {Publisher.Health}");
            }

            await Task.Yield();
            Thread.Yield();
            quiet = Publisher.IsQuiescent ? quiet + 1 : 0;
        }
    }

    /// <summary>Advances fake time in small steps, letting the publisher react after each one.</summary>
    public async Task AdvanceAsync(TimeSpan total, TimeSpan? step = null)
    {
        var size = step ?? s_defaultStep;
        await SettleAsync();
        var remaining = total;
        while (remaining > TimeSpan.Zero)
        {
            var chunk = remaining < size ? remaining : size;
            Time.Advance(chunk);
            remaining -= chunk;
            await SettleAsync();
        }
    }

    /// <summary>Advances time in whole seconds.</summary>
    public Task AdvanceSecondsAsync(double seconds, double stepSeconds = 0.1) =>
        AdvanceAsync(
            TimeSpan.FromMilliseconds(Math.Round(seconds * 1000)),
            TimeSpan.FromMilliseconds(Math.Round(stepSeconds * 1000)));

    /// <summary>Advances time in small steps until <paramref name="condition"/> holds, failing after <paramref name="limitSeconds"/> of fake time.</summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, double limitSeconds, double stepSeconds = 0.1)
    {
        var elapsed = 0.0;
        while (!condition())
        {
            Assert.True(elapsed < limitSeconds, $"Condition not reached within {limitSeconds} s of fake time.");
            await AdvanceSecondsAsync(stepSeconds, stepSeconds);
            elapsed += stepSeconds;
        }
    }

    /// <summary>Shuts the publisher down; a loop that never ends fails the test instead of hanging the run.</summary>
    public async ValueTask DisposeAsync() => await Publisher.DisposeAsync().Guard();
}
