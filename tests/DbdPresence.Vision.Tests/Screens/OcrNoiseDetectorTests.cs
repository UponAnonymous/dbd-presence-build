// Covers: FR-DET-18 to FR-DET-22 at detector level under recogniser noise (review U5 F5(e)). The fake recogniser used elsewhere is exact; here it is
// wrapped in INVENTED confusions (a letter read as a digit, a dropped letter, every second letter unreadable). They are not measurements of Windows
// OCR: how well it reads the game is UNKNOWN (PENDING-ENV). What is pinned is the safety property: noise may make a frame unreadable, but it must
// never produce a different scene, role, character, map or killer than the clean frame.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class OcrNoiseDetectorTests
{
    private static readonly Dictionary<string, Func<string, string>> s_noises = new()
    {
        // letters read as the digits they resemble (the folding corpus covers l/1/I, O/0, S/5, B/8)
        ["confusables"] = t => t.Replace('O', '0').Replace('I', '1').Replace('S', '5').Replace('B', '8'),

        // one letter lost from every long word
        ["dropThird"] = t => string.Join(' ', t.Split(' ').Select(w => w.Length >= 6 ? w.Remove(2, 1) : w)),

        // every second letter unreadable
        ["garble"] = t => new string([.. t.Select((c, i) => char.IsLetter(c) && i % 2 == 1 ? '?' : c)]),
    };

    private static List<PlayerLine> Rows => HudScenes.SurvivorGame();

    private static readonly Dictionary<string, Func<ImageBuffer>> s_screens = new()
    {
        ["main menu"] = () => HudScenes.MainMenu(1920),
        ["mode list"] = () => HudScenes.ModeList(1920, eventList: false),
        ["event mode list"] = () => HudScenes.ModeList(1920, eventList: true),
        ["lobby"] = () => HudScenes.Lobby(1920),
        ["searching"] = () => HudScenes.Searching(1920),
        ["searching (event)"] = () => HudScenes.Searching(1920, "HAUNTED BY DAYLIGHT"),
        ["pre-match lobby"] = () => HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"),
        ["match start"] = () => HudScenes.MatchStart(1920, "MOUNT ORMOND RESORT", "ORMOND"),
        ["match result"] = () => HudScenes.MatchResult(1920),
        ["scoreboard"] = () => HudScenes.Scoreboard(1920, Rows, 1),
        ["TAB match details"] = () => HudScenes.MatchDetails(1920, Rows, 1, "ORMOND - MOUNT ORMOND RESORT"),
        ["settings"] = () => HudScenes.Settings(1920),
    };

    public static TheoryData<string, string> NoiseOnScreen()
    {
        var data = new TheoryData<string, string>();
        foreach (var noise in s_noises.Keys)
        {
            foreach (var screen in s_screens.Keys)
            {
                data.Add(noise, screen);
            }
        }

        return data;
    }

    private static readonly ObservationKind[] s_kinds =
        [ObservationKind.Scene, ObservationKind.Role, ObservationKind.Character, ObservationKind.Map, ObservationKind.Killer, ObservationKind.QueueMode];

    private static async Task<Dictionary<ObservationKind, string?>> Read(ImageBuffer image, Func<string, string>? noise)
    {
        var h = new HudHarness();
        h.Ocr.Noise = noise;
        var frame = await h.Frame(image);
        return s_kinds.ToDictionary(k => k, k => HudHarness.Of(frame, k)?.Value);
    }

    [Theory]
    [MemberData(nameof(NoiseOnScreen))]
    public async Task Noise_MayMakeAFrameUnreadable_ButNeverChangesWhatIsReported(string noise, string screen)
    {
        var clean = await Read(s_screens[screen](), noise: null);
        var noisy = await Read(s_screens[screen](), s_noises[noise]);

        foreach (var kind in s_kinds)
        {
            // Either the same value as the clean frame, or nothing at all. A different value would be a wrong answer.
            Assert.True(noisy[kind] is null || noisy[kind] == clean[kind], $"{noise} on {screen}: {kind} became '{noisy[kind]}' instead of '{clean[kind]}'");
        }
    }

    [Theory]
    [InlineData("confusables")]
    [InlineData("dropThird")]
    public async Task MildNoise_StillReadsEveryScreen_WithTheSameResult(string noise)
    {
        // A letter read as a digit, or one letter lost from a long word, stays inside the edit tolerances. The only exception is the mode list,
        // whose short entries (KILLER, SURVIVOR) have no tolerance left once a letter is gone; it must then be unreadable, never another screen.
        foreach (var (name, make) in s_screens)
        {
            var clean = await Read(make(), noise: null);
            var noisy = await Read(make(), s_noises[noise]);

            if (noise == "dropThird" && name == "mode list")
            {
                Assert.True(noisy[ObservationKind.Scene] is null || noisy[ObservationKind.Scene] == clean[ObservationKind.Scene]);
                continue;
            }

            Assert.True(clean[ObservationKind.Scene] is not null, $"{name} is readable when clean");
            foreach (var kind in s_kinds)
            {
                Assert.True(noisy[kind] == clean[kind], $"{noise} on {name}: {kind} was '{noisy[kind]}', clean '{clean[kind]}'");
            }
        }
    }

    [Fact]
    public async Task MildNoiseOnTheOwnersRow_StillFindsTheRow_AndTheCharacterAndTheKiller()
    {
        var noisy = await Read(HudScenes.Scoreboard(1920, Rows, 1), s_noises["dropThird"]);

        Assert.Equal("Survivor", noisy[ObservationKind.Role]);
        Assert.Equal("dwight-fairfield", noisy[ObservationKind.Character]);
        Assert.Equal("the-lich", noisy[ObservationKind.Killer]);
    }

    [Fact]
    public async Task HeavyNoise_ReadsNoIdentityAtAll_NotAGuess()
    {
        foreach (var screen in new[] { "scoreboard", "TAB match details", "pre-match lobby", "match start" })
        {
            var noisy = await Read(s_screens[screen](), s_noises["garble"]);

            Assert.Null(noisy[ObservationKind.Role]);
            Assert.Null(noisy[ObservationKind.Character]);
            Assert.Null(noisy[ObservationKind.Map]);
            Assert.Null(noisy[ObservationKind.Killer]);
            Assert.Null(noisy[ObservationKind.Scene]);
        }
    }

    [Fact]
    public async Task ARealmReadAsAnotherRealm_MakesTheMapUnreadable_NotTheOtherRealmsMap()
    {
        // The noise swaps the realm line for another realm's name; the map name is read correctly. Map and realm disagree, so nothing is reported.
        var swapped = await Read(HudScenes.MatchStart(1920, "MOUNT ORMOND RESORT", "ORMOND"), t => t == "ORMOND" ? "COLDWIND FARM" : t);

        Assert.Null(swapped[ObservationKind.Map]);
        Assert.NotEqual("ormond/mount-ormond-resort", swapped[ObservationKind.Map]);
    }
}
