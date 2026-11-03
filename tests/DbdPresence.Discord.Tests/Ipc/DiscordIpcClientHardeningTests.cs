// Covers: FR-RPC-01 (ack timeout also bounds a stalled write, pipe notice), FR-RPC-08 (nonce attribution, sanitised and quoted Discord text; fixed failure descriptions per security review S-L1).
using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Discord.Tests.Ipc;

public sealed class DiscordIpcClientHardeningTests
{
    private const int Pid = 4242;

    [Fact]
    public async Task StalledActivityWrite_IsBoundedByTheAckTimeout()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.StallWrites = true;

        var pending = fixture.Client.SetActivityAsync(TestActivities.Simple("Stuck"), Pid, CancellationToken.None).AsTask();
        fixture.Time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(pending.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await pending.Guard();

        Assert.Equal(SetActivityOutcome.Timeout, result.Outcome);
        Assert.True(fixture.Server.Latest.ClosedByClient);
    }

    [Fact]
    public async Task StalledHandshakeWrite_IsBoundedByTheAckTimeout()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.StallWrites = true;

        var pending = fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).AsTask();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        var result = await pending.Guard();

        Assert.False(result.Success);
        Assert.Contains("did not answer", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorWithAnUnknownNonce_IsNotAttributedToTheRequestInFlight()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = (connection, _) => connection.SendError("22222222-2222-2222-2222-222222222222", 4001, "Someone else's");

        var pending = fixture.Client.SetActivityAsync(TestActivities.Simple("Mine"), Pid, CancellationToken.None).AsTask();
        var nonce = fixture.Server.Latest.SetActivityFrames[0].Nonce;
        fixture.Server.Latest.SendActivityAccepted(nonce);
        var result = await pending.Guard();

        Assert.Equal(SetActivityOutcome.Success, result.Outcome);
        Assert.False(fixture.Client.Disconnected.IsCompleted);
    }

    [Fact]
    public async Task ErrorWithAnUnknownNonce_LeavesTheRequestWaitingUntilItsOwnTimeout()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = (connection, _) => connection.SendError("22222222-2222-2222-2222-222222222222", 4001, "Someone else's");

        var pending = fixture.Client.SetActivityAsync(TestActivities.Simple("Mine"), Pid, CancellationToken.None).AsTask();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(SetActivityOutcome.Timeout, (await pending.Guard()).Outcome);
    }

    [Fact]
    public async Task DiscordErrorText_IsSanitisedBeforeItIsReturned()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = (connection, frame) => connection.SendError(frame.Nonce, 4000, "evil\\n\\u202Etext");

        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Bad"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.DiscordError, result.Outcome);
        Assert.Equal("evil text", result.Message);
    }

    [Fact]
    public async Task HandshakeCloseText_IsSanitisedAndQuoted()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.OnHandshake = (connection, _) => connection.Send(
            IpcOpcode.Close, """{"code":4000,"message":"Invalid\nClient\u202E ID"}""");

        var result = await fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();

        Assert.False(result.Success);
        Assert.Equal("Discord rejected the connection (code 4000). Discord said: \"Invalid Client ID\"", result.Message);
    }

    [Fact]
    public async Task ActivityWriteFailure_DoesNotEchoTheExceptionMessage()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.FailWrites = true;

        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Lost"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.Disconnected, result.Outcome);
        Assert.Equal("Sending the activity failed (IOException).", result.Message);
        Assert.DoesNotContain("private-name", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandshakeWriteFailure_DoesNotEchoTheExceptionMessage()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.FailWrites = true;

        var result = await fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();

        Assert.False(result.Success);
        Assert.Equal("Sending the handshake failed (IOException).", result.Message);
    }

    [Fact]
    public async Task UnreachableDiscord_DoesNotEchoTheExceptionMessage()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.Accepting = false;

        var result = await fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();

        Assert.Equal("Discord is not reachable (IOException).", result.Message);
    }

    [Fact]
    public async Task MalformedJson_ReportsAFixedDescription()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.Send(IpcOpcode.Frame, "{ this is not json C:\\Users\\private-name");
        var info = await fixture.Client.Disconnected.Guard();

        Assert.Equal("Protocol error: Discord sent malformed JSON.", info.Message);
    }

    [Fact]
    public async Task SkippedPipeNotice_IsAppendedToTheUnreachableMessage()
    {
        var time = new FakeTimeProvider();
        await using var client = new DiscordIpcClient(new RefusingTransport(), time, TimeSpan.FromSeconds(5));

        var result = await client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();

        Assert.False(result.Success);
        Assert.Equal(
            "Discord is not reachable (IOException). A program that isn't running as you is using Discord's pipe name; skipped.",
            result.Message);
    }

    private sealed class RefusingTransport : IDiscordTransport
    {
        public string? Endpoint => null;

        public string? Notice => NamedPipeDiscordTransport.UntrustedPipeNotice;

        public ValueTask ConnectAsync(CancellationToken cancellationToken) =>
            throw new IOException("No Discord IPC pipe accepted a connection.");

        public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not connected.");

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not connected.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
