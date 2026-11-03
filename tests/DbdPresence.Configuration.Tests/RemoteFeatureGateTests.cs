// Covers: FR-ONL-08 / SAFETY_AND_PRIVACY §2.3 (remote flags can only take capture and detection away; the user's own opt-in is always needed),
// the dismissed-announcement setting (validated, additive).
using DbdPresence.Core.Online;

namespace DbdPresence.Configuration.Tests;

public sealed class RemoteFeatureGateTests
{
    private static readonly DetectionSettings s_optedIn = new() { Enabled = true, RiskAcknowledged = true };

    private static RemoteConfigSnapshot Remote(bool captureOff = false, bool detectionOff = false) =>
        new(captureOff, detectionOff, true, [], null, true);

    [Fact]
    public void OptedInAndNotForcedOff_AllowsBoth()
    {
        var result = RemoteFeatureGate.For(s_optedIn, Remote());

        Assert.True(result.DetectionAllowed);
        Assert.True(result.CaptureAllowed);
    }

    [Fact]
    public void ForcedCaptureOff_KeepsDetectionButNotCapture()
    {
        var result = RemoteFeatureGate.For(s_optedIn, Remote(captureOff: true));

        Assert.True(result.DetectionAllowed);
        Assert.False(result.CaptureAllowed);
    }

    [Fact]
    public void ForcedDetectionOff_StopsCaptureToo()
    {
        var result = RemoteFeatureGate.For(s_optedIn, Remote(detectionOff: true));

        Assert.False(result.DetectionAllowed);
        Assert.False(result.CaptureAllowed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WithoutTheUsersOwnOptIn_NothingIsAllowed_WhateverTheServiceSays(bool enabled, bool acknowledged)
    {
        var settings = new DetectionSettings { Enabled = enabled, RiskAcknowledged = acknowledged };

        var open = RemoteFeatureGate.For(settings, Remote());

        Assert.False(open.DetectionAllowed);
        Assert.False(open.CaptureAllowed);
    }

    [Fact]
    public void SafeDefaults_AllowNothing()
    {
        var result = RemoteFeatureGate.For(s_optedIn, RemoteConfigSnapshot.SafeDefaults);

        Assert.False(result.DetectionAllowed);
        Assert.False(result.CaptureAllowed);
    }

    [Fact]
    public void NoRemoteSnapshotCanSwitchOnWhatTheUserLeftOff()
    {
        // Every combination of remote flags, with the user's detection off: nothing is ever allowed.
        foreach (var capture in new[] { false, true })
        {
            foreach (var detection in new[] { false, true })
            {
                var result = RemoteFeatureGate.For(new DetectionSettings(), Remote(capture, detection));

                Assert.False(result.DetectionAllowed);
                Assert.False(result.CaptureAllowed);
            }
        }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("0123456789abcdef", "0123456789abcdef")]
    [InlineData("0123456789ABCDEF", "")]
    [InlineData("short", "")]
    [InlineData("0123456789abcdef0", "")]
    [InlineData("zzzzzzzzzzzzzzzz", "")]
    public void DismissedAnnouncement_IsKeptOnlyWhenItLooksLikeAnId(string given, string expected)
    {
        var (settings, issues) = SettingsValidator.Validate(new AppSettings { Lifecycle = new LifecycleSettings { DismissedAnnouncement = given } });

        Assert.Equal(expected, settings.Lifecycle.DismissedAnnouncement);
        Assert.Equal(given.Length != 0 && expected.Length == 0, issues.Any(i => i.Path == "lifecycle.dismissedAnnouncement"));
    }
}
