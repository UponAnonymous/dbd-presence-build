// Covers: FR-AUTO-01 / FR-AUTO-02 / FR-AUTO-06 end to end through the coordinator: a hooked or result picture of the owner's pack reaches Discord only
// when the detector is Validated (with the class listed), live match details are on, and the pack has the picture; the preview shows the same
// picture (D29). Mechanics only: the engine is scripted, nothing here is a measurement.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Presentation.Detection;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class AutomaticArtworkTests : IDisposable
{
    private const string Cdn = "https://cdn.example.org/packs/";
    private static readonly DateTimeOffset s_start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly Harness _host = new();
    private readonly PackHarness _packs;
    private readonly FakeSink _sink = new();
    private readonly FakeLifecycleSource _game = new();
    private readonly ScriptedFeed _feed = new();
    private readonly PresenceCoordinator _coordinator;

    public AutomaticArtworkTests()
    {
        _packs = new PackHarness(_host);
        _coordinator = new PresenceCoordinator(
            _host.Settings, _host.Catalog, _game, _sink, new FakeTimeProvider(s_start.AddSeconds(60)), _packs.Provider, detection: _feed);
        _coordinator.Start();
        _game.SetRunning(s_start.AddMinutes(-1));
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _host.Dispose();
    }

    private static PackEntry Entry(string pack, string key)
    {
        var content = System.Text.Encoding.UTF8.GetBytes("image-" + key);
        return new PackEntry(
            $"{pack}-{key.Replace(':', '-')}", pack, PackEntryKind.App, key, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
            content.Length, 1024, 1024, PackImageFormat.Png, null);
    }

    private static PackEntry[] AllNine() => [.. DbdPresence.Core.Composition.AutomaticIcons.AllSlugs.Select(s => Entry("owner", "app:" + s))];

    private void GivePack(bool behaviour = false)
    {
        var packs = new[] { new PackInfo("owner", "owner", 1, "DBD Presence", "Owner content.", null, true, behaviour) };
        _packs.Library.SetSynced(new SyncedPacks(new PackIndex(packs, AllNine()), new AllCached(), e => Cdn + e.CacheFileName, null, e => "/cache/" + e.CacheFileName));
    }

    private void LiveStats(bool on) =>
        _host.Settings.Update(s => s with
        {
            Profiles = [.. s.Profiles.Select(p => p.Id == s.ActiveProfileId ? p with { Visibility = p.Visibility with { ShowLiveMatchStats = on } } : p)],
        });

    private string? Small => _sink.Last?.SmallImage;

    private static string Url(string slug) => Cdn + Entry("owner", "app:" + slug).CacheFileName;

    [Fact]
    public void AHookedSurvivor_GetsTheHookPicture_WhenEverythingIsOn()
    {
        GivePack();
        LiveStats(true);

        _feed.Trial("Survivor", OwnStatuses.Hooked, validated: true);

        Assert.Equal(Url("status-hooked"), Small);
        Assert.Equal(Url("status-hooked"), _coordinator.Snapshot.Artwork!.StatusImages["status-hooked"]);
    }

    [Fact]
    public void TheSamePictureIsInThePreview_D29()
    {
        GivePack();
        LiveStats(true);
        _feed.Trial("Survivor", OwnStatuses.Dying, validated: true);

        var preview = ActivityPreview.From(_coordinator.Snapshot.Published, "DBD", _coordinator.Snapshot.Artwork!, s_start, _packs.Provider.Match);

        Assert.Equal(Url("status-dying"), preview.SmallImage!.Reference);
        Assert.Equal(ImageTileKind.Picture, preview.SmallImage.Kind);
        Assert.Equal("/cache/" + Entry("owner", "app:status-dying").CacheFileName, preview.SmallImage.Location!.Path);
    }

    [Fact]
    public void WithLiveDetailsOff_TheRoleIconStays_EvenWhenTheHookIsConfirmedAndValidated()
    {
        GivePack();
        LiveStats(false);

        _feed.Trial("Survivor", OwnStatuses.Hooked, validated: true);

        Assert.Equal("role_survivor", Small);
    }

    [Fact]
    public void WithoutAValidationReport_NothingIsPublished_ThatIsTheDefault()
    {
        GivePack();
        LiveStats(true);

        _feed.Trial("Survivor", OwnStatuses.Hooked, validated: false);

        Assert.Equal("role_survivor", Small);
    }

    [Fact]
    public void WithAnEmptyPack_OnlyTheRoleIconShows_NothingIsBundled()
    {
        LiveStats(true);

        _feed.Trial("Survivor", OwnStatuses.Hooked, validated: true);

        Assert.Equal("role_survivor", Small);
        Assert.Empty(_coordinator.Snapshot.Artwork!.StatusImages);
    }

    [Fact]
    public void AnEscapedSurvivor_GetsTheResultPicture_OnTheResultScreen_AndLiveDetailsAreNotNeeded()
    {
        GivePack();
        LiveStats(false);

        _feed.ResultScreen("Survivor", ResultOutcomes.Escaped, validated: true);

        Assert.Equal(Url("result-escaped"), Small);
    }

    [Fact]
    public void AKillersResult_UsesTheKillerPicture_AndTheBehaviourLabelFollowsThePicture()
    {
        GivePack(behaviour: true);

        _feed.ResultScreen("Killer", ResultOutcomes.BrutalKiller, validated: true);

        Assert.Equal(Url("result-brutal"), Small);
        Assert.True(_coordinator.Snapshot.Artwork!.IsBehaviourArtwork);
    }

    [Fact]
    public void PrivateStyle_ShowsNoStatusOrResultPicture()
    {
        GivePack();
        LiveStats(true);
        _host.Settings.Update(s => s with { Privacy = s.Privacy with { StreamerMode = true } });

        _feed.Trial("Survivor", OwnStatuses.Hooked, validated: true);

        Assert.DoesNotContain(Cdn, _sink.Last?.SmallImage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsersOwnDiscordApplication_UsesAStatusKeyOnlyAfterTheUserConfirmsIt()
    {
        LiveStats(true);
        _host.Settings.Update(s => s with { Discord = s.Discord with { UseOwnApplication = true, OwnApplicationId = "123456789012345678" } });
        _feed.Trial("Survivor", OwnStatuses.Hooked, validated: true);
        Assert.Empty(_coordinator.Snapshot.Artwork!.StatusImages);

        _host.Settings.Update(s => s with
        {
            Profiles = [.. s.Profiles.Select(p => p.Id == s.ActiveProfileId ? p with { Artwork = p.Artwork with { ConfirmedUploadedKeys = ["status_hooked"] } } : p)],
        });

        Assert.Equal("status_hooked", _coordinator.Snapshot.Artwork!.StatusImages["status-hooked"]);
        Assert.Equal("status_hooked", Small);
    }

    private sealed class AllCached : IPackContentCache
    {
        public bool Contains(PackEntry entry) => true;
    }

    /// <summary>A feed whose engine is scripted into a survivor or killer trial or its result screen. MECHANICS ONLY.</summary>
    private sealed class ScriptedFeed : IDetectionFeed
    {
        private static readonly ContextKey s_context = new("p", "1920x1080", "en-US", "1");

        public event EventHandler? Changed;

        public MatchSnapshot? Snapshot { get; private set; }

        public IReadOnlyDictionary<DetectorId, DetectorSettings> DetectorSettings { get; private set; } = new Dictionary<DetectorId, DetectorSettings>();

        public PublicationOptions Options { get; } = new();

        public IMapNameResolver? Maps => null;

        public void Trial(string role, string status, bool validated)
        {
            var (engine, time) = Start(validated, [OwnStatuses.Hooked, OwnStatuses.Dying]);
            Feed(engine, time, DetectorId.Scene, ObservationKind.Scene, "Lobby", 3);
            Feed(engine, time, DetectorId.Role, ObservationKind.Role, role, 3);
            Feed(engine, time, DetectorId.Scene, ObservationKind.Scene, "Loading", 3);
            Feed(engine, time, DetectorId.Scene, ObservationKind.Scene, "InTrial", 3);
            Feed(engine, time, DetectorId.Status, ObservationKind.OwnStatus, status, 3);
            Publish(engine);
        }

        public void ResultScreen(string role, string outcome, bool validated)
        {
            var (engine, time) = Start(validated, [outcome]);
            Feed(engine, time, DetectorId.Scene, ObservationKind.Scene, "Lobby", 3);
            Feed(engine, time, DetectorId.Role, ObservationKind.Role, role, 3);
            Feed(engine, time, DetectorId.Scene, ObservationKind.Scene, "Loading", 3);
            Feed(engine, time, DetectorId.Scene, ObservationKind.Scene, "InTrial", 3);
            Feed(engine, time, DetectorId.Scene, ObservationKind.Scene, "Results", 3);
            var survivor = role == "Survivor";
            Feed(engine, time, survivor ? DetectorId.SurvivorResult : DetectorId.KillerResult, survivor ? ObservationKind.SurvivorResult : ObservationKind.KillerResult, outcome, 3);
            Publish(engine);
        }

        private (MatchStateEngine Engine, FakeTimeProvider Time) Start(bool validated, IReadOnlyList<string> classes)
        {
            static DetectorSettings Earned(IReadOnlyList<string>? list = null) => new(DetectionTier.Validated, ThresholdProvenance.Measured, 0.05, "1", "p", list);
            DetectorSettings = validated
                ? new Dictionary<DetectorId, DetectorSettings>
                {
                    [DetectorId.Scene] = Earned(),
                    [DetectorId.Role] = Earned(),
                    [DetectorId.Status] = Earned(classes),
                    [DetectorId.SurvivorResult] = Earned(classes),
                    [DetectorId.KillerResult] = Earned(classes),
                }
                : new Dictionary<DetectorId, DetectorSettings>
                {
                    [DetectorId.Scene] = Earned(),
                    [DetectorId.Role] = Earned(),
                };
            var time = new FakeTimeProvider(s_start);
            var engine = new MatchStateEngine(time, settings: DetectorSettings);
            engine.SetGameState(GameState.Running);
            engine.SetDeclaredMode(GameMode.Standard);
            return (engine, time);
        }

        private static void Feed(MatchStateEngine engine, FakeTimeProvider time, DetectorId detector, ObservationKind kind, string value, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                engine.Submit(new Observation(detector, 1, time.GetUtcNow(), kind, value, 0.9, "test", s_context));
            }
        }

        private void Publish(MatchStateEngine engine)
        {
            Snapshot = engine.Snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
