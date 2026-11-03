using System.Runtime.Versioning;
using System.Text;

namespace DbdPresence.LocalCi.Windows;

/// <summary>Installs WSL itself (<c>wsl --install --no-distribution</c>) and updates it (<c>wsl --update</c>) under the administrator's own token: the only wsl.exe starts that do, from set-up and from the weekly update when nobody uses WSL. Everything else runs as the dedicated account.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WslInstaller : IWslInstaller
{
    private const int RestartRequired = 3010;

    private readonly ICiLog _log;

    public WslInstaller(ICiLog log)
    {
        _log = log;
    }

    public async Task<bool> InstallWithoutDistributionAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ProgramPaths.Wsl, ["--install", "--no-distribution"], Encoding.Unicode, null, line => _log.Info("wsl: " + line), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or RestartRequired))
        {
            throw new InvalidOperationException($"Installing WSL failed (exit code {result.ExitCode}): {WslOutput.LastLine(result.Output)}");
        }

        return result.ExitCode == RestartRequired;
    }

    public async Task<string> UpdateAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ProgramPaths.Wsl, ["--update"], Encoding.Unicode, null, line => _log.Info("wsl: " + line), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Updating WSL failed (exit code {result.ExitCode}): {WslOutput.LastLine(result.Output)}");
        }

        return WslOutput.LastLine(result.Output);
    }
}
