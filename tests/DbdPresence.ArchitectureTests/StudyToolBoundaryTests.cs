// Covers: SAFETY_AND_PRIVACY section 3 (study capture is owner-run and local only), NFR-SEC-01, NFR-PRIV-01. The unshipped tool in
// tools/DbdPresence.StudyCapture has no network code: no networking references, no host or URL literals, no P/Invoke of its own,
// no process start, and its source holds none of the networking identifiers. Tools are outside src/, so they are checked here.
namespace DbdPresence.ArchitectureTests;

public sealed class StudyToolBoundaryTests
{
    private static readonly string[] s_bannedSourceTokens =
    [
        "System.Net", "HttpClient", "HttpListener", "Socket", "WebClient", "TcpClient", "UdpClient", "WebRequest", "NamedPipe",
        "http://", "https://", "ftp://", "wss://", "ws://", "Process.Start", "ProcessStartInfo", "DllImport", "LibraryImport", "ReadProcessMemory",
    ];

    private static string ToolDirectory => Path.Combine(ProductAssemblies.RepositoryRoot, "tools", "DbdPresence.StudyCapture");

    [Fact]
    public void StudyToolSource_ContainsNoNetworkProcessOrNativeCode()
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
    public void TheSharedRecorderSource_ContainsNoNetworkProcessOrNativeCode()
    {
        // The recorder moved into the Study library (FR-LAB-06) and is linked into the tool as source, so the tool's own scan above no longer sees it.
        var files = Directory.GetFiles(Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.Study", "Recording"), "*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(f => s_bannedSourceTokens
                .Where(t => File.ReadAllText(f).Contains(t, StringComparison.Ordinal))
                .Select(t => $"{Path.GetFileName(f)} contains '{t}'"))
            .ToList();

        Assert.True(violations.Count == 0, string.Join("\n  ", violations));
    }

    [Fact]
    public void StudyToolProject_ReferencesOnlyCoreAndThePlatformAdapter_AndNoPackages()
    {
        var project = File.ReadAllText(Path.Combine(ToolDirectory, "DbdPresence.StudyCapture.csproj"));

        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        var references = System.Text.RegularExpressions.Regex.Matches(project, "ProjectReference Include=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["../../src/DbdPresence.Core/DbdPresence.Core.csproj", "../../src/DbdPresence.Platform.Windows/DbdPresence.Platform.Windows.csproj"],
            references);
    }

    [Fact]
    public void BuiltStudyTool_SatisfiesTheSafetyRules_HasNoHostLiteralsAndNoOnlineOrNetworkReference()
    {
        var binDir = Path.Combine(ToolDirectory, "bin", ProductAssemblies.Configuration);
        var paths = Directory.Exists(binDir) ? Directory.GetFiles(binDir, "DbdPresence.StudyCapture.dll", SearchOption.AllDirectories) : [];
        Assert.True(paths.Length > 0, $"No built study tool under {binDir}. Build the solution first.");

        foreach (var path in paths)
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Empty(SafetyRules.All(assembly));
            Assert.Empty(SafetyRules.HostLiterals(assembly, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            Assert.Empty(assembly.PInvokes);
            Assert.DoesNotContain("DbdPresence.Online", assembly.AssemblyReferences);
            Assert.DoesNotContain(assembly.AssemblyReferences, r => SafetyRules.NetworkAssemblies.Contains(r));
        }
    }

    [Fact]
    public void SourceTokenRule_IsNotVacuous_ItFlagsATokenInASample()
    {
        const string sample = "var c = new System.Net.Http.HttpClient();";

        Assert.Contains(s_bannedSourceTokens, t => sample.Contains(t, StringComparison.Ordinal));
    }
}
