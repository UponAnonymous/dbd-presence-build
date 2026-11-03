using System.Buffers.Text;
using System.Text.Json;
using DbdPresence.Core.Images;
using DbdPresence.Core.Online;
using DbdPresence.Online.Account;
using DbdPresence.Online.Api;
using DbdPresence.Online.Config;
using DbdPresence.Online.Http;
using DbdPresence.Online.Packs;
using DbdPresence.Online.Roster;
using DbdPresence.Online.SignIn;
using DbdPresence.Online.Study;
using DbdPresence.Online.Updates;
using DbdPresence.Online.Uploads;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Roster;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online;

/// <summary>What the composition root supplies to build the online layer (no HTTP types cross this boundary).</summary>
/// <param name="DataDirectory">Folder for the licence version floor and the stored configuration.</param>
/// <param name="DiscordClientId">The Discord application id used as OAuth client id (from the application catalog).</param>
/// <param name="Keys">The device key store.</param>
/// <param name="Secrets">The protected secret store.</param>
/// <param name="Browser">The browser launcher.</param>
/// <param name="Time">The clock.</param>
/// <param name="Log">Diagnostics.</param>
/// <param name="ImageLoader">Opens downloaded JPEG pack files in the image helper process.</param>
/// <param name="PackCacheDirectory">Folder of the synced pack cache (the verified files, the cached index and the sync state).</param>
/// <param name="AdditionalTrustedKeys">
/// Extra trusted keys, null in every release and production build. Only the UI-test build of the app (it has its own compile constant, which no
/// other build defines) passes a key here, so its UI tests can start unlocked through the normal verification path (D32).
/// </param>
/// <param name="EmbeddedRoster">
/// The roster built from the embedded vocabulary (<c>VocabularyRoster</c> at the composition root): the append-only baseline of roster
/// sync (FR-ROS-04). Null means no baseline beyond the previous roster.
/// </param>
/// <param name="PackSyncTestDirectory">
/// UI-test hook, set by the composition root only when the test data root variable is set: a folder with <c>packs.json</c>, <c>cdn/</c> and
/// <c>trust.json</c> that replaces the network for pack sync (no licence is asked for and no host is contacted). Null in every real run.
/// </param>
public sealed record OnlineOptions(
    string DataDirectory,
    string DiscordClientId,
    IDeviceKeyStore Keys,
    ISecretStore Secrets,
    IBrowserLauncher Browser,
    TimeProvider Time,
    IOnlineLog Log,
    ISafeImageLoader ImageLoader,
    string PackCacheDirectory,
    string? PackSyncTestDirectory = null,
    IReadOnlyDictionary<string, TrustedKey>? AdditionalTrustedKeys = null,
    CharacterRoster? EmbeddedRoster = null);

/// <summary>The built online layer.</summary>
/// <param name="Account">Sign-in, session, licence, invites and deletion.</param>
/// <param name="Config">The merged remote configuration.</param>
/// <param name="Packs">Pack sync: the verified pack cache and its status.</param>
/// <param name="Scheduler">Runs pack sync on start, every 6 hours and when online features become available; started by the host.</param>
/// <param name="Uploads">The user's uploads (FR-ONL-09).</param>
/// <param name="Roster">
/// The signed character roster (PLAN_V2_S5B §2.3–2.4): the composition root attaches its <see cref="IRosterConsumer"/>, which merges each
/// roster into the vocabulary with <c>Vocabulary.WithCharacters</c>.
/// </param>
/// <param name="UnknownCharacters">Where detection reports character names it could not find (D35); sends only from the owner's account.</param>
/// <param name="Updates">Signed updates (FR-ONL-10): the verified release feed and package downloads; the app adapts it to Velopack.</param>
/// <param name="KeyReplacements">The recovery-key flow (PLAN_V2 §6.2): applied at start and checked before each update check.</param>
/// <param name="StudyLabelling">The owner-only labelling relay client (ADR-0018): the relay answers for no account but the owner's own game account.</param>
public sealed record OnlineServices(
    AccountManager Account,
    RemoteConfigService Config,
    PackSyncService Packs,
    PackSyncScheduler Scheduler,
    UploadManager Uploads,
    RosterSyncService Roster,
    UnknownCharacterReporter UnknownCharacters,
    UpdateService Updates,
    KeyReplacementService KeyReplacements,
    StudyLabellingClient StudyLabelling)
{
    /// <summary>Builds the services for this build's environment (staging or production keys and host; ADR-0016).</summary>
    /// <param name="options">The collaborators.</param>
    /// <returns>The services.</returns>
    public static OnlineServices Create(OnlineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var environment = ServiceEnvironment.Current;
        var http = ServiceHttp.CreateClient(ServiceHttp.CreateHandler(environment.ApiHost, cdnHost: environment.CdnHost), environment.BaseAddress);
        var api = new ServiceClient(http, options.Time, environment.CdnHost);
        var files = new PhysicalPackFileStore();
        files.CreateDirectory(options.DataDirectory);
        var versions = new FileVersionStore(files, Path.Combine(options.DataDirectory, "online-versions.json"));
        var trusted = new Dictionary<string, TrustedKey>(environment.Keys, StringComparer.Ordinal);
        foreach (var (kid, key) in options.AdditionalTrustedKeys ?? new Dictionary<string, TrustedKey>())
        {
            trusted[kid] = key;
        }

        var verifier = new SignedDocumentVerifier(new TrustedKeySet(trusted), versions, options.Time);

        // The recovery-key flow runs first: a kept key replacement is verified and applied before any other signed document is read, so a
        // revoked key is never trusted again, not even offline (PLAN_V2 §6.2).
        IReleaseTransport releases = new HttpReleaseTransport(api, environment.ReleasePackagesBaseAddress);
        var keyReplacements = new KeyReplacementService(releases, verifier, trusted, files, Path.Combine(options.DataDirectory, "key-replacement.json"), options.Log);
        keyReplacements.LoadStored();

        var config = new RemoteConfigService(api, verifier, files, Path.Combine(options.DataDirectory, "config.json"), options.Time, options.Log, keyReplacements);
        var account = new AccountManager(new AccountDependencies(
            api, options.Keys, options.Secrets, verifier, config, options.Browser, new LoopbackListenerFactory(options.Time), options.Time, options.Log, options.DiscordClientId));

        // Pack sync (FR-ONL-07). The test hook swaps the network for a local folder and the account gate for an open one; everything after
        // that (verification, strict PNG, the image helper, the cache swap) is the real code.
        IPackSyncTransport transport = new HttpPackSyncTransport(api, environment.PackFilesBaseAddress);
        IPackSyncGate gate = new AccountPackSyncGate(account);
        var packVerifier = verifier;
        if (options.PackSyncTestDirectory is { Length: > 0 } testDirectory)
        {
            transport = new DirectoryPackSyncTransport(testDirectory);
            gate = new OpenPackSyncGate();
            packVerifier = new SignedDocumentVerifier(new TrustedKeySet(WithTestKey(environment.Keys, testDirectory, options.Log)), versions, options.Time);
        }

        // The character roster (S5b-4): the same verifier (its own purpose, roster) and the same schedule as the packs. The UI-test build has no
        // roster source, so it keeps the embedded vocabulary.
        IRosterTransport rosterTransport = options.PackSyncTestDirectory is { Length: > 0 } ? new NoRosterTransport() : new HttpRosterTransport(api);
        var roster = new RosterSyncService(new RosterSyncDependencies(
            rosterTransport, verifier, files, Path.Combine(options.DataDirectory, "roster.json"), options.EmbeddedRoster ?? CharacterRoster.Empty, gate, options.Time, options.Log));

        var cache = new PackCache(files, Path.Combine(options.PackCacheDirectory, "cache"));
        var packs = new PackSyncService(new PackSyncDependencies(
            transport, packVerifier, cache, files, options.PackCacheDirectory, options.ImageLoader, gate, options.Time, options.Log, roster));
        var uploads = new UploadManager(account, api, files, Path.Combine(options.DataDirectory, "uploads.json"), options.Time, options.Log);
        var reporter = new UnknownCharacterReporter(new AccountRosterReportClient(account, api, AccountRosterReportClient.BuildVersion()), account, options.Time, options.Log);
        uploads.RosterReporterObserved += (_, isReporter) => reporter.NoteReporter(isReporter);
        var releaseFloor = new FileReleaseFloor(files, Path.Combine(options.DataDirectory, "release-floor.json"), options.Log);
        var updates = new UpdateService(releases, verifier, new AccountPackSyncGate(account), releaseFloor, UpdateService.StableChannel, options.Time, options.Log, keyReplacements);
        return new OnlineServices(account, config, packs, new PackSyncScheduler(packs, gate, options.Time, options.Log, roster), uploads, roster, reporter, updates, keyReplacements, new StudyLabellingClient(account, api));
    }

    private static Dictionary<string, TrustedKey> WithTestKey(IReadOnlyDictionary<string, TrustedKey> keys, string directory, IOnlineLog log)
    {
        var all = new Dictionary<string, TrustedKey>(keys, StringComparer.Ordinal);
        try
        {
            using var json = DirectoryPackSyncTransport.ParseFile(Path.Combine(directory, "trust.json"));
            var kid = json.RootElement.GetProperty("kid").GetString()!;
            var spki = Base64Url.DecodeFromChars(json.RootElement.GetProperty("spki").GetString()!);
            all[kid] = new TrustedKey(spki, [SignedPurposes.PackIndex]);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException)
        {
            log.Warning("The pack sync test key could not be read: " + ex.GetType().Name);
        }

        return all;
    }
}
