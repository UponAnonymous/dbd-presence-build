// Covers: FR-CI-05 and FR-CI-09 (what the owner's local CI program may do with the Windows registry, with the arguments of the programs it starts, with recursive deletion and with
// environment variables and runtime settings), SAFETY_AND_PRIVACY section 2.1 (it never touches the game process), NFR-SEC-01. These are the "architecture-test blind spots" of the round 3
// red-team review (docs/reviews/2026-10-30-local-ci-round3-redteam.md): registry use unpoliced, process arguments not checked, recursive Directory.Delete not banned, runtime and environment
// knobs invisible. Like the rest of this class they read the tool's source and its built assembly; the small scanning helpers have their own tests with made-up code, so a rule that could never
// fail would show.
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DbdPresence.ArchitectureTests;

public sealed partial class LocalCiToolBoundaryTests
{
    /// <summary>The registry keys the tool names, each with the reason. Everything is under HKEY_LOCAL_MACHINE; one key is written (the sign-in screen's list of hidden accounts).</summary>
    private static readonly Dictionary<string, string> s_registryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList"] = "written: hides the dedicated account from the sign-in screen and shows it again on uninstall",
        [@"SYSTEM\CurrentControlSet\Services\seclogon"] = "read: is the Secondary Logon service switched off (it starts every command as the dedicated account)",
        [@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\"] = "read: where Windows put the profile folder of the dedicated account (the key of its security identifier)",
        [@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"] = "read: the machine's search path, for the audit of the places the dedicated account may write",
    };

    /// <summary>The members of RegistryKey the built tool may call; the second list is the writes.</summary>
    private static readonly string[] s_registryReadMembers = ["OpenSubKey", "GetValue", "Dispose"];

    private static readonly string[] s_registryWriteMembers = ["CreateSubKey", "SetValue", "DeleteValue"];

    /// <summary>The two classes that touch the registry, and the only two methods that write to it.</summary>
    private static readonly string[] s_registryClasses = ["DbdPresence.LocalCi.Windows.WindowsLocationSource", "DbdPresence.LocalCi.Windows.WindowsWslAccount"];

    private static readonly string[] s_registryWriters = ["HideFromSignInScreen", "ShowOnSignInScreenAgain"];

    /// <summary>
    /// Every command-line word the tool gives a program it starts, or reads from its own command line, each with the program it is for. A new word is a decision: it is added here with its reason, or
    /// the test fails (red-team round 3, blind spot "process arguments not checked"). Words that mount disks, import a disk in place, change a default or manage another user's machines are absent
    /// on purpose.
    /// </summary>
    private static readonly Dictionary<string, string> s_allowedArguments = new(StringComparer.Ordinal)
    {
        ["--import"] = "wsl.exe: makes a Linux machine from the verified image (read from standard input)",
        ["--export"] = "wsl.exe: writes the base image machine to standard output",
        ["--unregister"] = "wsl.exe: deletes a Linux machine of exactly this program's names",
        ["--terminate"] = "wsl.exe: stops one Linux machine",
        ["--shutdown"] = "wsl.exe: stops the account's whole virtual machine before and after each session",
        ["--list"] = "wsl.exe: lists the account's Linux machines",
        ["--quiet"] = "wsl.exe: --list as plain names",
        ["--all"] = "wsl.exe: --list including machines still being installed or uninstalled",
        ["--version"] = "wsl.exe: the WSL version, for the status (and the program's own --version)",
        ["--install"] = "wsl.exe: installs WSL itself (the only wsl.exe under the administrator's token)",
        ["--no-distribution"] = "wsl.exe: --install without a Linux machine",
        ["--update"] = "wsl.exe: the weekly update of WSL (the same class, the same token)",
        ["--exec"] = "wsl.exe: runs the fixed loader in a machine without a shell around it",
        ["-d"] = "wsl.exe: the machine to run in",
        ["-u"] = "wsl.exe: the Linux user to run as (the job's user, or root for the hardening step, the base image and the probe)",
        ["--cd"] = "wsl.exe: the folder to start in",
        ["-c"] = "sh: runs the fixed loader text",
        ["/Query"] = "schtasks.exe: is the sign-in task there",
        ["/Create"] = "schtasks.exe: creates it from the XML definition",
        ["/XML"] = "schtasks.exe: the definition file",
        ["/Run"] = "schtasks.exe: starts it",
        ["/End"] = "schtasks.exe: stops it",
        ["/Delete"] = "schtasks.exe: removes it",
        ["/TN"] = "schtasks.exe: the task name",
        ["/F"] = "schtasks.exe and taskkill.exe: without questions",
        ["/T"] = "taskkill.exe: with the programs the program started",
        ["/FI"] = "taskkill.exe: with the filter of the dedicated account (TaskkillArguments)",
        ["--elevated"] = "this program's own command line: started again through the administrator prompt",
        ["--owner"] = "this program's own command line: the security identifier of the owner who asked",
        ["--help"] = "this program's own command line",
        ["-h"] = "this program's own command line",
    };

    private static readonly string[] s_recursiveDeleteClasses =
    [
        "DbdPresence.LocalCi.SafeTree", "DbdPresence.LocalCi.Uninstaller", "DbdPresence.LocalCi.WslSandbox", "DbdPresence.LocalCi.Windows.WindowsWslAccount",
    ];

    /// <summary>The environment variables the tool reads by name; the rest are read through the short list of <c>ChildEnvironment</c> and the refused list of <c>ServeStartCheck</c>.</summary>
    private static readonly string[] s_environmentReads = ["PUBLIC", "SystemRoot"];

    /// <summary>The variables with which the .NET runtime loads code the program does not contain; the loop refuses to start when one is set.</summary>
    private static readonly string[] s_refusedRuntimeVariables =
    [
        "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_32", "CORECLR_PROFILER_PATH_64",
        "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "COR_PROFILER_PATH_32", "COR_PROFILER_PATH_64",
    ];

    /// <summary>
    /// The text of a source file with comments removed and raw string blocks (the scripts that run inside Linux) emptied, but the ordinary string literals kept, so that what a program is given can
    /// be read from what the code says. Strings are matched first so that a <c>//</c> inside one (a web address) is not taken for a comment.
    /// </summary>
    internal static string CodeKeepingStrings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Regex.Replace(
            text,
            "\"\"\"(?:.|\\n)*?\"\"\"|(?:@\\$?|\\$@)\"(?:[^\"]|\"\")*\"|\\$?\"(?:[^\"\\\\\\n]|\\\\.)*\"|'(?:[^'\\\\\\n]|\\\\.)'|//[^\\n]*|/\\*.*?\\*/",
            m => m.Value.StartsWith("\"\"\"", StringComparison.Ordinal) ? "\"\"" : m.Value.StartsWith("//", StringComparison.Ordinal) || m.Value.StartsWith("/*", StringComparison.Ordinal) ? string.Empty : m.Value,
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    /// <summary>The code with comments removed, raw string blocks and the text of every other string literal emptied: what the code does, without what it merely says.</summary>
    internal static string CodeWithoutStrings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Regex.Replace(CodeKeepingStrings(text), "(?:@\\$?|\\$@)\"(?:[^\"]|\"\")*\"|\\$?\"(?:[^\"\\\\\\n]|\\\\.)*\"", "\"\"", RegexOptions.CultureInvariant);
    }

    /// <summary>The command-line words (a whole string literal that is a dash option or a capitalised slash option) in a text of C# code.</summary>
    internal static IReadOnlyList<string> ArgumentWords(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return [.. Regex.Matches(CodeKeepingStrings(code), "\"(?<word>--?[A-Za-z][A-Za-z0-9-]*|/[A-Z][A-Za-z]*)\"", RegexOptions.CultureInvariant).Select(m => m.Groups["word"].Value)];
    }

    /// <summary>True when the code deletes a folder together with what is in it: <c>Directory.Delete(path, true)</c> or <c>.Delete(true)</c>, with or without the name <c>recursive</c>.</summary>
    internal static bool DeletesRecursively(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var text = CodeWithoutStrings(code);
        return Regex.IsMatch(text, @"Directory\.Delete\s*\([^;]*?,\s*(?:recursive\s*:\s*)?true\b", RegexOptions.CultureInvariant)
               || Regex.IsMatch(text, @"\.Delete\s*\(\s*(?:recursive\s*:\s*)?true\s*\)", RegexOptions.CultureInvariant);
    }

    /// <summary>True when the code makes an await return to the caller's context: <c>ConfigureAwait(true)</c> or <c>ConfigureAwait(</c> with anything but <c>false</c>.</summary>
    internal static bool ResumesOnTheCallersContext(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Regex.IsMatch(CodeWithoutStrings(code), @"ConfigureAwait\s*\((?>\s*)(?!false\s*\))", RegexOptions.CultureInvariant);
    }

    /// <summary>The registry key paths a text of C# code names: a verbatim string that starts with SOFTWARE\ or SYSTEM\ (or a HKEY_ root).</summary>
    internal static IReadOnlyList<string> RegistryPaths(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return [.. Regex.Matches(CodeKeepingStrings(code), "@\"(?<path>(?:SOFTWARE|SYSTEM)\\\\[^\"]*|HKEY_[^\"]*)\"", RegexOptions.CultureInvariant).Select(m => m.Groups["path"].Value)];
    }

    /// <summary>The names given to <c>GetEnvironmentVariable("...")</c> in a text of C# code.</summary>
    internal static IReadOnlyList<string> EnvironmentReads(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return [.. Regex.Matches(CodeKeepingStrings(code), "GetEnvironmentVariable\\(\\s*\"(?<name>[^\"]+)\"", RegexOptions.CultureInvariant).Select(m => m.Groups["name"].Value)];
    }

    // ---- The scanning helpers fail on made-up code (a rule that cannot fail proves nothing)

    [Fact]
    public void TheScanningHelpers_FindWhatTheyAreMeantToFind_InMadeUpCode()
    {
        Assert.True(DeletesRecursively("Directory.Delete(path, true);"));
        Assert.True(DeletesRecursively("Directory.Delete(Path.Combine(a, b), recursive: true);"));
        Assert.True(DeletesRecursively("new DirectoryInfo(path).Delete(true);"));
        Assert.False(DeletesRecursively("Directory.Delete(path, recursive: false);"));
        Assert.False(DeletesRecursively("Directory.Delete(path);"));
        Assert.False(DeletesRecursively("// Directory.Delete(path, true);\nvar text = \"Directory.Delete(path, true)\";"));

        Assert.Equal(["--mount", "/Query", "-d"], ArgumentWords("a(\"--mount\", \"/Query\", \"-d\", \"/bin/sh\", \"-\", \"plain\");"));
        Assert.Equal(["--import"], ArgumentWords("var x = \"--import\"; // \"--mount\"\nvar y = \"\"\"\n\"--update\"\n\"\"\";"));

        Assert.Equal([@"SOFTWARE\Vendor\Key", @"SYSTEM\Control"], RegistryPaths("const string A = @\"SOFTWARE\\Vendor\\Key\"; const string B = @\"SYSTEM\\Control\"; var c = \"SOFTWARE\\\\not-verbatim\";"));
        Assert.Equal(["HOME", "PUBLIC"], EnvironmentReads("Environment.GetEnvironmentVariable(\"HOME\"); Environment.GetEnvironmentVariable( \"PUBLIC\" ); var m = Environment.GetEnvironmentVariable;"));
        Assert.True(ResumesOnTheCallersContext("await x.ConfigureAwait(true);"));
        Assert.True(ResumesOnTheCallersContext("await x.ConfigureAwait(flag);"));
        Assert.False(ResumesOnTheCallersContext("await x.ConfigureAwait(false);\nawait y.ConfigureAwait( false );\n// await z.ConfigureAwait(true);"));
        Assert.True(NamesWord("var h = OpenProcess(access, false, id);", "OpenProcess"));
        Assert.True(NamesWord("[DllImport] static extern nint OpenProcess(", "OpenProcess"));
        Assert.False(NamesWord("NativeMethods.OpenProcessToken(own, access, out token);", "OpenProcess"));
        Assert.False(NamesWord("MyOpenProcess(x)", "OpenProcess"));
        Assert.Equal("a  b", CodeKeepingStrings("a /* gone */ b // gone").TrimEnd());
        Assert.Equal("var u = \"https://x\";", CodeKeepingStrings("var u = \"https://x\";"));
        Assert.Equal("var t = \"\"; Run();", CodeWithoutStrings("var t = \"Directory.Delete(p, true)\"; Run(); // note").TrimEnd());
    }

    // ---- Awaiting

    [Fact]
    public void EveryAwaitInTheTool_ContinuesWhereverItCan_NoneIsMadeToReturnToTheCallersContext()
    {
        var offenders = SourceFiles().Where(f => ResumesOnTheCallersContext(File.ReadAllText(f))).Select(Relative).ToList();

        Assert.True(offenders.Count == 0, "These files make an await return to the caller's context (the tool is a library of the loop and the console; every await says ConfigureAwait(false)): " + string.Join(", ", offenders));
    }

    // ---- Registry

    [Fact]
    public void TheRegistryIsOnlyReadAtThreeKeysAndWrittenAtOne_ByTwoClassesAndTwoMethods()
    {
        var named = SourceFiles().SelectMany(f => RegistryPaths(File.ReadAllText(f))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal([.. s_registryKeys.Keys.Order(StringComparer.OrdinalIgnoreCase)], named);

        // The name of this test says what is allowed: four keys in all, three only read and one written (round 4, QA minor 8: the count is now checked, not only said).
        Assert.Equal(3, s_registryKeys.Values.Count(reason => reason.StartsWith("read:", StringComparison.Ordinal)));
        Assert.Equal(1, s_registryKeys.Values.Count(reason => reason.StartsWith("written:", StringComparison.Ordinal)));
        Assert.Equal(4, s_registryKeys.Count);

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var reads = 0;
                var writes = 0;
                foreach (var (method, calls) in Calls(assembly))
                {
                    var registry = calls.Where(c => c.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal)).ToList();
                    if (registry.Count == 0)
                    {
                        continue;
                    }

                    var where = $"{method.DeclaringType}.{method.Name}";
                    Assert.True(s_registryClasses.Contains(Outer(method.DeclaringType)), $"{where} uses the registry but {Outer(method.DeclaringType)} is not one of the two classes that may.");
                    foreach (var call in registry)
                    {
                        if (call.StartsWith("Microsoft.Win32.Registry::", StringComparison.Ordinal))
                        {
                            Assert.True(call == "Microsoft.Win32.Registry::get_LocalMachine", $"{where} uses {call}: only HKEY_LOCAL_MACHINE may be used.");
                            continue;
                        }

                        var member = call[(call.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
                        Assert.True(call.StartsWith("Microsoft.Win32.RegistryKey::", StringComparison.Ordinal) && (s_registryReadMembers.Contains(member) || s_registryWriteMembers.Contains(member)),
                            $"{where} calls {call}, which is not one of the allowed registry calls.");
                        if (s_registryWriteMembers.Contains(member))
                        {
                            writes++;
                            Assert.True(method.DeclaringType == "DbdPresence.LocalCi.Windows.WindowsWslAccount" && s_registryWriters.Contains(method.Name),
                                $"{where} writes to the registry: only {string.Join(" and ", s_registryWriters)} of WindowsWslAccount may.");
                        }
                        else if (member != "Dispose")
                        {
                            reads++;
                        }
                    }
                }

                Assert.True(reads >= 4, $"Expected the four registry reads to be found, found {reads}.");
                Assert.True(writes >= 3, $"Expected the three registry writes (create, set, delete) to be found, found {writes}.");
            }
        }
    }

    // ---- Process arguments

    [Fact]
    public void TheProgramsTheToolStarts_AreOnlyEverGivenTheListedWords_AndNeverOneShellString()
    {
        var seen = SourceFiles().SelectMany(f => ArgumentWords(File.ReadAllText(f))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var unknown = seen.Where(w => !s_allowedArguments.ContainsKey(w)).ToList();
        Assert.True(unknown.Count == 0, "New command-line words need a reason in s_allowedArguments (and a review): " + string.Join(", ", unknown));
        var unused = s_allowedArguments.Keys.Where(w => !seen.Contains(w)).ToList();
        Assert.True(unused.Count == 0, "These words are listed but no longer used, so the list could grow unnoticed: " + string.Join(", ", unused));

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                // The words go into a list, never into one string that a shell would split again.
                var offenders = Calls(assembly)
                    .Where(c => c.Calls.Contains("System.Diagnostics.ProcessStartInfo::set_Arguments"))
                    .Select(c => $"{c.Method.DeclaringType}.{c.Method.Name}")
                    .ToList();
                Assert.True(offenders.Count == 0, "These methods build a process command line as one string: " + string.Join(", ", offenders));
            }
        }
    }

    // ---- Recursive deletion

    [Fact]
    public void NothingIsEverDeletedTogetherWithWhatIsInIt_ExceptByTheLinkFreeWalkOfSafeTree()
    {
        var offenders = SourceFiles().Where(f => DeletesRecursively(File.ReadAllText(f))).Select(Relative).ToList();
        Assert.True(offenders.Count == 0, "These files delete a folder together with its content in one call: " + string.Join(", ", offenders));

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var all = Calls(assembly).ToList();
                Assert.DoesNotContain(all, c => c.Calls.Contains("System.IO.DirectoryInfo::Delete") || c.Calls.Contains("System.IO.FileSystemInfo::Delete"));
                var callers = all.Where(c => c.Calls.Contains("System.IO.Directory::Delete")).Select(c => Outer(c.Method.DeclaringType)).Distinct().Order(StringComparer.Ordinal).ToList();
                Assert.Equal([.. s_recursiveDeleteClasses.Order(StringComparer.Ordinal)], callers);
            }
        }
    }

    // ---- Environment variables and runtime settings

    [Fact]
    public void TheToolReadsOnlyTheListedEnvironmentVariables_NeverSetsOneOrASwitch_AndRefusesTheRuntimesCodeLoadingOnes()
    {
        var reads = SourceFiles().SelectMany(f => EnvironmentReads(File.ReadAllText(f))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([.. s_environmentReads.Order(StringComparer.Ordinal)], reads);

        // The method itself is passed on (never called with a name made at run time) in the two places that hand it to the short list and the refused list.
        var passed = SourceFiles()
            .Where(f => Regex.IsMatch(CodeWithoutStrings(File.ReadAllText(f)), @"Environment\.GetEnvironmentVariable\s*[,)]", RegexOptions.CultureInvariant))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["Windows/LocalCiApplication.cs", "Windows/ProcessRunner.cs"], passed);

        // Nothing sets an environment variable of this process or a switch of the runtime.
        var setters = SourceFiles()
            .Where(f => Regex.IsMatch(CodeWithoutStrings(File.ReadAllText(f)), @"SetEnvironmentVariable|AppContext\s*\.\s*SetSwitch|AppContext\s*\.\s*SetData|GCSettings|ProfileOptimization", RegexOptions.CultureInvariant))
            .Select(Relative)
            .ToList();
        Assert.True(setters.Count == 0, "These files set an environment variable or a runtime switch: " + string.Join(", ", setters));

        // The refused list is exactly what is pinned here (a shortened list would let a profiler or a startup hook in).
        var commandLine = CodeKeepingStrings(File.ReadAllText(Path.Combine(ToolDirectory, "CommandLine.cs")));
        var block = Regex.Match(commandLine, @"CodeLoadingVariables\s*\{\s*get;\s*\}\s*=\s*\[(?<names>[^\]]*)\]", RegexOptions.CultureInvariant);
        Assert.True(block.Success, "CodeLoadingVariables was not found in CommandLine.cs.");
        var refused = Regex.Matches(block.Groups["names"].Value, "\"(?<name>[^\"]+)\"", RegexOptions.CultureInvariant).Select(m => m.Groups["name"].Value).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([.. s_refusedRuntimeVariables.Order(StringComparer.Ordinal)], refused);
    }

    [Fact]
    public void TheProgramIsBuiltWithoutStartupHooks_AndItsRuntimeSettingsAreWhatTheProjectSays()
    {
        var project = XDocument.Load(Path.Combine(ToolDirectory, "DbdPresence.LocalCi.csproj"));
        var properties = project.Descendants("PropertyGroup").SelectMany(g => g.Elements()).ToLookup(e => e.Name.LocalName, e => e.Value.Trim(), StringComparer.Ordinal);
        Assert.Equal(["false"], properties["StartupHookSupport"].ToList());
        Assert.Equal(["true"], properties["InvariantGlobalization"].ToList());
        Assert.Equal(["true"], properties["DeterministicSourcePaths"].ToList());
        Assert.False(properties.Contains("TieredCompilation") || properties.Contains("ServerGarbageCollector") || properties.Contains("ConcurrentGarbageCollection") || properties.Contains("AutoreleasePoolSupport"),
            "The project sets a runtime performance knob; each one is a decision and needs a reason here.");

        foreach (var assembly in BuiltTools())
        {
            using (assembly)
            {
                var runtimeConfig = Path.ChangeExtension(assembly.Path, ".runtimeconfig.json");
                Assert.True(File.Exists(runtimeConfig), $"No runtime configuration beside {assembly.Path}.");
                using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
                var configured = document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
                var names = configured.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

                // The startup hook provider is off in the built program, so DOTNET_STARTUP_HOOKS is ignored by the runtime, and nothing else that changes what the runtime loads is configured.
                Assert.False(configured.GetProperty("System.StartupHookProvider.IsSupported").GetBoolean());
                Assert.Equal(
                    [
                        "System.Globalization.Invariant",
                        "System.Globalization.PredefinedCulturesOnly",
                        "System.Reflection.Metadata.MetadataUpdater.IsSupported",
                        "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization",
                        "System.StartupHookProvider.IsSupported",
                    ],
                    names);
            }
        }
    }
}
