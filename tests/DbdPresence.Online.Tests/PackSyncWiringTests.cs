// Covers: FR-ONL-07 (the composed online layer: real files on disk, real verifier, strict PNG), ARCHITECTURE §8 (the UI-test hook replaces the
// network with a local folder and nothing else), FR-ONL-05 (a signed-out app contacts nothing), D8.
using System.Buffers.Text;
using System.Text;
using DbdPresence.Core.Images;
using DbdPresence.Online.Api;
using DbdPresence.Online.Packs;
using DbdPresence.Packs.Sync;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class PackSyncWiringTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdp-wiring-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task TestFolder_ReplacesTheNetwork_AndTheRealCodeVerifiesAndStoresThePacks()
    {
        using var publisher = new PackPublisher();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
        var fixture = WriteFixture(publisher, time, withTrust: true);
        using var devices = new FakeDeviceKeys();

        var services = OnlineServices.Create(Options(time, devices, fixture));
        try
        {
            var status = await services.Packs.SyncAsync(manual: false, Ct);

            Assert.True(status.CanSync); // the hook opens the gate; no licence is involved
            Assert.Equal(PackSyncOutcome.Updated, status.LastOutcome);
            var synced = services.Packs.Synced!;
            Assert.Equal(2, synced.Index.Entries.Count);
            Assert.All(synced.Index.Entries, e => Assert.Null(synced.UrlFor(e))); // local files are not public: Discord gets nothing for them
            var path = synced.LocalPathFor!(synced.Index.Entries[0])!;
            Assert.True(File.Exists(path));
            Assert.True(StrictPng.TryValidate(File.ReadAllBytes(path), out _, out _, out _));
            Assert.StartsWith(Path.Combine(_root, "synced"), path, StringComparison.Ordinal);
        }
        finally
        {
            services.Scheduler.Dispose();
            services.Packs.Dispose();
            services.Account.Dispose();
        }
    }

    [Fact]
    public async Task TestFolderWithoutItsKey_TrustsNothing()
    {
        using var publisher = new PackPublisher();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
        var fixture = WriteFixture(publisher, time, withTrust: false);
        using var devices = new FakeDeviceKeys();

        var services = OnlineServices.Create(Options(time, devices, fixture));
        try
        {
            var status = await services.Packs.SyncAsync(manual: false, Ct);

            Assert.Equal(PackSyncOutcome.IndexRefused, status.LastOutcome);
            Assert.Null(services.Packs.Synced);
        }
        finally
        {
            services.Scheduler.Dispose();
            services.Packs.Dispose();
            services.Account.Dispose();
        }
    }

    [Fact]
    public async Task WithoutTheHook_ASignedOutApp_ContactsNothingAndOffersNoCheck()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
        using var devices = new FakeDeviceKeys();

        var services = OnlineServices.Create(Options(time, devices, null));
        try
        {
            var status = await services.Packs.SyncAsync(manual: true, Ct);

            Assert.Equal(PackSyncOutcome.NotAllowed, status.LastOutcome);
            Assert.False(status.CanSync);
            Assert.Null(services.Packs.Synced);
        }
        finally
        {
            services.Scheduler.Dispose();
            services.Packs.Dispose();
            services.Account.Dispose();
        }
    }

    [Fact]
    public async Task Directory_Transport_AnswersUnreachable_ForMissingOrGarbledFiles()
    {
        var transport = new DirectoryPackSyncTransport(_root);

        Assert.Equal(ApiOutcome.Unreachable, (await transport.GetIndexAsync("stable", Ct)).Outcome);

        File.WriteAllText(Path.Combine(_root, "packs.json"), "not json");
        Assert.Equal(ApiOutcome.Unreachable, (await transport.GetIndexAsync("stable", Ct)).Outcome);

        File.WriteAllText(Path.Combine(_root, "packs.json"), """{"schemaVersion":1}""");
        Assert.Equal(ApiOutcome.Unreachable, (await transport.GetIndexAsync("stable", Ct)).Outcome);
    }

    private OnlineOptions Options(TimeProvider time, FakeDeviceKeys devices, string? fixture) =>
        new(
            Path.Combine(_root, "online"),
            "1563291325107937340",
            devices,
            new FakeSecrets(),
            new FakeBrowser(),
            time,
            new TestLog(),
            new FakeImageHelper(),
            Path.Combine(_root, "synced"),
            fixture);

    private string WriteFixture(PackPublisher publisher, TimeProvider time, bool withTrust)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(_root, "fixture")).FullName;
        Directory.CreateDirectory(Path.Combine(fixture, "cdn"));
        PublishedFile[] files =
        [
            PublishedFile.Png("official-role-killer", "official", "role:killer", 1),
            PublishedFile.Png("official-role-survivor", "official", "role:survivor", 2),
        ];
        foreach (var file in files)
        {
            File.WriteAllBytes(Path.Combine(fixture, "cdn", file.FileName), file.Bytes);
        }

        var document = publisher.Document(time.GetUtcNow(), 1, [new PublishedPack("official", "Official")], files);
        File.WriteAllText(Path.Combine(fixture, "packs.json"), PackPublisher.Wrap(document), Encoding.UTF8);
        if (withTrust)
        {
            File.WriteAllText(
                Path.Combine(fixture, "trust.json"),
                $$"""{"kid":"{{PackPublisher.Kid}}","spki":"{{Base64Url.EncodeToString(publisher.PublicKey)}}"}""",
                Encoding.UTF8);
        }

        return fixture;
    }
}
