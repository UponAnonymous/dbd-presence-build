// Covers (on Windows CI, real executable): FR-ONL-07 (the Packs panel of the Characters page: a synced official pack as a row, the "Updated" line, a
// real "Check for updates" button that is rate-limit aware, a refused file changes nothing), FR-PACK-03 (Behaviour's notice stays visible on the page),
// UI_SPEC "show nothing that cannot work" (no check button without online features), FR-APP-01/04 (both themes, screenshots).
//
// The app has no network in these tests. The test hook DBDP_PACK_SYNC_DIR, honoured only together with DBDP_DATA_ROOT (ARCHITECTURE §8),
// replaces the service and the CDN with a folder this test writes: packs.json (a document signed here with a throw-away key), cdn/<sha256>.png
// and trust.json (that key's public half, trusted for the pack-index purpose only). Everything after that is the real app: signature check,
// strict PNG check, cache swap, cards. The index validity window is built from the machine clock because the app verifies with its own.
// PENDING-ENV: these tests have not been run; they run only in the Windows CI job (ci-windows.yml).
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace DbdPresence.UiTests;

public sealed class PacksSyncFlowTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);
    private readonly ITestOutputHelper _output;

    public PacksSyncFlowTests(ITestOutputHelper output) => _output = output;

    private static string Seeded(string theme) =>
        $$"""{ "schemaVersion": 2, "firstRunCompleted": true, "appearance": { "theme": "{{theme}}" } }""";

    private static Dictionary<string, string> Hook(string folder) => new() { ["DBDP_PACK_SYNC_DIR"] = folder };

    /// <summary>Writes a signed pack index and its files. <paramref name="tamper"/> serves different bytes than the index lists for one file.</summary>
    private static string WriteSyncFolder(string root, bool behaviourArt, bool tamper = false)
    {
        var folder = Path.Combine(root, "sync-fixture");
        Directory.CreateDirectory(Path.Combine(folder, "cdn"));
        var killer = TestPng.Rgb(64, 64, 10);
        var survivor = TestPng.Rgb(64, 64, 90);
        var files = new[]
        {
            (Id: "official-role-killer", Key: "role:killer", Kind: "role", Bytes: killer, Licensed: "null"),
            (Id: "official-role-survivor", Key: "role:survivor", Kind: "role", Bytes: survivor, Licensed: """{"holder":"Example Studio"}"""),
        };
        foreach (var file in files)
        {
            var served = (byte[])file.Bytes.Clone();
            if (tamper && file.Id == "official-role-survivor")
            {
                Array.Clear(served, served.Length - 8, 8); // same length, different bytes: what a hostile CDN would serve
            }

            File.WriteAllBytes(Path.Combine(folder, "cdn", Hex(file.Bytes) + ".png"), served);
        }

        var entries = string.Join(
            ",",
            files.Select(f => $$"""{"id":"{{f.Id}}","pack":"official","kind":"{{f.Kind}}","key":"{{f.Key}}","sha256":"{{Hex(f.Bytes)}}","size":{{f.Bytes.Length}},"width":64,"height":64,"format":"png","licensed":{{f.Licensed}}}"""));
        var payload = $$"""{"packs":[{"id":"official","name":"Official","version":1,"author":"DBD Presence","licenceNote":"Fan content shown as a courtesy.","credit":"Art by the community","fallback":false,"behaviourArt":{{(behaviourArt ? "true" : "false")}}}],"entries":[{{entries}}]}""";

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var envelope = $$"""{"kid":"pack-index-ui-test","purpose":"pack-index","channel":"stable","version":1,"issuedAt":"{{Iso(now.AddHours(-1))}}","expiresAt":"{{Iso(now.AddDays(20))}}","payload":{{payload}}}""";
        var bytes = Encoding.UTF8.GetBytes(envelope);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var document = $$"""{"kid":"pack-index-ui-test","signed":"{{Base64Url.EncodeToString(bytes)}}","sig":"{{Base64Url.EncodeToString(signature)}}"}""";
        File.WriteAllText(Path.Combine(folder, "packs.json"), $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(document)}}}""");
        File.WriteAllText(Path.Combine(folder, "trust.json"), $$"""{"kid":"pack-index-ui-test","spki":"{{Base64Url.EncodeToString(key.ExportSubjectPublicKeyInfo())}}"}""");
        return folder;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static AutomationElement? WaitForCard(AppUnderTest app) => Retry.WhileNull(() => app.TryFind("Pack-official"), s_wait).Result;

    private static void OpenPacks(AppUnderTest app)
    {
        app.Navigate("characters");
        app.Find("ShowPacks").AsButton().Invoke();
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void CheckingForUpdates_ShowsTheOfficialPack_AndKeepsTheBehaviourNotice(string theme)
    {
        var tag = theme.ToLowerInvariant();
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded(theme));
        var folder = WriteSyncFolder(root, behaviourArt: true);
        using var app = AppUnderTest.Launch(root, Hook(folder));
        OpenPacks(app);

        app.Find("CheckForPacks").AsButton().Invoke();

        Assert.NotNull(WaitForCard(app));
        Assert.True(Retry.WhileFalse(() => app.Find("SyncStatus").Name.StartsWith("Updated", StringComparison.Ordinal), s_wait).Success, "The updated line did not appear.");
        Assert.NotNull(app.FindByName("Owner pack · 2 pictures · Creator: DBD Presence"));
        Assert.Contains("© Behaviour Interactive. Unofficial, not affiliated with or endorsed by Behaviour Interactive.", app.Find("CharactersBehaviourNotice").Name, StringComparison.Ordinal);
        Assert.Null(app.TryFind("RemovePack")); // official packs are removed by the owner, not by the user
        var synced = app.CaptureWindow($"characters-{tag}-packs-synced");
        app.MainWindow.Patterns.Transform.Pattern.Resize(880, 600);
        var minimum = app.CaptureWindow($"characters-{tag}-packs-synced-min");
        _output.WriteLine($"{synced}; {minimum}");
        app.Quit();
    }

    [Fact]
    public void ASecondCheckWithinAMinute_IsAskedToWait_WithoutContactingAnything()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        var folder = WriteSyncFolder(root, behaviourArt: false);
        using var app = AppUnderTest.Launch(root, Hook(folder));
        OpenPacks(app);
        app.Find("CheckForPacks").AsButton().Invoke();
        Assert.NotNull(WaitForCard(app));

        app.Find("CheckForPacks").AsButton().Invoke();

        Assert.True(
            Retry.WhileFalse(() => app.TryFind("SyncNote")?.Name.Contains("asked to wait", StringComparison.Ordinal) == true, s_wait).Success,
            "The rate-limit note did not appear.");
        Assert.NotNull(app.TryFind("Pack-official")); // the cached pack stays
        app.Quit();
    }

    [Fact]
    public void ATamperedFile_IsRefused_AndNoPackAppears()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        var folder = WriteSyncFolder(root, behaviourArt: false, tamper: true);
        using var app = AppUnderTest.Launch(root, Hook(folder));
        OpenPacks(app);

        app.Find("CheckForPacks").AsButton().Invoke();

        Assert.True(
            Retry.WhileFalse(() => app.TryFind("SyncNote")?.Name.Contains("safety checks", StringComparison.Ordinal) == true, s_wait).Success,
            "The refusal note did not appear.");
        Assert.Null(app.TryFind("Pack-official"));
        app.Quit();
    }

    [Fact]
    public void SyncedPack_IsStillThereAfterARestart_WithoutChecking()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        var folder = WriteSyncFolder(root, behaviourArt: true);
        using (var first = AppUnderTest.Launch(root, Hook(folder)))
        {
            OpenPacks(first);
            first.Find("CheckForPacks").AsButton().Invoke();
            Assert.NotNull(WaitForCard(first));
            first.Quit();
        }

        using var second = AppUnderTest.Launch(root, Hook(folder));
        OpenPacks(second);

        Assert.NotNull(WaitForCard(second));
        Assert.Contains("Updated", second.Find("SyncStatus").Name, StringComparison.Ordinal);
        second.Quit();
    }

    [Fact]
    public void WithoutOnlineFeatures_NoCheckButtonIsOffered_AndTheReasonIsShown()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        OpenPacks(app);

        Assert.Contains("Account page", app.Find("SyncUnavailable").Name, StringComparison.Ordinal);
        Assert.False(app.IsShown("CheckForPacks"), "A check button that cannot work must not be shown.");
        app.Quit();
    }
}
