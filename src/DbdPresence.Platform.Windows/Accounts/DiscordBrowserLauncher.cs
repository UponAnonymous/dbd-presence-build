using System.ComponentModel;
using System.Diagnostics;
using DbdPresence.Core.Online;

namespace DbdPresence.Platform.Windows.Accounts;

/// <summary>
/// Opens Discord's OAuth authorize page in the user's default browser (ADR-0016). This is the one named <c>Process.Start</c>
/// exception for the browser: it refuses any URL that is not https on discord.com under <c>/oauth2/authorize</c> and has no
/// credentials, then starts it through the shell so Windows picks the default browser. Windows behaviour is PENDING-ENV.
/// </summary>
public sealed class DiscordBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public bool TryOpen(string url)
    {
        if (url is null
            || !url.StartsWith("https://discord.com/oauth2/authorize?", StringComparison.Ordinal)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "discord.com" || uri.AbsolutePath != "/oauth2/authorize" || uri.UserInfo.Length != 0)
        {
            return false;
        }

        try
        {
            // The shell may hand the URL to an already running browser, in which case no process object comes back.
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Win32Exception)
        {
            // No default browser is registered, or the shell refused: reported to the user as "could not open your browser".
            return false;
        }
    }
}
