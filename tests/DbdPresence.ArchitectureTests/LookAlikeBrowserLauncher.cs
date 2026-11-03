namespace DbdPresence.Platform.Windows.Accounts;

/// <summary>
/// A look-alike of the real browser launcher (same full name, same URL literal, UseShellExecute set) compiled into the test assembly,
/// so InspectorSelfTests can prove the browser exemption also requires the right assembly. Never executed.
/// </summary>
internal static class DiscordBrowserLauncher
{
    internal static void Open(string url)
    {
        if (url.StartsWith("https://discord.com/oauth2/authorize?", StringComparison.Ordinal))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
    }
}
