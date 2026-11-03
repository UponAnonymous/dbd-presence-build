namespace DbdPresence.ArchitectureTests;

/// <summary>Every product project is inspected: adding a project under src/ without registering it fails.</summary>
public sealed class ProjectInventoryTests
{
    [Fact]
    public void EveryProjectUnderSrc_IsInspected()
    {
        var onDisk = Directory.GetDirectories(Path.Combine(ProductAssemblies.RepositoryRoot, "src"))
            .Where(d => File.Exists(Path.Combine(d, $"{Path.GetFileName(d)}.csproj")))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ProductAssemblies.Projects.Keys.Order(StringComparer.Ordinal), onDisk);
    }

    /// <summary>Every tool under tools/ is outside src/, so nothing inspects it unless its boundary test is registered here.</summary>
    private static readonly IReadOnlyDictionary<string, string> s_toolBoundaryTests = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DbdPresence.Eval"] = "EvalToolBoundaryTests.cs",
        ["DbdPresence.LocalCi"] = "LocalCiToolBoundaryTests.cs",
        ["DbdPresence.Packs.VectorGen"] = "VectorGenToolBoundaryTests.cs",
        ["DbdPresence.ReleaseSigner"] = "ReleaseSignerBoundaryTests.cs",
        ["DbdPresence.Spikes"] = "SpikeToolBoundaryTests.cs",
        ["DbdPresence.StudyCapture"] = "StudyToolBoundaryTests.cs",
    };

    [Fact]
    public void EveryToolUnderTools_HasABoundaryTestFile()
    {
        var onDisk = Directory.GetDirectories(Path.Combine(ProductAssemblies.RepositoryRoot, "tools"))
            .Where(d => File.Exists(Path.Combine(d, $"{Path.GetFileName(d)}.csproj")))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(s_toolBoundaryTests.Keys.Order(StringComparer.Ordinal), onDisk);
        foreach (var (tool, file) in s_toolBoundaryTests)
        {
            Assert.True(File.Exists(Path.Combine(ProductAssemblies.RepositoryRoot, "tests", "DbdPresence.ArchitectureTests", file)), $"{tool} has no boundary test file {file}.");
        }
    }
}
