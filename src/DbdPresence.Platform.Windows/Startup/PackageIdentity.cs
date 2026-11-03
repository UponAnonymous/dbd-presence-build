using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace DbdPresence.Platform.Windows.Startup;

/// <summary>
/// Whether this process runs with package identity, that is, from the Microsoft Store (MSIX) package (FR-LIFE-01). One binary serves both
/// installs: the Velopack Setup.exe copy and the Store package. A packaged app must not write the HKCU Run key (the write is redirected into
/// the package) and must not update itself (Store policy), so the app composes different services when this is true.
/// </summary>
public static class PackageIdentity
{
    private static readonly Lazy<bool> s_isPackaged = new(() => Detect(ReadFamilyName));

    /// <summary>Gets a value indicating whether the running process has package identity. Decided once, on first use.</summary>
    public static bool IsPackaged => s_isPackaged.Value;

    /// <summary>
    /// Decides from a reader of the package family name: a reader that returns a name means packaged; one that throws because the process has no
    /// identity means unpackaged. Exposed so the decision is testable without a package.
    /// </summary>
    /// <param name="readFamilyName">Returns the package family name, or throws when the process has no package identity.</param>
    /// <returns>True when a non-empty name was returned.</returns>
    internal static bool Detect(Func<string?> readFamilyName)
    {
        ArgumentNullException.ThrowIfNull(readFamilyName);
        try
        {
            return !string.IsNullOrEmpty(readFamilyName());
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // "The process has no package identity": a Velopack install, a portable copy or a development build.
            return false;
        }
    }

    private static string ReadFamilyName() => Package.Current.Id.FamilyName;
}
