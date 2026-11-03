using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Presentation.Detection;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// Settings, Screen reading (design §7.1 and §7.2, NFR-PRIV-01): off until the user turns it on through an inline confirmation that states the
/// anti-cheat risk. Turning it on sets both <see cref="DetectionSettings.Enabled"/> and <see cref="DetectionSettings.RiskAcknowledged"/>; turning it off
/// clears both, so the next time is a fresh, conscious choice. Nothing it reads reaches Discord until a detector has a validation report.
/// </summary>
public sealed partial class ScreenReadingViewModel : SettingsPageViewModel
{
    /// <summary>The caption under the switch (design §7.1).</summary>
    public const string Caption = "Lets the app see your role, character, map and game mode. It looks only at the Dead by Daylight window, never saves pictures of it, and never touches the game.";

    /// <summary>The title of the confirmation (design §7.2).</summary>
    public const string ConfirmTitle = "Turn on screen reading?";

    /// <summary>The label of the tick box in the confirmation.</summary>
    public const string UnderstoodLabel = "I understand";

    private readonly IDetectionStatusSource? _source;

    /// <summary>Initializes a new instance of the <see cref="ScreenReadingViewModel"/> class.</summary>
    /// <param name="settings">The settings service.</param>
    /// <param name="dispatcher">The UI dispatcher.</param>
    /// <param name="source">The live loop's status, or null in a build without screen reading.</param>
    public ScreenReadingViewModel(ISettingsService settings, IUiDispatcher dispatcher, IDetectionStatusSource? source = null)
        : base(settings, dispatcher)
    {
        _source = source;
        if (source is not null)
        {
            source.StatusChanged += OnStatusChanged;
        }

        Reload();
        RefreshStatus();
    }

    /// <summary>Gets the four sentences of the confirmation, in order (design §7.2).</summary>
    public IReadOnlyList<string> ConfirmLines { get; } =
    [
        "The app takes quick looks at the Dead by Daylight window to read things like your role and the map. Nothing is saved or sent anywhere.",
        "It never reads the game's memory or files, and never sends input.",
        "No anti-cheat system has approved this app. The risk is low, but it isn't zero.",
        "New details start as Learning and appear on Discord only once they're confirmed.",
    ];

    /// <summary>Gets a value indicating whether screen reading exists in this build.</summary>
    public bool IsAvailable => _source is not null;

    /// <summary>Gets a value indicating whether the user turned screen reading on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginTurnOnCommand), nameof(TurnOffCommand))]
    public partial bool IsOn { get; private set; }

    /// <summary>Gets the status line: "Off", "On · reading Dead by Daylight", "On · waiting for the game", "Not part of this version yet".</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Off";

    /// <summary>Gets a value indicating whether the confirmation is open.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTurnOnCommand))]
    public partial bool IsConfirming { get; private set; }

    /// <summary>Gets or sets a value indicating whether the user ticked "I understand".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTurnOnCommand))]
    public partial bool Understood { get; set; }

    /// <summary>Opens the confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanBegin))]
    public void BeginTurnOn()
    {
        Understood = false;
        IsConfirming = true;
    }

    /// <summary>Turns screen reading on after the tick.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void ConfirmTurnOn()
    {
        TurnOnConfirmed();
        IsConfirming = false;
        Understood = false;
    }

    /// <summary>
    /// Turns screen reading on. The caller has shown the four sentences of the confirmation and has the user's tick on "I understand": this sheet
    /// does, and so does the startup wizard's last step. Nothing else may call it.
    /// </summary>
    public void TurnOnConfirmed() =>
        Apply(s => s with { Detection = s.Detection with { Enabled = true, RiskAcknowledged = true } });

    /// <summary>Closes the confirmation without changing anything.</summary>
    [RelayCommand]
    private void CancelTurnOn()
    {
        IsConfirming = false;
        Understood = false;
    }

    /// <summary>Turns screen reading off; the acknowledgement is cleared too.</summary>
    [RelayCommand(CanExecute = nameof(IsOn))]
    private void TurnOff() => Apply(s => s with { Detection = s.Detection with { Enabled = false, RiskAcknowledged = false } });

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        IsOn = settings.Detection.Enabled && settings.Detection.RiskAcknowledged;
        RefreshStatus();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _source is not null)
        {
            _source.StatusChanged -= OnStatusChanged;
        }

        base.Dispose(disposing);
    }

    private bool CanBegin() => IsAvailable && !IsOn;

    private bool CanConfirm() => IsConfirming && Understood;

    private void OnStatusChanged(object? sender, EventArgs e) => Dispatcher.Post(RefreshStatus);

    private void RefreshStatus()
    {
        if (_source is null)
        {
            StatusText = "Not part of this version yet";
            return;
        }

        StatusText = !IsOn
            ? "Off"
            : _source.Status.Run switch
            {
                DetectionRunState.Reading => "On · reading Dead by Daylight",
                DetectionRunState.WaitingForGame => "On · waiting for the game",
                DetectionRunState.SwitchedOffRemotely => "On · switched off for now by the project's signed configuration",
                DetectionRunState.WindowUnavailable => "On · the game window can't be read right now",
                DetectionRunState.TextRecognitionUnavailable => "On · Windows text recognition isn't working right now",
                DetectionRunState.Problem => "On · hit a problem and will try again",
                _ => "On",
            };
    }
}
