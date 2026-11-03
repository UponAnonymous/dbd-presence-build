using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Study;

namespace DbdPresence.Presentation.Labelling;

/// <summary>
/// The owner-only recording section of the Study page (FR-LAB-06): Start recording (after a one-time confirmation of what is saved), a status line and Stop recording.
/// It is shown only while <see cref="IsAvailable"/> (the signed-in account is the owner's own game account) and the commands refuse to act otherwise, so a friend's
/// app never records. The recorder is the shared <see cref="IStudyRecorder"/>; it saves to the same folders as the console tool, so labelling reads the recordings unchanged.
/// </summary>
public sealed partial class StudyViewModel
{
    /// <summary>The words shown when Start recording is used while the labelling run is working.</summary>
    public const string LabellingRunningText = "Labelling is working. Start recording after it has finished.";

    /// <summary>The words shown when Start recording is used while the console recorder runs.</summary>
    public const string ConsoleRecorderRunningText = "The console study recorder is running. Close it first; two recorders at once would write over each other.";

    /// <summary>The words shown when Start recording is used before the one-time confirmation.</summary>
    public const string RecordingNotConfirmedText = "Confirm above first: nothing is recorded until you have read what is saved.";

    private string _recordingStatus = "Not recording.";
    private string? _recordingNote;

    /// <summary>Gets the heading of the one-time recording confirmation.</summary>
    public string RecordingConfirmTitle { get; } = "Turn on recording";

    /// <summary>Gets the paragraphs of the one-time recording confirmation: what is saved and that nothing is uploaded, in the console recorder's own words.</summary>
    public IReadOnlyList<string> RecordingConfirmParagraphs =>
    [
        .. StudyRecorderText.WhatItDoes("press Stop recording"),
        .. StudyRecorderText.Where(_recorder?.RootFolder ?? "DBD Presence Study", Environment.GetEnvironmentVariable),
        .. StudyRecorderText.Privacy(StudyRecorderText.ExpiryDate(_time.GetUtcNow())),
    ];

    /// <summary>Gets the text of the button that confirms recording.</summary>
    public string RecordingConfirmButtonText { get; } = "I understand, turn on recording";

    /// <summary>Gets a value indicating whether this build has a recorder and the page exists (the owner's own game account).</summary>
    public bool IsRecordingAvailable => IsAvailable && _recorder is not null && _owner.IsOwner == true;

    /// <summary>Gets a value indicating whether the owner confirmed what recording saves.</summary>
    public bool RecordingConfirmed => _settings.Current.Study.RecordingConfirmed;

    /// <summary>Gets a value indicating whether the recording confirmation panel is shown.</summary>
    public bool IsRecordingConfirmationNeeded => IsRecordingAvailable && !RecordingConfirmed;

    /// <summary>Gets a value indicating whether the Start and Stop buttons are shown.</summary>
    public bool IsRecordingControlsShown => IsRecordingAvailable && RecordingConfirmed;

    /// <summary>Gets a value indicating whether a recording is waiting for the game or saving pictures.</summary>
    public bool IsRecordingActive => _recorder?.Status.IsActive == true;

    /// <summary>Gets a value indicating whether Start recording can act now.</summary>
    public bool CanStartRecording => IsRecordingControlsShown && !IsRecordingActive;

    /// <summary>Gets a value indicating whether Stop recording can act now.</summary>
    public bool CanStopRecording => IsRecordingControlsShown && IsRecordingActive;

    /// <summary>Gets the status line: waiting for the game, recording N pictures, or stopped.</summary>
    public string RecordingStatusText
    {
        get => _recordingStatus;
        private set => SetProperty(ref _recordingStatus, value);
    }

    /// <summary>Gets what the recorder wants the owner to read (what the clean-up deleted, a capture problem), or null.</summary>
    public string? RecordingNote
    {
        get => _recordingNote;
        private set => SetProperty(ref _recordingNote, value);
    }

    /// <summary>Records the owner's one-time confirmation of what recording saves.</summary>
    [RelayCommand]
    public void ConfirmRecording()
    {
        if (!IsRecordingAvailable || RecordingConfirmed)
        {
            return;
        }

        _ = _settings.Update(s => s with { Study = s.Study with { RecordingConfirmed = true } });
    }

    /// <summary>Starts recording: waits for the game, then saves pictures of its window. Refuses for anyone but the owner, before the confirmation and while labelling works.</summary>
    /// <returns>A task that ends once the recorder has started.</returns>
    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    public async Task StartRecordingAsync()
    {
        // Re-read the owner gate itself: the cached page flag is not enough to start something that saves pictures.
        if (_recorder is null || _owner.IsOwner != true || !IsAvailable)
        {
            return;
        }

        if (!RecordingConfirmed)
        {
            Post(() => ProblemText = RecordingNotConfirmedText);
            return;
        }

        if (_consoleRecorderRunning())
        {
            Post(() => ProblemText = ConsoleRecorderRunningText);
            return;
        }

        bool labelling;
        lock (_gate)
        {
            labelling = _busy;
        }

        if (IsRunning || labelling)
        {
            Post(() => ProblemText = LabellingRunningText);
            return;
        }

        if (_recorder.Status.IsActive)
        {
            return;
        }

        Post(() => ProblemText = null);
        await _recorder.StartAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the recording and finishes its files.</summary>
    /// <returns>A task that ends when the recorder has stopped.</returns>
    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    public Task StopRecordingAsync() => _recorder is null ? Task.CompletedTask : _recorder.StopAsync();

    private void OnRecorderChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        var status = _recorder?.Status ?? StudyRecorderStatus.Initial;
        RecordingStatusText = StatusSentence(status);
        var notes = status.Notes ?? [];
        RecordingNote = notes.Count == 0 ? null : string.Join(' ', notes);
        NotifyRecordingChanged();
        if (status.State == StudyRecorderState.Stopped && status.Pictures > 0 && IsAvailable)
        {
            // The new recording appears in the list below, ready for labelling.
            LastWork = RefreshAsync();
        }
    });

    private void NotifyRecordingChanged()
    {
        OnPropertyChanged(nameof(IsRecordingAvailable));
        OnPropertyChanged(nameof(IsRecordingConfirmationNeeded));
        OnPropertyChanged(nameof(IsRecordingControlsShown));
        OnPropertyChanged(nameof(IsRecordingActive));
        OnPropertyChanged(nameof(CanStartRecording));
        OnPropertyChanged(nameof(CanStopRecording));
        StartRecordingCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
    }

    private static string StatusSentence(StudyRecorderStatus status)
    {
        var size = string.Create(CultureInfo.InvariantCulture, $"{status.Pictures} {(status.Pictures == 1 ? "picture" : "pictures")} ({status.Bytes / (1024 * 1024)} MB)");
        return status.State switch
        {
            StudyRecorderState.WaitingForGame => "Waiting for Dead by Daylight to start. Start the game now.",
            StudyRecorderState.Recording => $"Recording: {size} saved. It stops when you close the game or press Stop recording.",
            StudyRecorderState.Stopped => StoppedSentence(status, size),
            _ => "Not recording.",
        };
    }

    private static string StoppedSentence(StudyRecorderStatus status, string size)
    {
        var where = status.Folder is null ? string.Empty : $" Saved in {status.Folder}; it deletes itself on {status.ExpiryDate}.";
        return status.StopReason switch
        {
            "game closed" => $"Stopped: the game was closed. {size} saved.{where}",
            "owner stopped" or "owner pressed Q" => $"Stopped. {size} saved.{where}",
            "owner status lost" => $"Stopped: your account is no longer recognised as the owner's (signed out, or online features are off). {size} saved.{where}",
            "unexpected error" => $"Stopped because of an unexpected error (see the note below). {size} saved.{where}",
            "cancelled before it started" => "Not started.",
            "2 GB limit reached" => $"Stopped: this recording reached the 2 GB limit. {size} saved.{where}",
            "disk error" => $"Stopped: a picture could not be saved (is the disk full?). {size} saved.{where}",
            "folder error" => "Stopped: the folder for the pictures could not be created. Nothing was saved.",
            "stopped before the game started" => "Stopped before the game started. Nothing was saved.",
            _ => $"Stopped. {size} saved.{where}",
        };
    }
}
