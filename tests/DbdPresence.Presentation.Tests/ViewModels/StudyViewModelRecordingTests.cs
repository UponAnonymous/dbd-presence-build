// Covers: FR-LAB-06 (the owner-only Start recording / Stop recording section of the Study page: hidden for everyone but the owner and refused in the view-model,
// the one-time confirmation, the status line, labelling kept away from a recording that is going). Fakes only: no files, no capture, no game.
using DbdPresence.Presentation.Labelling;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Study;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class StudyViewModelRecordingTests : IDisposable
{
    private readonly Harness _host = new();
    private readonly FakeWorkbench _bench = new();
    private readonly OwnerAccountState _owner = new();
    private readonly FakeRecorder _recorder = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }

        _host.Dispose();
    }

    private bool _consoleRunning;

    private StudyViewModel Page(bool owner = true, bool confirmed = true, TimeSpan? stopWait = null)
    {
        if (owner)
        {
            _owner.Note(true);
        }

        if (confirmed)
        {
            _ = _host.Settings.Update(s => s with { Study = s.Study with { RecordingConfirmed = true, LabellingConfirmed = true } });
        }

        var page = new StudyViewModel(_bench, _owner, _host.Settings, _host.Game, _host.Dispatcher, _host.Time, () => _consoleRunning, _recorder) { RecorderStopWait = stopWait ?? TimeSpan.FromSeconds(5) };
        _owned.Add(page);
        return page;
    }

    [Fact]
    public async Task ANonOwner_SeesNoRecordingSection_AndCannotStartEvenWhenConfirmed()
    {
        var page = Page(owner: false);

        await page.StartRecordingAsync();

        Assert.False(page.IsRecordingAvailable);
        Assert.False(page.IsRecordingControlsShown);
        Assert.False(page.IsRecordingConfirmationNeeded);
        Assert.False(page.CanStartRecording);
        Assert.Equal(0, _recorder.Starts);
    }

    [Fact]
    public async Task AnUnknownOwnerState_IsTreatedAsNotTheOwner()
    {
        var page = Page(owner: false);
        _owner.Forget();

        await page.StartRecordingAsync();

        Assert.False(page.IsRecordingAvailable);
        Assert.Equal(0, _recorder.Starts);
    }

    [Fact]
    public async Task TheOwner_MustConfirmOnce_BeforeStarting()
    {
        var page = Page(confirmed: false);
        Assert.True(page.IsRecordingConfirmationNeeded);
        Assert.False(page.IsRecordingControlsShown);
        Assert.Contains(page.RecordingConfirmParagraphs, p => p.Contains("sends nothing anywhere", StringComparison.Ordinal));
        Assert.Contains(page.RecordingConfirmParagraphs, p => p.Contains("other players' names", StringComparison.Ordinal));

        await page.StartRecordingAsync();
        Assert.Equal(0, _recorder.Starts);

        page.ConfirmRecordingCommand.Execute(null);

        Assert.True(page.RecordingConfirmed);
        Assert.True(_host.Settings.Current.Study.RecordingConfirmed);
        Assert.True(page.IsRecordingControlsShown);
        Assert.False(page.IsRecordingConfirmationNeeded);
    }

    [Fact]
    public async Task StartAndStop_DriveTheRecorder_AndTheStatusLineFollowsIt()
    {
        var page = Page();
        Assert.True(page.CanStartRecording);
        Assert.False(page.CanStopRecording);
        Assert.Equal("Not recording.", page.RecordingStatusText);

        await page.StartRecordingAsync();
        Assert.Equal(1, _recorder.Starts);
        Assert.Contains("Waiting for Dead by Daylight", page.RecordingStatusText, StringComparison.Ordinal);
        Assert.False(page.CanStartRecording);
        Assert.True(page.CanStopRecording);

        _recorder.Set(new StudyRecorderStatus(StudyRecorderState.Recording, 3, 5 * 1024 * 1024, Folder: @"C:\Docs\2026-11-02_12-00-00", ExpiryDate: "2026-12-02"));
        Assert.Contains("Recording: 3 pictures (5 MB) saved", page.RecordingStatusText, StringComparison.Ordinal);

        await page.StopRecordingAsync();
        Assert.Equal(1, _recorder.Stops);
        Assert.Contains("Stopped. 3 pictures", page.RecordingStatusText, StringComparison.Ordinal);
        Assert.Contains("2026-12-02", page.RecordingStatusText, StringComparison.Ordinal);
        Assert.True(page.CanStartRecording);
    }

    [Fact]
    public async Task StartingTwice_StartsTheRecorderOnce()
    {
        var page = Page();

        await page.StartRecordingAsync();
        await page.StartRecordingAsync();

        Assert.Equal(1, _recorder.Starts);
    }

    [Fact]
    public async Task WhenTheAccountStopsBeingTheOwners_ARecordingThatIsGoingStops()
    {
        var page = Page();
        await page.StartRecordingAsync();

        _owner.Note(false);
        await page.LastWork;

        Assert.False(page.IsRecordingAvailable);
        Assert.Equal(1, _recorder.Stops);
        Assert.False(_recorder.Status.IsActive);
    }

    [Fact]
    public async Task LabellingIsRefused_WhileTheInAppRecorderIsRecording()
    {
        var page = Page();
        await page.StartRecordingAsync();

        await page.LabelNowAsync();

        Assert.Equal(StudyViewModel.RecorderRunningText, page.ProblemText);
        Assert.Equal(0, _bench.RunCount);
    }

    [Fact]
    public async Task TheConfirmation_NamesTheRealFolder_TheCaps_EveryWayItStops_AndThatLabellingSendsPictures()
    {
        var page = Page(confirmed: false);
        await page.LastWork;

        var text = string.Join('\n', page.RecordingConfirmParagraphs);

        Assert.Contains(_recorder.RootFolder, text, StringComparison.Ordinal);
        Assert.Contains("OneDrive", text, StringComparison.Ordinal);
        Assert.Contains("2 GB", text, StringComparison.Ordinal);
        Assert.Contains("resize", text, StringComparison.Ordinal);
        Assert.Contains("sign out", text, StringComparison.Ordinal);
        Assert.Contains("automatic labelling", text, StringComparison.Ordinal);
        Assert.Contains("other players' names", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartIsRefused_WhileTheConsoleRecorderRuns()
    {
        var page = Page();
        _consoleRunning = true;

        await page.StartRecordingAsync();

        Assert.Equal(0, _recorder.Starts);
        Assert.Equal(StudyViewModel.ConsoleRecorderRunningText, page.ProblemText);
    }

    [Fact]
    public async Task StartIsRefused_WhileALabellingRunIsGoing()
    {
        var page = Page();
        _bench.Hold = new TaskCompletionSource();
        var run = page.LabelNowAsync();

        await page.StartRecordingAsync();

        Assert.Equal(0, _recorder.Starts);
        Assert.Equal(StudyViewModel.LabellingRunningText, page.ProblemText);
        _bench.Hold.SetResult();
        await run;
    }

    [Fact]
    public void ErrorAndLostOwnerStops_AreSaidInPlainWords_NotAsANormalStop()
    {
        var page = Page();

        _recorder.Set(new StudyRecorderStatus(StudyRecorderState.Stopped, 4, 0, "unexpected error", @"C:\x", "2026-12-02", ["The recording stopped because of an unexpected error (InvalidOperationException)."]));
        Assert.Contains("unexpected error", page.RecordingStatusText, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", page.RecordingNote, StringComparison.Ordinal);

        _recorder.Set(new StudyRecorderStatus(StudyRecorderState.Stopped, 4, 0, "owner status lost", @"C:\x", "2026-12-02"));
        Assert.Contains("no longer recognised as the owner's", page.RecordingStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_WaitsOnlyAFewSeconds_ForARecorderThatNeverFinishes_AndStillCleansUp()
    {
        _recorder.StopNeverEnds = true;
        var page = Page(stopWait: TimeSpan.FromMilliseconds(50));
        await page.StartRecordingAsync();

        page.Dispose();

        Assert.Equal(1, _recorder.Stops);
        await page.RefreshAsync();
        Assert.Equal(1, _bench.ListCount);
    }

    [Fact]
    public async Task Dispose_DoesNotThrow_WhenTheRecorderFaults_AndNoListIsReadAfterIt()
    {
        _recorder.StopFaults = true;
        var page = Page();
        await page.StartRecordingAsync();
        var lists = _bench.ListCount;

        page.Dispose();
        _recorder.Set(new StudyRecorderStatus(StudyRecorderState.Stopped, 2, 10, "owner stopped", @"C:\x", "2026-12-02"));

        Assert.Equal(lists, _bench.ListCount);
    }

    private sealed class FakeRecorder : IStudyRecorder
    {
        public string RootFolder => @"D:\Recordings\DBD Presence Study";

        public bool StopNeverEnds { get; set; }

        public bool StopFaults { get; set; }

        public event EventHandler? StatusChanged;

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public StudyRecorderStatus Status { get; private set; } = StudyRecorderStatus.Initial;

        public Task StartAsync()
        {
            Starts++;
            Set(new StudyRecorderStatus(StudyRecorderState.WaitingForGame));
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stops++;
            if (StopNeverEnds)
            {
                return new TaskCompletionSource().Task;
            }

            if (StopFaults)
            {
                return Task.FromException(new InvalidOperationException("stop failed"));
            }

            Set(Status with { State = StudyRecorderState.Stopped, StopReason = "owner stopped" });
            return Task.CompletedTask;
        }

        public void Set(StudyRecorderStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
