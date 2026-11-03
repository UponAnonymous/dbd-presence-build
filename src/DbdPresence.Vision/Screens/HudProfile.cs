using DbdPresence.Core.Detection;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Screens;

/// <summary>How a region is used.</summary>
public enum HudRegionUse
{
    /// <summary>The text is read (OCR) and matched against closed lists.</summary>
    Text,

    /// <summary>Only the colours are measured (a highlighted row band); nothing is read.</summary>
    Colour,

    /// <summary>Both: the text is read and the colours are measured.</summary>
    TextAndColour,

    /// <summary>The picture is compared with templates the owner's own captures taught (a state icon); no text is read.</summary>
    Image,
}

/// <summary>One region of the HUD profile.</summary>
/// <param name="Id">Region id (see <see cref="HudRegionIds"/>).</param>
/// <param name="Rect">The region as fractions of the client area.</param>
/// <param name="Use">Whether it is read, measured or both.</param>
/// <param name="Detail">
/// True for regions read only after the cheap anchor regions have shown that the screen needs them (the rows of TAB and the scoreboard, the lobby
/// name pairs, the killer list). The anchor regions are read on every changed frame, the detail regions only when their screen is up.
/// </param>
public sealed record HudRegion(string Id, NormalizedRect Rect, HudRegionUse Use, bool Detail = false)
{
    /// <summary>Gets a value indicating whether the region's text is read.</summary>
    public bool ReadsText => Use is HudRegionUse.Text or HudRegionUse.TextAndColour;

    /// <summary>Gets a value indicating whether the region's colours are measured.</summary>
    public bool MeasuresColour => Use is HudRegionUse.Colour or HudRegionUse.TextAndColour;

    /// <summary>Gets a value indicating whether the region's picture is handed to the detectors (colour measurement or template matching).</summary>
    public bool ProvidesImage => MeasuresColour || Use == HudRegionUse.Image;
}

/// <summary>The ids of the regions of <see cref="HudProfile.Catalogue"/>. Rows are numbered from 0, top to bottom.</summary>
public static class HudRegionIds
{
    /// <summary>Bottom-left key hint: <c>QUIT [ESC]</c> on the main menu, <c>BACK [ESC]</c> on the other menus.</summary>
    public const string Escape = "corner.escape";

    /// <summary>The menu list on the left: main menu entries, or the mode list.</summary>
    public const string MenuList = "menu.list";

    /// <summary>The big button at the bottom right: <c>PLAY</c>, <c>CANCEL</c> or <c>READY</c>.</summary>
    public const string Button = "lobby.button";

    /// <summary>The elapsed time and <c>EST.</c> estimate under the button while searching.</summary>
    public const string SearchTimer = "search.timer";

    /// <summary>The event name above the button while searching an event queue.</summary>
    public const string SearchEventLabel = "search.eventLabel";

    /// <summary>The map name on the match start card.</summary>
    public const string StartMap = "start.map";

    /// <summary>The realm name under the map name on the match start card.</summary>
    public const string StartRealm = "start.realm";

    /// <summary>The <c>TAB MATCH DETAILS</c> hint at the top right.</summary>
    public const string StartTabHint = "start.tabHint";

    /// <summary>The spectator bar at the top.</summary>
    public const string SpectateBar = "spectate.bar";

    /// <summary>The <c>MATCH RESULT</c> label.</summary>
    public const string ResultLabel = "result.label";

    /// <summary>The outcome under the label.</summary>
    public const string ResultOutcome = "result.outcome";

    /// <summary>The scoreboard header (<c>MATCH</c> over <c>SCOREBOARD</c>).</summary>
    public const string ScoreHeader = "score.header";

    /// <summary>The tabs of TAB match details.</summary>
    public const string TabTabs = "tab.tabs";

    /// <summary>The <c>REALM - MAP</c> line of TAB match details.</summary>
    public const string TabRealmMap = "tab.realmMap";

    /// <summary>The tabs of the settings menu.</summary>
    public const string SettingsTabs = "settings.tabs";

    /// <summary>The owner's player name at the top right of every menu screen.</summary>
    public const string ProfileName = "menu.profileName";

    /// <summary>The power prompts at the bottom centre of the trial HUD (for example <c>VILE DARKNESS</c>), read only while the trial HUD is up.</summary>
    public const string PowerPrompt = "trial.prompt";

    /// <summary>The list at the right of the killer's pre-match lobby (own name with the red killer icon first).</summary>
    public const string KillerList = "lobby.killerList";

    /// <summary>
    /// The hook counter of the killer's own trial HUD (FR-AUTO-07), read only while the owner plays the killer. The catalogue says only "hook and generator
    /// counters below" the survivors list, so the position is an initial guess, UNKNOWN until calibrated on the owner's captures, and so is which of the
    /// two counters this is.
    /// </summary>
    public const string HookCounter = "hud.hooks";

    /// <summary>Gets the id of a left-column trial HUD row.</summary>
    public static string HudRow(int index) => $"hud.row.{index}";

    /// <summary>Gets the id of the icon cell left of a trial HUD row: the portrait, or the hooked, dying or dead icon that replaces it (FR-AUTO-03).</summary>
    public static string HudIcon(int index) => $"hud.icon.{index}";

    /// <summary>Gets the id of a pre-match lobby column holding character and player names.</summary>
    public static string LobbyPair(int index) => $"lobby.pair.{index}";

    /// <summary>Gets the id of the text of a TAB match details row.</summary>
    public static string TabRow(int index) => $"tab.row.{index}";

    /// <summary>Gets the id of the colour band of a TAB match details row.</summary>
    public static string TabBand(int index) => $"tab.band.{index}";

    /// <summary>Gets the id of the text of a scoreboard row.</summary>
    public static string ScoreRow(int index) => $"score.row.{index}";

    /// <summary>Gets the id of the colour band of a scoreboard row.</summary>
    public static string ScoreBand(int index) => $"score.band.{index}";
}

/// <summary>
/// A hue range with minimum saturation and value, in degrees (0 to 360) and fractions. When <paramref name="HueFrom"/> is larger than
/// <paramref name="HueTo"/> the range wraps through 0 (red).
/// </summary>
/// <param name="HueFrom">Start of the hue range in degrees.</param>
/// <param name="HueTo">End of the hue range in degrees.</param>
/// <param name="MinSaturation">Lowest HSV saturation, 0 to 1.</param>
/// <param name="MinValue">Lowest HSV value (brightness), 0 to 1.</param>
public sealed record HueBand(double HueFrom, double HueTo, double MinSaturation, double MinValue)
{
    /// <summary>Throws when the band is unusable.</summary>
    public void Validate()
    {
        if (!double.IsFinite(HueFrom) || !double.IsFinite(HueTo) || HueFrom is < 0 or > 360 || HueTo is < 0 or > 360
            || MinSaturation is < 0 or > 1 || MinValue is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HueFrom), "Hue must be within 0 to 360 and saturation and value within 0 to 1.");
        }
    }
}

/// <summary>
/// The colour test for the owner's highlighted row (gold as survivor, the red killer row as killer). The share of pixels of the row band inside a
/// hue band must reach <see cref="MinFraction"/>. <b>Every value is UNKNOWN until calibrated</b> on the owner's captures; the catalogue only says
/// "gold" and "red".
/// </summary>
/// <param name="Gold">The survivor highlight.</param>
/// <param name="Red">The killer row.</param>
/// <param name="MinFraction">The smallest share of band pixels that counts as highlighted.</param>
/// <param name="FullFraction">The share at which the highlight scores 1.</param>
/// <param name="IconMinFraction">The smallest share of the icon column beside the first line of the killer list that must be red for the killer icon (a small shape next to a name) to count.</param>
/// <param name="IconFullFraction">The share at which the killer icon scores 1.</param>
/// <param name="IconColumnWidth">The width of the icon column, as a fraction of the killer list region's width, measured from its left edge. The icon sits left of the name.</param>
public sealed record RowHighlightSettings(HueBand Gold, HueBand Red, double MinFraction, double FullFraction, double IconMinFraction = 0.10, double IconFullFraction = 0.40, double IconColumnWidth = 0.30)
{
    /// <summary>Gets the initial guess (provenance <see cref="ThresholdProvenance.InitialGuess"/>): a gold around 45 degrees and a red around 0 degrees.</summary>
    public static RowHighlightSettings InitialGuess { get; } = new(new HueBand(35, 58, 0.40, 0.40), new HueBand(345, 15, 0.50, 0.28), 0.12, 0.30);

    /// <summary>Throws when the settings are unusable.</summary>
    public void Validate()
    {
        Gold.Validate();
        Red.Validate();
        if (MinFraction is <= 0 or > 1 || FullFraction <= MinFraction || FullFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinFraction), "Require 0 < MinFraction < FullFraction <= 1.");
        }

        if (IconMinFraction is <= 0 or > 1 || IconFullFraction <= IconMinFraction || IconFullFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(IconMinFraction), "Require 0 < IconMinFraction < IconFullFraction <= 1.");
        }

        if (!double.IsFinite(IconColumnWidth) || IconColumnWidth is <= 0 or > 0.6)
        {
            throw new ArgumentOutOfRangeException(nameof(IconColumnWidth), "Require 0 < IconColumnWidth <= 0.6.");
        }
    }
}

/// <summary>Thresholds of the text and score rules of the HUD detectors. Initial guesses until measured.</summary>
/// <param name="MaxEditFraction">An anchor word is read when its edit distance (after folding OCR confusions) is at most this share of its length; 0.20 allows 0 edits in 4 letters and 1 in 5.</param>
/// <param name="OwnerNameMaxEditFraction">The same for the owner's player name.</param>
/// <param name="MinOwnerNameLength">Shorter owner names are never used to find the owner's row (they would match too much).</param>
/// <param name="MinScore">A scene reading needs at least this score, otherwise the frame is unreadable.</param>
/// <param name="MinMargin">The best scene must lead the best other scene by this much, otherwise the frame is unreadable.</param>
/// <param name="WeakCueScore">The score of a scene found only from the HUD player column (the trial HUD and the spectator view): these are the weakest cues in the catalogue, so they score above the score floor but below the engine's <c>TrialEntryFromMenuMinScore</c> (0.80) and below every two-cue screen.</param>
/// <param name="OwnNameBonusScore">The score of the trial HUD when the first row also carries the owner's name. It stays below <paramref name="SingleCueCap"/> (a lone <c>READY</c> button) and below the engine's menu-to-trial floor: a name in a free-text column is still a weak cue.</param>
/// <param name="SingleCueCap">The most a single-cue screen (the pre-match lobby's <c>READY</c> button alone) can score.</param>
/// <param name="ResultLabelOnlyScore">The score of the result screen when the label is read but the outcome is not in the closed list (on-screen wording is UNKNOWN for all outcomes but <c>Escaped</c>).</param>
/// <param name="MinHudRows">How many of the two lower HUD name rows must hold name-like text for the HUD cue.</param>
/// <param name="MinNameLetters">The fewest letters a row text needs to count as name-like.</param>
/// <param name="PromptAgreementCap">The most the killer read from power prompts can score. Prompts are free text at the bottom of the HUD, so even an agreement of two prompts stays below the strong cues (an initial guess).</param>
/// <param name="StatusMinScore">The lowest correlation with a learned template for the own-row icon to be read as that class (an initial guess).</param>
/// <param name="StatusMinMargin">How far the best class must lead the second class in correlation, otherwise the icon is unreadable (an initial guess).</param>
public sealed record HudThresholds(
    double MaxEditFraction = 0.20,
    double OwnerNameMaxEditFraction = 0.15,
    int MinOwnerNameLength = 3,
    double MinScore = 0.60,
    double MinMargin = 0.15,
    double WeakCueScore = 0.65,
    double OwnNameBonusScore = 0.75,
    double SingleCueCap = 0.85,
    double ResultLabelOnlyScore = 0.70,
    int MinHudRows = 2,
    int MinNameLetters = 3,
    double PromptAgreementCap = 0.85,
    double StatusMinScore = 0.80,
    double StatusMinMargin = 0.15)
{
    /// <summary>Throws when a threshold is unusable.</summary>
    public void Validate()
    {
        double[] fractions = [MaxEditFraction, OwnerNameMaxEditFraction, MinScore, MinMargin, WeakCueScore, OwnNameBonusScore, SingleCueCap, ResultLabelOnlyScore, PromptAgreementCap, StatusMinScore, StatusMinMargin];
        if (fractions.Any(f => !double.IsFinite(f) || f is < 0 or > 1) || MaxEditFraction >= 0.5 || OwnerNameMaxEditFraction >= 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEditFraction), "Fractions must lie within [0, 1] and edit fractions below 0.5.");
        }

        if (MinScore > WeakCueScore || WeakCueScore > OwnNameBonusScore || OwnNameBonusScore >= SingleCueCap)
        {
            throw new ArgumentOutOfRangeException(nameof(WeakCueScore), "Require MinScore <= WeakCueScore <= OwnNameBonusScore < SingleCueCap: a weak cue must never outrank a button.");
        }

        if (MinOwnerNameLength < 2 || MinHudRows < 1 || MinHudRows > 2 || MinNameLetters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinOwnerNameLength));
        }
    }
}

/// <summary>
/// How often the live loop looks at the game window and how much of its time it may spend doing so. These are profile values (FR-DET-25): every
/// figure is an <b>initial guess</b> until a Windows run on the owner's hardware measures it, like the other profile values. The busy-time budget is the
/// share of wall time the loop may spend inside one cycle (capture, cropping and text recognition); it is a proxy that keeps the loop cheap, not a
/// measured CPU percentage (the CPU cost of a running loop is PENDING-ENV).
/// </summary>
/// <param name="MenuInterval">The time between cycles while a menu, the lobby, the search or no scene is on screen.</param>
/// <param name="TrialInterval">The time between cycles while a trial (or the menu laid over it, or spectating) is on screen.</param>
/// <param name="MinInterval">The shortest time between two cycles, whatever else is configured.</param>
/// <param name="LowImpactFactor">How many times longer the interval is when the user chose low-impact mode (it halves sampling).</param>
/// <param name="BusyBudget">The largest share of wall time one cycle may take: after a slow cycle the loop waits long enough to stay inside it.</param>
public sealed record HudSampling(TimeSpan MenuInterval, TimeSpan TrialInterval, TimeSpan MinInterval, int LowImpactFactor = 2, double BusyBudget = 0.15)
{
    /// <summary>Gets the initial guess: one cycle a second in menus and in trials, never faster than every 500 ms, half the rate in low-impact mode, at most 15% busy.</summary>
    public static HudSampling InitialGuess { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));

    /// <summary>Throws when a value is unusable.</summary>
    public void Validate()
    {
        if (MinInterval <= TimeSpan.Zero || MenuInterval < MinInterval || TrialInterval < MinInterval || MenuInterval > TimeSpan.FromSeconds(30) || TrialInterval > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(MinInterval), "Require 0 < MinInterval <= MenuInterval, TrialInterval <= 30 s.");
        }

        if (LowImpactFactor is < 1 or > 8 || !double.IsFinite(BusyBudget) || BusyBudget is <= 0.01 or > 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(BusyBudget), "Require 1 <= LowImpactFactor <= 8 and 0.01 < BusyBudget <= 0.5.");
        }
    }
}

/// <summary>
/// Where the text anchors of the HUD sit and which thresholds apply (<c>docs/evidence/hud-cue-catalogue.md</c>). The regions are fractions of the
/// window measured by the owner on <b>1920x1080 at the owner's UI and HUD scale</b>; other resolutions with the same aspect ratio use the same
/// fractions, other aspect ratios and UI scales are UNKNOWN, and a frame whose aspect differs is read as unreadable rather than guessed. No game
/// asset is stored here: the profile holds positions, closed word lists live in the vocabulary, and the owner's own calibration supplies colours.
/// </summary>
public sealed class HudProfile
{
    /// <summary>The number of rows in TAB match details and on the scoreboard (one killer and four survivors).</summary>
    public const int PlayerRows = 5;

    /// <summary>The number of left-column trial HUD name rows read.</summary>
    public const int HudRows = 4;

    /// <summary>The number of columns the pre-match lobby band is cut into.</summary>
    public const int LobbyColumns = 4;

    /// <summary>The profile id of <see cref="Catalogue"/>.</summary>
    public const string CatalogueId = "hud-catalogue-2026-10-24-1080p";

    private static readonly double[] s_hudRowCentres = [0.43, 0.51, 0.59, 0.68];
    private static readonly double[] s_tabRowCentres = [0.186, 0.292, 0.397, 0.503, 0.616];
    private static readonly double[] s_scoreRowCentres = [0.247, 0.358, 0.469, 0.580, 0.691];

    private readonly Dictionary<string, HudRegion> _byId;

    /// <summary>Initializes a new instance of the <see cref="HudProfile"/> class.</summary>
    /// <param name="profileId">The id copied into observation contexts.</param>
    /// <param name="provenance">Whether the thresholds were measured. The catalogue profile is <see cref="ThresholdProvenance.InitialGuess"/>.</param>
    /// <param name="referenceWidth">Width the regions were measured on.</param>
    /// <param name="referenceHeight">Height the regions were measured on.</param>
    /// <param name="aspectTolerance">How far the frame's aspect ratio may differ from the reference, relative.</param>
    /// <param name="regions">All regions.</param>
    /// <param name="thresholds">The text and score thresholds.</param>
    /// <param name="highlight">The colour test for the owner's row.</param>
    /// <param name="sampling">How often the live loop looks and its busy-time budget; null uses <see cref="HudSampling.InitialGuess"/>.</param>
    public HudProfile(
        string profileId,
        ThresholdProvenance provenance,
        int referenceWidth,
        int referenceHeight,
        double aspectTolerance,
        IReadOnlyList<HudRegion> regions,
        HudThresholds thresholds,
        RowHighlightSettings highlight,
        HudSampling? sampling = null)
    {
        ProfileId = profileId;
        Provenance = provenance;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        AspectTolerance = aspectTolerance;
        Regions = regions;
        Thresholds = thresholds;
        Highlight = highlight;
        Sampling = sampling ?? HudSampling.InitialGuess;
        Validate();
        _byId = regions.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    /// <summary>Gets the id copied into observation contexts.</summary>
    public string ProfileId { get; }

    /// <summary>Gets whether the thresholds were measured.</summary>
    public ThresholdProvenance Provenance { get; }

    /// <summary>Gets the width the regions were measured on.</summary>
    public int ReferenceWidth { get; }

    /// <summary>Gets the height the regions were measured on.</summary>
    public int ReferenceHeight { get; }

    /// <summary>Gets how far the frame's aspect ratio may differ from the reference, relative.</summary>
    public double AspectTolerance { get; }

    /// <summary>Gets all regions.</summary>
    public IReadOnlyList<HudRegion> Regions { get; }

    /// <summary>Gets the text and score thresholds.</summary>
    public HudThresholds Thresholds { get; }

    /// <summary>Gets the colour test for the owner's row.</summary>
    public RowHighlightSettings Highlight { get; }

    /// <summary>Gets how often the live loop looks and how much of its time it may use (initial guesses until measured).</summary>
    public HudSampling Sampling { get; }

    /// <summary>Gets the profile built from the owner's screenshots of 2026-10-24 (1920x1080, the owner's UI and HUD scale). All thresholds are initial guesses.</summary>
    public static HudProfile Catalogue { get; } = BuildCatalogue();

    /// <summary>Returns a copy with other thresholds (for calibration and tests).</summary>
    public HudProfile WithThresholds(HudThresholds thresholds) => new(ProfileId, Provenance, ReferenceWidth, ReferenceHeight, AspectTolerance, Regions, thresholds, Highlight, Sampling);

    /// <summary>Returns a copy with another row colour test (for calibration and tests).</summary>
    public HudProfile WithHighlight(RowHighlightSettings highlight) => new(ProfileId, Provenance, ReferenceWidth, ReferenceHeight, AspectTolerance, Regions, Thresholds, highlight, Sampling);

    /// <summary>Returns a copy with other sampling values (for calibration and tests).</summary>
    public HudProfile WithSampling(HudSampling sampling) => new(ProfileId, Provenance, ReferenceWidth, ReferenceHeight, AspectTolerance, Regions, Thresholds, Highlight, sampling);

    /// <summary>
    /// Returns why a detector that reads this profile may not run above <see cref="DetectionTier.Disabled"/> with <paramref name="settings"/>, or null.
    /// A tier above Disabled needs a <see cref="ThresholdProvenance.Measured"/> profile, and that profile must be the one the tier was validated on
    /// (<see cref="DetectorSettings.ValidatedProfileId"/> equal to <see cref="ProfileId"/>), on top of the checks of
    /// <see cref="DetectorSettings.Problem"/>. The catalogue profile is an initial guess, so it can never leave Disabled (review U5 F4).
    /// </summary>
    /// <param name="settings">The settings configured for the detector.</param>
    public string? PublicationProblem(DetectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Tier == DetectionTier.Disabled)
        {
            return null;
        }

        if (Provenance != ThresholdProvenance.Measured)
        {
            return "the HUD profile has initial-guess provenance; only a measured profile may be above Disabled";
        }

        if (string.IsNullOrWhiteSpace(settings.ValidatedProfileId))
        {
            return "the tier names no validated HUD profile";
        }

        return string.Equals(settings.ValidatedProfileId, ProfileId, StringComparison.Ordinal)
            ? settings.Problem()
            : "the tier was validated on another HUD profile";
    }

    /// <summary>Gets a region by id, or null when the profile has none.</summary>
    public HudRegion? Region(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Gets the regions as detector requests.</summary>
    public IReadOnlyList<RegionRequest> Requests(Func<HudRegion, bool>? filter = null) =>
        [.. Regions.Where(r => filter?.Invoke(r) ?? true).Select(r => new RegionRequest(r.Id, r.Rect))];

    /// <summary>Returns whether a frame of this size has the aspect ratio the regions were measured on.</summary>
    public bool AspectMatches(int width, int height) =>
        width > 0 && height > 0 && Math.Abs(((double)width / height / ((double)ReferenceWidth / ReferenceHeight)) - 1.0) <= AspectTolerance;

    /// <summary>Throws when the profile is inconsistent.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        if (ReferenceWidth < 1 || ReferenceHeight < 1 || AspectTolerance is <= 0 or >= 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(ReferenceWidth));
        }

        Thresholds.Validate();
        Highlight.Validate();
        Sampling.Validate();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in Regions)
        {
            if (!ids.Add(region.Id))
            {
                throw new ArgumentException($"Duplicate region id '{region.Id}'.");
            }
        }
    }

    private static HudProfile BuildCatalogue()
    {
        // Text regions get a small margin (an initial guess): the catalogue's boxes are the owner's approximate positions of the text, and a few
        // pixels of slack keep a slightly different font scale inside the box.
        const double Pad = 0.004;
        const double PadX = 0.005;
        var regions = new List<HudRegion>();

        void Text(string id, double x, double y, double w, double h, bool detail = false, bool colour = false, bool padSides = true)
        {
            var y0 = Math.Max(0, y - Pad);
            var y1 = Math.Min(1, y + h + Pad);
            var x0 = padSides ? Math.Max(0, x - PadX) : x;
            var x1 = padSides ? Math.Min(1, x + w + PadX) : x + w;
            regions.Add(new HudRegion(id, new NormalizedRect(x0, y0, x1 - x0, y1 - y0), colour ? HudRegionUse.TextAndColour : HudRegionUse.Text, detail));
        }

        void Band(string id, double x, double y, double w, double h) =>
            regions.Add(new HudRegion(id, new NormalizedRect(x, y, w, h), HudRegionUse.Colour, Detail: true));

        Text(HudRegionIds.Escape, 0.05, 0.91, 0.08, 0.04);
        Text(HudRegionIds.MenuList, 0.11, 0.07, 0.16, 0.58);
        Text(HudRegionIds.Button, 0.74, 0.88, 0.07, 0.05);
        Text(HudRegionIds.SearchTimer, 0.89, 0.945, 0.07, 0.02);
        Text(HudRegionIds.SearchEventLabel, 0.73, 0.845, 0.12, 0.02);
        Text(HudRegionIds.StartMap, 0.04, 0.81, 0.36, 0.05);
        Text(HudRegionIds.StartRealm, 0.04, 0.865, 0.26, 0.03);
        Text(HudRegionIds.StartTabHint, 0.84, 0.06, 0.12, 0.03);
        Text(HudRegionIds.SpectateBar, 0.33, 0.03, 0.34, 0.04);
        Text(HudRegionIds.ResultLabel, 0.04, 0.205, 0.09, 0.02);
        Text(HudRegionIds.ResultOutcome, 0.04, 0.235, 0.14, 0.05);
        Text(HudRegionIds.ScoreHeader, 0.05, 0.10, 0.13, 0.06);
        Text(HudRegionIds.TabTabs, 0.09, 0.06, 0.20, 0.03);
        Text(HudRegionIds.SettingsTabs, 0.09, 0.06, 0.41, 0.03);
        for (var i = 0; i < s_hudRowCentres.Length; i++)
        {
            Text(HudRegionIds.HudRow(i), 0.097, s_hudRowCentres[i] - 0.02, 0.093, 0.04);
        }

        Text(HudRegionIds.ProfileName, 0.80, 0.07, 0.11, 0.03); // an anchor: it also blocks the weak trial cues when it holds unread text
        Text(HudRegionIds.KillerList, 0.86, 0.27, 0.09, 0.08, detail: true, colour: true);
        Text(HudRegionIds.PowerPrompt, 0.35, 0.865, 0.31, 0.03, detail: true); // bottom centre, killer HUD; read only while the trial HUD is up
        regions.Add(new HudRegion(HudRegionIds.HudIcon(0), new NormalizedRect(0.050, s_hudRowCentres[0] - 0.026, 0.040, 0.052), HudRegionUse.Image)); // the cell left of the owner's name; the position is an initial guess
        Text(HudRegionIds.HookCounter, 0.05, 0.74, 0.06, 0.06, detail: true); // below the killer's survivor list; an initial guess, read only while the owner plays the killer
        Text(HudRegionIds.TabRealmMap, 0.40, 0.81, 0.22, 0.02, detail: true); // the catalogue's 0.42-0.58 widened to the row text column, an initial guess
        const double BandX0 = 0.35;
        const double BandWidth = 0.55;
        for (var i = 0; i < LobbyColumns; i++)
        {
            Text(HudRegionIds.LobbyPair(i), BandX0 + (i * BandWidth / LobbyColumns), 0.14, BandWidth / LobbyColumns, 0.10, detail: true, padSides: false); // the columns tile the band, so they are not widened
        }

        for (var i = 0; i < PlayerRows; i++)
        {
            Text(HudRegionIds.TabRow(i), 0.40, s_tabRowCentres[i] - 0.04, 0.22, 0.08, detail: true);
            Band(HudRegionIds.TabBand(i), 0.38, s_tabRowCentres[i] - 0.04, 0.26, 0.08);
            Text(HudRegionIds.ScoreRow(i), 0.11, s_scoreRowCentres[i] - 0.04, 0.21, 0.08, detail: true);
            Band(HudRegionIds.ScoreBand(i), 0.09, s_scoreRowCentres[i] - 0.04, 0.25, 0.08);
        }

        return new HudProfile(CatalogueId, ThresholdProvenance.InitialGuess, 1920, 1080, 0.02, regions, new HudThresholds(), RowHighlightSettings.InitialGuess);
    }
}
