using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DbdPresence.LocalCi.Windows;

/// <summary>Questions about this PC and the administrator prompt. PENDING-ENV: not run on a Windows PC yet.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMachine : IMachine
{
    private const int UacDeclined = 1223;

    private readonly ICiLog _log;

    public WindowsMachine(ICiLog log)
    {
        _log = log;
    }

    public int WindowsBuild => Environment.OSVersion.Version.Build;

    public long FreeDiskBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public bool? VirtualizationEnabled() => NativeMethods.IsProcessorFeaturePresent(NativeMethods.VirtualizationFirmwareEnabled);

    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public int RelaunchElevated(string program, string arguments)
    {
        var start = new ProcessStartInfo(ProgramPaths.Require(program), arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory };
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the program again.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacDeclined)
        {
            _log.Warn("The administrator prompt was declined.");
            return 1;
        }
    }
}

/// <summary>Starts this program's installed copy with the same words (when it is started from another place) and shows its output in this window.</summary>
[SupportedOSPlatform("windows")]
internal static class HandOver
{
    public static int Run(string installedProgram, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(ProgramPaths.Require(installedProgram)) { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the installed copy of the program.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
