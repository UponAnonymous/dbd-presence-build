using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Presentation.Presence;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// The always-available controls (command bar, Home, tray): pause, the saved setup in use, connection and game status, and the connection
/// test. One instance is shared so every surface shows the same state. Nothing is declared by hand any more (D30), and the Private style lives on
/// Discord status.
/// </summary>
public sealed partial class PresenceControlsViewModel : SettingsPageViewModel
{
    private readonly TimeSpan _testDuration = PresenceCoordinator.TestDuration;
    private readonly PresenceCoordinator _coordinator;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="PresenceControlsViewModel"/> class.</summary>
    public PresenceControlsViewModel(PresenceCoordinator coordinator, ISettingsService settings, IUiDispatcher dispatcher, TimeProvider time)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(time);
        _coordinator = coordinator;
        _time = time;
        Connection = new StatusLine(string.Empty, null, StatusTone.Neutral);
        Game = Connection;
        _coordinator.SnapshotChanged += OnSnapshotChanged;
        Reload();
        RefreshFromSnapshot();
    }

    /// <summary>Gets or sets a value indicating whether presence is paused (runtime only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel), nameof(PauseName), nameof(PauseGlyph))]
    public partial bool IsPaused { get; set; }

    /// <summary>Gets the text of the Pause button: "Pause" while presence runs, "Resume" while it is paused (design §2).</summary>
    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    /// <summary>Gets the spoken name of the Pause button.</summary>
    public string PauseName => IsPaused ? "Resume my Discord status" : "Pause my Discord status";

    /// <summary>Gets the icon of the Pause button (Segoe Fluent / MDL2: Play while paused, Pause otherwise).</summary>
    public string PauseGlyph => IsPaused ? "\uE768" : "\uE769";

    /// <summary>Gets the profiles to choose from.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<string>> Profiles { get; private set; } = [];

    /// <summary>Gets or sets the active profile id.</summary>
    [ObservableProperty]
    public partial string? ActiveProfileId { get; set; }

    /// <summary>Gets the Discord connection status.</summary>
    [ObservableProperty]
    public partial StatusLine Connection { get; private set; }

    /// <summary>Gets what the live check for Discord on this PC found (the startup wizard's step 2).</summary>
    [ObservableProperty]
    public partial DiscordCheck DiscordCheck { get; private set; }

    /// <summary>Gets the game status.</summary>
    [ObservableProperty]
    public partial StatusLine Game { get; private set; }

    /// <summary>Gets a value indicating whether a Discord application is selected and, for your own application, has a valid Application ID.</summary>
    [ObservableProperty]
    public partial bool IsConfigured { get; private set; }

    /// <summary>Gets the connection test result text, if a test ran.</summary>
    [ObservableProperty]
    public partial string? ConnectionTestText { get; private set; }

    /// <summary>Gets a one-line summary for the tray tooltip.</summary>
    [ObservableProperty]
    public partial string TraySummary { get; private set; } = "DBD Presence";

    /// <summary>Gets the help text for the test button; the length comes from the coordinator's real test duration.</summary>
    public string TestConnectionHelp => $"Shows a test status on Discord for {TestSeconds}.";

    private string TestSeconds =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)_testDuration.TotalSeconds} seconds");

    /// <summary>Sends a temporary test activity to Discord.</summary>
    [RelayCommand]
    private void TestConnection() =>
        ConnectionTestText = _coordinator.StartConnectionTest() switch
        {
            ConnectionTestStart.Started => $"Test sent. Check your Discord profile: it should show “Testing DBD Presence” for {TestSeconds}.",
            ConnectionTestStart.ActivityHidden => "Your status is paused or hidden. Resume it, or turn off “Hide my activity completely”, to run the test.",
            ConnectionTestStart.Locked => "DBD Presence is locked. Sign in with Discord first.",
            _ => "Enter a valid App ID for your own Discord app first, or switch back to the shared one (Settings, Advanced).",
        };

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        // Replace the list only when it changed, so an open profile selector is not reset by unrelated edits.
        var profiles = settings.Profiles.Select(p => new Choice<string>(p.Id, p.Name)).ToList();
        if (!profiles.SequenceEqual(Profiles))
        {
            Profiles = profiles;
        }

        ActiveProfileId = settings.ActiveProfile.Id;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.SnapshotChanged -= OnSnapshotChanged;
        }

        base.Dispose(disposing);
    }

    partial void OnIsPausedChanged(bool value) => _coordinator.SetPaused(value);

    partial void OnActiveProfileIdChanged(string? value)
    {
        if (value is not null)
        {
            Apply(s => s with { ActiveProfileId = value });
        }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => Dispatcher.Post(RefreshFromSnapshot);

    /// <summary>Re-reads the snapshot (also called once a second by Home so retry countdowns stay current).</summary>
    public void RefreshStatus() => RefreshFromSnapshot();

    private void RefreshFromSnapshot()
    {
        var snapshot = _coordinator.Snapshot;
        var now = _time.GetUtcNow();
        IsPaused = snapshot.Paused;
        IsConfigured = snapshot.IsConfigured;
        Connection = StatusText.Connection(snapshot.Connection, snapshot.IsConfigured, now);
        DiscordCheck = snapshot.Connection.Status switch
        {
            Discord.RpcStatus.Connected => DiscordCheck.Connected,
            Discord.RpcStatus.Disconnected when snapshot.Connection.NextRetryUtc is not null || snapshot.Connection.LastError is not null => DiscordCheck.NotOpen,
            _ => DiscordCheck.Looking,
        };
        Game = StatusText.Game(snapshot.Game, _time.LocalTimeZone);
        TraySummary = TrayText(snapshot);

        if (snapshot.ConnectionTest is { } test)
        {
            ConnectionTestText = test.Succeeded
                ? "Discord accepted an update while the test was showing. If your profile showed “Testing DBD Presence”, everything works."
                : now < test.EndsUtc
                    ? "Test sent. Waiting for Discord to accept it…"
                    : "Discord did not confirm the test. Check that the Discord desktop app is running on this PC and, if you use your own Discord app, that its App ID is correct.";
        }
    }

    /// <summary>The tray tooltip (design §14): "DBD Presence · Discord connected · Playing". Where the user is appears only once the app confirmed it.</summary>
    private static string TrayText(PresenceSnapshot snapshot)
    {
        var discord = snapshot.Connection.Status == Discord.RpcStatus.Connected ? "Discord connected" : "Discord not connected";
        var game = snapshot.Paused
            ? "Paused"
            : snapshot.Game.State switch
            {
                GameState.Running => PlayingWords(snapshot.State),
                GameState.Launching => "Game starting",
                _ => "Game not running",
            };
        return $"DBD Presence · {discord} · {game}";
    }

    private static string PlayingWords(PresenceState state) => state switch
    {
        PresenceState.MainMenu => "In the main menu",
        PresenceState.Matchmaking => "Looking for a match",
        PresenceState.Lobby or PresenceState.LobbySurvivor or PresenceState.LobbyKiller => "In a lobby",
        PresenceState.Loading => "Loading a match",
        PresenceState.InTrial or PresenceState.InTrialSurvivor or PresenceState.InTrialKiller => "In a match",
        PresenceState.Results => "After the match",
        _ => "Playing",
    };
}
