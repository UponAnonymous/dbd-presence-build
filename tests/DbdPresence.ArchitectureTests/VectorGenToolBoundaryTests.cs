// Covers: NFR-SEC-01 and threat-model review TM-11. The developer tool in tools/DbdPresence.Packs.VectorGen regenerates the signing test vectors
// under tests/fixtures/signing. It never ships, has no project or package references, and uses no network, process or native code.
namespace DbdPresence.ArchitectureTests;

public sealed class VectorGenToolBoundaryTests
{
    private static readonly string[] s_bannedSourceTokens =
    [
        "System.Net", "HttpClient", "HttpListener", "Socket", "WebClient", "TcpClient", "UdpClient", "WebRequest", "NamedPipe",
        "http://", "https://", "Process.Start", "ProcessStartInfo", "System.Diagnostics.Process", "DllImport", "LibraryImport",
        "ReadProcessMemory", "OpenProcess", "Assembly.Load", "Activator.CreateInstance",
    ];

    private static string ToolDirectory => Path.Combine(ProductAssemblies.RepositoryRoot, "tools", "DbdPresence.Packs.VectorGen");

    [Fact]
    public void VectorGenSource_ContainsNoNetworkProcessOrNativeCode()
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
    public void VectorGenProject_HasNoPackageOrProjectReferences()
    {
        var project = File.ReadAllText(Path.Combine(ToolDirectory, "DbdPresence.Packs.VectorGen.csproj"));

        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltVectorGen_HasNoPInvoke_NoProductReference_AndNoNetworkReference()
    {
        var binDir = Path.Combine(ToolDirectory, "bin", ProductAssemblies.Configuration);
        var paths = Directory.Exists(binDir) ? Directory.GetFiles(binDir, "DbdPresence.Packs.VectorGen.dll", SearchOption.AllDirectories) : [];
        Assert.True(paths.Length > 0, $"No built vector tool under {binDir}. Build the solution first.");

        foreach (var path in paths)
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Empty(assembly.PInvokes);
            Assert.DoesNotContain(assembly.AssemblyReferences, r => r.StartsWith("DbdPresence.", StringComparison.Ordinal));
            Assert.DoesNotContain(assembly.AssemblyReferences, r => SafetyRules.NetworkAssemblies.Contains(r));
        }
    }
}
