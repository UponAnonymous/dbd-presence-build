// Covers: D32 / FR-ONL-13 (a fresh install shows only the lock screen: sign-in, no invite box before sign-in, no navigation, no first-run setup behind it).
// Runs on Windows CI only. These are the only UI tests that use the normal build (LaunchLocked): every other UI test starts the UI-test build
// unlocked through a licence signed with the test key (UiTestUnlock). A full sign-in against the staging service is PENDING-ENV until the owner has
// set up Cloudflare (docs/owner/cloudflare-setup.md), so these tests stop at the sign-in button.
namespace DbdPresence.UiTests;

public sealed class AccountPageTests
{
    private readonly ITestOutputHelper _output;

    public AccountPageTests(ITestOutputHelper output) => _output = output;

    private static string Seeded(string theme) =>
        $$"""{ "schemaVersion": 2, "firstRunCompleted": true, "appearance": { "theme": "{{theme}}", "fontScalePercent": 100 } }""";

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void FreshInstall_ShowsTheLockScreenOnly_AndIsCaptured(string theme)
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded(theme));
        using var app = AppUnderTest.LaunchLocked(root);

        Assert.Contains("invite-only", app.Find("UnlockReason").Name, StringComparison.Ordinal);
        Assert.NotNull(app.Find("UnlockSignIn"));
        Assert.False(app.IsShown("UnlockInviteCode")); // the invite box appears only after sign-in, and never in public mode
        Assert.False(IsReachable(app, "nav-home")); // nothing of the app is reachable behind the lock
        _output.WriteLine(app.CaptureWindow($"unlock-{theme.ToLowerInvariant()}"));
    }

    [Fact]
    public void FreshInstall_ShowsNoFirstRunBehindTheLock_AndOffersNoWayToSkipIt()
    {
        using var app = AppUnderTest.LaunchLocked();

        Assert.NotNull(app.Find("UnlockSignIn"));
        Assert.False(IsReachable(app, "FirstRunNext"));
        Assert.False(IsReachable(app, "FirstRunSkip"));
        Assert.Null(app.TryFind("FirstRunContinueOffline"));
    }

    /// <summary>True when the element is in the tree and can be used. The lock screen covers the rest of the window and switches it off, so a covered element is disabled.</summary>
    private static bool IsReachable(AppUnderTest app, string automationId) => app.TryFind(automationId) is { IsEnabled: true, IsOffscreen: false };
}
