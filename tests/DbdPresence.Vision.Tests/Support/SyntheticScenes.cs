using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Tests.Support;

/// <summary>
/// Invented screen layouts (shapes and test glyphs) used to exercise the detector pipeline and the evaluator. They are not DBD screens and
/// are not derived from any game asset. The "calibration" helper learns templates from them the way the owner's calibration wizard will learn
/// them from real captures.
/// </summary>
internal static class SyntheticScenes
{
    /// <summary>The width of the frames the calibration helpers learn from.</summary>
    public const int CalibrationWidth = 640;

    public static readonly NormalizedRect LobbyRegion = new(0.30, 0.74, 0.40, 0.22);
    public static readonly NormalizedRect TrialRegion = new(0.02, 0.74, 0.20, 0.22);
    public static readonly NormalizedRect ResultsRegion = new(0.15, 0.10, 0.70, 0.60);
    public static readonly NormalizedRect SurvivorRegion = new(0.05, 0.05, 0.30, 0.30);
    public static readonly NormalizedRect KillerRegion = new(0.65, 0.05, 0.30, 0.30);

    /// <summary>The part of each region that holds the distinctive element, relative to the region.</summary>
    public static readonly NormalizedRect Anchor = new(0.05, 0.05, 0.90, 0.90);

    public static ImageBuffer Render(Scene scene, int width, int brightness = 0, int seed = 1, int noiseOneIn = 100)
    {
        var height = width * 9 / 16;
        var canvas = new SyntheticCanvas(width, height, 40);
        switch (scene)
        {
            case Scene.Lobby:
                canvas.Rect(0.35, 0.80, 0.30, 0.10, 200).Text(0.40, 0.815, 0.07, "READY", 30);
                break;
            case Scene.InTrial:
                canvas.Ellipse(0.04, 0.80, 0.07, 0.12, 220).Text(0.13, 0.82, 0.08, "5", 230).Text(0.15, 0.82, 0.08, "0", 230);
                break;
            case Scene.Results:
                canvas.Rect(0.20, 0.20, 0.60, 0.08, 150).Rect(0.20, 0.32, 0.60, 0.08, 110).Rect(0.20, 0.44, 0.60, 0.08, 190).Text(0.25, 0.22, 0.04, "KILL", 20);
                break;
            default:
                break;
        }

        return canvas.Disturb(brightness, seed, noiseOneIn).ToImage();
    }

    public static ImageBuffer RenderRole(Role role, int width, int brightness = 0, int seed = 1)
    {
        var height = width * 9 / 16;
        var canvas = new SyntheticCanvas(width, height, 40);
        if (role == Role.Survivor)
        {
            canvas.Ellipse(0.08, 0.08, 0.12, 0.20, 210).Rect(0.22, 0.12, 0.08, 0.04, 160);
        }
        else
        {
            canvas.Rect(0.70, 0.08, 0.20, 0.05, 200).Rect(0.70, 0.17, 0.12, 0.05, 200).Text(0.70, 0.24, 0.05, "KILL", 120);
        }

        return canvas.Disturb(brightness, seed).ToImage();
    }

    /// <summary>Learns a scene profile from one calibration frame per scene (the user's local calibration, in miniature).</summary>
    public static AnchorProfile CalibrateScenes(double minScore = 0.5, double minMargin = 0.2, ThresholdProvenance provenance = ThresholdProvenance.InitialGuess)
    {
        var lobby = Render(Scene.Lobby, CalibrationWidth, seed: 100);
        var trial = Render(Scene.InTrial, CalibrationWidth, seed: 101);
        var results = Render(Scene.Results, CalibrationWidth, seed: 102);
        RegionRequest[] regions = [new("lobby", LobbyRegion), new("trial", TrialRegion), new("results", ResultsRegion)];
        return new AnchorProfile(
            "synthetic-1",
            regions,
            [
                Label("Lobby", "lobby", lobby, LobbyRegion),
                Label("InTrial", "trial", trial, TrialRegion),
                Label("Results", "results", results, ResultsRegion),
            ],
            minScore,
            minMargin,
            provenance);
    }

    public static AnchorProfile CalibrateRoles(double minScore = 0.5, double minMargin = 0.2)
    {
        var survivor = RenderRole(Role.Survivor, CalibrationWidth, seed: 200);
        var killer = RenderRole(Role.Killer, CalibrationWidth, seed: 201);
        RegionRequest[] regions = [new("survivor", SurvivorRegion), new("killer", KillerRegion)];
        return new AnchorProfile(
            "synthetic-roles-1",
            regions,
            [
                Label("Survivor", "survivor", survivor, SurvivorRegion),
                Label("Killer", "killer", killer, KillerRegion),
            ],
            minScore,
            minMargin);
    }

    private static LabelAnchors Label(string label, string regionId, ImageBuffer sampleFrame, NormalizedRect region) =>
        new(label, [new FeatureBinding(regionId, TemplateFeature.FromSample(sampleFrame.Crop(region), Anchor))]);

    public static ContextKey Context(int width = CalibrationWidth, string locale = "en-US") => new("synthetic-1", $"{width}x{width * 9 / 16}", locale, "synthetic");
}
