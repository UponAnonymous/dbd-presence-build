// Covers: FR-DET-12 / D30 (the game mode is detected, never guessed), FR-DET-21 (queue mode), DETECTION_VALIDATION section 7.
// The catalogue supports exactly two mode readings: the normal queue (mode list header PLAY) and a limited-time event (header PLAY EVENT with the
// event name below it, or the event name above CANCEL). Entries such as CUSTOM GAME and LEARN TO PLAY are menu items that are only offered, not a
// chosen mode; 2v8, custom game and the tutorial have no screen cue the owner has seen yet (GAME_COVERAGE M3 to M5: UNKNOWN). These tests pin that no
// other mode is ever reported from the cues we do have. MECHANICS ONLY: drawn text read back by a fake recogniser.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class GameModeCoverageTests
{
    private static readonly string[] s_unseenModes = ["custom-game", "2v8", "tutorial", "learn-to-play"];

    private static Observation? Queue(HudFrame f) => HudHarness.Of(f, ObservationKind.QueueMode);

    [Fact]
    public async Task TheNormalModeList_OffersCustomGameAndLearnToPlay_YetReadsNormalOnly()
    {
        var frame = await new HudHarness().Frame(HudScenes.ModeList(1920, eventList: false));

        Assert.Equal(QueueModes.Normal, Queue(frame)!.Value);
        Assert.All(frame.Observations, o => Assert.DoesNotContain(o.Value ?? string.Empty, s_unseenModes));
    }

    [Theory]
    [InlineData("CUSTOM GAME")]
    [InlineData("2V8")]
    [InlineData("LEARN TO PLAY")]
    [InlineData("LIGHTS OUT")]
    [InlineData("MAP SHOWCASE")]
    public async Task ASearchingLabelThatIsNotAnEventInTheClosedList_ReadsNoMode(string label)
    {
        var frame = await new HudHarness().Frame(HudScenes.Searching(1920, label));

        Assert.Null(Queue(frame)?.Value);
    }

    [Theory]
    [InlineData("LIGHTS OUT")]
    [InlineData("2V8")]
    [InlineData("CUSTOM GAME")]
    public async Task AnEventListWhoseSecondLineIsAnUnseenMode_ReadsNoMode(string second)
    {
        // Whether rotating modes appear under PLAY EVENT is UNKNOWN: a name that is not in the closed list of seen events is unreadable, never a mode.
        var frame = await new HudHarness().Frame(HudScenes.ModeList(1920, eventList: true, eventName: second));

        Assert.Null(Queue(frame)?.Value);
    }

    [Fact]
    public async Task ACustomGameHeaderAlone_IsNoModeListAndReadsNothing()
    {
        var canvas = HudScenes.Canvas(1920)
            .TextIn(HudScenes.R(HudRegionIds.Escape), "BACK [ESC]")
            .TextIn(HudScenes.R(HudRegionIds.MenuList), "CUSTOM GAME", "KILLER", "SURVIVOR");

        var frame = await new HudHarness().Frame(canvas.ToImage());

        Assert.Null(Queue(frame));
    }

    [Fact]
    public async Task ASearchingScreenWithNoLabel_IsNeverCalledNormal()
    {
        var frame = await new HudHarness().Frame(HudScenes.Searching(1920));

        Assert.Equal(ScreenKind.Searching, frame.Screen);
        Assert.Null(Queue(frame));
    }
}
