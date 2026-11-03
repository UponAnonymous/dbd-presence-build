namespace DbdPresence.LocalCi;

/// <summary>
/// <c>wsl --install</c> and <c>wsl --update</c> with a time limit of their own (local CI round 3, red-team L2): an installer that hangs is ended after <see cref="WslLimits.Install"/> (the update after
/// <see cref="WslLimits.Update"/>) and the program says so, instead of waiting for ever. The limits are generous (they normally take a few minutes); they are there for a step that does not finish.
/// </summary>
public sealed class LimitedWslInstaller : IWslInstaller
{
    private readonly IWslInstaller _inner;
    private readonly TimeSpan _limit;
    private readonly TimeProvider _time;

    public LimitedWslInstaller(IWslInstaller inner, TimeSpan limit, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(time);
        _inner = inner;
        _limit = limit;
        _time = time;
    }

    public Task<bool> InstallWithoutDistributionAsync(CancellationToken cancellationToken) =>
        TimeBox.RunAsync(_limit, _time, "Installing WSL", _inner.InstallWithoutDistributionAsync, cancellationToken);

    public Task<string> UpdateAsync(CancellationToken cancellationToken) =>
        TimeBox.RunAsync(WslLimits.Update, _time, "Updating WSL", _inner.UpdateAsync, cancellationToken);
}
