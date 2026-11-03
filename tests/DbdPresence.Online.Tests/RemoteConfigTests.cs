// Covers: FR-ONL-08 (signed config; flags only disable; expiry fallbacks keep the application list, ADR-0012), SAFETY_AND_PRIVACY §2.3,
// NFR-SVC-04 (config purpose, rollback), ADR-0016 (config key is scoped to its purpose).
using System.Reflection;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Config;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Tests;

public sealed class RemoteConfigTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void ServeConfig(AccountHarness h, Func<string> document) =>
        h.Service.Handler = r => r.Path == "/v1/config"
            ? FakeService.Json(200, $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(document())}}}""")
            : h.DefaultHandler(r);

    [Fact]
    public async Task ValidConfig_MergesFlagsAndTheApplicationList()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), disableCapture: true));

        Assert.True(await h.Config.RefreshAsync(Ct));

        var config = h.Config.Current;
        Assert.True(config.CaptureForcedOff);
        Assert.False(config.DetectionForcedOff);
        Assert.True(config.OnlineFeaturesEnabled);
        Assert.True(config.IsFresh);
        Assert.Equal(["dbd", "alt"], config.Applications.Select(a => a.Key));
        Assert.Equal("alt", config.DefaultApplicationKey);
        Assert.Equal("1563291325107937341", config.Applications[1].ApplicationId);
    }

    [Fact]
    public async Task EmergencySwitch_TurnsOnlineFeaturesOff_ButNotPresence()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), emergency: true));
        await h.Account.SignInAsync(Ct);

        Assert.False(h.Config.Current.OnlineFeaturesEnabled);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
    }

    [Fact]
    public async Task Config_CanOnlyDisable_NeverEnable()
    {
        using var h = new AccountHarness();
        // A hostile or buggy payload with every "enable" spelling we can think of: none is read.
        var payload = """
            {"schemaVersion":1,"applications":[],"disable":{"capture":false,"detection":false},"emergencyStop":false,
             "enable":{"capture":true,"detection":true},"enableCapture":true,"capture":true,"detection":true,"optIn":true,"features":["capture"]}
            """;
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), payloadOverride: payload));

        Assert.True(await h.Config.RefreshAsync(Ct));

        var config = h.Config.Current;
        Assert.False(config.CaptureForcedOff);
        Assert.False(config.DetectionForcedOff);
        var boolMembers = typeof(RemoteConfigSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(bool)).Select(p => p.Name).Order().ToArray();
        // Every flag is a "forced off" switch; nothing in the merged result can grant capture, detection or an opt-in.
        Assert.Equal(["AccessPublic", "CaptureForcedOff", "DetectionForcedOff", "IsFresh", "OnlineFeaturesEnabled"], boolMembers);
        // AccessPublic (D32) can only skip the invite step; it never unlocks, and this hostile payload does not set it.
        Assert.False(config.AccessPublic);
    }

    [Fact]
    public async Task ExpiredConfig_FallsBackToSafeDefaults_ButKeepsTheApplicationListAndDefault()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), disableCapture: false));
        await h.Config.RefreshAsync(Ct);
        Assert.True(h.Config.Current.OnlineFeaturesEnabled);

        h.Time.Advance(TimeSpan.FromDays(15));

        var config = h.Config.Current;
        Assert.False(config.IsFresh);
        Assert.False(config.OnlineFeaturesEnabled);
        Assert.True(config.CaptureForcedOff);
        Assert.True(config.DetectionForcedOff);
        Assert.Equal(["dbd", "alt"], config.Applications.Select(a => a.Key)); // D8: presence keeps the right application
        Assert.Equal("alt", config.DefaultApplicationKey);
    }

    [Fact]
    public void NoConfigAtAll_UsesTheSafeBuiltInDefaults()
    {
        using var h = new AccountHarness();

        var config = h.Config.Current;

        Assert.Equal(RemoteConfigSnapshot.SafeDefaults, config);
        Assert.Empty(config.Applications);
        Assert.False(config.OnlineFeaturesEnabled);
    }

    [Fact]
    public async Task StoredConfig_IsReloadedAfterARestart_EvenWhenExpired()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow()));
        await h.Config.RefreshAsync(Ct);
        h.Time.Advance(TimeSpan.FromDays(40));

        var reloaded = new RemoteConfigService(h.Api, h.Verifier, h.Files, "online/config.json", h.Time, h.Log);

        Assert.Equal(["dbd", "alt"], reloaded.Current.Applications.Select(a => a.Key));
        Assert.False(reloaded.Current.OnlineFeaturesEnabled);
    }

    [Fact]
    public async Task Refused_Documents_KeepTheLastGoodCopy()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), disableCapture: true, version: 100));
        await h.Config.RefreshAsync(Ct);
        var good = h.Config.Current;

        var attempts = new Dictionary<string, Func<string>>
        {
            ["rolled back"] = () => h.Issuer.Config(h.Time.GetUtcNow(), version: 99),
            ["wrong purpose"] = () => h.Issuer.Licence(h.Time.GetUtcNow(), "u", "c"),
            ["lifetime too long"] = () => h.Issuer.Config(h.Time.GetUtcNow(), version: 101, lifetime: TimeSpan.FromDays(60)),
            ["expired"] = () => h.Issuer.Config(h.Time.GetUtcNow().AddDays(-20), version: 102),
            ["not a document"] = () => "{}",
            ["disable flag is a string"] = () => h.Issuer.Config(h.Time.GetUtcNow(), version: 103, payloadOverride: """{"schemaVersion":1,"applications":[],"disable":{"capture":"no","detection":false},"emergencyStop":false}"""),
            ["bad application id"] = () => h.Issuer.Config(h.Time.GetUtcNow(), version: 104, payloadOverride: """{"schemaVersion":1,"applications":[{"key":"x","applicationId":"abc","name":"X","description":""}],"disable":{"capture":false,"detection":false},"emergencyStop":false}"""),
            ["unknown schema"] = () => h.Issuer.Config(h.Time.GetUtcNow(), version: 105, payloadOverride: """{"schemaVersion":2,"applications":[],"disable":{"capture":false,"detection":false},"emergencyStop":false}"""),
        };

        foreach (var (name, document) in attempts)
        {
            ServeConfig(h, document);
            Assert.False(await h.Config.RefreshAsync(Ct), name);
            Assert.Equal(good, h.Config.Current);
        }
    }

    [Fact]
    public async Task ServiceDown_KeepsTheLastGoodConfig()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow()));
        await h.Config.RefreshAsync(Ct);
        var good = h.Config.Current;
        h.Service.Throw = new HttpRequestException("down");

        Assert.False(await h.Config.RefreshAsync(Ct));

        Assert.Equal(good, h.Config.Current);
    }

    [Fact]
    public async Task Verifier_CommitsTheConfigVersion_OnlyAfterAcceptance()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), payloadOverride: "{}", version: 500));
        Assert.False(await h.Config.RefreshAsync(Ct));
        Assert.Null(h.Versions.GetHighest(SignedPurposes.Config, "stable"));

        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), version: 501));
        Assert.True(await h.Config.RefreshAsync(Ct));
        Assert.Equal(501, h.Versions.GetHighest(SignedPurposes.Config, "stable"));
    }

    private static string PayloadWith(string extra) =>
        $$"""{"schemaVersion":1,"applications":[],"disable":{"capture":false,"detection":false},"emergencyStop":false{{extra}}}""";

    [Fact]
    public async Task BannerAndMinimumVersion_AreAppliedWhileTheConfigIsFresh()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), payloadOverride: PayloadWith(""","minimumVersion":"1.2.3","banner":"  Maintenance on Friday.  " """)));

        Assert.True(await h.Config.RefreshAsync(Ct));

        var config = h.Config.Current;
        Assert.Equal("Maintenance on Friday.", config.Banner);
        Assert.Equal("1.2.3", config.MinimumVersion);
        Assert.Equal(16, config.AnnouncementId!.Length);
        Assert.True(config.IsUpdateNeeded("1.2.2+abc1234"));
        Assert.False(config.IsUpdateNeeded("1.2.3"));
        Assert.False(config.IsUpdateNeeded("1.10.0-beta"));
    }

    [Fact]
    public async Task NullOrMissingBanner_MeansNoBanner_AndAnEmptyOneToo()
    {
        using var h = new AccountHarness();
        var next = 1;

        foreach (var extra in new[] { "", ""","banner":null,"minimumVersion":null""", ""","banner":"   " """ })
        {
            ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), payloadOverride: PayloadWith(extra), version: next++));
            Assert.True(await h.Config.RefreshAsync(Ct), extra);
            Assert.Null(h.Config.Current.Banner);
            Assert.Null(h.Config.Current.AnnouncementId);
            Assert.False(h.Config.Current.IsUpdateNeeded("0.0.1"));
        }
    }

    [Fact]
    public async Task ABannerOrVersionOfTheWrongShape_RefusesTheWholeDocument()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), version: 10));
        await h.Config.RefreshAsync(Ct);
        var good = h.Config.Current;
        var tooLong = new string('x', 281);
        var attempts = new[]
        {
            ""","banner":42""",
            ""","banner":["x"]""",
            $$""","banner":"{{tooLong}}" """,
            ""","banner":"line\u0007bell" """,
            ""","minimumVersion":"1.2" """,
            ""","minimumVersion":"v1.2.3" """,
            ""","minimumVersion":7""",
        };

        var version = 11;
        foreach (var extra in attempts)
        {
            ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), payloadOverride: PayloadWith(extra), version: version++));
            Assert.False(await h.Config.RefreshAsync(Ct), extra);
            Assert.Equal(good, h.Config.Current);
        }
    }

    [Fact]
    public async Task AfterExpiry_TheBannerAndTheUpdateNoteAreGone_ButTheApplicationListStays()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow()));
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), payloadOverride: """{"schemaVersion":1,"applications":[{"key":"dbd","applicationId":"1563291325107937340","name":"Dead by Daylight","description":"Unofficial"}],"defaultApplication":"dbd","disable":{"capture":false,"detection":false},"emergencyStop":false,"minimumVersion":"9.0.0","banner":"Hello"}"""));
        await h.Config.RefreshAsync(Ct);
        Assert.Equal("Hello", h.Config.Current.Banner);

        h.Time.Advance(TimeSpan.FromDays(15));

        var config = h.Config.Current;
        Assert.Null(config.Banner);
        Assert.Null(config.MinimumVersion);
        Assert.Equal(["dbd"], config.Applications.Select(a => a.Key));
    }
}
