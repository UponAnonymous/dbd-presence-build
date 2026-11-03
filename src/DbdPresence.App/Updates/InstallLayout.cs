using System.IO;
using DbdPresence.Packs.Releases;
using Velopack.Locators;

namespace DbdPresence.App.Updates;

/// <summary>Facts about the Velopack install this process runs from (ADR-0006 "Layout").</summary>
internal static class InstallLayout
{
    /// <summary>The launcher at the install root, named after the installer's <c>--packTitle</c> (build/pack-installer.ps1).</summary>
    public const string LauncherName = "DBD Presence.exe";

    /// <summary>
    /// The program the "start with Windows" entry should run. In an installed or portable copy that is the launcher at the install root, which
    /// stays in place across updates; the program itself lives in the versioned <c>current\</c> folder that an update replaces (ADR-0006 open
    /// finding, fixed in V2-S6). Anywhere else (a development build) it is the running program.
    /// </summary>
    /// <param name="locator">Velopack's view of the install, or null when there is none.</param>
    /// <param name="processPath">The running program.</param>
    /// <returns>The path to register.</returns>
    public static string StartupExecutable(IVelopackLocator? locator, string processPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(processPath);
        string? root;
        try
        {
            root = locator?.CurrentlyInstalledVersion is null ? null : locator.RootAppDir;
        }
        catch (NotSupportedException)
        {
            // A test locator without a root: there is no install, so the running program is used.
            root = null;
        }

        if (root is { Length: > 0 } && File.Exists(Path.Combine(root, LauncherName)))
        {
            return Path.Combine(root, LauncherName);
        }

        return processPath;
    }

    /// <summary>The running version as the release feed compares it: Velopack's installed version, else the assembly's version.</summary>
    /// <param name="locator">Velopack's view of the install, or null.</param>
    /// <param name="informationalVersion">The assembly's informational version (may end with <c>+&lt;commit&gt;</c>).</param>
    /// <returns>The version, or null when neither can be read as a release version (the updater then stays off).</returns>
    public static ReleaseVersion? CurrentVersion(IVelopackLocator? locator, string informationalVersion)
    {
        string? installed;
        try
        {
            installed = locator?.CurrentlyInstalledVersion?.ToNormalizedString();
        }
        catch (NotSupportedException)
        {
            // A test locator without a version: fall back to the assembly's version.
            installed = null;
        }

        if (ReleaseVersion.TryParse(installed) is { } fromInstall)
        {
            return fromInstall;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return ReleaseVersion.TryParse(plus >= 0 ? informationalVersion[..plus] : informationalVersion);
    }
}
