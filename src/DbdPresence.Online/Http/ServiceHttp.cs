using System.Net;
using System.Net.Sockets;

namespace DbdPresence.Online.Http;

/// <summary>A request was refused before it left the PC: not https, not the allow-listed host, or carrying credentials in the URL (ADR-0016).</summary>
public sealed class HostNotAllowedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="HostNotAllowedException"/> class.</summary>
    public HostNotAllowedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HostNotAllowedException"/> class.</summary>
    /// <param name="message">Why the request was refused.</param>
    public HostNotAllowedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HostNotAllowedException"/> class.</summary>
    /// <param name="message">Why the request was refused.</param>
    /// <param name="innerException">The underlying failure.</param>
    public HostNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The service answered with a redirect. Redirects are never followed (ADR-0016).</summary>
public sealed class RedirectRefusedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="RedirectRefusedException"/> class.</summary>
    public RedirectRefusedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RedirectRefusedException"/> class.</summary>
    /// <param name="message">What happened.</param>
    public RedirectRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RedirectRefusedException"/> class.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure.</param>
    public RedirectRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Refuses any request whose target is not https on the compiled-in host allow-list (ADR-0016). It sees the real target even when a
/// proxy is used, and it also turns a redirect answer into an error so a redirect can never lead anywhere else. The CDN host, when
/// there is one, is further restricted to GET of <c>/packs/&lt;64 lowercase hex&gt;.png|jpg</c> (FR-ONL-07) and of
/// <c>/releases/&lt;64 lowercase hex&gt;.nupkg</c> (signed update packages, FR-ONL-10) without a query.
/// </summary>
public sealed class AllowListHandler : DelegatingHandler
{
    private const string PackFilePrefix = "/packs/";
    private const string ReleasePackagePrefix = "/releases/";
    private const string ReleasePackageExtension = ".nupkg";
    private const int HashLength = 64;
    private readonly IReadOnlySet<string> _hosts;
    private readonly string? _cdnHost;

    /// <summary>Initializes a new instance of the <see cref="AllowListHandler"/> class.</summary>
    /// <param name="hosts">The allowed host names (the CDN host too, when there is one).</param>
    /// <param name="inner">The handler that sends allowed requests.</param>
    /// <param name="cdnHost">The one host that is only for pack file downloads, or null when none is used.</param>
    public AllowListHandler(IReadOnlySet<string> hosts, HttpMessageHandler inner, string? cdnHost = null)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        _hosts = hosts;
        _cdnHost = cdnHost;
    }

    /// <summary>Checks that a CDN target is a pack file path: <c>/packs/&lt;sha256&gt;.png</c> or <c>.jpg</c>, lowercase hex, no query or fragment.</summary>
    /// <param name="uri">The target.</param>
    /// <returns>True for a pack file URL.</returns>
    public static bool IsPackFilePath(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (!path.StartsWith(PackFilePrefix, StringComparison.Ordinal) || path.Length != PackFilePrefix.Length + HashLength + 4)
        {
            return false;
        }

        var name = path.AsSpan(PackFilePrefix.Length);
        var extension = name[HashLength..];
        if (!extension.SequenceEqual(".png") && !extension.SequenceEqual(".jpg"))
        {
            return false;
        }

        foreach (var c in name[..HashLength])
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks that a CDN target is an update package path: <c>/releases/&lt;sha256&gt;.nupkg</c>, lowercase hex, no query or fragment.</summary>
    /// <param name="uri">The target.</param>
    /// <returns>True for an update package URL.</returns>
    public static bool IsReleasePackagePath(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (!path.StartsWith(ReleasePackagePrefix, StringComparison.Ordinal)
            || path.Length != ReleasePackagePrefix.Length + HashLength + ReleasePackageExtension.Length
            || !path.EndsWith(ReleasePackageExtension, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in path.AsSpan(ReleasePackagePrefix.Length, HashLength))
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks a target against an allow-list.</summary>
    /// <param name="uri">The target.</param>
    /// <param name="hosts">The allowed hosts.</param>
    /// <returns>True when the target is https, on the default port, without credentials, on an allowed host.</returns>
    public static bool IsAllowed(Uri? uri, IReadOnlySet<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        return uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } && hosts.Contains(uri.IdnHost);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAllowed(request.RequestUri, _hosts))
        {
            throw new HostNotAllowedException("The request target is not on the host allow-list.");
        }

        if (_cdnHost is not null
            && string.Equals(request.RequestUri!.IdnHost, _cdnHost, StringComparison.OrdinalIgnoreCase)
            && (request.Method != HttpMethod.Get || !(IsPackFilePath(request.RequestUri) || IsReleasePackagePath(request.RequestUri))))
        {
            throw new HostNotAllowedException("The CDN host is only for downloading pack files and update packages.");
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new RedirectRefusedException("The service answered with a redirect, which is never followed.");
        }

        return response;
    }
}

/// <summary>Builds the one HTTP client the app uses for the service (ADR-0016).</summary>
public static class ServiceHttp
{
    /// <summary>Largest response body read, in bytes.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates the production handler chain: allow-list over a hardened <see cref="SocketsHttpHandler"/>.</summary>
    /// <param name="host">The allowed API host.</param>
    /// <param name="proxy">The proxy to honour; the system default when null.</param>
    /// <param name="cdnHost">The CDN host for pack file downloads, or null when the client does not download files.</param>
    /// <returns>The handler. The caller disposes the client that wraps it.</returns>
    public static HttpMessageHandler CreateHandler(string host, IWebProxy? proxy = null, string? cdnHost = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var hosts = cdnHost is null ? [host] : new[] { host, cdnHost };
        return new AllowListHandler(new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase), CreateInnerHandler(host, proxy, cdnHost), cdnHost);
    }

    /// <summary>The hardened socket handler under the allow-list: no redirects, no cookies, timeouts, the system proxy, and the connect check when no proxy is used.</summary>
    /// <param name="host">The allowed API host.</param>
    /// <param name="proxy">The proxy to honour; the system default when null.</param>
    /// <param name="cdnHost">The CDN host, or null.</param>
    /// <returns>The handler.</returns>
    internal static SocketsHttpHandler CreateInnerHandler(string host, IWebProxy? proxy = null, string? cdnHost = null)
    {
        var hosts = new HashSet<string>(cdnHost is null ? [host] : [host, cdnHost], StringComparer.OrdinalIgnoreCase);
        var effectiveProxy = proxy ?? HttpClient.DefaultProxy;
        var inner = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            Proxy = effectiveProxy,
            ConnectTimeout = s_connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 32,
            MaxConnectionsPerServer = 4,
        };
        if (!hosts.Any(h => UsesProxyFor(effectiveProxy, h)))
        {
            inner.ConnectCallback = (context, cancellationToken) => ConnectAsync(context.DnsEndPoint, hosts, cancellationToken);
        }

        return inner;
    }

    /// <summary>Creates a client over <paramref name="handler"/> with a base address and no built-in timeout (callers pass cancellation).</summary>
    /// <param name="handler">The handler chain.</param>
    /// <param name="baseAddress">The API base address.</param>
    /// <returns>The client.</returns>
    public static HttpClient CreateClient(HttpMessageHandler handler, Uri baseAddress) =>
        new(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

    /// <summary>Whether requests to <paramref name="host"/> would go through <paramref name="proxy"/>.</summary>
    /// <param name="proxy">The proxy.</param>
    /// <param name="host">The API host.</param>
    /// <returns>True when a proxy applies.</returns>
    public static bool UsesProxyFor(IWebProxy proxy, string host)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        var target = new Uri($"https://{host}/");
        return !proxy.IsBypassed(target) && proxy.GetProxy(target) is { } via && via != target;
    }

    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, HashSet<string> hosts, CancellationToken cancellationToken)
    {
        if (endpoint is not { Port: 443 } || !hosts.Contains(endpoint.Host))
        {
            throw new HostNotAllowedException("The connection target is not on the host allow-list.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
