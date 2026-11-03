// Covers: D35 (only the owner's app reports unknown character names; friends' apps never send anything), the report payload (folded name, role,
// a random per-match id and the app version only), batching and the 30-minute pace, rate-limit backoff, nothing kept while signed out or with
// online features off, project rule 3 (only the compiled-in API host).
using System.Text.Json;
using DbdPresence.Core.Game;
using DbdPresence.Core.Online;
using DbdPresence.Core.Roster;
using DbdPresence.Online.Api;
using DbdPresence.Online.Roster;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class UnknownCharacterReporterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwnersApp_SendsFoldedNamesInBatchesOfFour_WithOneRandomIdPerMatch()
    {
        var w = new ReporterWorld(isReporter: true);
        w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, MatchKey: 1));
        w.Reporter.Report(new UnknownCharacterSighting("Nova Reyés", Role.Survivor, MatchKey: 1));
        w.Reporter.Report(new UnknownCharacterSighting("THE LANTERN", Role.Killer, MatchKey: 1)); // the same folded name in the same match
        for (var m = 2; m <= 4; m++)
        {
            w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, MatchKey: m));
        }

        Assert.Equal(5, w.Reporter.Pending);
        Assert.Empty(w.Client.Sent);
        await w.AdvanceAsync(UnknownCharacterReporter.FirstDelay);

        var batch = Assert.Single(w.Client.Sent);
        Assert.Equal(CharacterSightingReport.MaxPerReport, batch.Count);
        Assert.Equal(("the lantern", "killer"), (batch[0].FoldedName, batch[0].Role));
        Assert.Equal(("nova reyes", "survivor"), (batch[1].FoldedName, batch[1].Role));
        Assert.Equal(batch[0].MatchId, batch[1].MatchId);
        Assert.Equal(3, batch.Select(s => s.MatchId).Distinct().Count());
        Assert.All(batch, s => Assert.Matches("^[0-9a-f]{32}$", s.MatchId));
        Assert.DoesNotContain(batch, s => s.MatchId.TrimStart('0') is "1" or "2" or "3");
        Assert.Equal(1, w.Reporter.Pending);

        // The rest waits for the 30-minute pace.
        await w.AdvanceAsync(UnknownCharacterReporter.MinInterval - TimeSpan.FromSeconds(1));
        Assert.Single(w.Client.Sent);
        await w.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, w.Client.Sent.Count);
        Assert.Equal(0, w.Reporter.Pending);
        Assert.Equal(1, w.Client.Checks);
    }

    [Fact]
    public async Task AFriendsApp_NeverSends_AndDropsEveryName()
    {
        var w = new ReporterWorld(isReporter: false);
        w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, 1));

        await w.AdvanceAsync(UnknownCharacterReporter.FirstDelay);

        Assert.Equal(1, w.Client.Checks);
        Assert.Empty(w.Client.Sent);
        Assert.Equal(0, w.Reporter.Pending);
        w.Reporter.Report(new UnknownCharacterSighting("The Warden", Role.Killer, 2));
        Assert.Equal(0, w.Reporter.Pending);
        await w.AdvanceAsync(TimeSpan.FromHours(7));
        Assert.Empty(w.Client.Sent);
    }

    [Fact]
    public async Task TheRefreshSayingNotTheOwner_QueuesNothing_AndMakesNoCallOfItsOwn()
    {
        // Round-2 security review I1: the normal account refresh tells the flag, so a friend's app never holds a candidate name.
        var w = new ReporterWorld(isReporter: true);
        w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, 1));
        Assert.Equal(1, w.Reporter.Pending);

        w.Reporter.NoteReporter(false);
        Assert.Equal(0, w.Reporter.Pending);
        w.Reporter.Report(new UnknownCharacterSighting("The Warden", Role.Killer, 2));
        Assert.Equal(0, w.Reporter.Pending);
        await w.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(0, w.Client.Checks);
        Assert.Empty(w.Client.Sent);
    }

    [Fact]
    public async Task TheRefreshSayingOwner_SkipsTheSeparateCheck()
    {
        var w = new ReporterWorld(isReporter: false);
        w.Reporter.NoteReporter(true);
        w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, 1));
        await w.AdvanceAsync(UnknownCharacterReporter.FirstDelay);

        Assert.Equal(0, w.Client.Checks);
        Assert.Single(w.Client.Sent);
    }

    [Fact]
    public async Task TheServiceSayingNotTheOwner_StopsReporting()
    {
        var w = new ReporterWorld(isReporter: true);
        w.Client.SendAnswer = new RosterReportAnswer(RosterReportStatus.NotReporter);
        w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, 1));
        w.Reporter.Report(new UnknownCharacterSighting("The Warden", Role.Killer, 1));
        await w.AdvanceAsync(UnknownCharacterReporter.FirstDelay);

        Assert.Single(w.Client.Sent);
        Assert.Equal(0, w.Reporter.Pending);
        w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, 2));
        Assert.Equal(0, w.Reporter.Pending);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("x", 1)]
    [InlineData("東京", 1)]
    [InlineData("A name that is far too long to be a character name at all", 1)]
    [InlineData("The Lantern", 0)]
    public async Task UnusableNamesAndUnknownRoles_AreDropped(string name, int role)
    {
        var w = new ReporterWorld(isReporter: true);
        w.Reporter.Report(new UnknownCharacterSighting(name, role == 0 ? Role.None : Role.Killer, 1));

        Assert.Equal(0, w.Reporter.Pending);
        await w.AdvanceAsync(UnknownCharacterReporter.FirstDelay);
        Assert.Equal(0, w.Client.Checks);
    }

    [Fact]
    public async Task RateLimitedAndOffline_WaitAndKeepTheNames_WhileNotAllowedAndSignOutForgetThem()
    {
        var w = new ReporterWorld(isReporter: true);
        w.Client.SendAnswer = new RosterReportAnswer(RosterReportStatus.RateLimited, RetryAfter: TimeSpan.FromHours(2));
        w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, 1));
        await w.AdvanceAsync(UnknownCharacterReporter.FirstDelay);
        Assert.Single(w.Client.Sent);
        Assert.Equal(1, w.Reporter.Pending);

        w.Client.SendAnswer = new RosterReportAnswer(RosterReportStatus.Offline);
        await w.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Single(w.Client.Sent);
        await w.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(2, w.Client.Sent.Count);
        Assert.Equal(1, w.Reporter.Pending);

        w.Client.SendAnswer = new RosterReportAnswer(RosterReportStatus.NotAllowed);
        await w.AdvanceAsync(UnknownCharacterReporter.MinInterval);
        Assert.Equal(3, w.Client.Sent.Count);
        Assert.Equal(0, w.Reporter.Pending);

        w.Reporter.Report(new UnknownCharacterSighting("The Warden", Role.Killer, 2));
        Assert.Equal(1, w.Reporter.Pending);
        w.Account.SignOut();
        Assert.Equal(0, w.Reporter.Pending);
    }

    [Fact]
    public void AtMost32NamesWait()
    {
        var w = new ReporterWorld(isReporter: true);
        for (var i = 0; i < 40; i++)
        {
            w.Reporter.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, i));
        }

        Assert.Equal(UnknownCharacterReporter.MaxQueued, w.Reporter.Pending);
        Assert.Contains(w.Log.Lines, l => l.Contains("dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNullSink_AcceptsAndDropsEverything()
    {
        NullUnknownCharacterSink.Instance.Report(new UnknownCharacterSighting("The Lantern", Role.Killer, 1));
        Assert.Throws<ArgumentNullException>(() => NullUnknownCharacterSink.Instance.Report(null!));
    }

    [Fact]
    public async Task TheRealClient_SendsOnlyTheFoldedNameRoleMatchAndVersion_ToTheApiHost()
    {
        using var h = new AccountHarness();
        var reporter = true;
        h.Service.Handler = request => request.Path switch
        {
            "/v1/me" when request.Method == HttpMethod.Get => FakeService.Json(200, """{"schemaVersion":1,"user":{"id":"u","rosterReporter":""" + (reporter ? "true" : "false") + "}}"),
            "/v1/roster/sightings" => FakeService.Json(200, """{"schemaVersion":1,"accepted":1,"ignored":{"known":0,"blocked":0,"full":0},"added":[]}"""),
            _ => h.DefaultHandler(request),
        };
        Assert.True((await h.Account.SignInAsync(Ct)).Succeeded);
        var client = new AccountRosterReportClient(h.Account, h.Api, "0.4.1");

        Assert.Equal(new RosterReportAnswer(RosterReportStatus.Ok, true), await client.CheckReporterAsync(Ct));
        var answer = await client.SendAsync([new CharacterSightingReport("the lantern", "killer", new string('a', 32))], Ct);

        Assert.Equal(RosterReportStatus.Ok, answer.Status);
        var sent = Assert.Single(h.Service.To("/v1/roster/sightings"));
        Assert.Equal(AccountHarness.Host, sent.Host);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.StartsWith("Bearer at_", sent.Authorization, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(["appVersion", "sightings"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("0.4.1", body.RootElement.GetProperty("appVersion").GetString());
        var item = Assert.Single(body.RootElement.GetProperty("sightings").EnumerateArray());
        Assert.Equal(["name", "role", "match"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal("the lantern", item.GetProperty("name").GetString());

        reporter = false;
        Assert.False((await client.CheckReporterAsync(Ct)).IsReporter);
        h.Service.Handler = request => request.Path == "/v1/roster/sightings" ? FakeService.Json(403, """{"schemaVersion":1,"error":"not_roster_reporter"}""") : h.DefaultHandler(request);
        Assert.Equal(RosterReportStatus.NotReporter, (await client.SendAsync([new CharacterSightingReport("the lantern", "killer", new string('b', 32))], Ct)).Status);
        h.Service.Handler = request => request.Path == "/v1/roster/sightings" ? FakeService.Json(400, """{"schemaVersion":1,"error":"invalid_request"}""") : h.DefaultHandler(request);
        Assert.Equal(RosterReportStatus.Refused, (await client.SendAsync([new CharacterSightingReport("the lantern", "killer", new string('c', 32))], Ct)).Status);
        Assert.Matches(@"^\d+\.\d+\.\d+$", AccountRosterReportClient.BuildVersion());
        Assert.Throws<ArgumentException>(() => new AccountRosterReportClient(h.Account, h.Api, "latest"));
    }

    [Fact]
    public async Task TheRealClient_ContactsNothingWhenSignedOut()
    {
        using var h = new AccountHarness();
        var client = new AccountRosterReportClient(h.Account, h.Api, "0.4.1");

        Assert.Equal(RosterReportStatus.NotAllowed, (await client.CheckReporterAsync(Ct)).Status);
        Assert.Equal(RosterReportStatus.NotAllowed, (await client.SendAsync([new CharacterSightingReport("the lantern", "killer", new string('a', 32))], Ct)).Status);
        Assert.Empty(h.Service.Requests);
    }

    private sealed class ReporterWorld
    {
        public ReporterWorld(bool isReporter)
        {
            Client = new FakeClient(isReporter);
            Reporter = new UnknownCharacterReporter(Client, Account, Time, Log);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));

        public TestLog Log { get; } = new();

        public FakeAccount Account { get; } = new();

        public FakeClient Client { get; }

        public UnknownCharacterReporter Reporter { get; }

        public async Task AdvanceAsync(TimeSpan by)
        {
            Time.Advance(by);
            await Reporter.LastFlush;
        }
    }

    private sealed class FakeClient(bool isReporter) : IRosterReportClient
    {
        public int Checks { get; private set; }

        public List<IReadOnlyList<CharacterSightingReport>> Sent { get; } = [];

        public RosterReportAnswer SendAnswer { get; set; } = new(RosterReportStatus.Ok);

        public Task<RosterReportAnswer> CheckReporterAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult(new RosterReportAnswer(RosterReportStatus.Ok, isReporter));
        }

        public Task<RosterReportAnswer> SendAsync(IReadOnlyList<CharacterSightingReport> sightings, CancellationToken cancellationToken)
        {
            Sent.Add(sightings);
            return Task.FromResult(SendAnswer);
        }
    }

    private sealed class FakeAccount : IAccountService
    {
        public event EventHandler? Changed;

        public AccountSnapshot Current { get; private set; } = new(OnlineState.Active, "Owner", true, null, null);

        public void SignOut()
        {
            Current = AccountSnapshot.SignedOut;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<AccountResult> SignInAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountResult> RedeemInviteAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountResult> SignOutAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountResult> DeleteAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccountResult> RefreshAsync(bool force, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
