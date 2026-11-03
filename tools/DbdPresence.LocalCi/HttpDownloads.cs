namespace DbdPresence.LocalCi;

/// <summary>
/// Downloads from the approved hosts. Redirects are followed by hand so every hop is checked against <see cref="AllowedHosts"/>
/// (a release address on github.com answers with a redirect to release-assets.githubusercontent.com). No token is ever sent here. A download may start only at an
/// official runner release file or a file of Canonical's Ubuntu 24.04 folder, and it stops when it grows past the limit it was given.
/// The handler behind <c>client</c> must not follow redirects itself.
/// </summary>
public sealed class HttpDownloads : IDownloads
{
    private const int MaximumRedirects = 5;

    /// <summary>The largest text answer (a checksum list, release notes) that is read.</summary>
    public const long MaximumTextBytes = 4L * 1024 * 1024;

    /// <summary>How long a download or an answer may go quiet before it is given up (a dead connection); the request's own timeout only covers the start of the answer.</summary>
    public static readonly TimeSpan DefaultReadInactivityLimit = TimeSpan.FromMinutes(2);

    private readonly HttpClient _client;
    private readonly TimeSpan _inactivityLimit;
    private readonly TimeProvider _time;

    /// <param name="client">A client whose handler does not follow redirects.</param>
    /// <param name="readInactivityLimit">How long one read may wait for data (default 2 minutes).</param>
    /// <param name="time">The clock the limit runs on (tests pass a fake one).</param>
    public HttpDownloads(HttpClient client, TimeSpan? readInactivityLimit = null, TimeProvider? time = null)
    {
        _client = client;
        _inactivityLimit = readInactivityLimit ?? DefaultReadInactivityLimit;
        _time = time ?? TimeProvider.System;
    }

    public async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(url, MaximumTextBytes, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            await CopyLimitedAsync(stream, memory, MaximumTextBytes, url, cancellationToken).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }
    }

    public async Task DownloadToFileAsync(Uri url, string destinationPath, long maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(url, maximumBytes, cancellationToken).ConfigureAwait(false);
        DataFolder.RequireExists(Path.GetDirectoryName(destinationPath)!);
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                await CopyLimitedAsync(source, target, maximumBytes, url, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CopyLimitedAsync(Stream source, Stream target, long maximumBytes, Uri url, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read;
            try
            {
                read = await StreamStall.ReadAsync(source, buffer, _inactivityLimit, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (StreamStalledException ex)
            {
                // A transient failure like any other lost connection: the caller's retry takes the download again.
                throw new HttpRequestException($"{url.Host} stopped sending data. {ex.Message}", ex);
            }

            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new DownloadVerificationException($"{url.Host} sent more than the {maximumBytes / (1024 * 1024)} MB allowed; the download was stopped.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri url, long maximumBytes, CancellationToken cancellationToken)
    {
        if (!AllowedHosts.IsAllowedDownloadStart(url))
        {
            throw new HttpRequestException($"Refusing to download from {url.Host}{url.AbsolutePath}: it is not an address on the approved list.");
        }

        var current = url;
        for (var hop = 0; hop <= MaximumRedirects; hop++)
        {
            if (!AllowedHosts.IsAllowedDownload(current))
            {
                throw new HttpRequestException($"Refusing to download from {current.Host}: it is not on the approved list.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new HttpRequestException($"{current.Host} sent a redirect without an address.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException($"{current.Host} answered {status}.");
            }

            if (response.Content.Headers.ContentLength is { } announced && announced > maximumBytes)
            {
                response.Dispose();
                throw new DownloadVerificationException($"{current.Host} announced {announced / (1024 * 1024)} MB, more than the {maximumBytes / (1024 * 1024)} MB allowed; nothing was downloaded.");
            }

            return response;
        }

        throw new HttpRequestException("Too many redirects.");
    }
}
