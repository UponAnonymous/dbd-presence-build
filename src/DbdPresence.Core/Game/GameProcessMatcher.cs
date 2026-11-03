namespace DbdPresence.Core.Game;

/// <summary>Which executables count as the game or its launcher (FR-PROC-01..03; FEASIBILITY D18/G8).</summary>
public sealed class GameProcessMatcher
{
    /// <summary>Game executables known from Discord's detectable-games registry (Steam, Epic).</summary>
    public static readonly IReadOnlyList<string> DefaultGameExecutables = ["DeadByDaylight-Win64-Shipping.exe", "DeadByDaylight-EGS-Shipping.exe"];

    /// <summary>Launcher/bootstrapper executables.</summary>
    public static readonly IReadOnlyList<string> DefaultLauncherExecutables = ["DeadByDaylight.exe"];

    private readonly HashSet<string> _games;
    private readonly HashSet<string> _launchers;

    /// <summary>Initializes a new instance of the <see cref="GameProcessMatcher"/> class.</summary>
    /// <param name="extraGameExecutables">User-configured extra game executable names (e.g. another store build).</param>
    public GameProcessMatcher(IEnumerable<string>? extraGameExecutables = null)
    {
        _games = new HashSet<string>(DefaultGameExecutables.Concat(extraGameExecutables ?? []).Select(Normalise), StringComparer.OrdinalIgnoreCase);
        _launchers = new HashSet<string>(DefaultLauncherExecutables.Select(Normalise), StringComparer.OrdinalIgnoreCase);
        _launchers.ExceptWith(_games);
    }

    /// <summary>Returns the first game process (lowest PID for determinism), if any.</summary>
    public RunningProcess? FindGame(IEnumerable<RunningProcess> processes) =>
        processes.Where(p => _games.Contains(p.Name)).OrderBy(p => p.Id).FirstOrDefault();

    /// <summary>Returns whether a launcher-only process is present.</summary>
    public bool HasLauncher(IEnumerable<RunningProcess> processes) => processes.Any(p => _launchers.Contains(p.Name));

    private static string Normalise(string executable)
    {
        var name = executable.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
