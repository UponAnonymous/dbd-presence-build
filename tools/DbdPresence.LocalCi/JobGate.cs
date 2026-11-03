namespace DbdPresence.LocalCi;

public readonly record struct GateDecision(bool Allowed, string Reason);

/// <summary>
/// Decides whether a new runner, or heavy maintenance such as a download or the weekly image build, may start now (FR-CI-03): never while Dead by Daylight runs. Only the names of running
/// programs are read. A Linux job does not wait for the owner to be idle: it runs in its own virtual machine and opens nothing on the owner's screen (decision D41: only Linux jobs run on the PC).
/// </summary>
public sealed class JobGate
{
    private readonly IProcessList _processes;
    private readonly Func<IReadOnlyList<string>> _names;

    /// <param name="processes">The running programs (only their names are looked at).</param>
    /// <param name="names">The names that mean the game runs; the built-in three unless the owner added more (<see cref="GameNames"/>).</param>
    public JobGate(IProcessList processes, Func<IReadOnlyList<string>>? names = null)
    {
        _processes = processes;
        _names = names ?? (() => LocalCiDefaults.GameProcessNames);
    }

    public GateDecision Check()
    {
        if (_processes.IsAnyRunning(_names()))
        {
            return new GateDecision(false, "Dead by Daylight is running");
        }

        return new GateDecision(true, "allowed");
    }
}
