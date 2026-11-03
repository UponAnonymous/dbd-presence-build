using System.Reflection;

namespace DbdPresence.ArchitectureTests;

/// <summary>Locates every built output of the product projects under <c>src/*/bin/{Configuration}/</c>.</summary>
internal static class ProductAssemblies
{
    /// <summary>Product projects and the assembly file each produces. <see cref="ProjectInventoryTests"/> keeps it complete.</summary>
    public static readonly IReadOnlyDictionary<string, string> Projects = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DbdPresence.Core"] = "DbdPresence.Core.dll",
        ["DbdPresence.Configuration"] = "DbdPresence.Configuration.dll",
        ["DbdPresence.Discord"] = "DbdPresence.Discord.dll",
        ["DbdPresence.Vision"] = "DbdPresence.Vision.dll",
        ["DbdPresence.Study"] = "DbdPresence.Study.dll",
        ["DbdPresence.Packs"] = "DbdPresence.Packs.dll",
        ["DbdPresence.Online"] = "DbdPresence.Online.dll",
        ["DbdPresence.Presentation"] = "DbdPresence.Presentation.dll",
        ["DbdPresence.Platform.Windows"] = "DbdPresence.Platform.Windows.dll",
        ["DbdPresence.ImageHelper"] = "DbdPresence.ImageHelper.dll",
        ["DbdPresence.App"] = "DBD-Presence.dll",
    };

    public static string RepositoryRoot { get; } = FindRoot();

    public static string Configuration { get; } =
        typeof(ProductAssemblies).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "BuildConfiguration").Value ?? "Debug";

    /// <summary>All built copies of a project's assembly (every TFM/RID folder, incl. publish output). Fails if none exist.</summary>
    public static IReadOnlyList<string> PathsOf(string project)
    {
        var binDir = Path.Combine(RepositoryRoot, "src", project, "bin", Configuration);
        var paths = Directory.Exists(binDir)
            ? Directory.GetFiles(binDir, Projects[project], SearchOption.AllDirectories)
            : [];
        if (paths.Length == 0)
        {
            throw new FileNotFoundException($"No built assembly for {project} under {binDir}. Build the solution first.");
        }

        return paths;
    }

    /// <summary>(project, path) pairs for every built product assembly.</summary>
    public static TheoryData<string, string> All()
    {
        var data = new TheoryData<string, string>();
        foreach (var project in Projects.Keys)
        {
            foreach (var path in PathsOf(project))
            {
                data.Add(project, Path.GetRelativePath(RepositoryRoot, path));
            }
        }

        return data;
    }

    public static string Absolute(string relativePath) => Path.Combine(RepositoryRoot, relativePath);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbdPresence.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (DbdPresence.slnx) not found.");
    }
}
