// Covers: D32 / FR-ONL-13 (no unlock bypass: the UI-test build trusts an extra licence key, and no release or production output can). The
// Release output (every copy under bin/{Configuration}, including a publish folder) must not contain the test key id, the test key itself or the
// UI-test compile constant's code; the trusted-key list of the service must not carry it; and the constant is defined only for the UI-test build.
using System.Text;
using System.Text.RegularExpressions;

namespace DbdPresence.ArchitectureTests;

public sealed partial class UiTestKeyGuardTests
{
    private const string TestKeyId = "licence-uitest-1";
    private const string TestKeySpki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcM3EqSu1j0pDkuPpCfxgB96mNT16ktcx1JYatNcnkmYucJlrDs4jEkmWjVAWXqLBmdNga9SFBJbovbbfxcUm4w";

    public static TheoryData<string> ShippedProjects() => new() { "DbdPresence.App", "DbdPresence.Online", "DbdPresence.Platform.Windows", "DbdPresence.Presentation", "DbdPresence.Core", "DbdPresence.Study", "DbdPresence.Vision" };

    [Theory]
    [MemberData(nameof(ShippedProjects))]
    public void BuiltOutput_NeverContainsTheUiTestKeyOrItsId(string project)
    {
        foreach (var path in ProductAssemblies.PathsOf(project))
        {
            var bytes = File.ReadAllBytes(path);

            foreach (var needle in new[] { TestKeyId, TestKeySpki, "UiTestTrust" })
            {
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(needle)) || Contains(bytes, Encoding.UTF8.GetBytes(needle)), $"{path} contains '{needle}'.");
            }
        }
    }

    private static readonly string[] s_hooks = ["DBDP_PACK_SYNC_DIR", "DBDP_STUDY_OWNER"];

    private static readonly string[] s_shipped = ["DbdPresence.App", "DbdPresence.Online", "DbdPresence.Platform.Windows", "DbdPresence.Presentation", "DbdPresence.Core", "DbdPresence.Study", "DbdPresence.Vision"];

    /// <summary>The environment variables that only the UI-test build reads: the pack-sync folder (V2-S4b) and the owner-account stand-in for the Study page (AL-5).</summary>
    public static TheoryData<string> HookVariables() => [.. s_hooks];

    public static TheoryData<string, string> ShippedProjectsWithHooks()
    {
        var data = new TheoryData<string, string>();
        foreach (var project in s_shipped)
        {
            foreach (var hook in s_hooks)
            {
                data.Add(project, hook);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedProjectsWithHooks))]
    public void BuiltOutput_HasNoUiTestHook(string project, string hook)
    {
        foreach (var path in ProductAssemblies.PathsOf(project))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.False(
                Contains(bytes, Encoding.Unicode.GetBytes(hook)) || Contains(bytes, Encoding.UTF8.GetBytes(hook)),
                $"{path} contains the UI-test hook variable {hook} (TM-10).");
        }
    }

    [Theory]
    [MemberData(nameof(HookVariables))]
    public void TheUiTestHookVariables_AppearInSourceOnlyInsideTheUiTestConstant(string hook)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ProductAssemblies.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            var guarded = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#if DBDP_UITEST", StringComparison.Ordinal))
                {
                    guarded = true;
                }
                else if (trimmed.StartsWith("#else", StringComparison.Ordinal) || trimmed.StartsWith("#endif", StringComparison.Ordinal))
                {
                    guarded = false;
                }
                else if (trimmed.Contains(hook, StringComparison.Ordinal) && !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    Assert.True(guarded, $"{file} uses {hook} outside #if DBDP_UITEST.");
                }
            }
        }
    }

    [Fact]
    public void EmbeddedServiceKeyList_HasNoTestKey_InTheSourceOrTheBuiltAssembly()
    {
        var source = File.ReadAllText(Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.Online", "TrustedServiceKeys.json"));
        Assert.DoesNotContain("uitest", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestKeySpki, source, StringComparison.Ordinal);

        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.Online"))
        {
            // The list is an embedded resource: stored as plain UTF-8 inside the assembly.
            Assert.False(Contains(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(TestKeyId)), $"{path} embeds the test key id.");
        }
    }

    [Fact]
    public void TheUiTestConstant_IsDefinedOnlyForTheUiTestBuild_AndOnlyTheAppCompilesIt()
    {
        var root = ProductAssemblies.RepositoryRoot;
        var app = File.ReadAllText(Path.Combine(root, "src", "DbdPresence.App", "DbdPresence.App.csproj"));
        var definitions = DefineLine().Matches(app);
        var match = Assert.Single(definitions);
        var group = app[..match.Index];
        Assert.Contains("Condition=\"'$(DbdUiTestBuild)' == 'true'\"", group[group.LastIndexOf("<PropertyGroup", StringComparison.Ordinal)..], StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal))
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("DBDP_UITEST", StringComparison.Ordinal))
            {
                Assert.True(file.Contains($"DbdPresence.App{Path.DirectorySeparatorChar}", StringComparison.Ordinal), $"{file} mentions the UI-test constant outside the app project.");
            }
        }

        foreach (var props in new[] { "Directory.Build.props", "Directory.Packages.props" })
        {
            Assert.DoesNotContain("DBDP_UITEST", File.ReadAllText(Path.Combine(root, props)), StringComparison.Ordinal);
        }

        foreach (var workflow in Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows")))
        {
            Assert.DoesNotContain("DbdUiTestBuild", File.ReadAllText(workflow), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheUiTestKeyFile_IsGuardedByTheConstant()
    {
        var text = File.ReadAllText(Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.App", "Composition", "UiTestTrust.cs"));

        Assert.StartsWith("#if DBDP_UITEST", text, StringComparison.Ordinal);
        Assert.EndsWith("#endif", text.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains(TestKeySpki, text, StringComparison.Ordinal);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [GeneratedRegex(@"<DefineConstants>[^<]*DBDP_UITEST[^<]*</DefineConstants>")]
    private static partial Regex DefineLine();
}
