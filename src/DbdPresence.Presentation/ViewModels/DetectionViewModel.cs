using CommunityToolkit.Mvvm.ComponentModel;
using DbdPresence.Configuration;
using DbdPresence.Core.Detection;
using DbdPresence.Presentation.Detection;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>One line of the "What the app sees" card.</summary>
/// <param name="Label">What it is (Scene, Role, Character, Map, Mode).</param>
/// <param name="Value">What the app confirmed, or "Not sure yet".</param>
/// <param name="Mark">"Confirmed" (the engine confirmed it from the screen), "Learning" (not confirmed yet) or "You set this" (declared by the user, not seen).</param>
public sealed record DetectionRow(string Label, string Value, string Mark)
{
    /// <summary>The mark of a value the engine confirmed from the screen.</summary>
    public const string ConfirmedMark = "Confirmed";

    /// <summary>The mark of a value that is not confirmed.</summary>
    public const string LearningMark = "Learning";

    /// <summary>The mark of a value the user declared.</summary>
    public const string DeclaredMark = "You set this";

    /// <summary>Gets a value indicating whether the engine confirmed the value from the screen.</summary>
    public bool IsConfirmed => Mark == ConfirmedMark;
}

/// <summary>
/// The "What the app sees" card on Home (in the app only): what the engine has confirmed from the game window, with a confirmed or learning mark for each
/// of scene, role, character, map and mode, the status line of the screen reader, and, when the app can know it, how many matches are recorded for the
/// detection study. It is shown only while the user has screen reading on. Nothing on this card is sent to Discord (the card says so).
/// </summary>
public sealed partial class DetectionViewModel : SettingsPageViewModel
{
    private readonly IDetectionStatusSource _source;
    private readonly DetectionNames _names;
    private readonly IStudyProgress? _progress;

    /// <summary>Initializes a new instance of the <see cref="DetectionViewModel"/> class.</summary>
    public DetectionViewModel(ISettingsService settings, IUiDispatcher dispatcher, IDetectionStatusSource source, DetectionNames names, IStudyProgress? progress = null)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(names);
        _source = source;
        _names = names;
        _progress = progress;
        _source.StatusChanged += OnStatusChanged;
        Reload();
        Refresh();
    }

    /// <summary>Gets a value indicating whether the user turned screen reading on (the card is shown only then).</summary>
    [ObservableProperty]
    public partial bool IsOn { get; private set; }

    /// <summary>Gets the lines of the card.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetectionRow> Rows { get; private set; } = [];

    /// <summary>Gets the status line of the screen reader.</summary>
    [ObservableProperty]
    public partial string StatusLine { get; private set; } = DetectionText.Off;

    /// <summary>Gets the progress line, or null when the app cannot know how many matches are recorded.</summary>
    [ObservableProperty]
    public partial string? ProgressLine { get; private set; }

    /// <summary>Rebuilds the card from the latest status. Cheap; the Home clock calls it while the page is visible.</summary>
    public void Refresh()
    {
        var status = _source.Status;
        var match = status.Match;
        Rows =
        [
            Row("Scene", match?.Scene, v => Enum.TryParse<Scene>(v, out var s) ? DetectionNames.SceneName(s) : v),
            Row("Role", match?.Role, DetectionNames.Role),
            Row("Character", match?.Character, _names.Character),
            Row("Map", match?.Map, _names.Map),
            Row("Mode", match?.QueueMode, _names.Mode),
        ];
        StatusLine = status.Detail;
        ProgressLine = _progress?.MatchesRecorded is { } n ? DetectionText.Progress(n, DetectionText.MatchGoal) : null;
    }

    /// <inheritdoc />
    protected override void Load(AppSettings settings) => IsOn = settings.Detection.Enabled && settings.Detection.RiskAcknowledged;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.StatusChanged -= OnStatusChanged;
        }

        base.Dispose(disposing);
    }

    private static DetectionRow Row(string label, ConfirmedField? field, Func<string, string> words)
    {
        if (field is { State: FieldState.Confirmed, LastValue: { } value })
        {
            return new DetectionRow(label, words(value), field.Source == FieldSource.UserDeclared ? DetectionRow.DeclaredMark : DetectionRow.ConfirmedMark);
        }

        return new DetectionRow(label, "Not sure yet", DetectionRow.LearningMark);
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Dispatcher.Post(Refresh);
}
