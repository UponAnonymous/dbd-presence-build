// Covers: FR-DET-18 (the regions of the profile are the ones the owner measured). Review U5 F5(d). The fixtures draw INTO the profile's own regions, so
// a region mis-transcribed from docs/evidence/hud-cue-catalogue.md would be drawn into and read back without any other test noticing. These tests
// pin the profile to the catalogue's table values, which are written out below by hand (not computed from the profile). MECHANICS ONLY: positions of
// text boxes taken from the owner's screenshots; nothing here is accuracy.
using DbdPresence.Vision.Screens;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class HudProfileCatalogueTests
{
    // The profile adds a small margin to every text box ("every text region also carries a small margin: 0.4% vertically, 0.5% sideways, except the
    // lobby columns, which tile their band"), see the note under the detector map of the catalogue.
    private const double PadX = 0.005;
    private const double PadY = 0.004;
    private const double Tolerance = 1e-9;

    /// <summary>Catalogue table values: x0, x1, y0, y1 as fractions of the window, 1920x1080.</summary>
    private static readonly (string Id, double X0, double X1, double Y0, double Y1)[] s_boxes =
    [
        // States table. Where two states give different extents for one region, the union is the region.
        (HudRegionIds.Escape, 0.05, 0.13, 0.91, 0.95), // main menu: bottom-left QUIT [ESC]
        (HudRegionIds.MenuList, 0.11, 0.27, 0.07, 0.65), // main menu 0.08-0.65 and the event mode list from 0.07
        (HudRegionIds.Button, 0.74, 0.81, 0.88, 0.93), // lobby 0.74-0.80, searching 0.74-0.81
        (HudRegionIds.SearchTimer, 0.89, 0.96, 0.945, 0.965),
        (HudRegionIds.SearchEventLabel, 0.73, 0.85, 0.845, 0.865),
        (HudRegionIds.StartMap, 0.04, 0.40, 0.81, 0.86),
        (HudRegionIds.StartRealm, 0.04, 0.30, 0.865, 0.895),
        (HudRegionIds.StartTabHint, 0.84, 0.96, 0.06, 0.09),
        (HudRegionIds.SpectateBar, 0.33, 0.67, 0.03, 0.07),
        (HudRegionIds.ResultLabel, 0.04, 0.13, 0.205, 0.225),
        (HudRegionIds.ResultOutcome, 0.04, 0.18, 0.235, 0.285),
        (HudRegionIds.ScoreHeader, 0.05, 0.18, 0.10, 0.16),
        (HudRegionIds.TabTabs, 0.09, 0.29, 0.06, 0.09),
        (HudRegionIds.SettingsTabs, 0.09, 0.50, 0.06, 0.09),
        // "Who the owner is": the player name at the top right of every menu screen.
        (HudRegionIds.ProfileName, 0.80, 0.91, 0.07, 0.10),
        // In trial (killer): "power prompts as text at the bottom centre (seen: VILE DARKNESS, CAST FLIGHT OF THE DAMNED, CAST FLY)", prompts 0.35-0.66, 0.865-0.895.
        (HudRegionIds.PowerPrompt, 0.35, 0.66, 0.865, 0.895),
        // Pre-match lobby (killer): the list at the right.
        (HudRegionIds.KillerList, 0.86, 0.95, 0.27, 0.35),
        // Match details: the catalogue's REALM - MAP box is 0.42-0.58; the profile widens it to the row text column 0.40-0.62 (documented, an initial
        // guess, because the longest seen line is too tight in the catalogue's box).
        (HudRegionIds.TabRealmMap, 0.40, 0.62, 0.81, 0.83),
    ];

    public static TheoryData<string, double, double, double, double> CatalogueBoxes()
    {
        var data = new TheoryData<string, double, double, double, double>();
        foreach (var (id, x0, x1, y0, y1) in s_boxes)
        {
            data.Add(id, x0, x1, y0, y1);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CatalogueBoxes))]
    public void EveryCataloguedBox_IsTheCataloguesTableValue_PlusTheDocumentedMargin(string id, double x0, double x1, double y0, double y1)
    {
        var rect = HudProfile.Catalogue.Region(id)!.Rect;

        Assert.Equal(x0 - PadX, rect.X, Tolerance);
        Assert.Equal(x1 + PadX, rect.X + rect.Width, Tolerance);
        Assert.Equal(y0 - PadY, rect.Y, Tolerance);
        Assert.Equal(y1 + PadY, rect.Y + rect.Height, Tolerance);
    }

    [Fact]
    public void TheTrialHudRows_AreCentredOnTheCataloguesRowPositions_AndStartAtItsLeftEdge()
    {
        // "names 0.097-0.16 at y ~ 0.43, 0.51, 0.59, 0.68" (survivor); the killer's survivors reach 0.19 and the profile reads up to 0.19.
        double[] centres = [0.43, 0.51, 0.59, 0.68];
        for (var i = 0; i < centres.Length; i++)
        {
            var rect = HudProfile.Catalogue.Region(HudRegionIds.HudRow(i))!.Rect;

            Assert.Equal(0.097 - PadX, rect.X, Tolerance);
            Assert.Equal(0.19 + PadX, rect.X + rect.Width, Tolerance);
            Assert.Equal(centres[i], rect.Y + (rect.Height / 2), Tolerance);
        }
    }

    [Fact]
    public void TheMatchDetailsRows_AreCentredOnTheCataloguesPositions_AndSpanTheNameColumn()
    {
        // "names 0.40-0.62 at y ~ 0.186, 0.292, 0.397, 0.503, 0.616"
        double[] centres = [0.186, 0.292, 0.397, 0.503, 0.616];
        for (var i = 0; i < centres.Length; i++)
        {
            var text = HudProfile.Catalogue.Region(HudRegionIds.TabRow(i))!.Rect;
            var band = HudProfile.Catalogue.Region(HudRegionIds.TabBand(i))!.Rect;

            Assert.Equal(0.40 - PadX, text.X, Tolerance);
            Assert.Equal(0.62 + PadX, text.X + text.Width, Tolerance);
            Assert.Equal(centres[i], text.Y + (text.Height / 2), Tolerance);
            Assert.True(band.X <= text.X && band.X + band.Width >= text.X + text.Width, "the colour band must cover the name column");
            Assert.Equal(centres[i], band.Y + (band.Height / 2), Tolerance);
        }
    }

    [Fact]
    public void TheScoreboardRows_AreCentredOnTheCataloguesPositions_AndSpanTheRowColumn()
    {
        // "rows 0.11-0.32 at y ~ 0.247, 0.358, 0.469, 0.580, 0.691"
        double[] centres = [0.247, 0.358, 0.469, 0.580, 0.691];
        for (var i = 0; i < centres.Length; i++)
        {
            var text = HudProfile.Catalogue.Region(HudRegionIds.ScoreRow(i))!.Rect;
            var band = HudProfile.Catalogue.Region(HudRegionIds.ScoreBand(i))!.Rect;

            Assert.Equal(0.11 - PadX, text.X, Tolerance);
            Assert.Equal(0.32 + PadX, text.X + text.Width, Tolerance);
            Assert.Equal(centres[i], text.Y + (text.Height / 2), Tolerance);
            Assert.True(band.X <= text.X && band.X + band.Width >= text.X + text.Width, "the colour band must cover the row column");
            Assert.Equal(centres[i], band.Y + (band.Height / 2), Tolerance);
        }
    }

    [Fact]
    public void ThePreMatchLobbyColumns_TileTheCataloguesBand_WithoutAGapOrAnOverlap()
    {
        // "positions vary (band 0.35-0.90, 0.14-0.24)"
        var columns = Enumerable.Range(0, HudProfile.LobbyColumns).Select(i => HudProfile.Catalogue.Region(HudRegionIds.LobbyPair(i))!.Rect).ToList();

        Assert.Equal(0.35, columns[0].X, Tolerance);
        Assert.Equal(0.90, columns[^1].X + columns[^1].Width, Tolerance);
        for (var i = 1; i < columns.Count; i++)
        {
            Assert.Equal(columns[i - 1].X + columns[i - 1].Width, columns[i].X, Tolerance);
        }

        Assert.All(columns, c =>
        {
            Assert.Equal(0.14 - PadY, c.Y, Tolerance);
            Assert.Equal(0.24 + PadY, c.Y + c.Height, Tolerance);
        });
    }

    [Fact]
    public void TheCatalogueTableAboveCoversEveryTextRegionOfTheProfile()
    {
        // A region added to the profile without a catalogue value to pin it to would escape the tests above.
        var pinned = s_boxes.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

        var unpinned = HudProfile.Catalogue.Regions
            .Where(r => r.ReadsText && !pinned.Contains(r.Id))
            .Select(r => r.Id)
            .Where(id => id != HudRegionIds.HookCounter) // its position is not in the catalogue (UNKNOWN); OwnStatusAndOutcomeDetectorTests pins it as an initial guess
            .Where(id => !id.StartsWith("hud.row.", StringComparison.Ordinal) && !id.StartsWith("tab.row.", StringComparison.Ordinal)
                && !id.StartsWith("score.row.", StringComparison.Ordinal) && !id.StartsWith("lobby.pair.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(unpinned);
    }
}
