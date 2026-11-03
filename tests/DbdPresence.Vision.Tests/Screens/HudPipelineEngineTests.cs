// Covers: FR-DET-01 (the pipeline only emits observations; the engine alone confirms; Disabled detectors never publish), FR-DET-02, FR-DET-18 to
// FR-DET-23 end to end. MECHANICS ONLY: a scripted game session of drawn screens, read by a fake recogniser, through the real detectors and the real
// engine on a fake clock. It shows the wiring, not accuracy: every detector stays Disabled (no validation report exists).
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class HudPipelineEngineTests
{
    private const string EventId = "event-haunted-by-daylight";

    private readonly HudHarness _hud = new(ownerName: null);
    private readonly MatchStateEngine _engine;

    public HudPipelineEngineTests()
    {
        var options = new EngineOptions
        {
            IsKnownCharacterId = HudHarness.Lists.IsCharacterId,
            IsKnownKillerId = HudHarness.Lists.IsKillerId,
            IsKnownEventModeId = HudHarness.Lists.IsEventModeId,
            IsKnownMapId = id => HudHarness.Lists.RealmOfMap(id) is not null,
        };
        _engine = new MatchStateEngine(_hud.Time, options);
        _engine.SetGameState(GameState.Running);
        _engine.SetDeclaredMode(GameMode.Standard);
    }

    private MatchSnapshot Snapshot => _engine.Snapshot;

    private async Task Show(ImageBuffer image, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var result = await _hud.Frame(image, _engine.Snapshot.ConfirmedScene, advance: 0);
            foreach (var observation in result.Observations)
            {
                _engine.Submit(observation);
            }

            _engine.Tick();
            _hud.Time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task AWholeSurvivorSession_FromTheMainMenuToTheScoreboard_IsConfirmedByTheEngineStepByStep()
    {
        const int Width = 1920;
        var rows = HudScenes.SurvivorGame();

        await Show(HudScenes.MainMenu(Width), 4);
        Assert.Equal(Scene.MainMenu, Snapshot.ConfirmedScene);
        Assert.True(_hud.Pipeline.OwnerNameKnown); // learned from the top right of the menu

        await Show(HudScenes.ModeList(Width, eventList: true), 7);
        Assert.Equal(Scene.Other, Snapshot.ConfirmedScene);
        Assert.Equal(EventId, Snapshot.QueueMode.ConfirmedValue);

        await Show(HudScenes.Lobby(Width), 4);
        Assert.Equal(Scene.Lobby, Snapshot.ConfirmedScene);

        await Show(HudScenes.Searching(Width, "HAUNTED BY DAYLIGHT"), 7);
        Assert.Equal(Scene.Matchmaking, Snapshot.ConfirmedScene);
        Assert.Equal(EventId, Snapshot.QueueMode.ConfirmedValue);

        await Show(HudScenes.PreMatchSurvivor(Width, "DWIGHT FAIRFIELD"), 8);
        Assert.Equal(Scene.Lobby, Snapshot.ConfirmedScene);
        Assert.Equal("dwight-fairfield", Snapshot.Character.ConfirmedValue);
        Assert.Equal(Role.Survivor, Snapshot.DetectedRole);
        Assert.Equal(FieldSource.Detected, Snapshot.Character.Source);

        await Show(HudScenes.MatchStart(Width, "MOUNT ORMOND RESORT", "ORMOND"), 8);
        Assert.Equal(Scene.InTrial, Snapshot.ConfirmedScene);
        Assert.Equal(0, Snapshot.Diagnostics.IllegalTransitions);
        Assert.Equal(StartQuality.Exact, Snapshot.Session!.Quality);
        Assert.Equal("ormond/mount-ormond-resort", Snapshot.Map.ConfirmedValue);
        Assert.Equal("dwight-fairfield", Snapshot.Character.ConfirmedValue); // survived the run-up to the trial
        var session = Snapshot.Session;

        await Show(HudScenes.TrialHud(Width), 4);
        Assert.Equal(Scene.InTrial, Snapshot.ConfirmedScene);

        await Show(HudScenes.MatchDetails(Width, rows, 1, "ORMOND - MOUNT ORMOND RESORT"), 8);
        Assert.Equal(Scene.InMatchMenu, Snapshot.ConfirmedScene);
        Assert.Equal(session, Snapshot.Session); // the menu over the trial does not restart it
        Assert.Equal("ormond/mount-ormond-resort", Snapshot.Map.ConfirmedValue);
        Assert.Equal("the-lich", Snapshot.Killer.ConfirmedValue);
        Assert.Equal("dwight-fairfield", Snapshot.Character.ConfirmedValue);
        Assert.Equal(0, Snapshot.Diagnostics.UndetectedBoundaries);

        await Show(HudScenes.Settings(Width), 4);
        Assert.Equal(Scene.InMatchMenu, Snapshot.ConfirmedScene); // opened from a trial: it belongs to the trial
        Assert.Equal(session, Snapshot.Session);

        await Show(HudScenes.TrialHud(Width), 4);
        Assert.Equal(Scene.InTrial, Snapshot.ConfirmedScene);
        Assert.Equal(session, Snapshot.Session);

        await Show(HudScenes.Scoreboard(Width, rows, 1), 8);
        Assert.Equal(Scene.Results, Snapshot.ConfirmedScene);
        Assert.Null(Snapshot.Session);
        Assert.Equal("ormond/mount-ormond-resort", Snapshot.LastSession!.MapId);
        Assert.Equal("the-lich", Snapshot.Killer.ConfirmedValue);
        Assert.Equal("dwight-fairfield", Snapshot.Character.ConfirmedValue);

        Assert.Equal(0, Snapshot.Diagnostics.IllegalTransitions);
        Assert.All(Snapshot.Health.Values, health => Assert.NotEqual(HealthState.Degraded, health.State));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ALobbyFollowedByWeakTrialHudFramesOnly_NeverReachesInTrialOrSpectating(bool survivorView, bool spectatorBar)
    {
        // Review U5 F1 / F5(g), with the real detectors and the real engine: the pre-match lobby is confirmed, then its READY button goes unread while
        // the player column shows names. The HUD cue is weak (0.65, or 0.75 with the owner's name), so the lobby must stand.
        await Show(HudScenes.MainMenu(1920), 4);
        await Show(HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"), 8);
        Assert.Equal(Scene.Lobby, Snapshot.ConfirmedScene);

        await Show(HudScenes.TrialHud(1920, survivorView, spectatorBar: spectatorBar), 7);

        Assert.Equal(Scene.Lobby, Snapshot.ConfirmedScene);
        Assert.Null(Snapshot.Session);
        Assert.Equal(0, Snapshot.Diagnostics.IllegalTransitions);
    }

    [Fact]
    public async Task TheMatchStartCard_StillCarriesTheLobbyIntoTheTrial_AsAnExactStart()
    {
        await Show(HudScenes.MainMenu(1920), 4);
        await Show(HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"), 8);

        await Show(HudScenes.MatchStart(1920, "MOUNT ORMOND RESORT", "ORMOND"), 4);

        Assert.Equal(Scene.InTrial, Snapshot.ConfirmedScene);
        Assert.Equal(StartQuality.Exact, Snapshot.Session!.Quality);
    }

    [Fact]
    public async Task EveryDetectorIsDisabled_SoNothingOfThisReachesDiscord()
    {
        await Show(HudScenes.MainMenu(1920), 4);
        await Show(HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"), 8);
        Assert.Equal("dwight-fairfield", Snapshot.Character.ConfirmedValue);

        var none = PublicationPolicy.Compose(Snapshot, new Dictionary<DetectorId, DetectorSettings>(), new PublicationOptions(), HudHarness.Vocabulary, _hud.Time.GetUtcNow(), null);

        Assert.Equal(PresenceState.Running, none.Inputs.State);
        Assert.Equal(Role.None, none.Inputs.DetectedRole);
        Assert.Contains(none.Suppressed, s => s.Field == FieldKind.Scene);
        Assert.All(_hud.Pipeline.Detectors, d => Assert.Equal(DetectionTier.Disabled, d.DefaultTier));
    }

    [Fact]
    public async Task ASingleMisreadFrame_NeverChangesAConfirmedFieldOrTheScene()
    {
        await Show(HudScenes.MainMenu(1920), 4);
        await Show(HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"), 8);
        Assert.Equal("dwight-fairfield", Snapshot.Character.ConfirmedValue);

        // One frame in which the owner's pair names another character, then the true screen again.
        await Show(HudScenes.PreMatchSurvivor(1920, "MEG THOMAS"), 1);
        await Show(HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"), 2);

        Assert.Equal(Scene.Lobby, Snapshot.ConfirmedScene);
        Assert.Equal("dwight-fairfield", Snapshot.Character.ConfirmedValue);
    }

    [Fact]
    public async Task AnAnonymousScoreboard_ConfirmsNoCharacterAndNoRole()
    {
        await Show(HudScenes.MainMenu(1920), 4);
        var rows = new List<PlayerLine>
        {
            new("MEG THOMAS", "MEG THOMAS"),
            new("DWIGHT FAIRFIELD", "DWIGHT FAIRFIELD"),
            new("CLAUDETTE MOREL", "CLAUDETTE MOREL"),
            new("NEA KARLSSON", "NEA KARLSSON"),
            new("THE LICH", "THE LICH"),
        };

        await Show(HudScenes.Scoreboard(1920, rows, 1), 10);

        Assert.Equal(Scene.Results, Snapshot.ConfirmedScene);
        Assert.Equal(FieldState.Unknown, Snapshot.Character.State);
        Assert.Equal(Role.None, Snapshot.DetectedRole);
    }

    [Fact]
    public async Task StartingTheAppInsideAMatch_TabRecoversTheMapTheCharacterAndTheKiller_OnceTheOwnersNameIsKnown()
    {
        var hud = new HudHarness(ownerName: HudScenes.Owner);
        var engine = new MatchStateEngine(hud.Time);
        engine.SetGameState(GameState.Running);
        engine.SetDeclaredMode(GameMode.Standard);
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "THE DECIMATED BORGO - THE SHATTERED SQUARE");

        for (var i = 0; i < 8; i++)
        {
            var result = await hud.Frame(tab, engine.Snapshot.ConfirmedScene, advance: 0);
            foreach (var observation in result.Observations)
            {
                engine.Submit(observation);
            }

            engine.Tick();
            hud.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var s = engine.Snapshot;
        Assert.Equal(Scene.InMatchMenu, s.ConfirmedScene);
        Assert.Equal(StartQuality.JoinedLate, s.Session!.Quality);
        Assert.Equal("the-decimated-borgo/the-shattered-square", s.Map.ConfirmedValue);
        Assert.Equal("dwight-fairfield", s.Character.ConfirmedValue);
        Assert.Equal("the-lich", s.Killer.ConfirmedValue);
        Assert.Equal(Role.Survivor, s.DetectedRole);
    }

    [Fact]
    public async Task AKillerSession_TabAndTheScoreboardGiveTheKillersNameAndTheOwnersRole()
    {
        var rows = HudScenes.SurvivorGame("THE NIGHTMARE", 4);
        await Show(HudScenes.MainMenu(1920), 4);
        await Show(HudScenes.PreMatchKiller(1920), 8);
        Assert.Equal(Role.Killer, Snapshot.DetectedRole);
        Assert.Equal(FieldState.Unknown, Snapshot.Character.State); // the killer's own character is not shown in the lobby

        await Show(HudScenes.MatchStart(1920, "ROTTEN FIELDS", "COLDWIND FARM"), 8);
        await Show(HudScenes.MatchDetails(1920, rows, 4, "COLDWIND FARM - ROTTEN FIELDS", survivorView: false), 8);

        Assert.Equal(Scene.InMatchMenu, Snapshot.ConfirmedScene);
        Assert.Equal("the-nightmare", Snapshot.Character.ConfirmedValue);
        Assert.Equal("the-nightmare", Snapshot.Killer.ConfirmedValue);
        Assert.Equal("coldwind-farm/rotten-fields", Snapshot.Map.ConfirmedValue);
        Assert.Equal(Role.Killer, Snapshot.DetectedRole);
    }
}
