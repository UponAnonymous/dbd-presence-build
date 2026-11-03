using DbdPresence.Study;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>A scripted labelling pipeline for the Study page. It answers at once, unless a run is held.</summary>
internal sealed class FakeWorkbench : IStudyWorkbench
{
    public List<RecordingStatus> Recordings { get; } = [];

    public StudyRunReport Report { get; set; } = StudyRunReport.Nothing;

    public int RunCount { get; private set; }

    public int ListCount { get; private set; }

    /// <summary>Gets or sets a gate that holds a run until it is completed (null: the run ends at once).</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Gets or sets what a run does to the list when it ends.</summary>
    public Action? OnRunEnd { get; set; }

    public IReadOnlyList<string> ProgressSeen => _progress;

    public SpotCheckSheet? Sheet { get; set; }

    public SpotCheckAnswers? Saved { get; private set; }

    public SpotCheckSaveResult SaveResult { get; set; } = new(new SpotCheckVerdict(true, 2, 1.0, 0, []), null);

    private readonly List<string> _progress = [];

    public Task<IReadOnlyList<RecordingStatus>> ListAsync(CancellationToken cancellationToken)
    {
        ListCount++;
        return Task.FromResult<IReadOnlyList<RecordingStatus>>([.. Recordings]);
    }

    /// <summary>Gets the tokens the runs were given (a run is cancelled through its token).</summary>
    public List<CancellationToken> Tokens { get; } = [];

    /// <summary>Gets the <c>manual</c> flag of every run, in order.</summary>
    public List<bool> ManualRuns { get; } = [];

    public async Task<StudyRunReport> LabelNewAsync(bool manual, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        RunCount++;
        Tokens.Add(cancellationToken);
        ManualRuns.Add(manual);
        progress?.Report("Labelling the screens, part 1 of 1.");
        _progress.Add("Labelling the screens, part 1 of 1.");
        if (Hold is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        OnRunEnd?.Invoke();
        return Report;
    }

    /// <summary>Gets or sets an exception that opening a spot-check throws (a state file that cannot be read), or null.</summary>
    public Exception? OpenThrows { get; set; }

    public Task<SpotCheckSheet?> OpenSpotCheckAsync(string recordingId, CancellationToken cancellationToken) =>
        OpenThrows is { } failure ? Task.FromException<SpotCheckSheet?>(failure) : Task.FromResult(Sheet);

    public Task<SpotCheckSaveResult> SaveSpotCheckAsync(string recordingId, SpotCheckAnswers answers, CancellationToken cancellationToken)
    {
        if (SaveResult.Verdict is not null)
        {
            Saved = answers;
        }

        return Task.FromResult(SaveResult);
    }

    public static RecordingStatus Status(string id, RecordingStage stage, bool automatic = true, string? failure = null, long? input = null, long? output = null, int skipped = 0, bool retryStopped = false) =>
        new(id, stage, automatic, failure, input, output, skipped, null, retryStopped);

    public static SpotCheckSheet OneOfEach() => new(
        "2026-10-24_19-30-12",
        [new SpotCheckSegment(0, "19:30:12", 8, [[1, 2, 3]]), new SpotCheckSegment(2, "19:31:00", 6, [[4, 5, 6]])],
        [new SpotCheckMatch(0, [[7, 8, 9]])],
        [new("MainMenu", "Main menu"), new("Lobby", "Lobby, before or after searching")],
        [new("Survivor", "Survivor"), new("Killer", "Killer"), new("?", "Can't tell")],
        [new("?", "Not shown / can't tell"), new("dwight-fairfield", "Dwight Fairfield")],
        [new("?", "Not shown / can't tell"), new("ormond/mount-ormond-resort", "Mount Ormond Resort")],
        [new("Escaped", "Escaped"), new("Dead", "Dead"), new("?", "Can't tell")],
        [new(string.Empty, "Skip"), new("?", "Not shown / can't tell"), new("the-lich", "The Lich")],
        [new(string.Empty, "Skip"), new("?", "Not shown / can't tell"), new("normal", "Normal queue")]);
}
