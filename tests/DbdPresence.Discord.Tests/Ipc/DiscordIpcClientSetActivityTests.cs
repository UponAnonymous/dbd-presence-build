// Covers: FR-RPC-04 (SET_ACTIVITY maps only validated fields, timestamps in ms), FR-RPC-09/FR-RPC-10 (buttons, activity type), FR-RPC-01 (nonce, clear, pid), FR-RPC-08 (error responses).
using System.Text.Json;
using DbdPresence.Core.Presence;
using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Ipc;

public sealed class DiscordIpcClientSetActivityTests
{
    private const int Pid = 4242;

    private static async Task<(ClientFixture Fixture, ReceivedFrame Sent, SetActivityResult Result)> SendAsync(ValidatedActivity? activity)
    {
        var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        var result = await fixture.Client.SetActivityAsync(activity, Pid, CancellationToken.None).Guard();
        var sent = Assert.Single(fixture.Server.Latest.SetActivityFrames);
        return (fixture, sent, result);
    }

    [Fact]
    public async Task SetActivity_EveryFieldIsMappedExactly()
    {
        var (fixture, sent, result) = await SendAsync(TestActivities.Full());
        await using var owner = fixture;

        Assert.Equal(SetActivityOutcome.Success, result.Outcome);
        var expected = """
            {"cmd":"SET_ACTIVITY","args":{"pid":4242,"activity":{"type":5,"details":"In a trial","state":"3 generators left","timestamps":{"start":1792756800000},"assets":{"large_image":"map_haddonfield","large_text":"Haddonfield","small_image":"https://example.com/role.png","small_text":"Survivor"},"buttons":[{"label":"Watch live","url":"https://twitch.tv/example"},{"label":"Discord","url":"https://discord.gg/example"}],"status_display_type":2,"name":"Dead by Daylight"}},"nonce":"NONCE"}
            """;
        Assert.Equal(expected, sent.Text.Replace(sent.Nonce!, "NONCE", StringComparison.Ordinal));
        Assert.Empty(fixture.Server.Violations);
    }

    [Fact]
    public async Task SetActivity_TimestampIsUnixMilliseconds()
    {
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, 123, TimeSpan.Zero);
        var (fixture, sent, _) = await SendAsync(TestActivities.From(new ActivityDraft { Details = "Timer", StartTimestamp = start }));
        await using var owner = fixture;

        var milliseconds = sent.Activity.GetProperty("timestamps").GetProperty("start").GetInt64();

        Assert.Equal(start.ToUnixTimeMilliseconds(), milliseconds);
        Assert.Equal(1792756800123, milliseconds);
    }

    [Fact]
    public async Task SetActivity_NullAndEmptyFieldsAreOmitted()
    {
        var (fixture, sent, _) = await SendAsync(TestActivities.Simple("Only details"));
        await using var owner = fixture;

        Assert.Equal("""{"type":0,"details":"Only details"}""", sent.Activity.GetRawText());
    }

    [Fact]
    public async Task SetActivity_AssetsObjectContainsOnlyPresentImages()
    {
        var (fixture, sent, _) = await SendAsync(TestActivities.From(new ActivityDraft { Details = "Pic", LargeImage = "big", LargeText = "Big picture" }));
        await using var owner = fixture;

        Assert.Equal("""{"large_image":"big","large_text":"Big picture"}""", sent.Activity.GetProperty("assets").GetRawText());
    }

    [Fact]
    public async Task SetActivity_WhenNoImageResolved_PayloadHasNoAssetsAtAll()
    {
        // D16: with no bundled art, an unresolved slot is sent as no image. Image hover text without an image is dropped by the validator.
        var (fixture, sent, _) = await SendAsync(TestActivities.From(new ActivityDraft
        {
            Details = "Text only",
            State = "In the lobby",
            LargeImage = null,
            LargeText = "Hover text with no image",
            SmallImage = null,
            SmallText = "Small hover text",
        }));
        await using var owner = fixture;

        Assert.False(sent.Activity.TryGetProperty("assets", out _));
        Assert.DoesNotContain("large_image", sent.Activity.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("small_image", sent.Activity.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("""{"type":0,"details":"Text only","state":"In the lobby"}""", sent.Activity.GetRawText());
    }

    [Fact]
    public async Task SetActivity_DisplayNameIsSentAsName_AndOnlyWhenSet()
    {
        var (named, namedSent, _) = await SendAsync(TestActivities.From(new ActivityDraft { Details = "Named", NameOverride = "  Fog   Watcher " }));
        await using var namedOwner = named;
        var (plain, plainSent, _) = await SendAsync(TestActivities.Simple("Plain"));
        await using var plainOwner = plain;

        Assert.Equal("Fog Watcher", namedSent.Activity.GetProperty("name").GetString());
        Assert.False(plainSent.Activity.TryGetProperty("name", out _));
    }

    [Theory]
    [InlineData(StatusDisplay.Name, 0)]
    [InlineData(StatusDisplay.State, 1)]
    [InlineData(StatusDisplay.Details, 2)]
    public async Task SetActivity_StatusDisplayTypeFlowsThroughAlongsideTheName(StatusDisplay display, int expected)
    {
        var (fixture, sent, _) = await SendAsync(TestActivities.From(new ActivityDraft { Details = "Status", NameOverride = "DBD", StatusDisplay = display }));
        await using var owner = fixture;

        Assert.Equal(expected, sent.Activity.GetProperty("status_display_type").GetInt32());
        Assert.Equal("DBD", sent.Activity.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SetActivity_LongestNameAndLabelledHoverText_StayFarUnderTheFrameCap()
    {
        var label = "Unofficial · © Behaviour Interactive";
        var draft = new ActivityDraft
        {
            Details = new string('d', 128),
            State = new string('s', 128),
            NameOverride = new string('n', 128),
            LargeImage = "https://cdn.example.com/" + new string('a', 200),
            LargeText = new string('x', 86) + " · " + label, // 86 + 4 + 38 = 128 UTF-8 bytes
            SmallImage = "https://cdn.example.com/" + new string('b', 200),
            SmallText = new string('y', 128),
            Buttons = [new ButtonDraft("Watch live", "https://example.com/" + new string('u', 400)), new ButtonDraft("Discord", "https://example.com/" + new string('v', 400))],
            StatusDisplay = StatusDisplay.Name,
        };
        var (fixture, sent, _) = await SendAsync(TestActivities.From(draft));
        await using var owner = fixture;

        Assert.True(sent.Payload.Length < IpcFrameCodec.MaxPayloadBytes / 4);
        Assert.EndsWith(label, sent.Activity.GetProperty("assets").GetProperty("large_text").GetString(), StringComparison.Ordinal);
        Assert.Equal(128, sent.Activity.GetProperty("name").GetString()!.Length);
    }

    [Theory]
    [InlineData(ActivityKind.Playing, 0)]
    [InlineData(ActivityKind.Listening, 2)]
    [InlineData(ActivityKind.Watching, 3)]
    [InlineData(ActivityKind.Competing, 5)]
    public async Task SetActivity_TypeIsTheNumericActivityKind(ActivityKind kind, int expected)
    {
        var (fixture, sent, _) = await SendAsync(TestActivities.From(new ActivityDraft { Kind = kind, Details = "Kind" }));
        await using var owner = fixture;

        Assert.Equal(expected, sent.Activity.GetProperty("type").GetInt32());
    }

    [Fact]
    public async Task SetActivity_NonAsciiTextSurvivesAsValidJson()
    {
        var (fixture, sent, _) = await SendAsync(TestActivities.Simple("Café 🔪 <fog> & \"quotes\""));
        await using var owner = fixture;

        Assert.Equal("Café 🔪 <fog> & \"quotes\"", sent.Details);
    }

    [Fact]
    public async Task SetActivity_NullClearsThePresenceAndStillCarriesThePid()
    {
        var (fixture, sent, result) = await SendAsync(null);
        await using var owner = fixture;

        Assert.Equal(SetActivityOutcome.Success, result.Outcome);
        Assert.True(sent.IsClear);
        Assert.Equal(Pid, JsonDocument.Parse(sent.Payload).RootElement.GetProperty("args").GetProperty("pid").GetInt32());
        Assert.Contains("\"activity\":null", sent.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetActivity_EachRequestGetsItsOwnNonce_AndIsSentAsOneFrame()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        await fixture.Client.SetActivityAsync(TestActivities.Simple("One"), Pid, CancellationToken.None).Guard();
        await fixture.Client.SetActivityAsync(TestActivities.Simple("Two"), Pid, CancellationToken.None).Guard();

        var nonces = fixture.Server.Latest.SetActivityFrames.Select(f => f.Nonce).ToArray();
        Assert.Equal(2, nonces.Distinct(StringComparer.Ordinal).Count());
        Assert.All(nonces, nonce => Assert.True(Guid.TryParse(nonce, out _)));
        Assert.Empty(fixture.Server.Violations);
    }

    [Fact]
    public async Task SetActivity_ResponseWithAnotherNonceDoesNotCompleteTheRequest()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = (connection, frame) =>
        {
            connection.SendActivityAccepted("11111111-1111-1111-1111-111111111111");
        };

        var pending = fixture.Client.SetActivityAsync(TestActivities.Simple("One"), Pid, CancellationToken.None).AsTask();
        fixture.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(pending.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(SetActivityOutcome.Timeout, (await pending.Guard()).Outcome);
    }

    [Fact]
    public async Task SetActivity_DiscordErrorIsReturnedWithCodeAndMessage_AndTheConnectionStaysUsable()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Error(4000, "Invalid activity");

        var rejected = await fixture.Client.SetActivityAsync(TestActivities.Simple("Bad"), Pid, CancellationToken.None).Guard();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;
        var accepted = await fixture.Client.SetActivityAsync(TestActivities.Simple("Good"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.DiscordError, rejected.Outcome);
        Assert.Equal(4000, rejected.Code);
        Assert.Equal("Invalid activity", rejected.Message);
        Assert.Equal(SetActivityOutcome.Success, accepted.Outcome);
    }

    [Fact]
    public async Task SetActivity_ErrorWithoutNonceIsAttributedToTheRequestInFlight()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = (connection, _) => connection.SendError(null, 4002, "Invalid payload");

        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Bad"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.DiscordError, result.Outcome);
        Assert.Equal(4002, result.Code);
    }

    [Fact]
    public async Task SetActivity_BeforeTheHandshakeReportsDisconnected()
    {
        await using var fixture = new ClientFixture();

        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Early"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.Disconnected, result.Outcome);
        Assert.Empty(fixture.Server.Connections);
    }

    [Fact]
    public async Task SetActivity_CancellationStopsWaitingButKeepsTheConnection()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Ignore;
        using var cancel = new CancellationTokenSource();

        var pending = fixture.Client.SetActivityAsync(TestActivities.Simple("Slow"), Pid, cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Guard());
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;
        var next = await fixture.Client.SetActivityAsync(TestActivities.Simple("Fast"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.Success, next.Outcome);
        Assert.False(fixture.Client.Disconnected.IsCompleted);
    }
}
