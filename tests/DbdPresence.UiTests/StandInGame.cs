using System.Diagnostics;

namespace DbdPresence.UiTests;

/// <summary>
/// A harmless stand-in for the game: a copy of cmd.exe named DeadByDaylight-Win64-Shipping.exe, kept alive by an
/// open stdin. The app only ever looks at process names and start times, so this is all detection can see.
/// </summary>
internal sealed class StandInGame : IDisposable
{
    private readonly string _dir;
    private readonly Process _process;

    private StandInGame(string dir, Process process)
    {
        _dir = dir;
        _process = process;
    }

    public static StandInGame Start()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbdp-game", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "DeadByDaylight-Win64-Shipping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);
        var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        return new StandInGame(dir, process);
    }

    public void Stop()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit(5000);
        }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}
