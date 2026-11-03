// Covers: C1 (100% of transition-table cells, legal and illegal), FR-DET-02 (exhaustive transition table with
// guards, ARCHITECTURE §4.2). Mechanics only: observation sequences are scripted, nothing here measures accuracy.
using DbdPresence.Core.Detection;

namespace DbdPresence.Core.Tests.Detection;

public sealed class SceneTransitionTableTests
{
    // Independent transcription of the edges in the ARCHITECTURE §4.2 diagram (not read from the implementation).
    private static readonly HashSet<(Scene From, Scene To)> s_diagramEdges =
    [
        (Scene.Unknown, Scene.MainMenu), (Scene.Unknown, Scene.Matchmaking), (Scene.Unknown, Scene.Lobby), (Scene.Unknown, Scene.Loading),
        (Scene.Unknown, Scene.InTrial), (Scene.Unknown, Scene.Spectating), (Scene.Unknown, Scene.Results), (Scene.Unknown, Scene.Other),
        (Scene.Unknown, Scene.InMatchMenu),
        (Scene.MainMenu, Scene.Matchmaking), (Scene.MainMenu, Scene.Lobby), (Scene.MainMenu, Scene.Loading), (Scene.MainMenu, Scene.Other),
        (Scene.Other, Scene.MainMenu), (Scene.Other, Scene.Lobby),
        (Scene.Lobby, Scene.Other), (Scene.Lobby, Scene.Matchmaking), (Scene.Lobby, Scene.Loading), (Scene.Lobby, Scene.MainMenu),
        (Scene.Lobby, Scene.InTrial), // no loading-screen cue in the catalogue
        (Scene.Matchmaking, Scene.Lobby), (Scene.Matchmaking, Scene.MainMenu),
        (Scene.Loading, Scene.InTrial), (Scene.Loading, Scene.MainMenu),
        (Scene.InTrial, Scene.Spectating), (Scene.InTrial, Scene.Results), (Scene.InTrial, Scene.MainMenu), (Scene.InTrial, Scene.InMatchMenu),
        (Scene.Spectating, Scene.Results), (Scene.Spectating, Scene.MainMenu), (Scene.Spectating, Scene.InMatchMenu),
        (Scene.InMatchMenu, Scene.InTrial), (Scene.InMatchMenu, Scene.Spectating), (Scene.InMatchMenu, Scene.Results), (Scene.InMatchMenu, Scene.MainMenu),
        (Scene.Results, Scene.MainMenu), (Scene.Results, Scene.Lobby), (Scene.Results, Scene.Matchmaking), (Scene.Results, Scene.Other),
    ];

    public static TheoryData<Scene, Scene> AllCells()
    {
        var data = new TheoryData<Scene, Scene>();
        foreach (var from in Enum.GetValues<Scene>())
        {
            foreach (var to in Enum.GetValues<Scene>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    private static TransitionKind Expected(Scene from, Scene to) =>
        from == to ? TransitionKind.Same
        : to == Scene.Unknown || s_diagramEdges.Contains((from, to)) ? TransitionKind.Legal
        : TransitionKind.Illegal;

    [Fact]
    public void Table_HasTenByTenCells_WithHandCountedKinds()
    {
        // 10 scenes -> 100 cells: 10 diagonal; 39 diagram edges (the 31 of the original diagram plus the 8 the HUD catalogue needs:
        // Unknown -> InMatchMenu, Lobby -> InTrial, InTrial <-> InMatchMenu, Spectating <-> InMatchMenu, InMatchMenu -> Results and -> MainMenu)
        // + 9 "fall back to Unknown" = 48 legal; 42 illegal.
        var kinds = Enum.GetValues<Scene>()
            .SelectMany(from => Enum.GetValues<Scene>().Select(to => SceneTransitions.Classify(from, to)))
            .ToList();

        Assert.Equal(100, kinds.Count);
        Assert.Equal(10, kinds.Count(k => k == TransitionKind.Same));
        Assert.Equal(48, kinds.Count(k => k == TransitionKind.Legal));
        Assert.Equal(42, kinds.Count(k => k == TransitionKind.Illegal));
        Assert.Equal(39, s_diagramEdges.Count);
    }

    [Theory]
    [MemberData(nameof(AllCells))]
    public void Classify_MatchesTheDiagram_ForEveryCell(Scene from, Scene to) =>
        Assert.Equal(Expected(from, to), SceneTransitions.Classify(from, to));

    [Theory]
    [MemberData(nameof(AllCells))]
    public void Engine_BehavesAsTheTableSays_ForEveryCell(Scene from, Scene to)
    {
        // Long loss timers so that only the scripted evidence decides.
        var options = new EngineOptions { SceneLostMenu = TimeSpan.FromMinutes(5), SceneLostTrial = TimeSpan.FromMinutes(5) };
        var h = new EngineHarness(options);
        if (from != Scene.Unknown)
        {
            h.ConfirmScene(from);
        }

        Assert.Equal(from, h.Snapshot.ConfirmedScene);
        var kind = SceneTransitions.Classify(from, to);

        switch (kind)
        {
            case TransitionKind.Same when to == Scene.Unknown:
                // Unknown is never observed: a detector that says "Unknown" is malformed and changes nothing.
                Assert.Equal(SubmitResult.RejectedInvalid, h.SceneFrame(Scene.Unknown));
                break;

            case TransitionKind.Same:
                h.ConfirmScene(to, frames: 6);
                Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
                break;

            case TransitionKind.Legal when to == Scene.Unknown:
                // Evidence loss: any scene falls back to Unknown (modelled by the loss timer, not by an observation).
                var lossy = new EngineHarness(new EngineOptions { SceneLostMenu = TimeSpan.FromSeconds(10), SceneLostTrial = TimeSpan.FromSeconds(30) });
                lossy.ConfirmScene(from);
                lossy.Advance(31);
                lossy.Engine.Tick();
                Assert.Equal(Scene.Unknown, lossy.Snapshot.ConfirmedScene);
                Assert.Equal(0, lossy.Snapshot.Diagnostics.IllegalTransitions);
                return;

            case TransitionKind.Legal:
                h.ConfirmScene(to, frames: 2);
                Assert.Equal(from, h.Snapshot.ConfirmedScene); // K = 3: two frames never move the scene.
                h.ConfirmScene(to, frames: 1);
                Assert.Equal(to, h.Snapshot.ConfirmedScene);
                Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
                break;

            default:
                // Illegal: K frames are not enough; 2K frames apply it via Unknown and count an anomaly.
                h.ConfirmScene(to, frames: 5);
                Assert.Equal(from, h.Snapshot.ConfirmedScene);
                Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
                h.ConfirmScene(to, frames: 1);
                Assert.Equal(to, h.Snapshot.ConfirmedScene);
                Assert.Equal(1, h.Snapshot.Diagnostics.IllegalTransitions);
                break;
        }
    }

    [Fact]
    public void IllegalJump_MainMenuToResults_NeverCreatesASession()
    {
        // MainMenu -> Results is the architecture's own example of an illegal edge; Results with no trial is accepted
        // as a scene only after doubled evidence, and no session or outcome can attach to it.
        var h = new EngineHarness();
        h.ConfirmScene(Scene.MainMenu);
        h.ConfirmScene(Scene.Results, frames: 6);

        Assert.Equal(Scene.Results, h.Snapshot.ConfirmedScene);
        Assert.Null(h.Snapshot.Session);
        Assert.Null(h.Snapshot.LastSession);
        Assert.Equal(1, h.Snapshot.Diagnostics.IllegalTransitions);
    }
}
