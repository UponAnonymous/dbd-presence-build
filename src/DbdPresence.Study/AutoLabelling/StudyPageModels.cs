namespace DbdPresence.Study;

/// <summary>Where a recording stands, in the order of its life. The Study page words each stage for the owner.</summary>
public enum RecordingStage
{
    /// <summary>The recording has no review file and no labels.</summary>
    New,

    /// <summary>The review file was made; nobody has labelled the recording yet.</summary>
    ReviewFileMade,

    /// <summary>The recording has labels and its blind spot-check is due.</summary>
    SpotCheckReady,

    /// <summary>The recording has labels and its blind spot-check is not due yet (the owner's own labels wait a day).</summary>
    SpotCheckLater,

    /// <summary>The spot-check was done and passed.</summary>
    SpotCheckDone,

    /// <summary>The spot-check was done and the labels are disputed; they are reworked in the study tool.</summary>
    Disputed,

    /// <summary>The last automatic run could not label the recording, or its files could not be read.</summary>
    Failed,
}

/// <summary>One recording of the study folder as the Study page shows it.</summary>
/// <param name="Id">The recording (its folder name).</param>
/// <param name="Stage">Where it stands.</param>
/// <param name="Automatic">Whether its labels came from the automatic run (not from the owner).</param>
/// <param name="Failure">A plain sentence that says what went wrong, or null.</param>
/// <param name="InputTokens">Tokens read in the last automatic run of this recording, or null when it has none.</param>
/// <param name="OutputTokens">Tokens written in that run, or null.</param>
/// <param name="SkippedPictures">How many pictures could not be sent in that run.</param>
/// <param name="SpotCheckDueAt">When the spot-check is or was due, or null when the recording has no labels.</param>
/// <param name="AutomaticRetryStopped">True when the recording failed twice in a row, so the automatic run leaves it alone until the owner asks for it.</param>
public sealed record RecordingStatus(
    string Id,
    RecordingStage Stage,
    bool Automatic,
    string? Failure,
    long? InputTokens,
    long? OutputTokens,
    int SkippedPictures,
    DateTimeOffset? SpotCheckDueAt,
    bool AutomaticRetryStopped = false);

/// <summary>What stopped an automatic run, reduced to what the page tells the owner.</summary>
public enum StudyProblem
{
    /// <summary>Nothing stopped it.</summary>
    None,

    /// <summary>The labelling service is not set up.</summary>
    NotSetUp,

    /// <summary>This account may not label (it is not the owner's own game account).</summary>
    NotAllowed,

    /// <summary>The day's allowance of requests is used up.</summary>
    DailyCap,

    /// <summary>The labelling service stayed busy.</summary>
    Busy,

    /// <summary>The run sent as many requests as one run may and stopped; the next run continues.</summary>
    RunLimit,

    /// <summary>Some other failure; the recording's own line says which.</summary>
    Other,
}

/// <summary>How an automatic run ended.</summary>
/// <param name="Labelled">How many recordings were labelled and imported.</param>
/// <param name="Failed">How many recordings could not be labelled.</param>
/// <param name="ReviewFilesMade">How many review files were made.</param>
/// <param name="Problem">What stopped the run, or <see cref="StudyProblem.None"/>.</param>
/// <param name="Requests">How many labelling requests the run sent.</param>
/// <param name="InputTokens">Tokens read over all requests of the run.</param>
/// <param name="OutputTokens">Tokens written over all requests of the run.</param>
public sealed record StudyRunReport(int Labelled, int Failed, int ReviewFilesMade, StudyProblem Problem, int Requests = 0, long InputTokens = 0, long OutputTokens = 0)
{
    /// <summary>Gets a report for a run that had nothing to do.</summary>
    public static StudyRunReport Nothing { get; } = new(0, 0, 0, StudyProblem.None);
}

/// <summary>A choice in a list on the spot-check page.</summary>
/// <param name="Value">What is saved (a scene, a role, a closed-list id, "?" or empty).</param>
/// <param name="Label">What the owner reads.</param>
public sealed record SpotCheckChoice(string Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>A range of pictures asked again. It carries no earlier answer and no suggestion.</summary>
/// <param name="Index">The range's number in the recording's plan.</param>
/// <param name="StartLocal">The local time of its first picture, as text.</param>
/// <param name="Seconds">How long it lasts.</param>
/// <param name="Pictures">Small PNG pictures spread over the range.</param>
public sealed record SpotCheckSegment(int Index, string StartLocal, double Seconds, IReadOnlyList<byte[]> Pictures);

/// <summary>A match asked again. It carries no earlier answer and no suggestion.</summary>
/// <param name="StartIndex">The range where the match starts.</param>
/// <param name="Pictures">A strip of small PNG pictures spread over the match.</param>
public sealed record SpotCheckMatch(int StartIndex, IReadOnlyList<byte[]> Pictures);

/// <summary>Everything the spot-check page shows for one recording.</summary>
/// <param name="RecordingId">The recording.</param>
/// <param name="Segments">The ranges asked again.</param>
/// <param name="Matches">The matches asked again.</param>
/// <param name="Scenes">The scene choices.</param>
/// <param name="Roles">The role choices.</param>
/// <param name="Characters">The character choices (the closed list).</param>
/// <param name="Maps">The map choices (the closed list).</param>
/// <param name="Results">The result choices (survivors).</param>
/// <param name="Killers">The killer choices (the closed list, optional).</param>
/// <param name="Queues">The queue choices (the closed list, optional).</param>
public sealed record SpotCheckSheet(
    string RecordingId,
    IReadOnlyList<SpotCheckSegment> Segments,
    IReadOnlyList<SpotCheckMatch> Matches,
    IReadOnlyList<SpotCheckChoice> Scenes,
    IReadOnlyList<SpotCheckChoice> Roles,
    IReadOnlyList<SpotCheckChoice> Characters,
    IReadOnlyList<SpotCheckChoice> Maps,
    IReadOnlyList<SpotCheckChoice> Results,
    IReadOnlyList<SpotCheckChoice> Killers,
    IReadOnlyList<SpotCheckChoice> Queues);

/// <summary>The owner's scene answer for one range.</summary>
/// <param name="Index">The range.</param>
/// <param name="Scene">The scene.</param>
public sealed record SpotCheckSegmentAnswer(int Index, string Scene);

/// <summary>The owner's answers for one match. Empty text means not answered.</summary>
/// <param name="StartIndex">The range where the match starts.</param>
/// <param name="Role">Survivor, Killer or "?".</param>
/// <param name="Character">A character id or "?".</param>
/// <param name="Map">A map id or "?".</param>
/// <param name="Result">A result (survivors) or "?".</param>
/// <param name="Killer">A killer id, "?" or empty.</param>
/// <param name="Queue">A queue id, "?" or empty.</param>
public sealed record SpotCheckMatchAnswer(int StartIndex, string Role, string Character, string Map, string Result, string Killer, string Queue);

/// <summary>All answers of one spot-check.</summary>
/// <param name="Segments">One answer per range asked.</param>
/// <param name="Matches">One answer per match asked.</param>
public sealed record SpotCheckAnswers(IReadOnlyList<SpotCheckSegmentAnswer> Segments, IReadOnlyList<SpotCheckMatchAnswer> Matches);

/// <summary>How a spot-check was graded (kappa at least 0.9 and no disagreeing role, character, map, result or killer).</summary>
/// <param name="Passed">Whether the labels were accepted. When false they are disputed.</param>
/// <param name="SampledUnits">How many ranges and matches were asked again.</param>
/// <param name="Kappa">Cohen's kappa over the scene answers, or null when every answer was the same class.</param>
/// <param name="SceneDisagreements">How many scene answers differ.</param>
/// <param name="FactDisagreements">Which facts differ.</param>
public sealed record SpotCheckVerdict(bool Passed, int SampledUnits, double? Kappa, int SceneDisagreements, IReadOnlyList<string> FactDisagreements);

/// <summary>The answer to saving a spot-check: a verdict, or a plain sentence that says why nothing was saved.</summary>
/// <param name="Verdict">The verdict, or null when nothing was saved.</param>
/// <param name="Problem">Why nothing was saved, or null.</param>
public sealed record SpotCheckSaveResult(SpotCheckVerdict? Verdict, string? Problem);

/// <summary>
/// What the owner-only Study page needs from the labelling pipeline: the recordings with their state, the automatic run, and the blind spot-check.
/// The real one is <c>StudyWorkbench</c>; the page's tests use a fake.
/// </summary>
public interface IStudyWorkbench
{
    /// <summary>Lists the recordings of the study folder, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>One entry per recording.</returns>
    Task<IReadOnlyList<RecordingStatus>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Labels every recording that has no labels: makes the review file, has it labelled, takes the labels in. One run at a time is the caller's rule.</summary>
    /// <param name="manual">True when the owner asked for the run: a recording that failed twice in automatic runs is tried again. False for the run after the game closes, which never retries it.</param>
    /// <param name="progress">Told in plain words how far it is, or null.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>How it ended.</returns>
    Task<StudyRunReport> LabelNewAsync(bool manual, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Makes the spot-check rows of a recording whose spot-check is due.</summary>
    /// <param name="recordingId">The recording.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows, or null when the recording has no spot-check due.</returns>
    Task<SpotCheckSheet?> OpenSpotCheckAsync(string recordingId, CancellationToken cancellationToken);

    /// <summary>Grades the answers with the existing rules and writes the result.</summary>
    /// <param name="recordingId">The recording.</param>
    /// <param name="answers">The owner's answers.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The verdict, or why nothing was saved.</returns>
    Task<SpotCheckSaveResult> SaveSpotCheckAsync(string recordingId, SpotCheckAnswers answers, CancellationToken cancellationToken);
}
