// Covers: SAFETY_AND_PRIVACY section 2.1 (nothing touches the game process), NFR-SEC-01, and threat-model review TM-11. The unshipped spike tool in
// tools/DbdPresence.Spikes is run by the owner on their own PC (sign-in loopback, Discord IPC cadence). It legitimately opens a loopback listener,
// the Discord pipe and the system browser, so those are allowed; it must not P/Invoke, inspect or open processes, load code, use an HTTP client, or
// reach product projects other than the Discord adapter. Tools are outside src/, so they are checked here.
namespace DbdPresence.ArchitectureTests;

public sealed class SpikeToolBoundaryTests
{
    private static readonly string[] s_bannedSourceTokens =
    [
        "HttpClient", "WebClient", "WebRequest", "UdpClient", "DllImport", "LibraryImport", "ReadProcessMemory", "OpenProcess",
        "Process.GetProcess", "GetProcessesByName", "Assembly.Load", "Activator.CreateInstance", "NativeLibrary", "System.Reflection.Emit",
    ];

    private static string ToolDirectory => Path.Combine(ProductAssemblies.RepositoryRoot, "tools", "DbdPresence.Spikes");

    [Fact]
    public void SpikeToolSource_HasNoHttpClientNativeCodeOrProcessInspection()
    {
        var files = Directory.GetFiles(ToolDirectory, "*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(f => s_bannedSourceTokens
                .Where(t => File.ReadAllText(f).Contains(t, StringComparison.Ordinal))
                .Select(t => $"{Path.GetFileName(f)} contains '{t}'"))
            .ToList();

        Assert.True(violations.Count == 0, string.Join("\n  ", violations));
    }

    [Fact]
    public void SpikeToolProject_ReferencesOnlyTheDiscordAdapter_AndNoPackages()
    {
        var project = File.ReadAllText(Path.Combine(ToolDirectory, "DbdPresence.Spikes.csproj"));

        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        var references = System.Text.RegularExpressions.Regex.Matches(project, "ProjectReference Include=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToArray();
        Assert.Equal(["../../src/DbdPresence.Discord/DbdPresence.Discord.csproj"], references);
    }

    [Fact]
    public void BuiltSpikeTool_HasNoPInvoke_NoProductOrPlatformReference_AndStartsOnlyTheBrowser()
    {
        var binDir = Path.Combine(ToolDirectory, "bin", ProductAssemblies.Configuration);
        var paths = Directory.Exists(binDir) ? Directory.GetFiles(binDir, "DbdPresence.Spikes.dll", SearchOption.AllDirectories) : [];
        Assert.True(paths.Length > 0, $"No built spike tool under {binDir}. Build the solution first.");

        foreach (var path in paths)
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Empty(assembly.PInvokes);
            Assert.DoesNotContain("DbdPresence.Online", assembly.AssemblyReferences);
            Assert.DoesNotContain("DbdPresence.Platform.Windows", assembly.AssemblyReferences);
            Assert.DoesNotContain("System.Net.Http", assembly.AssemblyReferences);
            Assert.DoesNotContain(
                assembly.MemberReferences,
                m => m.ParentType == "System.Diagnostics.Process" && m.MemberName != "Start" && m.MemberName != "Dispose");
        }
    }

    [Fact]
    public void NoProductProject_ReferencesATool()
    {
        foreach (var project in Directory.EnumerateFiles(Path.Combine(ProductAssemblies.RepositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(project).Replace('\\', '/');
            Assert.DoesNotContain("tools/", text, StringComparison.Ordinal);
        }
    }
}
