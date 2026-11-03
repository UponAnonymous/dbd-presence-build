using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DbdPresence.Study;

namespace DbdPresence.Presentation.Labelling;

/// <summary>One recording in the Study page's list, worded for the owner.</summary>
public sealed class RecordingRowViewModel
{
    /// <summary>Initializes a new instance of the <see cref="RecordingRowViewModel"/> class.</summary>
    /// <param name="status">The recording's state.</param>
    public RecordingRowViewModel(RecordingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
        StateText = Describe(status);
        TokensText = status.InputTokens is { } read && status.OutputTokens is { } written
            ? string.Create(CultureInfo.InvariantCulture, $"Last automatic run: {read:N0} tokens read, {written:N0} written")
            : null;
        SkippedText = status.SkippedPictures > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{status.SkippedPictures} {(status.SkippedPictures == 1 ? "picture" : "pictures")} could not be sent")
            : null;
    }

    /// <summary>Gets the recording's state as the service reported it.</summary>
    public RecordingStatus Status { get; }

    /// <summary>Gets the recording (its folder name, which is the start time).</summary>
    public string Id => Status.Id;

    /// <summary>Gets the state in plain words.</summary>
    public string StateText { get; }

    /// <summary>Gets the token counts of the last automatic run, or null when there is none. Never a money figure: the rates are not known.</summary>
    public string? TokensText { get; }

    /// <summary>Gets the line about pictures that could not be sent, or null when none were skipped.</summary>
    public string? SkippedText { get; }

    /// <summary>Gets a value indicating whether the spot-check can be done now.</summary>
    public bool CanSpotCheck => Status.Stage == RecordingStage.SpotCheckReady;

    /// <summary>Gets the name a screen reader announces for the row's spot-check button.</summary>
    public string SpotCheckName => $"Spot-check of {Id}";

    /// <summary>Gets the name a screen reader announces for the row.</summary>
    public string AutomationName => $"Recording {Id}: {StateText}";

    private static string Describe(RecordingStatus s) => s.Stage switch
    {
        RecordingStage.New => "New",
        RecordingStage.ReviewFileMade => "Review file made",
        RecordingStage.SpotCheckReady => (s.Automatic ? "Labelled automatically" : "Labelled by you") + " — spot-check ready (about 2 minutes)",
        RecordingStage.SpotCheckLater => "Labelled in the study tool — spot-check not due yet",
        RecordingStage.SpotCheckDone => "Spot-check done",
        RecordingStage.Disputed => "Disputed — rework it in the study tool",
        _ => "Failed: " + (string.IsNullOrWhiteSpace(s.Failure) ? "the recording could not be labelled." : s.Failure)
            + (s.AutomaticRetryStopped ? " It failed twice in a row, so it is not tried again automatically; “Label new recordings now” tries it again (every try costs tokens)." : string.Empty),
    };
}

/// <summary>One range of pictures in the spot-check. It holds no earlier answer and no suggestion: the choice starts empty.</summary>
public sealed partial class SpotCheckSegmentRowViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="SpotCheckSegmentRowViewModel"/> class.</summary>
    /// <param name="segment">The range.</param>
    /// <param name="number">Its place in the list, from 1.</param>
    /// <param name="count">How many ranges the list has.</param>
    /// <param name="scenes">The scene choices.</param>
    public SpotCheckSegmentRowViewModel(SpotCheckSegment segment, int number, int count, IReadOnlyList<SpotCheckChoice> scenes)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(scenes);
        Segment = segment;
        Scenes = scenes;
        Title = string.Create(CultureInfo.InvariantCulture, $"Screen {number} of {count}, from {segment.StartLocal}, about {Math.Round(segment.Seconds):0} seconds");
    }

    /// <summary>Gets the range.</summary>
    public SpotCheckSegment Segment { get; }

    /// <summary>Gets the heading of the row.</summary>
    public string Title { get; }

    /// <summary>Gets the small pictures of the range.</summary>
    public IReadOnlyList<byte[]> Pictures => Segment.Pictures;

    /// <summary>Gets the scene choices.</summary>
    public IReadOnlyList<SpotCheckChoice> Scenes { get; }

    /// <summary>Gets the name a screen reader announces for the choice.</summary>
    public string ChoiceName => $"Which screen is this? {Title}";

    /// <summary>Gets or sets the chosen scene; null until the owner chooses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Missing))]
    public partial SpotCheckChoice? SelectedScene { get; set; }

    /// <summary>Gets or sets a value indicating whether Save was pressed while this row was not answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Missing))]
    public partial bool Checked { get; set; }

    /// <summary>Gets the words shown beside an unanswered row after a failed save, or null.</summary>
    public string? Missing => Checked && SelectedScene is null ? "Needs an answer" : null;
}

/// <summary>One match in the spot-check. It holds no earlier answer and no suggestion: every choice starts empty (the optional ones start at Skip).</summary>
public sealed partial class SpotCheckMatchRowViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="SpotCheckMatchRowViewModel"/> class.</summary>
    /// <param name="match">The match.</param>
    /// <param name="number">Its place in the list, from 1.</param>
    /// <param name="count">How many matches the list has.</param>
    /// <param name="sheet">The sheet that holds the closed lists.</param>
    public SpotCheckMatchRowViewModel(SpotCheckMatch match, int number, int count, SpotCheckSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(sheet);
        Match = match;
        Roles = sheet.Roles;
        Characters = sheet.Characters;
        Maps = sheet.Maps;
        Results = sheet.Results;
        Killers = sheet.Killers;
        Queues = sheet.Queues;
        Title = string.Create(CultureInfo.InvariantCulture, $"Match {number} of {count}");
        SelectedKiller = Killers.FirstOrDefault(c => c.Value.Length == 0);
        SelectedQueue = Queues.FirstOrDefault(c => c.Value.Length == 0);
    }

    /// <summary>Gets the match.</summary>
    public SpotCheckMatch Match { get; }

    /// <summary>Gets the heading of the row.</summary>
    public string Title { get; }

    /// <summary>Gets the name a screen reader announces for the role choice.</summary>
    public string RoleName => $"{Title}: your role";

    /// <summary>Gets the name a screen reader announces for the character choice.</summary>
    public string CharacterName => $"{Title}: your character";

    /// <summary>Gets the name a screen reader announces for the map choice.</summary>
    public string MapName => $"{Title}: map";

    /// <summary>Gets the name a screen reader announces for the result choice.</summary>
    public string ResultName => $"{Title}: result";

    /// <summary>Gets the name a screen reader announces for the killer choice.</summary>
    public string KillerName => $"{Title}: killer, optional";

    /// <summary>Gets the name a screen reader announces for the queue choice.</summary>
    public string QueueName => $"{Title}: queue, optional";

    /// <summary>Gets the pictures spread over the match.</summary>
    public IReadOnlyList<byte[]> Pictures => Match.Pictures;

    /// <summary>Gets the role choices.</summary>
    public IReadOnlyList<SpotCheckChoice> Roles { get; }

    /// <summary>Gets the character choices.</summary>
    public IReadOnlyList<SpotCheckChoice> Characters { get; }

    /// <summary>Gets the map choices.</summary>
    public IReadOnlyList<SpotCheckChoice> Maps { get; }

    /// <summary>Gets the result choices.</summary>
    public IReadOnlyList<SpotCheckChoice> Results { get; }

    /// <summary>Gets the killer choices.</summary>
    public IReadOnlyList<SpotCheckChoice> Killers { get; }

    /// <summary>Gets the queue choices.</summary>
    public IReadOnlyList<SpotCheckChoice> Queues { get; }

    /// <summary>Gets or sets the chosen role.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResult), nameof(Missing))]
    public partial SpotCheckChoice? SelectedRole { get; set; }

    /// <summary>Gets or sets the chosen character.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Missing))]
    public partial SpotCheckChoice? SelectedCharacter { get; set; }

    /// <summary>Gets or sets the chosen map.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Missing))]
    public partial SpotCheckChoice? SelectedMap { get; set; }

    /// <summary>Gets or sets the chosen result (asked only for a survivor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Missing))]
    public partial SpotCheckChoice? SelectedResult { get; set; }

    /// <summary>Gets or sets the chosen killer (optional).</summary>
    [ObservableProperty]
    public partial SpotCheckChoice? SelectedKiller { get; set; }

    /// <summary>Gets or sets the chosen queue (optional).</summary>
    [ObservableProperty]
    public partial SpotCheckChoice? SelectedQueue { get; set; }

    /// <summary>Gets or sets a value indicating whether Save was pressed while this row was not complete.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Missing))]
    public partial bool Checked { get; set; }

    /// <summary>Gets a value indicating whether the result is asked (only for a survivor).</summary>
    public bool ShowResult => SelectedRole?.Value == "Survivor";

    /// <summary>Gets the words shown beside an incomplete row after a failed save, or null.</summary>
    public string? Missing => Checked && !IsComplete ? "Needs an answer" : null;

    /// <summary>Gets a value indicating whether every required choice is made.</summary>
    public bool IsComplete => SelectedRole is not null && SelectedCharacter is not null && SelectedMap is not null && (!ShowResult || SelectedResult is not null);
}

/// <summary>
/// The blind spot-check of one recording (DETECTION_VALIDATION section 4.2): the rows the existing re-check asks again, with pictures and choices and nothing
/// else. It never shows an earlier answer or a suggestion, and there is no way to see one from here.
/// </summary>
public sealed partial class SpotCheckViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="SpotCheckViewModel"/> class.</summary>
    /// <param name="sheet">The rows.</param>
    public SpotCheckViewModel(SpotCheckSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        Sheet = sheet;
        Segments = [.. sheet.Segments.Select((s, i) => new SpotCheckSegmentRowViewModel(s, i + 1, sheet.Segments.Count, sheet.Scenes))];
        Matches = [.. sheet.Matches.Select((m, i) => new SpotCheckMatchRowViewModel(m, i + 1, sheet.Matches.Count, sheet))];
    }

    /// <summary>Gets the rows.</summary>
    public SpotCheckSheet Sheet { get; }

    /// <summary>Gets the recording.</summary>
    public string RecordingId => Sheet.RecordingId;

    /// <summary>Gets the heading.</summary>
    public string Title => $"Spot-check of {RecordingId}";

    /// <summary>Gets the instruction, in plain words.</summary>
    public string Intro { get; } = "Look at the pictures and choose what you see, without looking at anything else. Nothing here shows what the automatic labelling said, so your answers are your own. It takes about 2 minutes.";

    /// <summary>Gets the screen rows.</summary>
    public ObservableCollection<SpotCheckSegmentRowViewModel> Segments { get; }

    /// <summary>Gets the match rows.</summary>
    public ObservableCollection<SpotCheckMatchRowViewModel> Matches { get; }

    /// <summary>Gets a value indicating whether there are matches to answer.</summary>
    public bool HasMatches => Matches.Count > 0;

    /// <summary>Gets or sets what the last save attempt said, or null.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>
    /// Collects the answers. Returns null, marks the unanswered rows and sets <see cref="Message"/> when something still needs an answer.
    /// </summary>
    /// <returns>The answers, or null.</returns>
    public SpotCheckAnswers? CollectAnswers()
    {
        foreach (var row in Segments)
        {
            row.Checked = true;
        }

        foreach (var row in Matches)
        {
            row.Checked = true;
        }

        var missing = Segments.Count(r => r.SelectedScene is null) + Matches.Count(r => !r.IsComplete);
        if (missing > 0)
        {
            Message = missing == 1
                ? "1 thing still needs an answer (marked “Needs an answer”)."
                : string.Create(CultureInfo.InvariantCulture, $"{missing} things still need an answer (marked “Needs an answer”).");
            return null;
        }

        Message = null;
        return new SpotCheckAnswers(
            [.. Segments.Select(r => new SpotCheckSegmentAnswer(r.Segment.Index, r.SelectedScene!.Value))],
            [.. Matches.Select(r => new SpotCheckMatchAnswer(
                r.Match.StartIndex,
                r.SelectedRole!.Value,
                r.SelectedCharacter!.Value,
                r.SelectedMap!.Value,
                r.ShowResult ? r.SelectedResult!.Value : string.Empty,
                r.SelectedKiller?.Value ?? string.Empty,
                r.SelectedQueue?.Value ?? string.Empty))]);
    }
}
