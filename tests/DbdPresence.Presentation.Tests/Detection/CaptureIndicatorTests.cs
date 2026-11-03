// Covers: SAFETY_AND_PRIVACY section 5 (since 2026-11-02 the yellow capture border is requested off, so the tray tooltip always says when the game window is read
// or recorded), FR-LIFE-02 (tray), FR-LAB-06 (in-app recording).
using DbdPresence.Presentation.Detection;

namespace DbdPresence.Presentation.Tests.Detection;

public sealed class CaptureIndicatorTests
{
    [Theory]
    [InlineData(DetectionRunState.Off, false, null)]
    [InlineData(DetectionRunState.SwitchedOffRemotely, false, null)]
    [InlineData(DetectionRunState.WaitingForGame, false, null)]
    [InlineData(DetectionRunState.Problem, false, null)]
    [InlineData(DetectionRunState.Reading, false, CaptureIndicator.Reading)]
    [InlineData(DetectionRunState.WindowUnavailable, false, CaptureIndicator.Reading)]
    [InlineData(DetectionRunState.TextRecognitionUnavailable, false, CaptureIndicator.Reading)]
    [InlineData(DetectionRunState.Off, true, CaptureIndicator.Recording)]
    [InlineData(DetectionRunState.Reading, true, CaptureIndicator.Recording)]
    public void SaysWhenTheGameWindowIsLookedAt_AndRecordingWins(DetectionRunState run, bool recording, string? expected) =>
        Assert.Equal(expected, CaptureIndicator.Words(run, recording));

    [Fact]
    public void TheTooltip_KeepsTheIndicatorWhole_WithinWindowsLimit()
    {
        Assert.Equal("DBD Presence · Discord connected · Playing", CaptureIndicator.Tooltip("DBD Presence · Discord connected · Playing", null));
        Assert.Equal("DBD Presence · Playing · Reading the screen", CaptureIndicator.Tooltip("DBD Presence · Playing", CaptureIndicator.Reading));

        var tooltip = CaptureIndicator.Tooltip(new string('x', 200), CaptureIndicator.Recording);
        Assert.Equal(127, tooltip.Length);
        Assert.EndsWith("… · Recording the game window", tooltip, StringComparison.Ordinal);
        Assert.Equal(127, CaptureIndicator.Tooltip(new string('x', 200), null).Length);
    }
}
