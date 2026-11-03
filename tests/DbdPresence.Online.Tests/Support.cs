using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Account;
using DbdPresence.Online.Api;
using DbdPresence.Online.Config;
using DbdPresence.Online.Http;
using DbdPresence.Online.SignIn;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

/// <summary>Signs licence and config documents like the service does, with a throw-away key.</summary>
internal sealed class TestIssuer : IDisposable
{
    public const string Kid = "licence-test";
    private static readonly string[] s_activeFeatures = ["online"];

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TrustedKeySet Trusted => new(new Dictionary<string, TrustedKey>
    {
        [Kid] = new TrustedKey(_key.ExportSubjectPublicKeyInfo(), [SignedPurposes.Licence, SignedPurposes.Config]),
    });

    /// <summary>The config/licence key as a trusted key, for a verifier that also trusts other keys.</summary>
    public TrustedKey AsTrustedKey() => new(_key.ExportSubjectPublicKeyInfo(), [SignedPurposes.Licence, SignedPurposes.Config]);

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public string Licence(DateTimeOffset now, string sub, string cnf, string status = "active", TimeSpan? lifetime = null, string purpose = SignedPurposes.Licence, bool withOtherKey = false, DateTimeOffset? serverTime = null)
    {
        var expires = now + (lifetime ?? TimeSpan.FromDays(7));
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["sub"] = sub,
            ["cnf"] = cnf,
            ["status"] = status,
            ["features"] = status == "active" ? s_activeFeatures : Array.Empty<string>(),
            ["iat"] = Iso(now),
            ["nbf"] = Iso(now),
            ["exp"] = Iso(expires),
            ["serverTime"] = Iso(serverTime ?? now),
        });
        return Sign(purpose, now.ToUnixTimeSeconds(), now, expires, payload, withOtherKey);
    }

    public string Config(DateTimeOffset now, string? payloadOverride = null, long? version = null, TimeSpan? lifetime = null, bool disableCapture = false, bool emergency = false, string purpose = SignedPurposes.Config, string accessMode = "invite-only", bool withOtherKey = false)
    {
        var payload = payloadOverride ?? $$"""
            {"schemaVersion":1,"accessMode":"{{accessMode}}","applications":[{"key":"dbd","applicationId":"1563291325107937340","name":"Dead by Daylight","description":"Unofficial"},{"key":"alt","applicationId":"1563291325107937341","name":"Fog Presence","description":"Unofficial"}],"defaultApplication":"alt","disable":{"capture":{{(disableCapture ? "true" : "false")}},"detection":false},"emergencyStop":{{(emergency ? "true" : "false")}},"minimumVersion":"0.1.0","banner":null}
            """;
        return Sign(purpose, version ?? now.ToUnixTimeSeconds(), now, now + (lifetime ?? TimeSpan.FromDays(14)), payload, withOtherKey);
    }

    public void Dispose()
    {
        _key.Dispose();
        _other.Dispose();
    }

    private string Sign(string purpose, long version, DateTimeOffset issued, DateTimeOffset expires, string payloadJson, bool withOtherKey)
    {
        const string kid = Kid; // the "other key" case claims the trusted kid but signs with a different key
        var envelope = $$"""{"kid":"{{kid}}","purpose":"{{purpose}}","channel":"stable","version":{{version}},"issuedAt":"{{Iso(issued)}}","expiresAt":"{{Iso(expires)}}","payload":{{payloadJson}}}""";
        var bytes = Encoding.UTF8.GetBytes(envelope);
        var signature = (withOtherKey ? _other : _key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $$"""{"kid":"{{kid}}","signed":"{{Base64Url.EncodeToString(bytes)}}","sig":"{{Base64Url.EncodeToString(signature)}}"}""";
    }
}

/// <summary>A fake clock that can also be set backwards (a user turning the system clock back).</summary>
internal sealed class RewindableTime(DateTimeOffset start) : FakeTimeProvider(start)
{
    private TimeSpan _skew;

    public void Rewind(TimeSpan by) => _skew -= by;

    public void ResetSkew() => _skew = TimeSpan.Zero;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _skew;
}

/// <summary>A software P-256 device key.</summary>
internal sealed class FakeDeviceKeys : IDeviceKeyStore, IDisposable
{
    private ECDsa? _key;

    public int Deleted { get; private set; }

    public DevicePublicKey Public => GetOrCreatePublicKey();

    public DevicePublicKey GetOrCreatePublicKey()
    {
        _key ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = _key.ExportParameters(false).Q;
        return new DevicePublicKey(q.X!, q.Y!);
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        _key ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool Verify(byte[] data, byte[] signature) =>
        _key!.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Delete()
    {
        Deleted++;
        _key?.Dispose();
        _key = null;
    }

    public void Dispose() => _key?.Dispose();
}

internal sealed class FakeSecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public string? Read(string name) => Values.TryGetValue(name, out var v) ? v : null;

    public void Write(string name, string value) => Values[name] = value;

    public void Delete(string name) => Values.Remove(name);
}

internal sealed class TestLog : IOnlineLog
{
    public List<string> Lines { get; } = [];

    public void Warning(string message) => Lines.Add(message);
}

internal sealed class MemoryFiles : IPackFileStore
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public bool Exists(string path) => Files.ContainsKey(path);

    public long Length(string path) => Files[path].Length;

    public byte[] ReadAllBytes(string path) => Files.TryGetValue(path, out var b) ? b : throw new FileNotFoundException(path);

    public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes) => Files[path] = bytes.ToArray();

    public void Move(string source, string destination)
    {
        Files[destination] = Files[source];
        Files.Remove(source);
    }

    public void Delete(string path) => Files.Remove(path);

    public void CreateDirectory(string path)
    {
    }

    public IReadOnlyList<string> ListFiles(string directory) => [.. Files.Keys.Where(k => k.StartsWith(directory, StringComparison.Ordinal))];
}

internal sealed class FakeBrowser : IBrowserLauncher
{
    public string? LastUrl { get; private set; }

    public bool Succeeds { get; set; } = true;

    public string Query(string name) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(LastUrl!).Query)[name] ?? throw new InvalidOperationException(name);

    public bool TryOpen(string url)
    {
        LastUrl = url;
        return Succeeds;
    }
}

internal sealed class FakeListeners(FakeBrowser browser) : ICallbackListenerFactory
{
    /// <summary>What the browser "returns": null means a correct code with the real state.</summary>
    public Func<string, CallbackResult>? Reply { get; set; }

    public int Started { get; private set; }

    public ICallbackListener Start()
    {
        Started++;
        return new Listener(browser, Reply);
    }

    private sealed class Listener(FakeBrowser browser, Func<string, CallbackResult>? reply) : ICallbackListener
    {
        public int Port => 51234;

        public Task<CallbackResult> WaitAsync(string expectedState, CancellationToken cancellationToken)
        {
            var state = browser.Query("state");
            return Task.FromResult(reply is null ? new CallbackResult(CallbackKind.Code, "auth-code-1", state) : reply(state));
        }

        public void Dispose()
        {
        }
    }
}

internal sealed record Recorded(HttpMethod Method, string Path, string Host, string Body, string? Authorization);

/// <summary>A stand-in for the service: records every request and answers with whatever the test sets.</summary>
internal sealed class FakeService : HttpMessageHandler
{
    public List<Recorded> Requests { get; } = [];

    public Func<Recorded, HttpResponseMessage> Handler { get; set; } = _ => Json(404, """{"schemaVersion":1,"error":"not_found"}""");

    public Exception? Throw { get; set; }

    public static HttpResponseMessage Json(int status, string body, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        }

        return response;
    }

    public IEnumerable<Recorded> To(string path) => Requests.Where(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new Recorded(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Host, body, request.Headers.Authorization?.ToString());
        Requests.Add(recorded);
        if (Throw is { } failure)
        {
            throw failure;
        }

        return Handler(recorded);
    }
}

/// <summary>An account manager wired to fakes, with a fake clock.</summary>
internal sealed class AccountHarness : IDisposable
{
    public const string UserId = "user-1";
    public const string Host = "api-staging.arcavale.com";

    private int _counter;

    public AccountHarness()
    {
        Time = new RewindableTime(new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.Zero));
        Browser = new FakeBrowser();
        Listeners = new FakeListeners(Browser);
        var handler = new AllowListHandler(new HashSet<string>([Host], StringComparer.OrdinalIgnoreCase), Service);
        Http = ServiceHttp.CreateClient(handler, new Uri($"https://{Host}/"));
        Api = new ServiceClient(Http, Time, "cdn.arcavale.com");
        Versions = new InMemoryVersionStore();
        Verifier = new SignedDocumentVerifier(Issuer.Trusted, Versions, Time);
        Config = new RemoteConfigService(Api, Verifier, Files, "online/config.json", Time, Log);
        Account = Build();
        Service.Handler = DefaultHandler;
    }

    public RewindableTime Time { get; }

    public TestIssuer Issuer { get; } = new();

    public FakeDeviceKeys Keys { get; } = new();

    public FakeSecrets Secrets { get; } = new();

    public TestLog Log { get; } = new();

    public MemoryFiles Files { get; } = new();

    public FakeBrowser Browser { get; }

    public FakeListeners Listeners { get; }

    public FakeService Service { get; } = new();

    public HttpClient Http { get; }

    public ServiceClient Api { get; }

    public InMemoryVersionStore Versions { get; }

    public SignedDocumentVerifier Verifier { get; }

    public RemoteConfigService Config { get; }

    public AccountManager Account { get; private set; }

    /// <summary>What the next sign-in or refresh answers: "granted" or "invite_required".</summary>
    public string Access { get; set; } = "granted";

    /// <summary>A licence document the service hands out instead of a correct one (for refusal tests).</summary>
    public string? LicenceOverride { get; set; }

    public string Thumbprint => Keys.Public.Thumbprint;

    public string CurrentRefreshToken { get; private set; } = string.Empty;

    public AccountManager Build() => new(new AccountDependencies(Api, Keys, Secrets, Verifier, Config, Browser, Listeners, Time, Log, "1563291325107937340"));

    /// <summary>A new manager over the same stored secrets (an app restart).</summary>
    public AccountManager Restart()
    {
        Account.Dispose();
        Account = Build();
        return Account;
    }

    public string LicenceNow(string status = "active", string? sub = null, string? cnf = null) =>
        Issuer.Licence(Time.GetUtcNow(), sub ?? UserId, cnf ?? Thumbprint, status);

    public string GrantJson(bool withUser = true)
    {
        _counter++;
        CurrentRefreshToken = $"rt_token{_counter:D4}";
        var now = Time.GetUtcNow();
        var licence = Access == "granted" ? JsonSerializer.Serialize(LicenceOverride ?? LicenceNow()) : "null";
        var user = withUser ? $$""","user":{"id":"{{UserId}}","displayName":"Meg Thomas"},"device":{"id":"dev-1","thumbprint":"{{Thumbprint}}"}""" : string.Empty;
        return $$"""{"schemaVersion":1,"accessToken":"at_token{{_counter:D4}}","accessTokenExpiresAt":"{{TestIssuer.Iso(now.AddHours(1))}}","refreshToken":"{{CurrentRefreshToken}}","refreshTokenExpiresAt":"{{TestIssuer.Iso(now.AddDays(90))}}","access":"{{Access}}","licence":{{licence}}{{user}}}""";
    }

    public HttpResponseMessage DefaultHandler(Recorded request) => request.Path switch
    {
        "/v1/auth/discord" => FakeService.Json(200, GrantJson()),
        "/v1/auth/refresh" => FakeService.Json(200, GrantJson(withUser: false)),
        "/v1/auth/logout" => FakeService.Json(200, """{"schemaVersion":1,"ok":true}"""),
        "/v1/me" => FakeService.Json(200, """{"schemaVersion":1,"deleted":true}"""),
        "/v1/invites/redeem" => FakeService.Json(200, $$"""{"schemaVersion":1,"access":"granted","alreadyHadAccess":false,"licence":{{JsonSerializer.Serialize(LicenceNow())}}}"""),
        "/v1/config" => FakeService.Json(200, $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(Issuer.Config(Time.GetUtcNow()))}}}"""),
        _ => FakeService.Json(404, """{"schemaVersion":1,"error":"not_found"}"""),
    };

    public void Dispose()
    {
        Account.Dispose();
        Http.Dispose();
        Issuer.Dispose();
        Keys.Dispose();
    }
}
