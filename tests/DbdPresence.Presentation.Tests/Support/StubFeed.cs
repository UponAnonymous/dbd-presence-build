using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Presentation.Detection;
using Microsoft.Extensions.Time.Testing;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>
/// A detection feed that has confirmed the scene (a lobby) and a role through a real <see cref="MatchStateEngine"/> with Validated, Measured settings, so
/// the coordinator publishes them. Since D30 nothing is declared by hand, so this is how a test gives the app a role. MECHANICS ONLY: it is scripted, not measured.
/// </summary>
internal sealed class StubFeed : IDetectionFeed
{
    private static readonly ContextKey s_context = new("p", "1920x1080", "en-US", "1");

    private static readonly IReadOnlyDictionary<DetectorId, DetectorSettings> s_validated = new Dictionary<DetectorId, DetectorSettings>
    {
        [DetectorId.Scene] = new(DetectionTier.Validated, ThresholdProvenance.Measured, 0.05, "1", "p"),
        [DetectorId.Role] = new(DetectionTier.Validated, ThresholdProvenance.Measured, 0.05, "1", "p"),
    };

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public MatchSnapshot? Snapshot { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<DetectorId, DetectorSettings> DetectorSettings => s_validated;

    /// <inheritdoc />
    public PublicationOptions Options { get; } = new();

    /// <inheritdoc />
    public IMapNameResolver? Maps => null;

    /// <summary>Creates a feed that has confirmed the given role in a lobby (None confirms the lobby only).</summary>
    /// <param name="start">The time the scripted engine starts at.</param>
    /// <param name="role">The role to confirm.</param>
    public static StubFeed With(DateTimeOffset start, GameRole role)
    {
        var feed = new StubFeed();
        feed.Snapshot = Build(start, role);
        return feed;
    }

    /// <summary>Forgets everything the feed confirmed (nothing is detected any more) and raises <see cref="Changed"/>.</summary>
    public void Clear()
    {
        Snapshot = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Confirms a different role and raises <see cref="Changed"/>.</summary>
    /// <param name="start">The time the scripted engine starts at.</param>
    /// <param name="role">The role.</param>
    public void SetRole(DateTimeOffset start, GameRole role)
    {
        Snapshot = Build(start, role);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static MatchSnapshot Build(DateTimeOffset start, GameRole role)
    {
        var time = new FakeTimeProvider(start);
        var engine = new MatchStateEngine(time, settings: s_validated);
        engine.SetGameState(GameState.Running);
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            engine.Submit(new Observation(DetectorId.Scene, 1, time.GetUtcNow(), ObservationKind.Scene, "Lobby", 0.9, "test", s_context));
        }

        if (role != GameRole.None)
        {
            for (var i = 0; i < 3; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                engine.Submit(new Observation(DetectorId.Role, 1, time.GetUtcNow(), ObservationKind.Role, role.ToString(), 0.9, "test", s_context));
            }
        }

        return engine.Snapshot;
    }
}
