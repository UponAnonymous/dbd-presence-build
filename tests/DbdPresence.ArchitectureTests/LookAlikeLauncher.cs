namespace DbdPresence.Platform.Windows.Images;

/// <summary>
/// A look-alike of the real launcher type (same full name, same helper file name) compiled into the test assembly, so
/// InspectorSelfTests can prove the launcher exemption also requires the right assembly. Never executed.
/// </summary>
internal static class SafeImageLoader
{
    internal static System.Diagnostics.Process? Launch() => System.Diagnostics.Process.Start("DbdPresence.ImageHelper.exe");

    internal static void Stop(System.Diagnostics.Process p) => p.Kill(true);
}
