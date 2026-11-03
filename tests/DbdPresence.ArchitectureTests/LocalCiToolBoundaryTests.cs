// Covers: FR-CI-05 (the owner's local CI program talks only to the approved hosts over HTTPS, starts only the listed programs by their full path, makes only the listed native calls,
// references no product assembly and no package), FR-CI-09 (the isolation steps are in the built program in the right order: the program only runs from its protected copy, the task is
// only registered after a location check, the job's program is put in its Job Object before it runs), SAFETY_AND_PRIVACY section 2.1 (it never reads, opens, injects into or ends the game
// process: the process list only looks at names; no permission or attribute of an existing file is ever rewritten and a finished job's folder is removed only through handles that follow no link),
// NFR-SEC-01. The unshipped tool in tools/DbdPresence.LocalCi runs the repository's GitHub jobs on the owner's PC in throwaway environments
// (D40, docs/owner/free-ci.md). Tools are outside src/, so they are checked here, from the source and from the built assembly. Security review 2026-10-30 (boundary test gaps).
using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbdPresence.ArchitectureTests;

public sealed partial class LocalCiToolBoundaryTests
{
    /// <summary>The hosts the tool may name or contact; the same list is checked against the tool's own list in DbdPresence.LocalCi.Tests.</summary>
    private static readonly HashSet<string> s_allowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "releases.ubuntu.com",
        // An XML namespace name in the scheduled-task definition. It is an identifier, never a request.
        "schemas.microsoft.com",
    };

    private static readonly string[] s_bannedSourceTokens =
    [
        "ReadProcessMemory", "WriteProcessMemory", "CreateRemoteThread", "VirtualAllocEx", "SetWindowsHookEx", "SendInput", "keybd_event", "mouse_event",
        "MainModule", ".Modules", "Assembly.Load", "Activator.CreateInstance", "NativeLibrary", "System.Reflection.Emit", "DllImport",
        "ServerCertificateCustomValidationCallback", "DangerousAcceptAnyServerCertificateValidator", "ftp://", "ws://", "wss://", "UdpClient", "TcpClient", "HttpListener",
        "WebClient", "WebRequest", "System.Net.Sockets",

        // Late-bound and by-name reflection, dynamic code and WMI (security review 2026-10-30, boundary test gaps): a call through these hides its target from the checks below.
        "MethodInfo", "ConstructorInfo", "FieldInfo", "PropertyInfo", ".GetMethod(", ".GetMethods(", ".GetField(", "Type.GetType", "GetTypeFromProgID", "GetTypeFromCLSID",
        "InvokeMember", "DynamicInvoke", "CreateDelegate", "GetDelegateForFunctionPointer", "GetFunctionPointerForDelegate", "System.Management", "ManagementObject", "Assembly.LoadFrom",
        "Assembly.LoadFile", "AssemblyLoadContext", "AppDomain.CreateDomain", "CSharpScript", "Expression.Lambda", ".Compile()", "NativeLibrary.Load", "GetProcAddress", "LoadLibrary",
        "ProcessStartInfo.Verb", "System.Dynamic",
    ];

    /// <summary>
    /// Banned names that are matched as whole words, not as pieces of a longer name: <c>OpenProcess</c> is how a program opens another program (the game), while <c>OpenProcessToken</c>, which the audit of
    /// writable places uses on the tool's own pseudo handle (<c>GetCurrentProcess</c>) to switch the backup right on and off, is another function (local CI round 4, red-team F1). The built program is held
    /// to the exact list of native calls as well (<c>kernel32.dll!OpenProcess</c> is not on it).
    /// </summary>
    private static readonly string[] s_bannedSourceWords = ["OpenProcess"];

    /// <summary>True when the text names the function as a word of its own (not as the start of a longer name).</summary>
    internal static bool NamesWord(string text, string word) =>
        Regex.IsMatch(text, $"(?<![A-Za-z0-9_]){Regex.Escape(word)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

    /// <summary>
    /// The only programs the tool starts, each by its full path (Environment.SystemDirectory, Program Files). wsl.exe runs the Linux machine (as the dedicated account, and once elevated to install
    /// WSL itself); schtasks.exe registers the sign-in task; taskkill.exe ends the programs of the dedicated account on uninstall, with a filter that names that account and no other
    /// (TaskkillArguments); DbdLocalCi.exe is this program's own installed copy (administrator prompt, hand-over). Decision D41 removed winget and powershell with the Windows jobs.
    /// </summary>
    private static readonly HashSet<string> s_allowedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "wsl.exe", "schtasks.exe", "taskkill.exe", "DbdLocalCi.exe",
    };

    /// <summary>
    /// Every native function the tool imports, each with the reason (security review 2026-10-30: "extend the native P/Invoke allow-list only by the calls you add, each with a comment").
    /// None of them reads or changes another program: the process calls act on the job program this tool started itself.
    /// </summary>
    private static readonly Dictionary<string, string> s_allowedNativeCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user32.dll!GetDesktopWindow"] = "the one window call the notice thread makes at the start, so that it is connected to the owner's desktop before any private window station exists",
        ["user32.dll!ShowWindow"] = "hides the console window of the background loop",
        ["user32.dll!MessageBoxTimeoutW"] = "the short notice window that closes itself",
        ["kernel32.dll!GetConsoleWindow"] = "finds the console window of the background loop to hide it",
        ["kernel32.dll!IsProcessorFeaturePresent"] = "the firmware virtualization flag WSL 2 needs",
        ["kernel32.dll!LocalFree"] = "frees the buffer DPAPI returns",
        ["crypt32.dll!CryptProtectData"] = "protects the GitHub token with the machine key and extra entropy",
        ["crypt32.dll!CryptUnprotectData"] = "reads the protected token back",
        ["netapi32.dll!NetUserAdd"] = "creates the dedicated account that owns the Linux machine",
        ["netapi32.dll!NetUserDel"] = "deletes it again (uninstall, or a new account that is not in the right group)",
        ["netapi32.dll!NetUserGetInfo"] = "asks whether the account exists, is switched on and carries this program's comment",
        ["netapi32.dll!NetUserSetInfo"] = "sets the account's new random password, and switches it on and unlocks it (set-up repairs the account it made)",
        ["netapi32.dll!NetUserGetLocalGroups"] = "checks that the account is in no group but Users",
        ["netapi32.dll!NetApiBufferFree"] = "frees the buffers the calls above return",
        ["userenv.dll!DeleteProfileW"] = "deletes the account's profile folder and its entry in the list of profiles (uninstall)",
        ["advapi32.dll!CreateProcessWithLogonW"] = "starts wsl.exe as the dedicated account, suspended, on a private desktop",
        ["user32.dll!CreateWindowStationW"] = "makes a private, non-interactive window station for one wsl.exe start",
        ["user32.dll!CreateDesktopW"] = "makes the private desktop on that station",
        ["user32.dll!CloseWindowStation"] = "closes the private station when the command is over",
        ["user32.dll!CloseDesktop"] = "closes the private desktop when the command is over",
        ["user32.dll!GetProcessWindowStation"] = "remembers this program's own window station before the private one is made",
        ["user32.dll!SetProcessWindowStation"] = "CreateDesktop needs the new station to be current for a moment; the own station is restored at once",
        ["kernel32.dll!CreateJobObjectW"] = "makes the Job Object that holds the wsl.exe process tree",
        ["kernel32.dll!SetInformationJobObject"] = "sets its limits (end all on close, process and memory limits) and its user-interface limits",
        ["kernel32.dll!QueryInformationJobObject"] = "counts the programs still in the job after it was ended",
        ["kernel32.dll!AssignProcessToJobObject"] = "puts the suspended wsl.exe in its Job Object before it runs",
        ["kernel32.dll!IsProcessInJob"] = "asks whether the new, still suspended wsl.exe is already in another Job Object (then the user-interface limits cannot be added to a nested job)",
        ["kernel32.dll!TerminateJobObject"] = "ends every program of the job",
        ["kernel32.dll!ResumeThread"] = "lets wsl.exe run once it is inside the Job Object",
        ["kernel32.dll!TerminateProcess"] = "ends wsl.exe when it could not be put in its Job Object",
        ["kernel32.dll!GetExitCodeProcess"] = "reads how wsl.exe ended",
        ["kernel32.dll!CloseHandle"] = "closes the handles of wsl.exe, its thread and the Job Object",
        ["kernel32.dll!MoveFileExW"] = "uninstall: has Windows delete the program's own file at the next restart",

        // The backup right, switched on for one read of the permissions of a folder and switched off again (local CI round 4, red-team F1): only BackupPrivilege calls these, only the audit of writable places reaches it.
        ["kernel32.dll!GetCurrentProcess"] = "the program's own process, to open its own token for the backup right",
        ["advapi32.dll!OpenProcessToken"] = "opens the program's own token, to switch the backup right on and off",
        ["advapi32.dll!LookupPrivilegeValueW"] = "the number of the backup right (SeBackupPrivilege), the only right the program ever switches on",
        ["advapi32.dll!AdjustTokenPrivileges"] = "switches the backup right on for one read of a folder's permissions, and off again",

        // Reading a file of the owner's (the program file set-up was started from) through a handle that follows no link (automated reviews 2026-10-30: confused deputy).
        ["kernel32.dll!CreateFileW"] = "opens the program file that set-up was started from, by its path, without following a link (FILE_FLAG_OPEN_REPARSE_POINT), for reading only",
        ["kernel32.dll!GetFileInformationByHandle"] = "asks that open file whether it is a link or a folder, so a link is refused",
    };

    /// <summary>The members of System.Diagnostics.Process the built tool may use: starting its own children and waiting for them, and looking up process names.</summary>
    private static readonly HashSet<string> s_allowedProcessMembers = new(StringComparer.Ordinal)
    {
        ".ctor", "Start", "Kill", "Dispose", "WaitForExit", "WaitForExitAsync", "GetProcessesByName",
        "get_ExitCode", "get_HasExited", "get_StandardInput", "get_StandardOutput", "get_StandardError", "set_StartInfo",
    };

    /// <summary>The types that may start a process; each must call ProgramPaths.Require in the same method.</summary>
    private static readonly string[] s_processStarters =
    [
        "DbdPresence.LocalCi.Windows.ProcessRunner",
        "DbdPresence.LocalCi.Windows.WindowsMachine",
        "DbdPresence.LocalCi.Windows.HandOver",
    ];

    private const string Require = "DbdPresence.LocalCi.Windows.ProgramPaths::Require";

    private static string ToolDirectory => Path.Combine(ProductAssemblies.RepositoryRoot, "tools", "DbdPresence.LocalCi");

    private static IEnumerable<string> SourceFiles() => Directory.GetFiles(ToolDirectory, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string file) => Path.GetRelativePath(ToolDirectory, file).Replace('\\', '/');

    private static IEnumerable<AssemblyInspector> BuiltTools()
    {
        var binDir = Path.Combine(ToolDirectory, "bin", ProductAssemblies.Configuration);
        var paths = Directory.Exists(binDir) ? Directory.GetFiles(binDir, "DbdLocalCi.dll", SearchOption.AllDirectories) : [];
        Assert.True(paths.Length > 0, $"No built local CI tool under {binDir}. Build the solution first.");
        foreach (var path in paths)
        {
            yield return new AssemblyInspector(path);
        }
    }

    private static string Outer(string declaringType)
    {
        var plus = declaringType.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? declaringType : declaringType[..plus];
    }

    /// <summary>For every method: the targets of its calls, in order.</summary>
    private static IEnumerable<(MethodBodyInfo Method, List<string> Calls)> Calls(AssemblyInspector assembly)
    {
        foreach (var method in assembly.MethodBodies())
        {
            var calls = new List<string>();
            foreach (var instruction in method.Instructions)
            {
                if (instruction.OpCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj && instruction.Operand is EntityHandle handle)
                {
                    calls.Add(assembly.Describe(handle));
                }
            }

            yield return (method, calls);
        }
    }

    [Fact]
    public void LocalCiSource_HasNoGameProcessAccess_NoCustomCertificateCheck_NoReflectionOrDynamicCode_AndNoOtherNetworkKinds()
    {
        var files = SourceFiles().ToList();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(f => s_bannedSourceTokens
                .Where(t => File.ReadAllText(f).Contains(t, StringComparison.Ordinal))
                .Concat(s_bannedSourceWords.Where(w => NamesWord(File.ReadAllText(f), w)))
                .Select(t => $"{Relative(f)} contains '{t}'"))
            .ToList();

        Assert.True(violations.Count == 0, string.Join("\n  ", violations));
    }

    [Fact]
    public void LocalCiSource_DoesNotUseTheKeywordDynamic()
    {
        var offenders = SourceFiles()
            .Where(f => Regex.IsMatch(StripCommentsAndStrings(File.ReadAllText(f)), @"\bdynamic\b"))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, "These files use 'dynamic':\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Comments and string literals blanked, so the word in prose does not count.</summary>
    private static string StripCommentsAndStrings(string code)
    {
        var withoutComments = Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return Regex.Replace(withoutComments, @"@?\$?""""""(?:.|\n)*?""""""|@?\$?""(?:[^""\\\n]|\\.|"""")*""", "\"\"", RegexOptions.CultureInvariant);
    }

    [Fact]
    public void LocalCiProject_ReferencesNoProjectAndNoPackage()
    {
        var project = File.ReadAllText(Path.Combine(ToolDirectory, "DbdPresence.LocalCi.csproj"));

        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
    }

    /// <summary>
    /// Local CI round 4, red-team F11: a package added through the shared build files (<c>Directory.Build.props</c> at the root, for example) is not in the tool's own project file, yet it would be built
    /// into <c>DbdLocalCi.exe</c>, which the owner runs with administrator rights. So the shared files may name no package and no project either, and the tool's lock file (which a locked restore
    /// in CI must match) holds nothing but the implicit trimming task that the SDK adds.
    /// </summary>
    [Fact]
    public void NeitherTheSharedBuildFilesNorTheLockFile_AddAPackageOrAProjectToTheLocalCiTool()
    {
        var folders = new[] { ProductAssemblies.RepositoryRoot, Path.Combine(ProductAssemblies.RepositoryRoot, "tools"), ToolDirectory };
        var names = new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "Directory.Packages.targets" };
        var shared = folders.SelectMany(folder => names.Select(name => Path.Combine(folder, name))).Where(File.Exists).ToList();

        Assert.Contains(shared, f => Path.GetFileName(f) == "Directory.Build.props");
        Assert.Contains(shared, f => Path.GetFileName(f) == "Directory.Packages.props");
        foreach (var file in shared)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PackageReference", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ProjectReference", text, StringComparison.Ordinal);
        }

        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(ToolDirectory, "packages.lock.json")));
        var packages = lockFile.RootElement.GetProperty("dependencies").EnumerateObject().SelectMany(framework => framework.Value.EnumerateObject().Select(package => package.Name)).Distinct().ToList();
        // No package at all: the single-file analyzer (Microsoft.NET.ILLink.Tasks at the build SDK's own version) is switched off, so nothing in the
        // restore graph depends on which patch of .NET the build machine has (NU1004 on GitHub's machines, 2026-11-01).
        Assert.Empty(packages);
    }

    [Fact]
    public void NoOtherProject_ExceptItsTestsAndTheBuildOrderEntry_ReferencesTheLocalCiTool()
    {
        var offenders = new List<string>();
        foreach (var root in new[] { "src", "tools", "tests" })
        {
            foreach (var project in Directory.EnumerateFiles(Path.Combine(ProductAssemblies.RepositoryRoot, root), "*.csproj", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(project);
                // The architecture tests list the tool for build order only (ReferenceOutputAssembly=false).
                if (name is "DbdPresence.LocalCi" or "DbdPresence.LocalCi.Tests" or "DbdPresence.ArchitectureTests")
                {
                    continue;
                }

                if (File.ReadAllText(project).Contains("DbdPresence.LocalCi", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetRelativePath(ProductAssemblies.RepositoryRoot, project));
                }
            }
        }

        Assert.True(offenders.Count == 0, "Projects that reference the local CI tool:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Processes_AreStartedOnlyInTheWindowsFolder_ByTheListedPrograms_AndTheProcessListOnlyLooksAtNames()
    {
        var offenders = new List<string>();
        var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            var relative = Relative(file);
            var inWindowsFolder = relative.StartsWith("Windows/", StringComparison.Ordinal);
            foreach (var token in new[] { "Process.Start", "new ProcessStartInfo", "new Process", "Process.GetProcess", "GetProcessesByName" })
            {
                if (text.Contains(token, StringComparison.Ordinal) && !inWindowsFolder)
                {
                    offenders.Add($"{relative} uses '{token}' outside Windows/");
                }
            }

            foreach (Match match in Regex.Matches(text, "\"(?<program>[A-Za-z0-9_.\\\\-]+\\.exe)\""))
            {
                programs.Add(match.Groups["program"].Value);
            }
        }

        // The only code that touches processes it did not start is the name lookup; it does nothing else with them.
        var lookup = File.ReadAllText(Path.Combine(ToolDirectory, "Windows", "SystemProbes.cs"));
        var lookupBody = lookup[lookup.IndexOf("class WindowsProcessList", StringComparison.Ordinal)..lookup.IndexOf("class MessageBoxNotifier", StringComparison.Ordinal)];
        foreach (var banned in new[] { "Kill", "MainModule", "Modules", "Handle", "Threads", "StartInfo", "Id", "ProcessName" })
        {
            if (Regex.IsMatch(lookupBody, $@"\.{banned}\b"))
            {
                offenders.Add($"WindowsProcessList uses .{banned} on a process it did not start");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
        Assert.Subset(s_allowedPrograms, programs);
        Assert.Contains("wsl.exe", programs);
        Assert.Contains("schtasks.exe", programs);
        Assert.Contains("taskkill.exe", programs);
    }

    [Fact]
    public void ElevationIsInOneFile_AndTheLogonStartIsOnlyCalledByTheJobRunner()
    {
        var elevated = SourceFiles().Where(f => File.ReadAllText(f).Contains("\"runas\"", StringComparison.Ordinal)).Select(Relative).ToList();
        Assert.Equal(["Windows/Machine.cs"], elevated);

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var callers = Calls(assembly)
                    .Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.NativeMethods::CreateProcessWithLogon"))
                    .Select(c => Outer(c.Method.DeclaringType))
                    .Distinct()
                    .ToList();

                Assert.Equal(["DbdPresence.LocalCi.Windows.LogonJob"], callers);
            }
        }
    }

    [Fact]
    public void HttpClientsDoNotFollowRedirectsThemselves_SoEveryHopIsChecked()
    {
        var application = File.ReadAllText(Path.Combine(ToolDirectory, "Windows", "LocalCiApplication.cs"));

        Assert.Contains("AllowAutoRedirect = false", application, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(application, "new HttpClient\\(|new\\(new SocketsHttpHandler"));

        // Written as a name with its namespace, with spaces or on a new line, the text above is not matched (round 4, QA minor 8): so no other file may construct either class by any spelling...
        foreach (var file in SourceFiles().Where(f => Relative(f) != "Windows/LocalCiApplication.cs"))
        {
            Assert.DoesNotMatch(@"new\s*(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?(?:HttpClient|HttpClientHandler|SocketsHttpHandler)\s*\(", CodeWithoutStrings(File.ReadAllText(file)));
        }

        // ...and the built program constructs them in one method only, whatever the source says.
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();
                foreach (var constructor in new[] { "System.Net.Http.HttpClient::.ctor", "System.Net.Http.SocketsHttpHandler::.ctor" })
                {
                    var makers = all.Where(c => c.Calls.Contains(constructor)).Select(c => $"{c.Method.DeclaringType}.{c.Method.Name}").Distinct().ToList();
                    Assert.Equal(["DbdPresence.LocalCi.Windows.LocalCiApplication.NewClient"], makers);
                }

                Assert.DoesNotContain(all, c => c.Calls.Contains("System.Net.Http.HttpClientHandler::.ctor"));
            }
        }
    }

    /// <summary>
    /// Round 4, QA minor 8: a file is deleted by name in a few places only, each of which deletes a path the program made itself (a partial download, a temporary file next to the file it replaces, an old
    /// log, the probe's test file by its exact name, a tree through <c>SafeTree</c>). A new call of <c>File.Delete</c> anywhere else is a new way to delete something and needs this list changed in review.
    /// </summary>
    [Fact]
    public void FilesAreDeletedByNameOnlyInTheClassesThatDeleteWhatTheyMadeThemselves()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var deleters = Calls(assembly)
                    .Where(c => c.Calls.Contains("System.IO.File::Delete"))
                    .Select(c => Outer(c.Method.DeclaringType))
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToList();

                Assert.Equal(
                    [
                        "DbdPresence.LocalCi.BaseImageBuilder",
                        "DbdPresence.LocalCi.FileCiLog",
                        "DbdPresence.LocalCi.ProbeMarkerFile",
                        "DbdPresence.LocalCi.RunnerCache",
                        "DbdPresence.LocalCi.SafeFileReplace",
                        "DbdPresence.LocalCi.SafeTree",
                        "DbdPresence.LocalCi.Windows.WindowsProgramInstaller",
                    ],
                    deleters);
            }
        }
    }

    [Fact]
    public void BuiltLocalCiTool_HasOnlyTheListedHosts_TheListedNativeCalls_NoProductReference_AndUsesProcessOnlyAsListed()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                Assert.Empty(SafetyRules.HostLiterals(assembly, s_allowedHosts));
                Assert.DoesNotContain(assembly.AssemblyReferences, r => r.StartsWith("DbdPresence.", StringComparison.Ordinal));
                Assert.DoesNotContain("System.Net.Sockets", assembly.AssemblyReferences);
                Assert.DoesNotContain("System.Net.HttpListener", assembly.AssemblyReferences);
                Assert.Empty(assembly.ComImportTypes);

                var native = assembly.PInvokes.Select(p => $"{p.Module}!{p.EntryPoint}").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Assert.NotEmpty(native);
                Assert.All(native, call => Assert.True(s_allowedNativeCalls.ContainsKey(call), $"{call} is not on the native allow-list (add it with its reason, in review)"));

                var processMembers = assembly.MemberReferences
                    .Where(m => m.ParentType == "System.Diagnostics.Process")
                    .Select(m => m.MemberName)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                Assert.All(processMembers, member => Assert.Contains(member, s_allowedProcessMembers));
                Assert.Contains("GetProcessesByName", processMembers);
            }
        }
    }

    [Fact]
    public void EveryNativeCallOnTheList_IsStillUsed_SoTheListCannotGrowQuietly()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var used = assembly.PInvokes.Select(p => $"{p.Module}!{p.EntryPoint}").ToHashSet(StringComparer.OrdinalIgnoreCase);

                Assert.All(s_allowedNativeCalls.Keys, call => Assert.Contains(call, used));
            }
        }
    }

    [Fact]
    public void BuiltLocalCiTool_UsesNoReflectionByName_NoDynamicCode_NoCodeGeneration_AndOnlyFrameworkAssemblies()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                Assert.Empty(SafetyRules.DynamicCode(assembly));
                Assert.Empty(SafetyRules.BannedTypes(assembly));
                Assert.Empty(SafetyRules.ProcessInspectionAssemblies(assembly));
                Assert.DoesNotContain("Microsoft.CSharp", assembly.AssemblyReferences);
                Assert.All(
                    assembly.AssemblyReferences,
                    reference => Assert.True(
                        reference.StartsWith("System.", StringComparison.Ordinal) || reference is "System" or "mscorlib" or "netstandard" or "Microsoft.Win32.Registry" or "Microsoft.Win32.Primitives",
                        $"The tool references {reference}, which is not a framework assembly."));
            }
        }
    }

    [Fact]
    public void EveryProcessStart_IsInAnApprovedClass_AndAsksProgramPathsForTheFullPathFirst()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var starts = 0;
                foreach (var (method, calls) in Calls(assembly))
                {
                    var starting = calls.Contains("System.Diagnostics.ProcessStartInfo::.ctor") || calls.Contains("System.Diagnostics.Process::Start")
                                   || calls.Contains("System.Diagnostics.ProcessStartInfo::set_FileName");
                    if (!starting)
                    {
                        continue;
                    }

                    starts++;
                    var outer = Outer(method.DeclaringType);
                    Assert.True(s_processStarters.Contains(outer), $"{method.DeclaringType}.{method.Name} starts a process but {outer} is not an approved starter.");
                    Assert.True(calls.Contains(Require), $"{method.DeclaringType}.{method.Name} starts a process without calling ProgramPaths.Require on its file name.");
                    Assert.DoesNotContain("System.Diagnostics.ProcessStartInfo::set_FileName", calls);
                }

                Assert.True(starts >= 3, $"Expected the three approved starters to be found, found {starts} starting methods.");
            }
        }
    }

    [Fact]
    public void KillingAProcessAndWritingToItsInput_AreOnlyDoneByTheProcessRunner()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var offenders = Calls(assembly)
                    .Where(c => c.Calls.Contains("System.Diagnostics.Process::Kill") || c.Calls.Contains("System.Diagnostics.Process::get_StandardInput"))
                    .Where(c => !Outer(c.Method.DeclaringType).Equals("DbdPresence.LocalCi.Windows.ProcessRunner", StringComparison.Ordinal))
                    .Select(c => $"{c.Method.DeclaringType}.{c.Method.Name}")
                    .ToList();

                Assert.True(offenders.Count == 0, "These methods kill a process or use its input: " + string.Join(", ", offenders));
            }
        }
    }

    [Fact]
    public void TheEnvironmentOfStartedProgramsIsBuiltFromTheShortList_NeverCopiedWholesale()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var runner = Calls(assembly).Where(c => Outer(c.Method.DeclaringType) == "DbdPresence.LocalCi.Windows.ProcessRunner" && c.Method.Name == "MoveNext").ToList();

                Assert.Contains(runner, c => c.Calls.Contains("DbdPresence.LocalCi.ChildEnvironment::Build"));
                Assert.Contains(runner, c => c.Calls.Contains("System.Collections.Generic.IDictionary`2::Clear") || c.Calls.Any(x => x.EndsWith("::Clear", StringComparison.Ordinal)));
                Assert.DoesNotContain(Calls(assembly), c => c.Calls.Contains("System.Environment::GetEnvironmentVariables"));
            }
        }
    }

    [Fact]
    public void EveryCommandThatRunsElevatedChecksTheRuntimeVariablesBeforeItRoutesOrAsksForTheAdministratorPrompt()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                // Local CI round 4, red-team F13: the copy that would ask for the administrator prompt refuses before it asks, and the elevated copy refuses again.
                var all = Calls(assembly).ToList();
                var start = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.Windows.LocalCiApplication+<RunAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var checks = start.IndexOf("DbdPresence.LocalCi.ElevatedStartCheck::Problems");
                Assert.True(checks >= 0, "RunAsync must check the runtime variables.");
                var routed = start.IndexOf("DbdPresence.LocalCi.RoutingDecision::For");
                Assert.True(routed > checks, "The runtime variables must be checked before the command is routed.");
                var prompt = start.IndexOf("DbdPresence.LocalCi.Windows.WindowsMachine::RelaunchElevated");
                Assert.True(prompt > checks, "The runtime variables must be checked before the administrator prompt is asked for.");
                var logCreated = start.IndexOf("DbdPresence.LocalCi.FileCiLog::.ctor");
                Assert.True(logCreated > checks, "The runtime variables must be checked before anything else is set up.");
            }
        }
    }

    [Fact]
    public void TheProgramRegistersTheSignInTaskOnlyAfterCheckingTheProgramLocation_AndTheLoopChecksItselfBeforeStarting()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                var scheduler = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.Windows.TaskSchedulerService+<InstallAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var checkedLocation = scheduler.IndexOf("DbdPresence.LocalCi.PathTrust::CheckProgramLocation");
                var wroteDefinition = scheduler.IndexOf("DbdPresence.LocalCi.Windows.ProtectedCreation::WriteNewFile");
                Assert.True(checkedLocation >= 0 && wroteDefinition > checkedLocation, "TaskSchedulerService.InstallAsync must check the program location before it writes the task definition.");

                var serve = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.Windows.LocalCiApplication+<ServeAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var startChecks = serve.IndexOf("DbdPresence.LocalCi.ServeStartCheck::Problems");
                var lockTaken = serve.IndexOf("DbdPresence.LocalCi.ServeLock::TryAcquire");
                Assert.True(startChecks >= 0 && lockTaken > startChecks, "The loop must run its start checks (installed copy, closed data folder) before it does anything else.");

                // The steps of set-up are in RunStepsAsync (RunAsync wraps them so that whatever happens a stopped loop is started again and routing that this PC does not serve is taken away). The
                // check of the installed program's location is made there; that the task is not registered after a failed check is shown by SetupAndUninstallTests (the registering call itself is
                // in a lambda of that method, which this scan cannot put in order).
                var setup = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.SetupWizard+<RunStepsAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                Assert.Contains("DbdPresence.LocalCi.IProgramInstaller::CheckInstalledLocation", setup);

                // Only the setup wizard registers the task, and it does so only in its guarded step after the check above.
                var registrars = all
                    .Where(c => c.Calls.Contains("DbdPresence.LocalCi.IScheduler::InstallAsync"))
                    .Select(c => Outer(c.Method.DeclaringType))
                    .Distinct()
                    .ToList();
                Assert.Equal(["DbdPresence.LocalCi.SetupWizard"], registrars);
            }
        }
    }

    [Fact]
    public void TheToolNeverRewritesThePermissionsOrAttributesOfAnExistingFile_AndNeverCleansAFolderTheAccountControlsWithSafeTree()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                // A hard link shares one security descriptor and one set of attributes with the file it points to (CreateHardLinkW documentation). A job's user can leave a hard link to a file
                // outside its folder, so an elevated clean-up that rewrote permissions or attributes inside the folder would rewrite those of the outside file.
                var rewriting = assembly.MemberReferences
                    .Where(m => m.MemberName is "SetAccessControl" or "SetSecurityDescriptorSddlForm" or "SetSecurityDescriptorBinaryForm" or "SetNamedSecurityInfo" or "SetSecurityInfo")
                    .Select(m => $"{m.ParentType}::{m.MemberName}")
                    .ToList();
                Assert.True(rewriting.Count == 0, "The tool applies a security descriptor to an existing file or folder: " + string.Join(", ", rewriting));

                var all = Calls(assembly).ToList();

                // Only SafeTree (for places that only Administrators can write to) clears a read-only attribute, and the attributes of a file are shared by all its hard links.
                var attributeWriters = all
                    .Where(c => c.Calls.Any(x => x is "System.IO.File::SetAttributes" or "System.IO.FileSystemInfo::set_Attributes"))
                    .Select(c => Outer(c.Method.DeclaringType))
                    .Distinct()
                    .ToList();
                Assert.Equal(["DbdPresence.LocalCi.SafeTree"], attributeWriters);

                // Nothing the dedicated account (or anything else a program of another user controls) can fill is ever deleted through SafeTree, which clears read-only attributes: a hard link there
                // would make that change the attributes of a file elsewhere. Those places are emptied by WSL itself (unregister) and only empty folders are removed.
                var viaSafeTree = all
                    .Where(c => Outer(c.Method.DeclaringType) is "DbdPresence.LocalCi.Windows.WindowsWslAccount" or "DbdPresence.LocalCi.WslClient" or "DbdPresence.LocalCi.LinuxJobHost"
                        or "DbdPresence.LocalCi.WslSandbox" or "DbdPresence.LocalCi.Windows.WslSettingsFile" or "DbdPresence.LocalCi.Windows.LogonAccountProcess")
                    .Where(c => c.Calls.Contains("DbdPresence.LocalCi.SafeTree::Delete"))
                    .Select(c => $"{c.Method.DeclaringType}.{c.Method.Name}")
                    .ToList();
                Assert.True(viaSafeTree.Count == 0, "These methods remove a folder the account's programs can write to with SafeTree: " + string.Join(", ", viaSafeTree));
            }
        }
    }

    [Fact]
    public void TheFilesOfOtherUsersAreReadWithoutFollowingLinks_AndTheAccountsSettingsAreReplacedByRenameOfAnUnguessableFile()
    {
        const string Verified = "DbdPresence.LocalCi.Windows.VerifiedUserFile::OpenRead";
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // The account's .wslconfig lives in the account's own profile, where that account's programs can write. The elevated program reads it through a handle that follows no link, writes by
                // renaming a new file with an unguessable name over it, and holds it open for reading only while a session lasts: no File member (which would work through the name) is used by that class.
                var settings = all.Where(c => Outer(c.Method.DeclaringType) == "DbdPresence.LocalCi.Windows.WslSettingsFile").ToList();
                Assert.NotEmpty(settings);
                var viaPath = settings.SelectMany(c => c.Calls.Where(x => x.StartsWith("System.IO.File::", StringComparison.Ordinal))).ToList();
                Assert.True(viaPath.Count == 0, "WslSettingsFile works through a path: " + string.Join(", ", viaPath));
                Assert.Contains(Verified, settings.Single(c => c.Method.Name == "Read").Calls);
                Assert.Contains(Verified, settings.Single(c => c.Method.Name == "OpenHeld").Calls);
                Assert.Contains("DbdPresence.LocalCi.SafeFileReplace::Write", settings.Single(c => c.Method.Name == "Write").Calls);

                // The owner's own .wslconfig is never touched: nothing reads the user profile folder of the current user any more.
                Assert.DoesNotContain(all, c => c.Calls.Contains("System.Environment::GetFolderPath") && Outer(c.Method.DeclaringType) == "DbdPresence.LocalCi.Windows.WslSettingsFile");

                // The program file set-up was started from is read through the same kind of handle, never copied by path.
                var install = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.Windows.WindowsProgramInstaller+<InstallAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                Assert.Contains(Verified, install);
                Assert.DoesNotContain(install, x => x is "System.IO.File::Copy" or "System.IO.File::OpenRead" or "System.IO.File::ReadAllBytes" or "System.IO.File::ReadAllText");

                // Nothing else opens a file of another user's.
                var openers = all
                    .Where(c => c.Calls.Contains(Verified))
                    .Select(c => Outer(c.Method.DeclaringType))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                Assert.Equal(["DbdPresence.LocalCi.Windows.WindowsProgramInstaller", "DbdPresence.LocalCi.Windows.WslSettingsFile"], openers);
            }
        }
    }

    [Fact]
    public void TheJobProgramIsPutInItsJobObject_BeforeItIsResumed_AndOnlyStartedSuspendedOnThePrivateDesktop()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var run = Calls(assembly).Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.Windows.LogonJob+<RunAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;

                var created = run.IndexOf("DbdPresence.LocalCi.Windows.PrivateDesktop::Create");
                var job = run.IndexOf("DbdPresence.LocalCi.Windows.JobObject::Create");
                var started = run.IndexOf("DbdPresence.LocalCi.Windows.LogonJob::StartSuspended");
                var assigned = run.IndexOf("DbdPresence.LocalCi.Windows.LogonJob::AssignOrEnd");
                var resumed = run.IndexOf("DbdPresence.LocalCi.Windows.NativeMethods::ResumeThread");
                var ended = run.IndexOf("DbdPresence.LocalCi.Windows.JobObject::EndAllAsync");

                Assert.True(created >= 0 && job >= 0 && started >= 0 && assigned >= 0 && resumed >= 0 && ended >= 0, "A step of the job isolation is missing from LogonJob.RunAsync.");
                Assert.True(created < started && job < started, "The private desktop and the Job Object must exist before the program is started.");
                Assert.True(started < assigned && assigned < resumed, "The program must be put in its Job Object before it is resumed.");
                Assert.True(resumed < ended, "The Job Object is ended after the program has run.");

                // The limits (end all on close, number of programs, memory) are on every job; the user-interface limits are added only to a job that is not nested, in AssignOrEnd, after the question
                // whether the new program is already in a job and before it is put in this one.
                var jobCreate = Calls(assembly).Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.JobObject" && c.Method.Name == "Create").Calls;
                Assert.Contains("DbdPresence.LocalCi.Windows.NativeMethods::SetJobExtendedLimits", jobCreate);
                Assert.DoesNotContain("DbdPresence.LocalCi.Windows.NativeMethods::SetJobUiRestrictions", jobCreate);
                var restrict = Calls(assembly).Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.JobObject" && c.Method.Name == "RestrictUserInterface").Calls;
                Assert.Contains("DbdPresence.LocalCi.Windows.NativeMethods::SetJobUiRestrictions", restrict);
                var assign = Calls(assembly).Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.LogonJob" && c.Method.Name == "AssignOrEnd").Calls;
                var asked = assign.IndexOf("DbdPresence.LocalCi.Windows.JobObject::IsInAnyJob");
                var limited = assign.IndexOf("DbdPresence.LocalCi.Windows.JobObject::RestrictUserInterface");
                var joined = assign.IndexOf("DbdPresence.LocalCi.Windows.JobObject::Assign");
                Assert.True(asked >= 0 && limited > asked && joined > limited, "AssignOrEnd must ask whether the program is in a job, then add the user-interface limits, then put it in the job.");
                Assert.Contains("DbdPresence.LocalCi.Windows.NativeMethods::TerminateProcess", assign);

                var desktop = Calls(assembly).Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.PrivateDesktop" && c.Method.Name == "CreateOnThisThread").Calls;
                Assert.Contains("DbdPresence.LocalCi.Windows.NativeMethods::CreateWindowStation", desktop);
                Assert.Contains("DbdPresence.LocalCi.Windows.NativeMethods::CreateDesktop", desktop);
                Assert.Contains("DbdPresence.LocalCi.Windows.PrivateDesktopSecurity::Descriptor", desktop);
            }
        }
    }

    [Fact]
    public void WslRunsUnderTheLoopsOwnTokenOnlyToInstallWslItself_EverythingElseIsStartedAsTheAccount()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // Only the installer and the wiring name the path of wsl.exe; the wiring only hands it to the client that runs it as the account.
                var namers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.ProgramPaths::get_Wsl")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal(["DbdPresence.LocalCi.Windows.LocalCiApplication", "DbdPresence.LocalCi.Windows.WslInstaller"], namers);

                // The programs started under the loop's own token (ProcessRunner) are asked for by these classes and no others.
                var runners = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.ProcessRunner::RunAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal(["DbdPresence.LocalCi.Windows.TaskSchedulerService", "DbdPresence.LocalCi.Windows.WindowsWslAccount", "DbdPresence.LocalCi.Windows.WslInstaller"], runners);

                // A command runs as the account only through the client, and the logon start is only in the account process.
                var asAccount = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IAccountProcess::RunAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.WslClient"], asAccount);
                var logon = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.LogonJob::RunAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.Windows.LogonAccountProcess"], logon);
            }
        }
    }

    [Fact]
    public void TaskkillIsOnlyEverGivenTheFilterOfTheDedicatedAccount_AndOnlyUninstallRemovesTheAccount()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                var killers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.ProgramPaths::get_Taskkill")).ToList();
                Assert.NotEmpty(killers);
                Assert.All(killers, c => Assert.Equal("DbdPresence.LocalCi.Windows.WindowsWslAccount", Outer(c.Method.DeclaringType)));
                Assert.All(killers, c => Assert.Contains("DbdPresence.LocalCi.TaskkillArguments::ForTheAccount", c.Calls));

                // Deleting the account, its profile and its sign-in entry is only in the account class, and only uninstall asks it to.
                foreach (var native in new[] { "NetUserDel", "DeleteProfile" })
                {
                    var callers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.NativeMethods::" + native)).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                    Assert.Equal(["DbdPresence.LocalCi.Windows.WindowsWslAccount"], callers);
                }

                var removers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IWslAccount::RemoveAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.Uninstaller"], removers);
            }
        }
    }

    [Fact]
    public void TheMachineIsOnlyEverShutDownAsTheAccount_AndEveryUseOfTheSandboxEndsWithAShutdown()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // wsl --shutdown is called by the sandbox only (at the start and at the end of a session), never by a job host or the setup directly.
                var shutdowners = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IWsl::ShutdownAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.WslSandbox"], shutdowners);

                // The sandbox's end shuts down and then lets go of the settings file.
                var end = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.WslSandbox+<EndAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                Assert.Contains("System.IDisposable::Dispose", end);
            }
        }
    }

    [Fact]
    public void TheAccountsProgramsAreEndedAndItsLockOutClearedByTheSandboxOnly_AndScriptsInTheLinuxMachineAreRunByThreeClassesOnly_WithRootGivenInThreePlaces()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // Local CI round 3, H1/L11: after every use of the Linux machine the account's programs are ended and checked gone, and a lock-out of the account is cleared before a start. Both are
                // done by the sandbox session, so no other code path can end the account's programs (uninstall does it inside the account class) or change the account's flags.
                var enders = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IWslAccount::EndProgramsAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.WslSandbox"], enders);
                var unlockers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IWslAccount::ClearLockout")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.WslSandbox"], unlockers);

                // A script goes into a Linux machine only from the base image build, from the isolation probe and from a job; each passes a time limit.
                var executors = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IWsl::ExecuteAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal(["DbdPresence.LocalCi.BaseImageBuilder", "DbdPresence.LocalCi.IsolationProbe", "DbdPresence.LocalCi.LinuxJobHost"], executors);
            }
        }

        // The root user of a Linux machine is named in exactly three places: the base image build (it installs the tools), the isolation probe (it tries what a job that gained root would try, on purpose,
        // in a throwaway machine with the owner watching) and the one hardening step of a job.
        var rootNamed = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("\"root\", ", StringComparison.Ordinal))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["BaseImageBuilder.cs", "IsolationProbe.cs", "LinuxJobHost.cs"], rootNamed);
    }

    [Fact]
    public void TheAccountHasModifyAndNotFullControlOnItsWorkFolder_AndTheFolderForItsMachinesMustBeARealFolder()
    {
        // Local CI round 3, L4: the account that owns the Linux machine can make and delete what is in its work folder but cannot change the permissions or the owner of the folder itself, and a link
        // where the folder for its machines must be is noticed (and moved aside by set-up).
        var account = File.ReadAllText(Path.Combine(ToolDirectory, "Windows", "WindowsWslAccount.cs"));
        Assert.Contains("modify: sid", account, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedCreation.Administrators, sid]", account, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedCreation.Administrators,sid]", account, StringComparison.Ordinal);

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();
                var problems = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.WindowsWslAccount" && c.Method.Name == "WorkFolderProblems").Calls;
                Assert.Contains("DbdPresence.LocalCi.SafeTree::IsLink", problems);
                var repair = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.WindowsWslAccount" && c.Method.Name == "CreateOrRepairWorkFolder").Calls;
                Assert.Contains("DbdPresence.LocalCi.Windows.FolderQuarantine::MoveAside", repair);
            }
        }
    }

    [Fact]
    public void TheIsolationProbeRunsOnlyItsFixedScript_AsRootInAThrowawayMachine_AndTheMarkerIsDeletedByExactPathAfterTheNameWasChecked()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // Local CI round 3, red-team H1: the probe's script is built by ScriptFor (its fixed text with a checked marker name in front) and by nothing else, and runs in the probe machine only.
                var run = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.IsolationProbe+<RunInMachineAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var built = run.IndexOf("DbdPresence.LocalCi.IsolationProbe::ScriptFor");
                var executed = run.IndexOf("DbdPresence.LocalCi.IWsl::ExecuteAsync");
                Assert.True(built >= 0 && executed > built, "The probe must build its script with ScriptFor before it runs it.");
                Assert.Contains("DbdPresence.LocalCi.WslSandbox::BeginAsync", run);
                Assert.Contains("DbdPresence.LocalCi.WslSandbox::RemoveDistributionAsync", run);
                var scriptFor = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.IsolationProbe" && c.Method.Name == "ScriptFor").Calls;
                Assert.Contains("DbdPresence.LocalCi.ProbeMarkerNames::IsMarker", scriptFor);

                // The marker file on Windows is removed by the exact path folder\name, only after its name was checked to be a marker's, and never recursively.
                var removal = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.ProbeMarkerFile" && c.Method.Name == "FindAndRemove").Calls;
                var checkedName = removal.IndexOf("DbdPresence.LocalCi.ProbeMarkerNames::IsMarker");
                var deleted = removal.IndexOf("System.IO.File::Delete");
                Assert.True(checkedName >= 0 && deleted > checkedName, "The marker file may only be deleted after its name was checked.");
                Assert.DoesNotContain(removal, x => x is "System.IO.Directory::Delete" or "DbdPresence.LocalCi.SafeTree::Delete");
                Assert.Contains("System.IO.Path::Combine", removal);

                // Nothing else looks for or deletes the marker. Local CI round 4, red-team F7: the leftovers list looks again, at the start of the loop, for a marker file a probe could not remove. That is one method of
                // one class, it takes the names from the entries of the list that are about marker files, and the removal itself is the same ProbeMarkerFile.FindAndRemove as above: the name must have the exact
                // shape of a marker's (a name that does not is refused with an exception), the path is the exact folder\name, only a plain file is deleted.
                var lookers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IProbeMarker::FindAndRemove")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal(["DbdPresence.LocalCi.IsolationProbe", "DbdPresence.LocalCi.ResidueRecorder"], lookers);
                var recorderMethods = all.Where(c => Outer(c.Method.DeclaringType) == "DbdPresence.LocalCi.ResidueRecorder" && c.Calls.Contains("DbdPresence.LocalCi.IProbeMarker::FindAndRemove")).Select(c => c.Method.Name).Distinct().ToList();
                Assert.Equal(["RecheckMarkerFiles"], recorderMethods);
                var recheck = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.ResidueRecorder" && c.Method.Name == "RecheckMarkerFiles").Calls;
                Assert.Contains("DbdPresence.LocalCi.StateStore::Load", recheck);
                Assert.DoesNotContain(recheck, x => x.StartsWith("System.IO.", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void WslIsUpdatedOnlyByTheWeeklyUpdate_WithTheExactArgumentAndOnlyInTheClassThatInstallsIt()
    {
        // Local CI round 3, red-team H1: wsl --update changes WSL for the whole PC, so it is started in one place (the class that also installs WSL, under the administrator's token), reached only
        // through the weekly update, which checks that nobody uses WSL first.
        var updaters = SourceFiles().Where(f => File.ReadAllText(f).Contains("\"--update\"", StringComparison.Ordinal)).Select(Relative).ToList();
        Assert.Equal(["Windows/WslInstaller.cs"], updaters);

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();
                var callers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IWslInstaller::UpdateAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal(["DbdPresence.LocalCi.WeeklyWslUpdate"], callers);

                // The weekly update looks at the running programs and the owner's switch before it starts the update.
                var run = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.WeeklyWslUpdate+<RunAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var looked = run.IndexOf("DbdPresence.LocalCi.IProcessList::IsAnyRunning");
                var started = run.IndexOf("DbdPresence.LocalCi.IWslInstaller::UpdateAsync");
                Assert.True(looked >= 0 && started > looked, "The weekly update must look at the running programs before it updates WSL.");
            }
        }
    }

    [Fact]
    public void ASessionOfTheLinuxMachineTakesTheMachineWideLockFirst_AndTheLoopIsOnlyStoppedBySetupTheProbeAndUninstall()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // Local CI round 4, red-team F3 and QA M-2: a session begins with wsl --shutdown, so it takes the lock that keeps a second program (the probe, set-up, another loop) out before anything is done
                // to the machine, and lets it go only after the machine was shut down and the account's programs were ended.
                var takers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.ISessionLock::TryAcquire")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.WslSandbox"], takers);
                var begin = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.WslSandbox+<BeginAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var locked = begin.IndexOf("DbdPresence.LocalCi.ISessionLock::TryAcquire");
                Assert.True(locked >= 0 && locked < begin.IndexOf("DbdPresence.LocalCi.IWslAccount::ClearLockout") && locked < begin.IndexOf("DbdPresence.LocalCi.IWsl::ShutdownAsync"),
                    "The session lock must be taken before the account is touched and before wsl --shutdown.");

                // The real program gives the sandbox the file lock in the closed data folder.
                var wiring = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.Windows.LocalCiApplication+<RunAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                Assert.Contains("DbdPresence.LocalCi.FileSessionLock::.ctor", wiring);

                // Stopping the background task (which ends a running check) is done by set-up and uninstall, and by the probe only after it asked GitHub that no check is running.
                var stoppers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IScheduler::StopAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal(["DbdPresence.LocalCi.ProbeCommand", "DbdPresence.LocalCi.SetupWizard", "DbdPresence.LocalCi.Uninstaller"], stoppers);
                var run = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.ProbeCommand+<RunAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var asked = run.IndexOf("DbdPresence.LocalCi.ProbeCommand::WhyTheLoopMustStayAsync");
                var stopped = run.IndexOf("DbdPresence.LocalCi.ProbeCommand::StopTheLoopAsync");
                var probed = run.IndexOf("DbdPresence.LocalCi.ProbeReview::RunAsync");
                Assert.True(asked >= 0 && stopped > asked && probed > stopped, "The probe must ask whether a check runs, then stop the loop, and only then run.");
            }
        }
    }

    [Fact]
    public void NoRunnerStartsWithoutAnAcceptedProbe_TheProbeIsRepeatedByTheProgramOnlyAfterItsOwnUpdate_AndWithoutReadingWindowsPermissions()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // Local CI round 4, red-team F5 and QA M-3. The job host asks the gate before it says it is ready, and asks again once the session of a job has read the WSL version, before anything of the
                // job (a leftover check, the base image, the import) is touched.
                var ready = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.LinuxJobHost" && c.Method.Name == "NotReadyReason").Calls;
                Assert.Contains("DbdPresence.LocalCi.ProbeGate::Problem", ready);
                var run = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.LinuxJobHost+<RunAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                var began = run.IndexOf("DbdPresence.LocalCi.WslSandbox::BeginAsync");
                var asked = run.IndexOf("DbdPresence.LocalCi.ProbeGate::Problem");

                // Round 4, F15: the import, the hardening, the isolation check and the runner are all inside the disk watch (a closure that only the watch calls), so the import is not in this method itself.
                var watched = run.IndexOf("DbdPresence.LocalCi.LinuxJobHost::WatchingTheDiskAsync");
                Assert.True(began >= 0 && asked > began && watched > asked, "A job must ask the probe gate after its session began and before the job's machine is imported (inside the disk watch).");
                Assert.DoesNotContain("DbdPresence.LocalCi.IWsl::ImportAsync", run);
                var importers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IWsl::ImportAsync") && Outer(c.Method.DeclaringType) == "DbdPresence.LocalCi.LinuxJobHost").ToList();
                Assert.NotEmpty(importers);
                Assert.DoesNotContain(run, x => x is "DbdPresence.LocalCi.LinuxJobHost::HardenAsync" or "DbdPresence.LocalCi.LinuxJobHost::RequireIsolationAsync");

                // The program repeats the probe by itself in one place, after its own WSL update, and that repeat never reads Windows permissions (it is the attempts only).
                var renewers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.ProbeRenewal::AfterWslUpdateAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.Maintenance"], renewers);
                var maintenance = all.Single(c => c.Method.DeclaringType.StartsWith("DbdPresence.LocalCi.Maintenance+<RunDueAsync>", StringComparison.Ordinal) && c.Method.Name == "MoveNext").Calls;
                Assert.True(maintenance.IndexOf("DbdPresence.LocalCi.Maintenance::UpdateWslAsync") is var updated && updated >= 0 && maintenance.IndexOf("DbdPresence.LocalCi.Maintenance::RenewTheProbeAsync") > updated,
                    "The probe is repeated after the update, not before.");
                var attemptsOnly = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IsolationProbe::RunAttemptsOnlyAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.ProbeRenewal"], attemptsOnly);
                var withAudit = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IsolationProbe::RunAsync")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.ProbeReview"], withAudit);
                Assert.DoesNotContain(all.Where(c => Outer(c.Method.DeclaringType) == "DbdPresence.LocalCi.ProbeRenewal"), c => c.Calls.Contains("DbdPresence.LocalCi.IWritableLocations::FindAsync"));
            }
        }

        // The program updates WSL only with the owner's yes (the question of set-up, default no), through one policy.
        var application = File.ReadAllText(Path.Combine(ToolDirectory, "Windows", "LocalCiApplication.cs"));
        Assert.Contains("WslUpdatePolicy.OffBecause(state.Load().WslUpdateAllowed, File.Exists(paths.WslUpdateOffFile))", application, StringComparison.Ordinal);
        var asking = File.ReadAllText(Path.Combine(ToolDirectory, "SetupWizard.cs"));
        Assert.Contains("s.WslUpdateAllowed = allowUpdates;", asking, StringComparison.Ordinal);
        Assert.Contains("_ui.Confirm(\"Update WSL once a week? (yes/no; Enter means no)\")", asking, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackupRightIsOnlyEverSwitchedOnByTheAuditsRead_ForReadingPermissionsAndNothingElse()
    {
        // Local CI round 4, red-team F1: an administrator's token holds the backup right switched off. The audit of writable places switches it on for the length of one read of a folder's permissions, after
        // the ordinary read was refused, and off again. It is the only right the program ever switches on, and nothing else may call the switch.
        var privilegeNames = SourceFiles()
            .SelectMany(f => Regex.Matches(CodeKeepingStrings(File.ReadAllText(f)), "\"(?<name>Se[A-Za-z]+Privilege)\"", RegexOptions.CultureInvariant).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["SeBackupPrivilege"], privilegeNames);

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                var adjusters = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.NativeMethods::AdjustTokenPrivileges")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.Windows.BackupPrivilege"], adjusters);

                // OpenProcessToken is not OpenProcess: it is only ever given the tool's own pseudo handle (GetCurrentProcess), and only here.
                var tokenOpeners = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.NativeMethods::OpenProcessToken")).ToList();
                Assert.All(tokenOpeners, c => Assert.Equal("DbdPresence.LocalCi.Windows.BackupPrivilege", Outer(c.Method.DeclaringType)));
                var open = Assert.Single(tokenOpeners).Calls;
                Assert.True(open.IndexOf("DbdPresence.LocalCi.Windows.NativeMethods::GetCurrentProcess") is var own && own >= 0 && open.IndexOf("DbdPresence.LocalCi.Windows.NativeMethods::OpenProcessToken") > own,
                    "The token that is opened must be the tool's own.");
                Assert.Single(all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.NativeMethods::GetCurrentProcess")), c => Outer(c.Method.DeclaringType) == "DbdPresence.LocalCi.Windows.BackupPrivilege");

                var enablers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.BackupPrivilege::TryEnable")).Select(c => $"{c.Method.DeclaringType}.{c.Method.Name}").Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.Windows.WindowsFileSecurityProbe.DescribeWithBackupRights"], enablers);

                // The read with the backup right is the ordinary read, between the switching on and the switching off, and is asked for by the audit only.
                var read = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.WindowsFileSecurityProbe" && c.Method.Name == "DescribeWithBackupRights").Calls;
                var on = read.IndexOf("DbdPresence.LocalCi.Windows.BackupPrivilege::TryEnable");
                var describe = read.IndexOf("DbdPresence.LocalCi.Windows.WindowsFileSecurityProbe::Describe");
                var off = read.IndexOf("DbdPresence.LocalCi.Windows.BackupPrivilege::SwitchOff");
                Assert.True(on >= 0 && describe > on && off > describe, "The backup right must be switched on, then the ordinary read made, then the right switched off.");
                Assert.Equal(2, read.Count(c => c == "DbdPresence.LocalCi.Windows.BackupPrivilege::SwitchOff"));

                var askers = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.IAuditSecurityProbe::DescribeWithBackupRights")).Select(c => Outer(c.Method.DeclaringType)).Distinct().ToList();
                Assert.Equal(["DbdPresence.LocalCi.AccountWritableLocations"], askers);
                var audit = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.AccountWritableLocations" && c.Method.Name == "Read").Calls;
                Assert.True(audit.IndexOf("DbdPresence.LocalCi.IFileSecurityProbe::Describe") is var ordinary && ordinary >= 0 && audit.IndexOf("DbdPresence.LocalCi.IAuditSecurityProbe::DescribeWithBackupRights") > ordinary,
                    "The audit reads the ordinary way first and only then, after a refusal, with the backup right.");
            }
        }
    }

    [Fact]
    public void ThePublicFolderTheEnvironmentNamesIsReadInOnePlace_AndOnlyAcceptedThroughThePolicy()
    {
        // Local CI round 4, red-team F13: %PUBLIC% belongs to the owner's account, so the elevated program never looks in a folder it names unless it is an ordinary folder on a local fixed disk.
        var readers = SourceFiles().Where(f => EnvironmentReads(File.ReadAllText(f)).Contains("PUBLIC")).Select(Relative).ToList();
        Assert.Equal(["Windows/PublicFolders.cs"], readers);

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();
                var folders = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.PublicFolders" && c.Method.Name == "Folders").Calls;
                Assert.Contains("DbdPresence.LocalCi.LocalPublicFolder::Folders", folders);

                var users = all.Where(c => c.Calls.Contains("DbdPresence.LocalCi.Windows.PublicFolders::Folders")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal(["DbdPresence.LocalCi.Windows.LocalCiApplication", "DbdPresence.LocalCi.Windows.WindowsLocationSource"], users);
            }
        }
    }

    [Fact]
    public void TheAccountsPasswordIsRegisteredWithTheRedactor_IsSavedBeforeTheAccountIsMadeOrChanged_AndIsWipedFromMemoryAfterTheLogon()
    {
        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();

                // Set-up makes a new random password at every run: it is registered with the redactor (so no log line or message can show it) and its record is saved before the account is created or
                // its password is set, so a stop in between leaves a record the next run replaces and never an account whose password nobody has.
                var ensure = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.WindowsWslAccount" && c.Method.Name == "Ensure").Calls;
                var registered = ensure.IndexOf("DbdPresence.LocalCi.Redactor::Register");
                var saved = ensure.IndexOf("DbdPresence.LocalCi.ISecretStore::Save");
                var created = ensure.IndexOf("DbdPresence.LocalCi.Windows.WindowsWslAccount::CreateAccount");
                var reset = ensure.IndexOf("DbdPresence.LocalCi.Windows.WindowsWslAccount::SetPassword");
                Assert.True(registered >= 0 && saved > registered, "The password must be registered with the redactor before it is saved.");
                Assert.True(created > saved && reset > saved, "The password record must be saved before the account is created or its password is set.");

                var credentials = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows.WindowsWslAccount" && c.Method.Name == "Credentials").Calls;
                Assert.Contains("DbdPresence.LocalCi.Redactor::Register", credentials);

                // The unmanaged copies of the password are zeroed and freed in the places that make them.
                foreach (var (type, method) in new[] { ("LogonJob", "StartSuspended"), ("WindowsWslAccount", "CreateAccount"), ("WindowsWslAccount", "SetPassword") })
                {
                    var made = all.Single(c => c.Method.DeclaringType == "DbdPresence.LocalCi.Windows." + type && c.Method.Name == method).Calls;
                    Assert.Contains("System.Runtime.InteropServices.Marshal::ZeroFreeCoTaskMemUnicode", made);
                }
            }
        }
    }
}
