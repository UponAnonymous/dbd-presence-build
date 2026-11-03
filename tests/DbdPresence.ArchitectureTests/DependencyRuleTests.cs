using System.Xml.Linq;

namespace DbdPresence.ArchitectureTests;

/// <summary>
/// ARCHITECTURE §2 dependency rule. Checked twice: on compiled assembly references (what the code uses)
/// and on csproj ProjectReferences (what the code could use — compilers drop unused references).
/// </summary>
public sealed class DependencyRuleTests
{
    private static readonly Dictionary<string, string[]> s_allowed = new(StringComparer.Ordinal)
    {
        ["DbdPresence.Core"] = [],
        ["DbdPresence.Configuration"] = ["DbdPresence.Core"],
        ["DbdPresence.Discord"] = ["DbdPresence.Core"],
        ["DbdPresence.Vision"] = ["DbdPresence.Core"],
        ["DbdPresence.Packs"] = ["DbdPresence.Core"],
        ["DbdPresence.Study"] = ["DbdPresence.Core", "DbdPresence.Vision"],
        ["DbdPresence.Online"] = ["DbdPresence.Core", "DbdPresence.Packs"],
        ["DbdPresence.Presentation"] = ["DbdPresence.Core", "DbdPresence.Configuration", "DbdPresence.Discord", "DbdPresence.Vision", "DbdPresence.Packs", "DbdPresence.Study"],
        ["DbdPresence.ImageHelper"] = [],
        ["DbdPresence.Platform.Windows"] = ["DbdPresence.Core", "DbdPresence.Vision", "DbdPresence.Discord"],
        ["DbdPresence.App"] =
        [
            "DbdPresence.Core", "DbdPresence.Configuration", "DbdPresence.Discord", "DbdPresence.Vision",
            "DbdPresence.Presentation", "DbdPresence.Platform.Windows", "DbdPresence.Packs", "DbdPresence.Online", "DbdPresence.Study",
        ],
    };

    public static TheoryData<string, string> Assemblies => ProductAssemblies.All();

    public static TheoryData<string> Projects => [.. ProductAssemblies.Projects.Keys];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void ProductAssembly_ReferencesOnlyAllowedProductAssemblies(string project, string relativePath)
    {
        using var assembly = new AssemblyInspector(ProductAssemblies.Absolute(relativePath));

        var productRefs = assembly.AssemblyReferences.Where(IsProduct).Select(ToProject).ToArray();
        var disallowed = productRefs.Except(s_allowed[project], StringComparer.Ordinal).ToArray();

        Assert.True(disallowed.Length == 0, $"{project} must not reference: {string.Join(", ", disallowed)}");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void ProjectFile_ReferencesOnlyAllowedProjects(string project)
    {
        var csproj = Path.Combine(ProductAssemblies.RepositoryRoot, "src", project, $"{project}.csproj");
        var references = XDocument.Load(csproj).Descendants("ProjectReference")
            .Where(e => !IsHelperBuildOrderReference(project, e))
            .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
            .ToArray();

        var disallowed = references.Except(s_allowed[project], StringComparer.Ordinal).ToArray();

        Assert.True(disallowed.Length == 0, $"{project}.csproj must not reference: {string.Join(", ", disallowed)}");
    }

    [Theory]
    [InlineData("DbdPresence.Core")]
    [InlineData("DbdPresence.Configuration")]
    [InlineData("DbdPresence.Discord")]
    [InlineData("DbdPresence.Vision")]
    [InlineData("DbdPresence.Study")]
    [InlineData("DbdPresence.Packs")]
    [InlineData("DbdPresence.Online")]
    [InlineData("DbdPresence.Presentation")]
    public void PlatformNeutralProject_DoesNotUseWindowsUiOrWinRt(string project)
    {
        string[] forbidden = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "Microsoft.Windows.SDK.NET", "WinRT.Runtime", "System.Drawing.Common"];
        foreach (var path in ProductAssemblies.PathsOf(project))
        {
            using var assembly = new AssemblyInspector(path);
            var hits = assembly.AssemblyReferences.Intersect(forbidden, StringComparer.Ordinal).ToArray();
            Assert.True(hits.Length == 0, $"{project} must stay platform-neutral but references: {string.Join(", ", hits)}");
        }

        var tfm = XDocument.Load(Path.Combine(ProductAssemblies.RepositoryRoot, "src", project, $"{project}.csproj"))
            .Descendants("TargetFramework").Single().Value;
        Assert.Equal("net10.0", tfm);
    }

    /// <summary>
    /// The one named exception to the project-reference rule: the app builds and ships the image helper (a separate
    /// process) but never links it, so the reference must say <c>ReferenceOutputAssembly="false"</c> (FR-PACK-10).
    /// The compiled-assembly check above still forbids any real reference from the app to the helper.
    /// </summary>
    private static bool IsHelperBuildOrderReference(string project, XElement reference) =>
        project == "DbdPresence.App"
        && Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!) == "DbdPresence.ImageHelper"
        && string.Equals((string?)reference.Attribute("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase);

    private static bool IsProduct(string reference) =>
        reference.StartsWith("DbdPresence.", StringComparison.Ordinal) || reference == "DBD-Presence";

    private static string ToProject(string assemblyName) => assemblyName == "DBD-Presence" ? "DbdPresence.App" : assemblyName;
}
