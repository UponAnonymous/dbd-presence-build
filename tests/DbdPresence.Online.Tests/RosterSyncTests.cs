// Covers: FR-ROS-04 (app roster sync: verify first, append-only, withdraw only non-embedded, keep last good, commit the version only after the
// merge and the cache write; rollback and expiry), FR-ROS-03 (purpose roster, channel stable, the service's signed-roster fixture), S5b-4 (Online
// part: fetch, verify, cache, merge through the composition root's consumer), FR-ONL-07 (runs with pack sync).
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbdPresence.Core.Game;
using DbdPresence.Online.Api;
using DbdPresence.Online.Packs;
using DbdPresence.Online.Roster;
using DbdPresence.Packs.Roster;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class RosterSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CharacterRoster s_embedded = CharacterRoster.Create(
    [
        new RosterCharacter("K01", "the-trapper", Role.Killer, "The Trapper", []),
        new RosterCharacter("S01", "dwight-fairfield", Role.Survivor, "Dwight Fairfield", []),
    ]);

    [Fact]
    public async Task NewRoster_IsVerifiedMergedCachedAndOnlyThenItsVersionRecorded()
    {
        using var w = new RosterWorld();
        w.Serve(10, Seed(), Character("K45", "the-lantern", "The Lantern"));
        var order = new List<string>();
        w.Consumer.OnApply = _ =>
        {
            order.Add("merge");
            Assert.False(w.Files.Exists(RosterWorld.CachePath));
            Assert.Null(w.Versions.GetHighest(SignedPurposes.Roster, "stable"));
        };
        w.Sync.Attach(w.Consumer);

        var outcome = await w.Sync.SyncAsync(Ct);

        Assert.Equal(RosterSyncOutcome.Updated, outcome);
        Assert.Equal(["merge"], order);
        Assert.True(w.Files.Exists(RosterWorld.CachePath));
        Assert.Equal(10, w.Versions.GetHighest(SignedPurposes.Roster, "stable"));
        var added = Assert.Single(w.Sync.Current!.NewCharacters(s_embedded));
        Assert.Equal(("K45", "the-lantern", Role.Killer, "The Lantern"), (added.Code, added.Id, added.Role, added.DisplayName));
        Assert.Same(w.Sync.Current, w.Consumer.Applied.Last());
        Assert.False(w.Sync.Status.CouldNotRefresh);
        Assert.Equal(w.Time.GetUtcNow(), w.Sync.Status.LastSuccessAt);
    }

    [Fact]
    public async Task TheServicesSignedRosterFixture_IsAcceptedAndMerges()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "roster", "signed-roster.json")))!.AsObject();
        var keys = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signing", "keys", "keys.json")));
        var trusted = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in keys.RootElement.GetProperty("keys").EnumerateArray())
        {
            trusted[key.GetProperty("kid").GetString()!] = new TrustedKey(Base64Url.DecodeFromChars(key.GetProperty("spki").GetString()!), [.. key.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!)]);
        }

        var time = new FakeTimeProvider(DateTimeOffset.Parse(fixture["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
        using var w = new RosterWorld(new SignedDocumentVerifier(new TrustedKeySet(trusted), new InMemoryVersionStore(), time), time);
        w.Document = fixture["documentText"]!.GetValue<string>();
        w.Sync.Attach(w.Consumer);

        Assert.Equal(RosterSyncOutcome.Updated, await w.Sync.SyncAsync(Ct));
        var roster = w.Sync.Current!;
        Assert.Equal(100, roster.Characters.Count);
        Assert.True(roster.Characters.Single(c => c.Code == "S55").Withdrawn);
        Assert.Equal(["K45"], roster.NewCharacters(s_embedded).Where(c => c.Code is "K45" or "S55").Select(c => c.Code));
        Assert.Equal("Leon S. Kennedy", roster.Characters.Single(c => c.Code == "S27").Aliases.Single().Name);
    }

    [Fact]
    public async Task SameContent_IsUpToDate_AndAWeeklyReSignIsKeptForItsLongerLife()
    {
        using var w = new RosterWorld();
        w.Serve(10, Seed());
        w.Sync.Attach(w.Consumer);
        Assert.Equal(RosterSyncOutcome.Updated, await w.Sync.SyncAsync(Ct));
        var first = w.Files.Files[RosterWorld.CachePath];

        w.Serve(10, Seed());
        Assert.Equal(RosterSyncOutcome.UpToDate, await w.Sync.SyncAsync(Ct));
        Assert.Equal(first, w.Files.Files[RosterWorld.CachePath]);

        w.Time.Advance(TimeSpan.FromDays(7));
        w.Serve(10, Seed());
        Assert.Equal(RosterSyncOutcome.UpToDate, await w.Sync.SyncAsync(Ct));
        Assert.NotEqual(first, w.Files.Files[RosterWorld.CachePath]);
        Assert.Single(w.Consumer.Applied);
        w.Time.Advance(TimeSpan.FromDays(25));
        Assert.False(w.Sync.Status.Expired);
    }

    [Fact]
    public async Task AnOlderVersion_IsRefusedAsARollback_AndTheLastGoodStays()
    {
        using var w = new RosterWorld();
        w.Sync.Attach(w.Consumer);
        w.Serve(20, Seed(), Character("K45", "the-lantern", "The Lantern"));
        await w.Sync.SyncAsync(Ct);
        var good = w.Sync.Current;

        w.Serve(19, Seed(), Character("K45", "the-lantern", "The Lantern"), Character("K46", "the-other", "The Other"));

        Assert.Equal(RosterSyncOutcome.Refused, await w.Sync.SyncAsync(Ct));
        Assert.Same(good, w.Sync.Current);
        Assert.True(w.Sync.Status.CouldNotRefresh);
        Assert.Equal(20, w.Versions.GetHighest(SignedPurposes.Roster, "stable"));
    }

    [Theory]
    [InlineData("dropped embedded code")]
    [InlineData("changed embedded id")]
    [InlineData("withdrawn embedded")]
    [InlineData("dropped earlier code")]
    [InlineData("changed earlier id")]
    public async Task AppendOnlyRules_RefuseTheWholeDocument(string change)
    {
        using var w = new RosterWorld();
        w.Sync.Attach(w.Consumer);
        w.Serve(1, Seed(), Character("K45", "the-lantern", "The Lantern"));
        Assert.Equal(RosterSyncOutcome.Updated, await w.Sync.SyncAsync(Ct));
        var good = w.Sync.Current;

        JsonObject[] next = change switch
        {
            "dropped embedded code" => [Character("K01", "the-trapper", "The Trapper"), Character("K45", "the-lantern", "The Lantern")],
            "changed embedded id" => [Character("K01", "the-trapper-2", "The Trapper"), Character("S01", "dwight-fairfield", "Dwight Fairfield"), Character("K45", "the-lantern", "The Lantern")],
            "withdrawn embedded" => [Character("K01", "the-trapper", "The Trapper", withdrawn: true), Character("S01", "dwight-fairfield", "Dwight Fairfield"), Character("K45", "the-lantern", "The Lantern")],
            "dropped earlier code" => Seed(),
            _ => [.. Seed(), Character("K45", "the-lantern-2", "The Lantern")],
        };
        w.Serve(2, next);

        Assert.Equal(RosterSyncOutcome.Refused, await w.Sync.SyncAsync(Ct));
        Assert.Same(good, w.Sync.Current);
        Assert.Equal(1, w.Versions.GetHighest(SignedPurposes.Roster, "stable"));
        Assert.Single(w.Consumer.Applied);
    }

    [Fact]
    public async Task WithdrawingALearnedCharacter_IsAccepted_AndLeavesItOutOfTheNewCharacters()
    {
        using var w = new RosterWorld();
        w.Sync.Attach(w.Consumer);
        w.Serve(1, Seed(), Character("K45", "the-lantern", "The Lantern"));
        await w.Sync.SyncAsync(Ct);
        w.Serve(2, Seed(), Character("K45", "the-lantern", "The Lantern", withdrawn: true));

        Assert.Equal(RosterSyncOutcome.Updated, await w.Sync.SyncAsync(Ct));
        Assert.Empty(w.Sync.Current!.NewCharacters(s_embedded));
        Assert.Equal(2, w.Consumer.Applied.Count);
    }

    [Fact]
    public async Task ARosterTheConsumerCannotMerge_IsRefused_AndNothingIsCachedOrRecorded()
    {
        using var w = new RosterWorld();
        w.Consumer.OnApply = _ => throw new FormatException("collides");
        w.Sync.Attach(w.Consumer);
        w.Serve(5, Seed(), Character("K45", "the-trappper", "The Trappper"));

        Assert.Equal(RosterSyncOutcome.Refused, await w.Sync.SyncAsync(Ct));
        Assert.Null(w.Sync.Current);
        Assert.False(w.Files.Exists(RosterWorld.CachePath));
        Assert.Null(w.Versions.GetHighest(SignedPurposes.Roster, "stable"));
    }

    [Fact]
    public async Task ARosterThatThrowsAnArgumentException_IsRefused_AndTheLastGoodRosterIsPutBackOnTheConsumers()
    {
        // Round-2 security review I3: detection's vocabulary merge can throw ArgumentException; the page and detection must stay in step.
        using var w = new RosterWorld();
        w.Sync.Attach(w.Consumer);
        w.Serve(1, Seed());
        Assert.Equal(RosterSyncOutcome.Updated, await w.Sync.SyncAsync(Ct));
        var good = w.Sync.Current;
        w.Consumer.OnApply = roster =>
        {
            if (roster.Characters.Count > good!.Characters.Count)
            {
                throw new ArgumentException("duplicate");
            }
        };
        w.Serve(2, Seed(), Character("K45", "the-lantern", "The Lantern"));
        var applied = w.Consumer.Applied.Count;

        Assert.Equal(RosterSyncOutcome.Refused, await w.Sync.SyncAsync(Ct));
        Assert.Same(good, w.Sync.Current);
        Assert.Equal(applied + 1, w.Consumer.Applied.Count);
        Assert.Same(good, w.Consumer.Applied[^1]);
    }

    [Theory]
    [InlineData("wrong purpose")]
    [InlineData("wrong channel")]
    [InlineData("unknown key")]
    [InlineData("expired")]
    [InlineData("bad payload")]
    [InlineData("bad name")]
    public async Task ADocumentThatFailsVerificationOrParsing_IsRefused(string fault)
    {
        using var w = new RosterWorld();
        w.Sync.Attach(w.Consumer);
        var payload = Payload(Seed());
        w.Document = fault switch
        {
            "wrong purpose" => w.Sign(1, payload, purpose: SignedPurposes.PackIndex),
            "wrong channel" => w.Sign(1, payload, channel: "beta"),
            "unknown key" => w.Sign(1, payload, kid: "someone-else"),
            "expired" => w.Sign(1, payload, issued: w.Time.GetUtcNow().AddDays(-31)),
            "bad payload" => w.Sign(1, """{"schemaVersion":2,"characters":[]}"""),
            _ => w.Sign(1, Payload([.. Seed(), Character("K45", "the-lantern", "The‮Lantern")])),
        };

        Assert.Equal(RosterSyncOutcome.Refused, await w.Sync.SyncAsync(Ct));
        Assert.Null(w.Sync.Current);
        Assert.Empty(w.Consumer.Applied);
    }

    [Fact]
    public async Task AfterRestart_TheCachedRosterIsUsedEvenWhenExpired_AndRaisesTheVersionFloor()
    {
        using var w = new RosterWorld();
        w.Sync.Attach(w.Consumer);
        w.Serve(30, Seed(), Character("K45", "the-lantern", "The Lantern"));
        await w.Sync.SyncAsync(Ct);
        w.Time.Advance(TimeSpan.FromDays(40));

        var versions = new InMemoryVersionStore();
        using var restarted = w.Restart(versions);

        Assert.Equal(30, versions.GetHighest(SignedPurposes.Roster, "stable"));
        Assert.NotNull(restarted.Current);
        Assert.True(restarted.Status.Expired);
        Assert.True(restarted.Status.CouldNotRefresh);
        var consumer = new RecordingConsumer();
        restarted.Attach(consumer);
        Assert.Single(consumer.Applied);
    }

    [Fact]
    public async Task Offline_RateLimited_AndGateClosed_KeepTheLastGood()
    {
        using var w = new RosterWorld();
        w.Sync.Attach(w.Consumer);
        w.Serve(1, Seed());
        await w.Sync.SyncAsync(Ct);
        var good = w.Sync.Current;

        w.Answer = new ApiResult<string>(ApiOutcome.Unreachable, null, null, null, null);
        Assert.Equal(RosterSyncOutcome.Offline, await w.Sync.SyncAsync(Ct));
        w.Answer = new ApiResult<string>(ApiOutcome.RateLimited, null, "rate_limited", null, TimeSpan.FromMinutes(1));
        Assert.Equal(RosterSyncOutcome.RateLimited, await w.Sync.SyncAsync(Ct));
        w.Gate.IsOpen = false;
        var requests = w.Requests;
        Assert.Equal(RosterSyncOutcome.NotAllowed, await w.Sync.SyncAsync(Ct));
        Assert.Equal(requests, w.Requests);
        Assert.Same(good, w.Sync.Current);
    }

    [Fact]
    public async Task ThePackScheduler_ChecksTheRosterAfterThePacks_AndCheckForNewPacksDoesToo()
    {
        using var w = new RosterWorld();
        w.Serve(1, Seed());
        var packs = new RecordingPackSync();
        using var scheduler = new PackSyncScheduler(packs, w.Gate, w.Time, w.Log, w.Sync);

        scheduler.Start();
        w.Time.Advance(PackSyncScheduler.StartDelay);
        await scheduler.LastRun;

        Assert.Equal(1, packs.Runs);
        Assert.Equal(1, w.Requests);
        w.Time.Advance(PackSyncScheduler.Interval);
        await scheduler.LastRun;
        Assert.Equal(2, w.Requests);
    }

    private static JsonObject Character(string code, string id, string name, bool withdrawn = false) => new()
    {
        ["code"] = code,
        ["id"] = id,
        ["role"] = code[0] == 'K' ? "killer" : "survivor",
        ["displayName"] = name,
        ["screenName"] = name,
        ["aliases"] = new JsonArray(),
        ["licensed"] = null,
        ["withdrawn"] = withdrawn,
    };

    private static JsonObject[] Seed() => [Character("K01", "the-trapper", "The Trapper"), Character("S01", "dwight-fairfield", "Dwight Fairfield")];

    private static string Payload(IEnumerable<JsonObject> characters) =>
        new JsonObject { ["schemaVersion"] = 1, ["characters"] = new JsonArray([.. characters.Select(c => (JsonNode?)c.DeepClone())]) }.ToJsonString();

    private sealed class RosterWorld : IRosterTransport, IDisposable
    {
        public const string CachePath = "online/roster.json";
        public const string Kid = "content-test";

        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public RosterWorld(SignedDocumentVerifier? verifier = null, FakeTimeProvider? time = null)
        {
            Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
            Verifier = verifier ?? new SignedDocumentVerifier(Trusted, Versions, Time);
            Sync = Build(Verifier);
        }

        public FakeTimeProvider Time { get; }

        public InMemoryVersionStore Versions { get; } = new();

        public SignedDocumentVerifier Verifier { get; }

        public MemoryFiles Files { get; } = new();

        public TestLog Log { get; } = new();

        public SwitchGate Gate { get; } = new();

        public RecordingConsumer Consumer { get; } = new();

        public RosterSyncService Sync { get; }

        public string? Document { get; set; }

        public ApiResult<string>? Answer { get; set; }

        public int Requests { get; private set; }

        private TrustedKeySet Trusted => new(new Dictionary<string, TrustedKey> { [Kid] = new TrustedKey(_key.ExportSubjectPublicKeyInfo(), [SignedPurposes.PackIndex, SignedPurposes.Roster]) });

        public void Serve(long version, IEnumerable<JsonObject> characters, params JsonObject[] more)
        {
            Document = Sign(version, Payload(characters.Concat(more)));
            Answer = null;
        }

        public string Sign(long version, string payloadJson, string purpose = SignedPurposes.Roster, string channel = "stable", string kid = Kid, DateTimeOffset? issued = null)
        {
            var at = issued ?? Time.GetUtcNow();
            var envelope = $$"""{"kid":"{{kid}}","purpose":"{{purpose}}","channel":"{{channel}}","version":{{version}},"issuedAt":"{{TestIssuer.Iso(at)}}","expiresAt":"{{TestIssuer.Iso(at.AddDays(30))}}","payload":{{payloadJson}}}""";
            var bytes = Encoding.UTF8.GetBytes(envelope);
            var signature = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return $$"""{"kid":"{{kid}}","signed":"{{Base64Url.EncodeToString(bytes)}}","sig":"{{Base64Url.EncodeToString(signature)}}"}""";
        }

        public RosterSyncService Restart(InMemoryVersionStore versions) => Build(new SignedDocumentVerifier(Trusted, versions, Time));

        public Task<ApiResult<string>> GetRosterAsync(string channel, CancellationToken cancellationToken)
        {
            Assert.Equal("stable", channel);
            Requests++;
            return Task.FromResult(Answer ?? (Document is null
                ? new ApiResult<string>(ApiOutcome.Unreachable, null, null, null, null)
                : new ApiResult<string>(ApiOutcome.Ok, Document, null, null, null)));
        }

        public void Dispose()
        {
            Sync.Dispose();
            _key.Dispose();
        }

        private RosterSyncService Build(SignedDocumentVerifier verifier) =>
            new(new RosterSyncDependencies(this, verifier, Files, CachePath, s_embedded, Gate, Time, Log));
    }

    private sealed class RecordingConsumer : IRosterConsumer
    {
        public List<RosterDocument> Applied { get; } = [];

        public Action<RosterDocument>? OnApply { get; set; }

        public void Apply(RosterDocument roster)
        {
            OnApply?.Invoke(roster);
            Applied.Add(roster);
        }
    }

    private sealed class SwitchGate : IPackSyncGate
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsOpen { get; set; } = true;
    }

    private sealed class RecordingPackSync : DbdPresence.Packs.Sync.IPackSyncService
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public int Runs { get; private set; }

        public DbdPresence.Packs.Sync.PackSyncStatus Status => DbdPresence.Packs.Sync.PackSyncStatus.Unavailable;

        public DbdPresence.Packs.Local.SyncedPacks? Synced => null;

        public Task<DbdPresence.Packs.Sync.PackSyncStatus> SyncAsync(bool manual, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(Status);
        }
    }
}
