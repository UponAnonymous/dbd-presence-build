// Covers: local CI round 4, QA minor 6 (the source hygiene gate, which CI runs without arguments, checks the owner's local CI program, its tests and the architecture tests as well as the product:
// no marker of unfinished work, no swallowed exception, no empty handler and no raw exception text in a log call in code that runs with administrator rights on the owner's PC).
namespace DbdPresence.ArchitectureTests;

public sealed class HygieneGateScopeTests
{
    private static string Script => File.ReadAllText(Path.Combine(ProductAssemblies.RepositoryRoot, "build", "check-source-hygiene.py"));

    [Theory]
    [InlineData("src")]
    [InlineData("tools/DbdPresence.LocalCi")]
    [InlineData("tests/DbdPresence.LocalCi.Tests")]
    [InlineData("tests/DbdPresence.ArchitectureTests")]
    public void TheHygieneGate_ChecksThisFolderWhenNoFolderIsNamed(string folder)
    {
        var line = Script.Split('\n').Single(l => l.StartsWith("DEFAULT_TARGETS", StringComparison.Ordinal));

        Assert.Contains($"\"{folder}\"", line, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(ProductAssemblies.RepositoryRoot, folder)), $"{folder} does not exist");
    }

    [Fact]
    public void CiRunsTheHygieneGateWithoutNamingFolders_SoItUsesTheDefaultList()
    {
        var workflow = File.ReadAllText(Path.Combine(ProductAssemblies.RepositoryRoot, ".github", "workflows", "ci-linux.yml"));

        Assert.Contains("run: python3 build/check-source-hygiene.py\n", workflow, StringComparison.Ordinal);
    }
}
