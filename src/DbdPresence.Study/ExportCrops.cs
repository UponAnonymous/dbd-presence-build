using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Study;

/// <summary>A named part of the window that the review file cuts out at full resolution.</summary>
/// <param name="Id">The name used in the file names and in the manifest.</param>
/// <param name="Covers">What on screen it is meant to show, in words for the reviewer.</param>
/// <param name="Rect">Where it is, as fractions of the window.</param>
internal sealed record ExportRegion(string Id, string Covers, NormalizedRect Rect);

/// <summary>
/// The parts of the window the labels depend on (the catalogue in <c>docs/evidence/hud-cue-catalogue.md</c>). Only the <b>positions</b> of the HUD
/// profile are used, to know where to cut; the crops are the pictures themselves, so the reviewer reads the text with their own eyes. No detector, recogniser
/// or reading is involved (an architecture test checks this file and its neighbours).
/// </summary>
internal static class ExportCrops
{
    /// <summary>Extra room around every cut, as a fraction of the window, so a text box that is a little off still fits.</summary>
    private const double Margin = 0.006;

    /// <summary>The id of the cut that shows the owner's own state icon.</summary>
    public const string OwnIconId = "own.icon";

    /// <summary>Returns the regions the review file cuts out of every chosen picture.</summary>
    public static IReadOnlyList<ExportRegion> Regions()
    {
        var profile = HudProfile.Catalogue;
        NormalizedRect Of(params string[] ids) => Union([.. ids.Select(id => (profile.Region(id) ?? throw new InvalidOperationException($"The HUD profile has no region '{id}'.")).Rect)]);

        var tab = Enumerable.Range(0, HudProfile.PlayerRows).SelectMany(i => new[] { HudRegionIds.TabRow(i), HudRegionIds.TabBand(i) }).ToArray();
        var score = Enumerable.Range(0, HudProfile.PlayerRows).SelectMany(i => new[] { HudRegionIds.ScoreRow(i), HudRegionIds.ScoreBand(i) }).ToArray();
        var lobby = Enumerable.Range(0, HudProfile.LobbyColumns).Select(HudRegionIds.LobbyPair).ToArray();
        return
        [
            new("profile.name", "Your player name at the top right of every menu screen", Of(HudRegionIds.ProfileName)),
            new("key.hint", "The key hint at the bottom left (QUIT or BACK)", Of(HudRegionIds.Escape)),
            new("button.timer", "The big button at the bottom right (PLAY, CANCEL or READY) and the search timer under it", Of(HudRegionIds.Button, HudRegionIds.SearchTimer)),
            new("event.label", "The event name above the big button while searching an event queue", Of(HudRegionIds.SearchEventLabel)),
            new("lobby.names", "The pre-match lobby band: character names with player names", Of(lobby)),
            new("start.card", "The match start card at the bottom left: map name over realm name", Of(HudRegionIds.StartMap, HudRegionIds.StartRealm)),
            new("tab.rows", "The rows of TAB match details: role icon, character name, player name", Of(tab)),
            new("tab.realmMap", "The REALM - MAP line under the rows of TAB match details", Of(HudRegionIds.TabRealmMap)),
            new(OwnIconId, "Your state icon in a trial: the small picture left of your name in the first row of the survivor list (your face, or the hook, down or dead icon that replaces it)", Of(HudRegionIds.HudIcon(0))),
            new("result.text", "The MATCH RESULT label and the outcome under it", Of(HudRegionIds.ResultLabel, HudRegionIds.ResultOutcome)),
            new("score.rows", "The scoreboard rows: role icon, character name, player name", Of(score)),

            // The killer's power prompts are not in the HUD profile (no detector reads them); the catalogue gives their place: 0.35-0.66, 0.865-0.895.
            new("killer.prompt", "The killer power prompt text at the bottom centre", Pad(new NormalizedRect(0.35, 0.865, 0.31, 0.03))),
        ];
    }

    /// <summary>Cuts a region out of a full picture. Returns the cut and where it was, in pixels.</summary>
    public static (ImageBuffer Crop, PixelRect Where) Cut(ImageBuffer image, ExportRegion region)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        var where = region.Rect.ToPixels(image.Width, image.Height);
        return (image.Crop(where), where);
    }

    private static NormalizedRect Union(IReadOnlyList<NormalizedRect> rects)
    {
        var x0 = rects.Min(r => r.X);
        var y0 = rects.Min(r => r.Y);
        var x1 = rects.Max(r => r.X + r.Width);
        var y1 = rects.Max(r => r.Y + r.Height);
        return Pad(new NormalizedRect(x0, y0, x1 - x0, y1 - y0));
    }

    private static NormalizedRect Pad(NormalizedRect rect)
    {
        var x0 = Math.Max(0, rect.X - Margin);
        var y0 = Math.Max(0, rect.Y - Margin);
        var x1 = Math.Min(1, rect.X + rect.Width + Margin);
        var y1 = Math.Min(1, rect.Y + rect.Height + Margin);
        return new NormalizedRect(x0, y0, x1 - x0, y1 - y0);
    }
}
