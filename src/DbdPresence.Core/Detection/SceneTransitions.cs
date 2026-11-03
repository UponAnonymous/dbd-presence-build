namespace DbdPresence.Core.Detection;

/// <summary>How a scene change relates to the transition graph.</summary>
public enum TransitionKind
{
    /// <summary>From and to are the same scene.</summary>
    Same,

    /// <summary>The edge exists in ARCHITECTURE §4.2 (or the target is Unknown, which any scene may fall back to).</summary>
    Legal,

    /// <summary>Not in the graph. Applied only via Unknown after extended confirmation, and counted as an anomaly.</summary>
    Illegal,
}

/// <summary>The scene transition table of ARCHITECTURE §4.2 (FR-DET-02). The 10 × 10 table is tested cell by cell.</summary>
public static class SceneTransitions
{
    private static readonly Dictionary<Scene, Scene[]> s_edges = new()
    {
        [Scene.Unknown] = [Scene.MainMenu, Scene.Matchmaking, Scene.Lobby, Scene.Loading, Scene.InTrial, Scene.Spectating, Scene.Results, Scene.Other, Scene.InMatchMenu],
        [Scene.MainMenu] = [Scene.Matchmaking, Scene.Lobby, Scene.Loading, Scene.Other],
        [Scene.Other] = [Scene.MainMenu, Scene.Lobby],
        [Scene.Lobby] = [Scene.Other, Scene.Matchmaking, Scene.Loading, Scene.MainMenu, Scene.InTrial],
        [Scene.Matchmaking] = [Scene.Lobby, Scene.MainMenu],
        [Scene.Loading] = [Scene.InTrial, Scene.MainMenu],
        [Scene.InTrial] = [Scene.Spectating, Scene.Results, Scene.MainMenu, Scene.InMatchMenu],
        [Scene.Spectating] = [Scene.Results, Scene.MainMenu, Scene.InMatchMenu],
        [Scene.InMatchMenu] = [Scene.InTrial, Scene.Spectating, Scene.Results, Scene.MainMenu],
        [Scene.Results] = [Scene.MainMenu, Scene.Lobby, Scene.Matchmaking, Scene.Other],
    };

    /// <summary>Classifies the change from one scene to another.</summary>
    public static TransitionKind Classify(Scene from, Scene to)
    {
        if (from == to)
        {
            return TransitionKind.Same;
        }

        return to == Scene.Unknown || Array.IndexOf(s_edges[from], to) >= 0
            ? TransitionKind.Legal
            : TransitionKind.Illegal;
    }

    /// <summary>Gets the scenes that may legally follow <paramref name="from"/> by observation (Unknown excluded).</summary>
    public static IReadOnlyList<Scene> LegalSuccessors(Scene from) => s_edges[from];
}
