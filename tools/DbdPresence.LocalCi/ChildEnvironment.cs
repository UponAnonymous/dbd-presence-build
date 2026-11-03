namespace DbdPresence.LocalCi;

/// <summary>
/// The environment the elevated loop gives the programs it starts (wsl, schtasks, taskkill) (security review 2026-10-30, M3, M4). It is built from a short list of
/// names, never copied wholesale: <c>WSLENV</c> would carry Windows variables into Linux, and a search path or temporary folder the owner can write to would let a program
/// without administrator rights put its own tool in front of the real one.
/// </summary>
public static class ChildEnvironment
{
    /// <summary>The variables that are passed on when they exist. Everything else, <c>WSLENV</c> and the <c>WSL_</c> family included, is dropped.</summary>
    private static readonly string[] s_passedOn =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "OS",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "NUMBER_OF_PROCESSORS",
        "COMPUTERNAME", "USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "ProgramData", "ALLUSERSPROFILE", "PUBLIC", "DriverData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "LOGONSERVER", "SESSIONNAME",
    ];

    /// <summary>
    /// Builds the environment. <c>PATH</c> is the Windows folders only; <c>TEMP</c> and <c>TMP</c> are <paramref name="temporaryDirectory"/> (a folder only Administrators and
    /// the system can write to), so an installer an elevated program starts cannot be tricked through the owner's own temporary folder.
    /// </summary>
    /// <param name="get">Reads one variable of the current process (null when it is not set).</param>
    /// <param name="systemRoot">The Windows folder, for example <c>C:\Windows</c>.</param>
    /// <param name="temporaryDirectory">A protected temporary folder.</param>
    public static IReadOnlyDictionary<string, string> Build(Func<string, string?> get, string systemRoot, string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentException.ThrowIfNullOrEmpty(systemRoot);
        ArgumentException.ThrowIfNullOrEmpty(temporaryDirectory);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in s_passedOn)
        {
            if (get(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        environment["PATH"] = string.Join(
            Path.PathSeparator,
            Path.Combine(systemRoot, "System32"),
            systemRoot,
            Path.Combine(systemRoot, "System32", "Wbem"));
        environment["TEMP"] = temporaryDirectory;
        environment["TMP"] = temporaryDirectory;
        return environment;
    }
}
