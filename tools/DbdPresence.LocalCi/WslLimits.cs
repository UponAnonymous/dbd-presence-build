namespace DbdPresence.LocalCi;

/// <summary>
/// The time limits of everything this program asks wsl.exe or a Linux machine to do (local CI round 3, red-team L2): nothing waits for ever. A step that runs out of time is ended and reported in plain
/// words; the numbers are generous (they are there for a step that hangs, not for a slow one), and the run of a job has a backstop beyond the job limits the loop keeps itself.
/// </summary>
public static class WslLimits
{
    /// <summary>A short management command (list, terminate, unregister, shutdown, version).</summary>
    public static TimeSpan Quick { get; } = TimeSpan.FromMinutes(3);

    /// <summary>Importing or exporting a whole Linux machine.</summary>
    public static TimeSpan Transfer { get; } = TimeSpan.FromMinutes(90);

    /// <summary>The step that closes user namespaces, as root, in a new job machine.</summary>
    public static TimeSpan Harden { get; } = TimeSpan.FromMinutes(2);

    /// <summary>The isolation check inside a new job machine.</summary>
    public static TimeSpan Preflight { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Unpacking the runner package in a new job machine.</summary>
    public static TimeSpan Unpack { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The run of a job: beyond the longest the loop itself ever waits (a runner that waits for a job and then the job limit), so this is only the last backstop.</summary>
    public static TimeSpan Run { get; } = LocalCiDefaults.MaximumWait + LocalCiDefaults.JobTimeout + TimeSpan.FromMinutes(15);

    /// <summary>The first base image script (the Windows-free settings).</summary>
    public static TimeSpan BaseSettings { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The second base image script (apt installs and the runner user).</summary>
    public static TimeSpan BaseSetup { get; } = TimeSpan.FromMinutes(60);

    /// <summary>One run of the isolation probe, as root, in the throwaway probe machine.</summary>
    public static TimeSpan Probe { get; } = TimeSpan.FromMinutes(5);

    /// <summary><c>wsl --install --no-distribution</c> (it installs a Windows feature and a package).</summary>
    public static TimeSpan Install { get; } = TimeSpan.FromMinutes(30);

    /// <summary><c>wsl --update</c> (it downloads and installs the WSL package).</summary>
    public static TimeSpan Update { get; } = TimeSpan.FromMinutes(30);

    /// <summary>The whole build of the Linux base image.</summary>
    public static TimeSpan ImageBuild { get; } = TimeSpan.FromMinutes(90);
}
