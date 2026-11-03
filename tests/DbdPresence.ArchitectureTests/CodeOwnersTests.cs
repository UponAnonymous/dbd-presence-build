// Covers: local CI round 4, red-team F11 and QA minor 1 (the files that decide what CI runs, what the owner's local CI program is built from and what guards its rules are owned by the owner in
// .github/CODEOWNERS: the workflows, the build scripts, the program, its tests and the whole architecture test project, the shared build files, the tool versions, the style rules and the solution;
// a build file added to the repository root later must get an owner too). The file does nothing unless "Require review from Code Owners" is switched on for main: UNVERIFIED, a repository setting.
namespace DbdPresence.ArchitectureTests;

public sealed class CodeOwnersTests
{
    private const string Owner = "@UponAnonymous";

    private static Dictionary<string, string[]> Entries()
    {
        var path = Path.Combine(ProductAssemblies.RepositoryRoot, ".github", "CODEOWNERS");
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1..], StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("/.github/")]
    [InlineData("/service/scripts/")]
    [InlineData("/build/")]
    [InlineData("/tools/DbdPresence.ReleaseSigner/")]
    [InlineData("/tools/DbdPresence.LocalCi/")]
    [InlineData("/tests/DbdPresence.LocalCi.Tests/")]
    [InlineData("/tests/DbdPresence.ArchitectureTests/")]
    [InlineData("/docs/owner/free-ci.md")]
    [InlineData("/Directory.Build.props")]
    [InlineData("/Directory.Build.targets")]
    [InlineData("/Directory.Packages.props")]
    [InlineData("/global.json")]
    [InlineData("/nuget.config")]
    [InlineData("/.config/")]
    [InlineData("/.editorconfig")]
    [InlineData("/DbdPresence.slnx")]
    public void WhatDecidesWhatCiRunsAndWhatTheProgramIsBuiltFromAndGuardedBy_IsOwnedByTheOwner(string pattern)
    {
        var entries = Entries();

        Assert.True(entries.TryGetValue(pattern, out var owners), $"CODEOWNERS has no line for {pattern}");
        Assert.Equal([Owner], owners);
    }

    [Fact]
    public void EveryBuildFileAtTheRepositoryRoot_HasAnOwnerLine()
    {
        var entries = Entries();
        var names = Directory.EnumerateFiles(ProductAssemblies.RepositoryRoot)
            .Select(f => Path.GetFileName(f))
            .Where(n => n.StartsWith("Directory.", StringComparison.Ordinal) || n.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || n.Equals("global.json", StringComparison.OrdinalIgnoreCase) || n.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase)
                || n.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Contains("Directory.Build.props", names);
        var missing = names.Where(n => !entries.ContainsKey("/" + n)).ToList();
        Assert.True(missing.Count == 0, "These build files at the repository root have no line in .github/CODEOWNERS: " + string.Join(", ", missing));
    }
}
