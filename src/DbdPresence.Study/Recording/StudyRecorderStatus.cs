namespace DbdPresence.Study;

/// <summary>Where the study recorder is.</summary>
public enum StudyRecorderState
{
    /// <summary>Never started.</summary>
    Idle,

    /// <summary>Started; waiting for Dead by Daylight to run.</summary>
    WaitingForGame,

    /// <summary>Saving pictures.</summary>
    Recording,

    /// <summary>Ended (see <see cref="StudyRecorderStatus.StopReason"/>); it can be started again.</summary>
    Stopped,
}

/// <summary>What the study recorder is doing now, in values the page turns into plain words.</summary>
/// <param name="State">The state.</param>
/// <param name="Pictures">Pictures saved in this recording.</param>
/// <param name="Bytes">Bytes saved in this recording.</param>
/// <param name="StopReason">Why it ended (<c>game closed</c>, <c>owner stopped</c>, <c>owner pressed Q</c>, <c>owner status lost</c>, <c>2 GB limit reached</c>, <c>disk error</c>, <c>folder error</c>, <c>unexpected error</c>, <c>stopped before the game started</c>, <c>cancelled before it started</c>), else null.</param>
/// <param name="Folder">The recording's folder, once there is one.</param>
/// <param name="ExpiryDate">The local date (yyyy-MM-dd) on which this recording deletes itself, once it has started.</param>
/// <param name="Notes">Plain sentences the recorder wants the owner to read: what the start-up clean-up deleted, anything it could not delete, capture problems, a metadata warning.</param>
public sealed record StudyRecorderStatus(
    StudyRecorderState State,
    int Pictures = 0,
    long Bytes = 0,
    string? StopReason = null,
    string? Folder = null,
    string? ExpiryDate = null,
    IReadOnlyList<string>? Notes = null)
{
    /// <summary>Gets the status of a recorder that was never started.</summary>
    public static StudyRecorderStatus Initial { get; } = new(StudyRecorderState.Idle);

    /// <summary>Gets a value indicating whether the recorder is working (waiting for the game or saving pictures).</summary>
    public bool IsActive => State is StudyRecorderState.WaitingForGame or StudyRecorderState.Recording;
}

/// <summary>The study recorder as the Study page sees it (FR-LAB-06).</summary>
public interface IStudyRecorder
{
    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Gets what the recorder is doing now.</summary>
    StudyRecorderStatus Status { get; }

    /// <summary>Gets the folder that holds the recordings (named in the confirmation).</summary>
    string RootFolder { get; }

    /// <summary>Starts waiting for the game and recording. Does nothing while a recording is already going, or when this account may not record.</summary>
    /// <returns>A task that ends once the recorder is started (not when the recording ends).</returns>
    Task StartAsync();

    /// <summary>Ends the recording, writes its metadata and waits until the files are closed. Does nothing when none is going; never throws.</summary>
    /// <returns>A task that ends when the recorder has stopped.</returns>
    Task StopAsync();
}
