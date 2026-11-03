// Covers: FR-LIFE-01 (one binary, two installs: a process with package identity uses the startup task, any other uses the Run key) and the
// Installed apps launcher's fixed address. Windows-only; the real package case is exercised by build/msix-smoke.ps1 (PENDING-ENV until CI runs it).
using System.Runtime.InteropServices;
using DbdPresence.Platform.Windows.Startup;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class PackageIdentityTests
{
    [Fact]
    public void Detect_ANameMeansPackaged() =>
        Assert.True(PackageIdentity.Detect(() => "DbdPresence_8wekyb3d8bbwe"));

    [Fact]
    public void Detect_NoIdentityMeansUnpackaged()
    {
        Assert.False(PackageIdentity.Detect(() => throw new InvalidOperationException("no package identity")));
#pragma warning disable CA2201 // The real API reports "no package identity" as a COMException; the test throws what Windows would.
        Assert.False(PackageIdentity.Detect(() => throw new COMException("no package identity", unchecked((int)0x80073D54))));
#pragma warning restore CA2201
        Assert.False(PackageIdentity.Detect(() => null));
        Assert.False(PackageIdentity.Detect(() => string.Empty));
    }

    [Fact]
    public void Detect_OtherFailuresAreNotHidden() =>
        Assert.Throws<ArgumentException>(() => PackageIdentity.Detect(() => throw new ArgumentException("unexpected")));

    [Fact]
    public void TheTestHostHasNoPackageIdentity() => Assert.False(PackageIdentity.IsPackaged);

    [Fact]
    public void InstalledAppsLauncher_OpensOnlyTheInstalledAppsPage() =>
        Assert.Equal("ms-settings:appsfeatures", InstalledAppsLauncher.SettingsUri);

    [Fact]
    public void StartupTaskId_MatchesTheManifest()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "build", "msix", "AppxManifest.xml")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var manifest = File.ReadAllText(Path.Combine(root, "build", "msix", "AppxManifest.xml"));
        Assert.Contains($"TaskId=\"{PackagedStartupTask.TaskId}\"", manifest, StringComparison.Ordinal);
    }
}
