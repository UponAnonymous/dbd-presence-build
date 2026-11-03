// Covers: FR-ONL-01 (PKCE S256, loopback listener bound to 127.0.0.1, a junk or idle connection never ends it, 120 s timeout, state check, authorize URL),
// ADR-0011 sign-in, RFC 7636 appendix B vector.
using System.Net;
using System.Net.Sockets;
using System.Text;
using DbdPresence.Online.SignIn;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class SignInFlowTests
{
    private const string State = "st-1";

    [Fact]
    public void Challenge_MatchesTheRfc7636Vector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", PkceChallenge.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Create_GivesAFreshVerifierChallengeAndState()
    {
        var a = PkceChallenge.Create();
        var b = PkceChallenge.Create();

        Assert.Matches("^[A-Za-z0-9_-]{43}$", a.Verifier);
        Assert.Equal(PkceChallenge.ChallengeFor(a.Verifier), a.Challenge);
        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.NotEqual(a.State, b.State);
        Assert.True(a.State.Length >= 32);
    }

    [Fact]
    public void AuthorizeUrl_CarriesTheS256ChallengeScopeStateAndLoopbackRedirect()
    {
        var pkce = PkceChallenge.Create();
        var url = pkce.BuildAuthorizeUrl("1563291325107937340", PkceChallenge.RedirectUri(51234));
        var uri = new Uri(url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("discord.com", uri.Host);
        Assert.Equal("/oauth2/authorize", uri.AbsolutePath);
        Assert.Equal(pkce.Challenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal(pkce.State, query["state"]);
        Assert.Equal("1563291325107937340", query["client_id"]);
        Assert.Equal("http://127.0.0.1:51234/callback", query["redirect_uri"]);
        Assert.DoesNotContain(pkce.Verifier, url, StringComparison.Ordinal);
    }

    private static async Task<string> GetAsync(int port, string target, string? host = null)
    {
        using var client = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork); // a plain TcpClient is dual-mode on Windows and reports ::ffff:127.0.0.1
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        var request = $"GET {target} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Listener_AcceptsOneCallback_AnswersAClosePage_AndReturnsCodeAndState()
    {
        var factory = new LoopbackListenerFactory(new FakeTimeProvider());
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        var response = await GetAsync(listener.Port, "/callback?code=abc123&state=st%2D1");
        var result = await wait;

        Assert.Equal(CallbackKind.Code, result.Kind);
        Assert.Equal("abc123", result.Code);
        Assert.Equal("st-1", result.State);
        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);
        Assert.Contains("You can close this tab", response, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listener_IsBoundToTheLoopbackAddressOnly()
    {
        var factory = new LoopbackListenerFactory(new FakeTimeProvider());
        using var listener = factory.Start();

        Assert.InRange(listener.Port, 1, 65535);
        using var client = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork); // a plain TcpClient is dual-mode on Windows and reports ::ffff:127.0.0.1
        await client.ConnectAsync(IPAddress.Loopback, listener.Port, TestContext.Current.CancellationToken);
        Assert.True(client.Connected);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)client.Client.RemoteEndPoint!).Address.MapToIPv4());
    }

    [Theory]
    [InlineData("/other?code=a&state=st-1")]
    [InlineData("/callback")]
    [InlineData("/callback?code=a")]
    public async Task Listener_RejectsAnythingButTheCallbackWithCodeAndState_AndKeepsWaiting(string target)
    {
        var factory = new LoopbackListenerFactory(new FakeTimeProvider());
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        var response = await GetAsync(listener.Port, target);

        Assert.DoesNotContain("HTTP/1.1 200", response, StringComparison.Ordinal);
        Assert.Matches("^HTTP/1.1 (400|404)", response);
        Assert.False(wait.IsCompleted);
        await GetAsync(listener.Port, "/callback?code=real&state=st-1");
        var result = await wait;
        Assert.Equal(CallbackKind.Code, result.Kind);
        Assert.Equal("real", result.Code);
    }

    [Fact]
    public async Task Listener_RejectsAForeignHostHeader_AgainstDnsRebinding_AndKeepsWaiting()
    {
        var factory = new LoopbackListenerFactory(new FakeTimeProvider());
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        var response = await GetAsync(listener.Port, "/callback?code=a&state=st-1", host: "evil.example.com");

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.False(wait.IsCompleted);
        await GetAsync(listener.Port, "/callback?code=ok&state=st-1");
        Assert.Equal("ok", (await wait).Code);
    }

    [Fact]
    public async Task Listener_WrongStateFirst_IsAnswered400_AndDoesNotEndTheSignIn()
    {
        var factory = new LoopbackListenerFactory(new FakeTimeProvider());
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        var response = await GetAsync(listener.Port, "/callback?code=forged&state=other");

        Assert.StartsWith("HTTP/1.1 400", response, StringComparison.Ordinal);
        Assert.False(wait.IsCompleted);
        await GetAsync(listener.Port, "/callback?code=real&state=st-1");
        var result = await wait;
        Assert.Equal(CallbackKind.Code, result.Kind);
        Assert.Equal("real", result.Code);
    }

    [Fact]
    public async Task Listener_JunkConnectionFirst_DoesNotEndTheSignIn()
    {
        var factory = new LoopbackListenerFactory(new FakeTimeProvider());
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        using (var junk = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork))
        {
            await junk.ConnectAsync(IPAddress.Loopback, listener.Port, TestContext.Current.CancellationToken);
            await junk.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\x16\x03\x01 not http\r\n\r\n"), TestContext.Current.CancellationToken);
        }

        await GetAsync(listener.Port, "/callback?code=abc&state=st-1");
        var result = await wait;

        Assert.Equal(CallbackKind.Code, result.Kind);
        Assert.Equal("abc", result.Code);
    }

    [Fact]
    public async Task Listener_ASlowIdleConnection_DoesNotBlockTheRealCallback()
    {
        var time = new FakeTimeProvider();
        var factory = new LoopbackListenerFactory(time);
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        using var idle = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        await idle.ConnectAsync(IPAddress.Loopback, listener.Port, TestContext.Current.CancellationToken);

        var response = await GetAsync(listener.Port, "/callback?code=fast&state=st-1");
        var result = await wait;

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal);
        Assert.Equal("fast", result.Code);
    }

    [Fact]
    public async Task Listener_ReportsDiscordErrors_AsDenied()
    {
        var factory = new LoopbackListenerFactory(new FakeTimeProvider());
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        await GetAsync(listener.Port, "/callback?error=access_denied&state=st-1");

        Assert.Equal(CallbackKind.Denied, (await wait).Kind);
    }

    [Fact]
    public async Task Listener_TimesOutAfter120Seconds_OnTheInjectedClock()
    {
        var time = new FakeTimeProvider();
        var factory = new LoopbackListenerFactory(time);
        using var listener = factory.Start();
        var wait = listener.WaitAsync(State, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(119));
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));
        var result = await wait;

        Assert.Equal(CallbackKind.TimedOut, result.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), LoopbackListenerFactory.Timeout);
    }
}
