// Covers: FR-LAB-06 (the in-app study recorder: start, record, stop; the same folders and files as the console tool so the labelling flow reads them; the 30-day
// clean-up runs when a recording starts; every way a recording can end publishes Stopped with a reason and can be started again; the owner gate inside the recorder;
// metadata written atomically; unreadable recordings reported; unique folders), PLAN_V2 D25, SAFETY_AND_PRIVACY section 3, reviews 2026-11-02-in-app-recording-*.
// A fake picture source and a fake game; the delay is a fake that advances the test clock, so nothing sleeps and no wall clock is read.
using System.Globalization;
using System.Text.Json;
using DbdPresence.Core.Capture;
using DbdPresence.Core.Game;
using DbdPresence.Core.Images;
using DbdPresence.Study;
using DbdPresence.StudyCapture;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class StudyRecorderTests : IDisposable
{
    private static readonly DateTimeOffset s_t0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbd-recorder-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(s_t0);
    private readonly FakeGame _game = new();
    private readonly FakeSource _source;
    private readonly SemaphoreSlim _parked = new(0);
    private readonly SemaphoreSlim _go = new(0);
    private readonly List<string> _warnings = [];
    private TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _mayRecord = true;
    private Func<int, IStudyFrameSource>? _factory;

    public StudyRecorderTests()
    {
        _source = new FakeSource(_time);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private StudyRecorder Recorder(string? root = null)
    {
        var recorder = new StudyRecorder(root ?? _root, _time, () => _game.Current, id => (_factory ?? (_ => _source))(id), "1.2.3", () => _mayRecord, async (span, token) =>
        {
            _time.Advance(span);
            _parked.Release();
            await _go.WaitAsync(token).ConfigureAwait(false);
        }, _warnings.Add);
        recorder.StatusChanged += (_, _) =>
        {
            if (recorder.Status.State == StudyRecorderState.Stopped)
            {
                _ended.TrySetResult();
            }
        };
        return recorder;
    }

    /// <summary>Lets the recorder's loop run one more step and waits until it has parked again (or has ended).</summary>
    private async Task StepAsync()
    {
        _go.Release();
        await Task.WhenAny(_parked.WaitAsync(TestContext.Current.CancellationToken), _ended.Task).ConfigureAwait(false);
    }

    /// <summary>Waits until the recorder has published Stopped (the loop ends by itself in the failure tests).</summary>
    private Task Ended() => _ended.Task.WaitAsync(TestContext.Current.CancellationToken);

    private static JsonDocument MetadataOf(string folder) => JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "metadata.json")));

    [Fact]
    public async Task StartRecordStop_WaitsForTheGame_SavesPicturesEveryTwoSeconds_AndWritesTheMetadata()
    {
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StudyRecorderState.WaitingForGame, recorder.Status.State);
        Assert.Empty(Directory.GetDirectories(_root));

        _game.Run(4242);
        await StepAsync();
        Assert.Equal(StudyRecorderState.Recording, recorder.Status.State);

        // 2 seconds are 8 ticks of 250 ms: pictures at 0 s and 2 s, none between.
        for (var i = 0; i < 9; i++)
        {
            await StepAsync();
        }

        await recorder.StopAsync();

        var status = recorder.Status;
        Assert.Equal(StudyRecorderState.Stopped, status.State);
        Assert.Equal("owner stopped", status.StopReason);
        Assert.Equal(2, status.Pictures);
        Assert.Equal(4242, _game.LastProcessId);
        Assert.True(_source.Disposed);
        var folder = Assert.IsType<string>(status.Folder);
        Assert.Equal(["frame-00001.png", "frame-00002.png"], Directory.GetFiles(folder, "frame-*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var picture in Directory.GetFiles(folder, "frame-*.png"))
        {
            Assert.True(StrictPng.TryValidate(File.ReadAllBytes(picture), out var width, out var height, out var failure), failure);
            Assert.Equal((4, 2), (width, height));
        }

        using var metadata = MetadataOf(folder);
        Assert.Equal(SessionRetention.ToolMarker, metadata.RootElement.GetProperty("Tool").GetString());
        Assert.Equal("1.2.3", metadata.RootElement.GetProperty("ToolVersion").GetString());
        Assert.Equal("owner stopped", metadata.RootElement.GetProperty("StopReason").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("Frames").GetArrayLength());
        Assert.False(File.Exists(Path.Combine(folder, SessionRetention.MetadataTempName)));
    }

    [Fact]
    public async Task StoppingWhileWaitingForTheGame_SavesNothingAndCreatesNoFolder()
    {
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);

        await recorder.StopAsync();

        Assert.Equal(StudyRecorderState.Stopped, recorder.Status.State);
        Assert.Equal("stopped before the game started", recorder.Status.StopReason);
        Assert.Null(recorder.Status.Folder);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task WhenTheGameCloses_TheRecordingEndsWithThatReason()
    {
        _game.Run(7);
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            await StepAsync();
        }

        _game.Close();
        for (var i = 0; i < 9 && recorder.Status.IsActive; i++)
        {
            await StepAsync();
        }

        await recorder.StopAsync();
        Assert.Equal("game closed", recorder.Status.StopReason);
        Assert.False(recorder.Status.IsActive);
        Assert.True(recorder.Status.Pictures >= 1);
    }

    [Fact]
    public async Task TheRecordingsLandWhereTheLabellingFlowReadsThem()
    {
        _game.Run(1);
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        await StepAsync();
        await recorder.StopAsync();

        var folders = StudySessions.RecordingFolders(_root);

        Assert.Equal(recorder.Status.Folder, Assert.Single(folders));
        Assert.Equal(_root, Path.GetDirectoryName(folders[0]));
        Assert.True(DateTime.TryParseExact(Path.GetFileName(folders[0]), SessionRetention.NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    [Fact]
    public async Task StartingARecording_AppliesTheThirtyDayRetention_ToTheRecordersOwnOldSessionsOnly()
    {
        var old = MakeSession("2026-09-01_10-00-00", s_t0.AddDays(-62), SessionRetention.ToolMarker);
        var recent = MakeSession("2026-10-20_10-00-00", s_t0.AddDays(-13), SessionRetention.ToolMarker);
        var foreign = MakeSession("2026-08-01_10-00-00", s_t0.AddDays(-93), "SomethingElse");
        var recorder = Recorder();

        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(foreign));
        Assert.Contains(recorder.Status.Notes ?? [], n => n.Contains("2026-09-01_10-00-00", StringComparison.Ordinal));
        await recorder.StopAsync();
    }

    [Fact]
    public async Task TheCleanUpAndItsReportCome_BeforeTheQuestionIsAsked_AndAnswerNoRecordsNothing()
    {
        var old = MakeSession("2026-09-01_10-00-00", s_t0.AddDays(-62), SessionRetention.ToolMarker);
        var recorder = Recorder();
        var oldGoneWhenAsked = false;
        var noteSeenWhenAsked = false;

        await recorder.StartAsync(() =>
        {
            oldGoneWhenAsked = !Directory.Exists(old);
            noteSeenWhenAsked = (recorder.Status.Notes ?? []).Count > 0;
            return false;
        });

        Assert.True(oldGoneWhenAsked);
        Assert.True(noteSeenWhenAsked);
        Assert.Equal(StudyRecorderState.Stopped, recorder.Status.State);
        Assert.Equal("cancelled before it started", recorder.Status.StopReason);
        Assert.Null(recorder.Status.Folder);
    }

    [Fact]
    public async Task StartingWhileRecording_DoesNothing()
    {
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);

        await recorder.StartAsync();

        Assert.Equal(StudyRecorderState.WaitingForGame, recorder.Status.State);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task TheQKeyReason_IsWrittenIntoTheMetadata()
    {
        _game.Run(3);
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        await StepAsync();

        await recorder.StopAsync("owner pressed Q");

        using var metadata = MetadataOf(Assert.IsType<string>(recorder.Status.Folder));
        Assert.Equal("owner pressed Q", metadata.RootElement.GetProperty("StopReason").GetString());
    }

    // ---- Review findings: stuck states (QA HIGH-1, MEDIUM-1, MEDIUM-2; security M3, L1, L2) ----

    [Fact]
    public async Task WhenTheGameLookupThrows_TheRecorderStopsWithAReasonAndANote_AndCanBeStartedAgain()
    {
        _game.ThrowOnce = true;
        var recorder = Recorder();

        await recorder.StartAsync();
        await Ended();
        await recorder.StopAsync();

        Assert.Equal(StudyRecorderState.Stopped, recorder.Status.State);
        Assert.Equal("unexpected error", recorder.Status.StopReason);
        Assert.Contains(recorder.Status.Notes ?? [], n => n.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.Contains(_warnings, w => w.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));

        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StudyRecorderState.WaitingForGame, recorder.Status.State);
        await recorder.StopAsync();
        Assert.False(recorder.Status.IsActive);
    }

    [Fact]
    public async Task WhenThePictureSourceCannotBeCreated_TheFilesAreClosedWithTheReason_AndStartWorksAgain()
    {
        _game.Run(9);
        _factory = _ => throw new InvalidOperationException("no capture");
        var recorder = Recorder();

        await recorder.StartAsync();
        await Ended();
        await recorder.StopAsync();

        Assert.Equal("unexpected error", recorder.Status.StopReason);
        Assert.Contains(recorder.Status.Notes ?? [], n => n.Contains("unexpected error", StringComparison.Ordinal));
        using (var metadata = MetadataOf(Assert.IsType<string>(recorder.Status.Folder)))
        {
            Assert.Equal("unexpected error", metadata.RootElement.GetProperty("StopReason").GetString());
        }

        _factory = null;
        _ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StudyRecorderState.Recording, recorder.Status.State);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task WhenAPictureCannotBeEncodedOrCaptured_MidRecording_ItIsAnErrorStopNotANormalOne()
    {
        _game.Run(9);
        _source.ThrowOnCapture = true;
        var recorder = Recorder();

        await recorder.StartAsync();
        await Ended();
        await recorder.StopAsync();

        Assert.Equal("unexpected error", recorder.Status.StopReason);
        Assert.Contains(recorder.Status.Notes ?? [], n => n.Contains("unexpected error", StringComparison.Ordinal));
        Assert.True(_source.Disposed);
    }

    [Fact]
    public async Task WhenTheCleanUpThrows_TheStartEndsCleanlyAndTheRecorderIsNotLeftWaiting()
    {
        var recorder = Recorder("bad\0root");

        await recorder.StartAsync();

        Assert.Equal(StudyRecorderState.Stopped, recorder.Status.State);
        Assert.False(recorder.Status.IsActive);
        Assert.Contains(recorder.Status.Notes ?? [], n => n.Contains("clean-up of old recordings failed", StringComparison.Ordinal));
        await recorder.StopAsync();
    }

    [Fact]
    public async Task AHandlerThatStartsAgainWhenTheRecordingStops_GetsACleanRecorderThatCanBeStopped()
    {
        _game.Run(5);
        var recorder = Recorder();
        var restarted = false;
        recorder.StatusChanged += (_, _) =>
        {
            if (recorder.Status.State == StudyRecorderState.Stopped && !restarted)
            {
                restarted = true;
                _ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = recorder.StartAsync();
            }
        };
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        await StepAsync();

        await recorder.StopAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(recorder.Status.IsActive);

        await recorder.StopAsync();

        Assert.True(restarted);
        Assert.False(recorder.Status.IsActive);
        Assert.Equal(StudyRecorderState.Stopped, recorder.Status.State);
    }

    [Fact]
    public async Task ADiskThatRefusesAPicture_EndsWithDiskError_AndANote_AndStartWorksAgain()
    {
        _game.Run(2);
        var recorder = Recorder();
        _source.BeforeCapture = () => Directory.CreateDirectory(Path.Combine(Assert.IsType<string>(recorder.Status.Folder), "frame-00001.png"));

        await recorder.StartAsync();
        await Ended();
        await recorder.StopAsync();

        Assert.Equal("disk error", recorder.Status.StopReason);
        Assert.Contains(recorder.Status.Notes ?? [], n => n.Contains("Could not save a picture", StringComparison.Ordinal));
        _source.BeforeCapture = null;
        await recorder.StartAsync();
        Assert.True(recorder.Status.IsActive);
        await recorder.StopAsync();
    }

    // ---- The owner gate inside the recorder (security M4) ----

    [Fact]
    public async Task ARecorderThatMayNotRecord_RefusesToStart_AndWritesNothing()
    {
        _mayRecord = false;
        var recorder = Recorder();

        await recorder.StartAsync();

        Assert.Equal(StudyRecorderState.Idle, recorder.Status.State);
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.Contains(_warnings, w => w.Contains("may not record", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheAccountMayNoLongerRecord_MidRecording_ItEndsWithThatReasonInTheMetadata()
    {
        _game.Run(2);
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        await StepAsync();

        _mayRecord = false;
        await StepAsync();

        Assert.Equal("owner status lost", recorder.Status.StopReason);
        using var metadata = MetadataOf(Assert.IsType<string>(recorder.Status.Folder));
        Assert.Equal("owner status lost", metadata.RootElement.GetProperty("StopReason").GetString());
    }

    [Fact]
    public async Task WhenTheAccountMayNoLongerRecord_WhileWaitingForTheGame_NoFolderIsMade()
    {
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);

        _mayRecord = false;
        _game.Run(4);
        _go.Release();
        await Ended();

        Assert.Equal("owner status lost", recorder.Status.StopReason);
        Assert.Null(recorder.Status.Folder);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task WhenTheAccountMayNoLongerRecord_WhileAPictureIsTaken_ThatPictureIsNotSaved()
    {
        _game.Run(2);
        var recorder = Recorder();
        _source.BeforeCapture = () => _mayRecord = false;

        await recorder.StartAsync();
        await Ended();

        Assert.Equal("owner status lost", recorder.Status.StopReason);
        var folder = Assert.IsType<string>(recorder.Status.Folder);
        Assert.Empty(Directory.GetFiles(folder, "frame-*.png"));
        using var metadata = MetadataOf(folder);
        Assert.Equal(0, metadata.RootElement.GetProperty("Frames").GetArrayLength());
    }

    // ---- Pictures: a resize makes one extra picture ----

    [Fact]
    public async Task AWindowResize_MakesOneExtraPictureOnceTheNewSizeHasSettled()
    {
        _game.Run(2);
        var recorder = Recorder();
        await recorder.StartAsync();
        await _parked.WaitAsync(TestContext.Current.CancellationToken);
        await StepAsync();
        _source.Size = (6, 2);

        for (var i = 0; i < 6; i++)
        {
            await StepAsync();
        }

        await recorder.StopAsync();

        using var metadata = MetadataOf(Assert.IsType<string>(recorder.Status.Folder));
        var frames = metadata.RootElement.GetProperty("Frames");
        Assert.Equal(2, frames.GetArrayLength());
        Assert.Equal("window-resized", frames[1].GetProperty("Reason").GetString());
    }

    // ---- Files: atomic metadata, unreadable recordings reported, unique folders (security M1, L4; QA LOW-1) ----

    [Fact]
    public void TheMetadata_IsWrittenAsideAndMovedIntoPlace_SoAStaleHalfFileNeverReplacesIt()
    {
        var session = SessionFolder.Create(_root, s_t0);
        File.WriteAllText(Path.Combine(session.Path, SessionRetention.MetadataTempName), "{\"Tool\":\"DbdPre");
        var metadata = new SessionMetadata { StartedUtc = s_t0 };

        Assert.True(session.TryWriteMetadata(metadata, out var problem), problem);

        using var written = MetadataOf(session.Path);
        Assert.Equal(SessionRetention.ToolMarker, written.RootElement.GetProperty("Tool").GetString());
        Assert.False(File.Exists(Path.Combine(session.Path, SessionRetention.MetadataTempName)));
    }

    [Fact]
    public void ARecordingWithPicturesAndTruncatedMissingOrOversizedMetadata_IsReportedAndKept_NotSilentlySkipped()
    {
        var truncated = Path.Combine(_root, "2026-08-01_10-00-00");
        Directory.CreateDirectory(truncated);
        File.WriteAllBytes(Path.Combine(truncated, "frame-00001.png"), [1]);
        File.WriteAllText(Path.Combine(truncated, "metadata.json"), "{\"Tool\":\"DbdPresence.StudyCa");
        var missing = Path.Combine(_root, "2026-08-02_10-00-00");
        Directory.CreateDirectory(missing);
        File.WriteAllBytes(Path.Combine(missing, "frame-00001.png"), [1]);
        var empty = Path.Combine(_root, "2026-08-03_10-00-00");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "metadata.json"), "{");

        var report = SessionRetention.Sweep(_root, _time);

        Assert.Equal(2, report.Problems.Count);
        Assert.Contains(report.Problems, p => p.FolderName == "2026-08-01_10-00-00");
        Assert.Contains(report.Problems, p => p.FolderName == "2026-08-02_10-00-00");
        Assert.True(Directory.Exists(truncated));
        Assert.True(Directory.Exists(missing));
        Assert.True(Directory.Exists(empty));
    }

    [Fact]
    public void TwoRecordingsStartedInTheSameSecond_GetTwoFolders_BothInTheNameFormatTheLabellingFlowReads()
    {
        var first = SessionFolder.Create(_root, s_t0);
        var second = SessionFolder.Create(_root, s_t0);

        Assert.NotEqual(first.Path, second.Path);
        foreach (var session in new[] { first, second })
        {
            Assert.True(session.TryWriteMetadata(new SessionMetadata { StartedUtc = s_t0 }, out _));
        }

        Assert.Equal(2, StudySessions.RecordingFolders(_root).Count);
    }

    // ---- The words (security M2, L8; QA MEDIUM-4) ----

    [Fact]
    public void TheWords_SayWhereThePicturesGo_TheCapsAndEveryWayItStops_AndThatLabellingSendsPictures()
    {
        var text = string.Join('\n', StudyRecorderText.WhatItDoes("press Stop recording"));

        Assert.Contains("automatic labelling", text, StringComparison.Ordinal);
        Assert.Contains("other players' names", text, StringComparison.Ordinal);
        Assert.Contains("2 GB", text, StringComparison.Ordinal);
        Assert.Contains("resize", text, StringComparison.Ordinal);
        Assert.Contains("sign out", text, StringComparison.Ordinal);
        Assert.Contains("press Stop recording", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolderLine_ShowsTheRealPath_AndWarnsWhenItIsInsideOneDrive_ElseStatesThePossibility()
    {
        var inside = StudyRecorderText.Where(Path.Combine(_root, "Docs", "DBD Presence Study"), name => name == "OneDrive" ? _root : null);
        var unknown = StudyRecorderText.Where(Path.Combine(_root, "DBD Presence Study"), _ => null);

        Assert.Contains(Path.Combine(_root, "Docs", "DBD Presence Study"), inside[0], StringComparison.Ordinal);
        Assert.StartsWith("WARNING: this folder is inside OneDrive", inside[1], StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", unknown[1], StringComparison.Ordinal);
        Assert.Contains("OneDrive or another sync service", unknown[1], StringComparison.Ordinal);
    }

    private string MakeSession(string name, DateTimeOffset started, string marker)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(new { Tool = marker, StartedUtc = started }));
        File.WriteAllBytes(Path.Combine(folder, "frame-00001.png"), [1, 2, 3]);
        return folder;
    }

    private sealed class FakeGame
    {
        private GameLifecycle _current = GameLifecycle.Initial;

        public GameLifecycle Current
        {
            get
            {
                if (ThrowOnce)
                {
                    ThrowOnce = false;
                    throw new InvalidOperationException("the process list could not be read");
                }

                return Volatile.Read(ref _current);
            }
        }

        public bool ThrowOnce { get; set; }

        public int LastProcessId { get; private set; }

        public void Run(int pid)
        {
            LastProcessId = pid;
            Volatile.Write(ref _current, new GameLifecycle { State = GameState.Running, Process = new GameProcessInfo(pid, "DeadByDaylight-Win64-Shipping.exe", null) });
        }

        public void Close() => Volatile.Write(ref _current, GameLifecycle.Initial);
    }

    private sealed class FakeSource(TimeProvider time) : IStudyFrameSource
    {
        public bool Disposed { get; private set; }

        public bool ThrowOnCapture { get; set; }

        public Action? BeforeCapture { get; set; }

        public (int Width, int Height) Size { get; set; } = (4, 2);

        public (int Width, int Height) LatestContentSize => Size;

        public Task<FullFrameSample> CaptureFullFrameAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnCapture)
            {
                throw new InvalidOperationException("capture failed");
            }

            BeforeCapture?.Invoke();
            var (width, height) = Size;
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)200);
            var state = new CaptureState(CaptureStatus.Ready, "ready", width, height, 96, WindowDisplayMode.Windowed, BorderOutcome.NotAttempted, ClientAreaOnly: true);
            return Task.FromResult(new FullFrameSample(state, new PixelBuffer(width, height, pixels), time.GetUtcNow()));
        }

        public void Dispose() => Disposed = true;
    }
}
