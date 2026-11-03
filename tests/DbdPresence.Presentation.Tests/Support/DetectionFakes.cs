using DbdPresence.Configuration;
using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Online;
using DbdPresence.Core.Roster;
using DbdPresence.Presentation.Detection;
using DbdPresence.Presentation.Presence;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>A signed remote configuration the test controls.</summary>
internal sealed class FakeRemote : IRemoteConfigSource
{
    public static readonly RemoteConfigSnapshot Allowing = new(CaptureForcedOff: false, DetectionForcedOff: false, OnlineFeaturesEnabled: true, [], null, IsFresh: true);

    public event EventHandler? Changed;

    public RemoteConfigSnapshot Current { get; private set; } = Allowing;

    public void Set(RemoteConfigSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A capture source that hands out crops of a drawn screen, like the Windows source does (same region rounding). MECHANICS ONLY.</summary>
internal sealed class FakeCapture(int processId) : IWindowCaptureSource
{
    public int ProcessId { get; } = processId;

    public ImageBuffer? Screen { get; set; }

    public CaptureStatus Status { get; set; } = CaptureStatus.Ready;

    public Exception? ThrowOnCapture { get; set; }

    public int Captures { get; private set; }

    public bool Disposed { get; private set; }

    public CaptureState State => new(Status, "fake capture state", Screen?.Width ?? 0, Screen?.Height ?? 0);

    public Task<CaptureState> StartAsync(CancellationToken cancellationToken) => Task.FromResult(State);

    public Task<CaptureSample> CaptureAsync(IReadOnlyList<NormalizedRegion> regions, CancellationToken cancellationToken)
    {
        Captures++;
        if (ThrowOnCapture is { } failure)
        {
            throw failure;
        }

        if (Status != CaptureStatus.Ready || Screen is null)
        {
            return Task.FromResult(new CaptureSample(State, [], DateTimeOffset.UnixEpoch));
        }

        var full = Screen.ToPixelBuffer();
        var crops = regions.Select(r => full.Crop(r.ToPixels(full.Width, full.Height))).ToArray();
        return Task.FromResult(new CaptureSample(State, crops, DateTimeOffset.UnixEpoch));
    }

    public Task<FullFrameSample> CaptureFullFrameAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("The live loop never asks for a whole frame.");

    public void Dispose() => Disposed = true;
}

/// <summary>The live loop wired to fakes, with a fake clock. Every screen is drawn text on flat colour (mechanics only).</summary>
internal sealed class LoopHarness : IDisposable
{
    public LoopHarness(
        bool detectionOn = true,
        HudProfile? profile = null,
        IReadOnlyDictionary<DetectorId, DetectorSettings>? requested = null,
        ITextRecognizer? recognizer = null,
        AppSettings? settings = null,
        FakeTimeProvider? time = null,
        string? gameVersion = null,
        IUnknownCharacterSink? unknownSink = null)
    {
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.Zero));
        Settings = new FakeSettingsService(settings ?? new AppSettings { Detection = new DetectionSettings { Enabled = detectionOn, RiskAcknowledged = detectionOn } });
        Game = new FakeLifecycleSource();
        Remote = new FakeRemote();
        Ocr = recognizer ?? new BitmapFontOcr();
        Loop = new DetectionLoop(new DetectionLoopParts(
            Settings,
            Remote,
            Game,
            pid =>
            {
                var capture = new FakeCapture(pid) { Screen = Screen };
                Captures.Add(capture);
                return capture;
            },
            Ocr,
            profile ?? HudProfile.Catalogue,
            HudHarness.Lists,
            Time,
            HudHarness.Vocabulary,
            requested,
            GameVersionOf: _ => gameVersion,
            UnknownCharacters: unknownSink));
    }

    public FakeTimeProvider Time { get; }

    public FakeSettingsService Settings { get; }

    public FakeLifecycleSource Game { get; }

    public FakeRemote Remote { get; }

    public ITextRecognizer Ocr { get; }

    public DetectionLoop Loop { get; }

    public List<FakeCapture> Captures { get; } = [];

    /// <summary>Gets or sets the screen the next capture source shows (existing sources keep their own).</summary>
    public ImageBuffer Screen { get; set; } = HudScenes.MainMenu(1280);

    public FakeCapture? Current => Captures.Count == 0 ? null : Captures[^1];

    public void Show(ImageBuffer screen)
    {
        Screen = screen;
        foreach (var capture in Captures)
        {
            capture.Screen = screen;
        }
    }

    public void StartGame() => Game.SetRunning(Time.GetUtcNow());

    /// <summary>Runs one cycle and moves the clock on by a second (the cadence of the profile).</summary>
    public async Task<TimeSpan> CycleAsync(double advanceSeconds = 1.0)
    {
        var delay = await Loop.RunCycleAsync(TestContext.Current.CancellationToken);
        Time.Advance(TimeSpan.FromSeconds(advanceSeconds));
        return delay;
    }

    public async Task CyclesAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await CycleAsync();
        }
    }

    /// <summary>A presence coordinator that uses the loop as its detection feed, publishing into a fake sink.</summary>
    public (PresenceCoordinator Coordinator, FakeSink Sink) Coordinator()
    {
        var sink = new FakeSink();
        var coordinator = new PresenceCoordinator(Settings, new BundledApplicationCatalog(), Game, sink, Time, detection: Loop);
        coordinator.Start();
        return (coordinator, sink);
    }

    public void Dispose() => Loop.Dispose();
}
