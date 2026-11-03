using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.Spikes;

/// <summary>
/// Step F: does Discord accept a loopback redirect with an arbitrary port when the registered redirect is
/// http://127.0.0.1/callback? The authorization code is never printed, stored or exchanged.
/// </summary>
internal static class LoopbackTest
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(120);

    public static async Task<(string Sent, string Response)> RunAsync(string appId, CancellationToken cancellationToken)
    {
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = Uri.EscapeDataString($"http://127.0.0.1:{port}/callback");
            var url = $"https://discord.com/oauth2/authorize?client_id={appId}&response_type=code&scope=identify&redirect_uri={redirect}&state={state}&code_challenge={challenge}&code_challenge_method=S256";
            var sent = $"Browser opened at {url}";

            Console.WriteLine($"Listening on 127.0.0.1:{port}/callback for up to {s_wait.TotalSeconds:0} s.");
            Console.WriteLine("Opening the browser. If Discord asks, click Authorize. If it shows 'invalid redirect_uri', note that.");
            try
            {
                using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                Console.WriteLine($"Could not open the browser ({ex.Message}). Open this address yourself:");
                Console.WriteLine(url);
            }

            return (sent, await AwaitCallbackAsync(listener, state, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> AwaitCallbackAsync(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_wait);
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, timeout.Token).ConfigureAwait(false);
                var path = target?.Split('?', 2)[0];
                if (path != "/callback")
                {
                    await RespondAsync(stream, "404 Not Found", "Not found.", timeout.Token).ConfigureAwait(false);
                    continue;
                }

                var query = ParseQuery(target!.Contains('?', StringComparison.Ordinal) ? target[(target.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty);
                await RespondAsync(stream, "200 OK", "You can close this tab.", timeout.Token).ConfigureAwait(false);

                var hasCode = query.TryGetValue("code", out var code) && !string.IsNullOrEmpty(code);
                var stateMatches = query.TryGetValue("state", out var returned) && string.Equals(returned, state, StringComparison.Ordinal);
                var error = query.TryGetValue("error", out var err) ? $"; error={err}" : string.Empty;
                return $"Callback arrived on the arbitrary port; code present: {(hasCode ? "yes" : "no")}; state matches: {(stateMatches ? "yes" : "no")}{error}. Code not recorded or exchanged.";
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return $"No callback within {s_wait.TotalSeconds:0} s.";
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
            var text = Encoding.ASCII.GetString(buffer, 0, filled);
            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd >= 0)
            {
                var parts = text[..lineEnd].Split(' ');
                return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0])] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return values;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
