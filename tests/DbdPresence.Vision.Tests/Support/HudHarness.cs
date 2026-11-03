using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Roster;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;
using Microsoft.Extensions.Time.Testing;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Tests.Support;

/// <summary>
/// Feeds synthetic frames through the real pipeline the way the live loop will: the frame is cut into the profile's regions with the capture
/// seam's rounding, the change-gated reader recognises them with the fake recogniser, the six detectors run. Time is a fake clock.
/// </summary>
internal sealed class HudHarness
{
    public static readonly Vocabulary Vocabulary = Vocabulary.LoadEmbedded();
    public static readonly HudVocabulary Lists = HudVocabulary.Create(Vocabulary);
    public static readonly ContextKey Context = new(HudProfile.CatalogueId, "1920x1080", "en-US", "10.2.0");

    public HudHarness(string? ownerName = HudScenes.Owner, ChangeGateOptions? gate = null, HudProfile? profile = null, IPromptObservationSink? sink = null, OwnStatusTemplates? statusTemplates = null, IUnknownCharacterSink? unknownSink = null, HudVocabulary? lists = null, Func<string?>? excludedName = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        Ocr = new BitmapFontOcr();
        Profile = profile ?? HudProfile.Catalogue;
        Pipeline = new HudDetectionPipeline(Profile, lists ?? Lists, Ocr, Time, gate, ownerName, sink, statusTemplates, unknownSink, excludedName);
    }

    public FakeTimeProvider Time { get; }

    public BitmapFontOcr Ocr { get; }

    public HudProfile Profile { get; }

    public HudDetectionPipeline Pipeline { get; }

    /// <summary>Cuts a frame the way the capture adapter does: normalised regions, rounded outwards.</summary>
    public static Dictionary<string, PixelBuffer> Crops(ImageBuffer frame, IEnumerable<RegionRequest> regions)
    {
        var full = frame.ToPixelBuffer();
        return regions.ToDictionary(
            r => r.Id,
            r => full.Crop(new NormalizedRegion(r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height).ToPixels(full.Width, full.Height)),
            StringComparer.Ordinal);
    }

    /// <summary>Processes one frame and advances the clock by <paramref name="advance"/> (one second by default).</summary>
    public async Task<HudFrame> Frame(ImageBuffer frame, Scene prior = Scene.Unknown, ContextKey? context = null, double advance = 1.0, GameRole role = GameRole.None)
    {
        var result = await Pipeline.ProcessAsync(Crops(frame, Pipeline.Regions), frame.Width, frame.Height, context ?? Context, prior, CancellationToken.None, role);
        Time.Advance(TimeSpan.FromSeconds(advance));
        return result;
    }

    public static Observation? Of(HudFrame frame, ObservationKind kind) => frame.Observations.FirstOrDefault(o => o.Kind == kind);
}
