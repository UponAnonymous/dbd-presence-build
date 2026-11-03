namespace DbdPresence.LocalCi;

/// <summary>
/// What the program remembers about the dedicated account's Linux machine between uses (round 3, H1, M3, L12): the text of <c>wsl --version</c>, and the boot id and kernel of the last job's machine. The
/// boot id is how a new job proves its machine is not the last job's (a machine that was not stopped keeps its boot id); the rest is shown by the status. Nothing here is a secret.
/// </summary>
public interface IMachineRecord
{
    /// <summary>The boot id of the last job's Linux machine; null when no job has run (or the state file was lost).</summary>
    string? LastBootId { get; }

    /// <summary>Remembers what <c>wsl --version</c> printed, and when.</summary>
    void RecordVersion(string text);

    /// <summary>Remembers the boot id and the kernel of the Linux machine a job runs in.</summary>
    /// <param name="bootId">The machine's boot id.</param>
    /// <param name="kernelRelease"><c>uname -r</c>.</param>
    /// <param name="kernelBuilt">The date in <c>uname -v</c>, when it has one.</param>
    void RecordBoot(string bootId, string? kernelRelease, DateTimeOffset? kernelBuilt);
}

/// <summary><see cref="IMachineRecord"/> in the state file.</summary>
public sealed class StateMachineRecord : IMachineRecord
{
    private readonly StateStore _state;
    private readonly TimeProvider _time;

    public StateMachineRecord(StateStore state, TimeProvider time)
    {
        _state = state;
        _time = time;
    }

    public string? LastBootId => _state.Load().LastBootId;

    public void RecordVersion(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.WslVersionText = text;
            s.WslVersionAt = now;
        });
    }

    public void RecordBoot(string bootId, string? kernelRelease, DateTimeOffset? kernelBuilt)
    {
        ArgumentException.ThrowIfNullOrEmpty(bootId);
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastBootId = bootId;
            s.KernelRelease = kernelRelease;
            s.KernelBuilt = kernelBuilt;
            s.KernelSeenAt = now;
        });
    }
}
