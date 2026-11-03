using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Presentation.Presence;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>The three looks of the status (design §4.1). "Private" is the former streamer mode.</summary>
public enum StatusStyle
{
    /// <summary>Where you are, your role, character and map, and a timer (<see cref="TemplatePresets.Detailed"/>).</summary>
    Detailed,

    /// <summary>Only "Playing Dead by Daylight" and a timer (<see cref="TemplatePresets.Minimal"/>).</summary>
    Simple,

    /// <summary>Only that you're playing; good for streaming (streamer mode on, the Private text set).</summary>
    Private,
}

/// <summary>The big picture choices (design §4.1, Pictures).</summary>
public enum BigPicture
{
    /// <summary>The character you play; falls back to your role icon.</summary>
    MyCharacter,

    /// <summary>Your role icon.</summary>
    MyRole,

    /// <summary>The map, once the app knows it.</summary>
    TheMap,

    /// <summary>The DBD Presence mark.</summary>
    AppLogo,

    /// <summary>No big picture.</summary>
    None,
}

/// <summary>The small badge choices (design §4.1, Pictures).</summary>
public enum SmallBadge
{
    /// <summary>
    /// Your role icon, which changes to a hook, down or match result picture while the app is sure of that moment and your pack has the picture (FR-AUTO-01).
    /// Private style keeps the role icon.
    /// </summary>
    Automatic,

    /// <summary>Your role icon.</summary>
    MyRole,

    /// <summary>The DBD Presence mark.</summary>
    AppLogo,

    /// <summary>No badge.</summary>
    None,
}

/// <summary>One of the three style cards: its words and what it would give Discord now.</summary>
public sealed partial class StyleCardViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="StyleCardViewModel"/> class.</summary>
    public StyleCardViewModel(StatusStyle style, string name, string caption)
    {
        Style = style;
        Name = name;
        Caption = caption;
    }

    /// <summary>Gets the style.</summary>
    public StatusStyle Style { get; }

    /// <summary>Gets the name.</summary>
    public string Name { get; }

    /// <summary>Gets the one-line description.</summary>
    public string Caption { get; }

    /// <summary>Gets the spoken name of the card.</summary>
    public string AutomationName => $"{Name} style. {Caption}";

    /// <summary>Gets or sets a value indicating whether this is the style in use.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Gets the first line this style would give Discord now (from the same live inputs; what is unknown is left out).</summary>
    [ObservableProperty]
    public partial string Line1 { get; private set; } = string.Empty;

    /// <summary>Gets the second line this style would give Discord now.</summary>
    [ObservableProperty]
    public partial string Line2 { get; private set; } = string.Empty;

    internal void SetLines(string first, string second)
    {
        Line1 = first;
        Line2 = second;
    }
}

/// <summary>
/// Discord status (design §4): choose how the status looks. A style (Detailed, Simple or Private) with live mini previews, a switch for every part of
/// the game that can reach Discord, the pictures, the name after "Playing", and the live card with its reasons. The text editor is
/// <see cref="Studio"/>, under Advanced. Styles map to the existing presets and to the streamer-mode switch, so nothing that worked is lost.
/// </summary>
public sealed partial class DiscordStatusViewModel : SettingsPageViewModel
{
    /// <summary>How long the "Your own text was replaced" note with Undo stays (design §4.1).</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    private readonly PresenceCoordinator _coordinator;
    private readonly TimeProvider _time;
    private readonly ITimer _clock;
    private (TemplateSet Templates, bool Private)? _beforeStyle;
    private ITimer? _undoTimer;

    /// <summary>Initializes a new instance of the <see cref="DiscordStatusViewModel"/> class.</summary>
    public DiscordStatusViewModel(
        PresenceControlsViewModel controls, PresenceCoordinator coordinator, ISettingsService settings, IUiDispatcher dispatcher, TimeProvider time,
        PrivacyViewModel privacy, AppStartupViewModel appStartup, PresenceStudioViewModel studio, ScreenReadingViewModel? screenReading = null)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(privacy);
        ArgumentNullException.ThrowIfNull(appStartup);
        ArgumentNullException.ThrowIfNull(studio);
        Controls = controls;
        Privacy = privacy;
        AppStartup = appStartup;
        Studio = studio;
        ScreenReading = screenReading;
        _coordinator = coordinator;
        _time = time;
        Cards =
        [
            new(StatusStyle.Detailed, "Detailed", "Where you are, your role, character and map, and a timer."),
            new(StatusStyle.Simple, "Simple", "Only “Playing Dead by Daylight” and a timer."),
            new(StatusStyle.Private, "Private", "Only that you're playing. Good for streaming."),
        ];
        Live = LivePreview.From(coordinator, settings.Current, time.GetUtcNow(), () => Controls.IsPaused = false);
        _coordinator.SnapshotChanged += OnSnapshotChanged;
        controls.PropertyChanged += OnControlsChanged;
        Reload();
        Refresh();
        _clock = time.CreateTimer(_ => Dispatcher.Post(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Gets the shared controls (pause, connection).</summary>
    public PresenceControlsViewModel Controls { get; }

    /// <summary>Gets the switches under "What to show" and the pause and game-closed choices.</summary>
    public PrivacyViewModel Privacy { get; }

    /// <summary>Gets the name after "Playing" and the member-list choice.</summary>
    public AppStartupViewModel AppStartup { get; }

    /// <summary>Gets the text editor shown under Advanced.</summary>
    public PresenceStudioViewModel Studio { get; }

    /// <summary>Gets a value indicating whether more than one saved setup exists; the chooser under Advanced is shown only then (design §4.3).</summary>
    public bool HasSeveralSetups => Controls.Profiles.Count > 1;

    /// <summary>Gets the screen reading model (the "Screen reading is off" strip), or null in a build without it.</summary>
    public ScreenReadingViewModel? ScreenReading { get; }

    /// <summary>Gets the three style cards.</summary>
    public IReadOnlyList<StyleCardViewModel> Cards { get; }

    /// <summary>Gets the big picture choices.</summary>
    public IReadOnlyList<Choice<BigPicture>> BigPictures { get; } =
    [
        new(BigPicture.MyCharacter, "My character"), new(BigPicture.MyRole, "My role"), new(BigPicture.TheMap, "The map"),
        new(BigPicture.AppLogo, "App logo"), new(BigPicture.None, "None"),
    ];

    /// <summary>Gets the small badge choices.</summary>
    public IReadOnlyList<Choice<SmallBadge>> SmallBadges { get; } =
        [new(SmallBadge.Automatic, "Automatic"), new(SmallBadge.MyRole, "My role"), new(SmallBadge.AppLogo, "App logo"), new(SmallBadge.None, "None")];

    /// <summary>Gets the words next to the Automatic choice (design §4.1: what it does, in plain words, with no invented values).</summary>
    public string AutomaticCaption { get; } =
        "Automatic shows your role icon. It changes to the hook, down or match result picture when the app is sure of that moment and your pack has the picture. "
        + "The hook and down pictures also need “Show live match details”. Private style never shows them.";

    /// <summary>Gets the style in use, or null when the text matches none of them ("Your own text").</summary>
    [ObservableProperty]
    public partial StatusStyle? SelectedStyle { get; private set; }

    /// <summary>Gets a value indicating whether the text in use differs from the style's own (the "Your own text" chip).</summary>
    [ObservableProperty]
    public partial bool HasOwnText { get; private set; }

    /// <summary>Gets a value indicating whether the Private style is in use (the switches stay editable but stay off Discord).</summary>
    public bool IsPrivate => SelectedStyle == StatusStyle.Private;

    /// <summary>Gets the note after a style replaced text the user wrote, while it can still be undone.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoStyleCommand))]
    public partial string? StyleNote { get; private set; }

    /// <summary>Gets the big picture in use, or null when different moments use different pictures.</summary>
    [ObservableProperty]
    public partial BigPicture? SelectedBig { get; private set; }

    /// <summary>Gets the small badge in use, or null when different moments use different badges.</summary>
    [ObservableProperty]
    public partial SmallBadge? SelectedSmall { get; private set; }

    /// <summary>Gets the live card, reasons and captions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(ShownReasons), nameof(ShowAllVisible), nameof(ShowAllLabel), nameof(PausedLine), nameof(Hints))]
    public partial LivePreview Live { get; private set; }

    /// <summary>Gets or sets a value indicating whether every reason is listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownReasons), nameof(ShowAllVisible))]
    public partial bool ShowAllReasons { get; set; }

    /// <summary>Gets the activity card.</summary>
    public ActivityPreview Preview => Live.Preview;

    /// <summary>Gets the reasons to list now.</summary>
    public IReadOnlyList<string> ShownReasons => Live.IsEmpty ? [] : ShowAllReasons ? Live.Reasons.All : Live.Reasons.Shown;

    /// <summary>Gets a value indicating whether the "Show all" button is shown.</summary>
    public bool ShowAllVisible => !Live.IsEmpty && Live.Reasons.HasMore && !ShowAllReasons;

    /// <summary>Gets the label of the "Show all" button.</summary>
    public string ShowAllLabel => Live.Reasons.ShowAllLabel;

    /// <summary>Gets the paused sentence above the card, or null when not paused.</summary>
    public string? PausedLine => Live.Hints.FirstOrDefault(h => h.Id == "paused")?.Message;

    /// <summary>Gets the fix buttons of the preview (Resume while paused).</summary>
    public IReadOnlyList<PreviewHint> Hints => Live.Hints;

    /// <summary>Gets the line under "The match" that explains what never goes on Discord.</summary>
    public string KillerLine { get; } =
        "The killer you're facing is never shown on Discord. It's about another player. Every other detail appears only once the app is sure of it.";

    /// <summary>Gets the caption when Big picture is None (FR-ART-01).</summary>
    public string NoBigPictureNote { get; } =
        "Discord shows a small badge only on top of a big picture. With no big picture, Discord shows its own app icon instead.";

    /// <summary>Gets a value indicating whether the no-big-picture caption is shown.</summary>
    public bool ShowNoBigPictureNote => SelectedBig == BigPicture.None;

    /// <summary>Gets the caption under the Private style's switches.</summary>
    public string PrivateNote { get; } = "Private style keeps these off Discord.";

    /// <summary>Gets a value indicating whether the strip about screen reading being off is shown.</summary>
    public bool ShowScreenReadingOffStrip => ScreenReading is { IsOn: false };

    /// <summary>Gets the text of the screen reading strip.</summary>
    public string ScreenReadingOffStrip { get; } =
        "Screen reading is off, so only the timer and “Playing Dead by Daylight” can appear. Turn it on in Settings.";

    /// <summary>Chooses a style.</summary>
    /// <param name="style">The style.</param>
    [RelayCommand]
    private void SelectStyle(StatusStyle style)
    {
        var settings = Settings.Current;
        var profile = settings.ActiveProfile;
        if (style == StatusStyle.Private)
        {
            Apply(s => s with { Privacy = s.Privacy with { StreamerMode = true } });
            return;
        }

        var target = style == StatusStyle.Simple ? TemplatePresets.Minimal : TemplatePresets.Detailed;
        var current = profile.Templates;
        var replacesOwnText = !SameText(current, TemplatePresets.Detailed) && !SameText(current, TemplatePresets.Minimal) && !SameText(current, target);
        var wasPrivate = settings.Privacy.StreamerMode;
        var big = SelectedBig;
        var small = SelectedSmall;
        Apply(s => WithActiveProfile(
            s with { Privacy = s.Privacy with { StreamerMode = false } },
            p => p with { Templates = WithPictures(SameText(p.Templates, target) ? p.Templates : target, big, small) }));
        if (replacesOwnText)
        {
            _beforeStyle = (current, wasPrivate);
            StyleNote = $"Switched to {(style == StatusStyle.Simple ? "Simple" : "Detailed")}. Your own text was replaced.";
            _undoTimer?.Dispose();
            _undoTimer = _time.CreateTimer(_ => Dispatcher.Post(ClearUndo), null, UndoWindow, Timeout.InfiniteTimeSpan);
        }
        else
        {
            ClearUndo();
        }
    }

    /// <summary>Restores the text from before the last style change.</summary>
    [RelayCommand(CanExecute = nameof(CanUndoStyle))]
    private void UndoStyle()
    {
        if (_beforeStyle is not { } before)
        {
            return;
        }

        Apply(s => WithActiveProfile(s with { Privacy = s.Privacy with { StreamerMode = before.Private } }, p => p with { Templates = before.Templates }));
        ClearUndo();
    }

    private bool CanUndoStyle() => _beforeStyle is not null;

    /// <summary>Chooses the big picture for every moment that shows one.</summary>
    /// <param name="choice">The picture.</param>
    [RelayCommand]
    private void SelectBigPicture(BigPicture choice)
    {
        var slot = BigSlot(choice);
        Apply(s => WithActiveProfile(s, p => p with
        {
            Templates = MapSlots(p.Templates, t => t with { LargeImage = slot }),
            StreamerTemplates = MapSlots(p.StreamerTemplates, t => IsPrivateSafe(slot) ? t with { LargeImage = slot } : t),
        }));
    }

    /// <summary>Chooses the small badge for every moment that shows one. "None" turns the badge off.</summary>
    /// <param name="choice">The badge.</param>
    [RelayCommand]
    private void SelectSmallBadge(SmallBadge choice)
    {
        var slot = SmallSlot(choice);
        Apply(s => WithActiveProfile(s, p => p with
        {
            Templates = MapSlots(p.Templates, t => t with { SmallImage = slot }),
            StreamerTemplates = MapSlots(p.StreamerTemplates, t => IsPrivateSafe(slot) ? t with { SmallImage = slot } : t),
            Visibility = p.Visibility with { ShowSmallImage = choice != SmallBadge.None },
        }));
    }

    /// <summary>Lists every reason.</summary>
    [RelayCommand]
    private void ShowAllReasonsNow() => ShowAllReasons = true;

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        var profile = settings.ActiveProfile;
        StatusStyle? style;
        bool own;
        if (settings.Privacy.StreamerMode)
        {
            style = StatusStyle.Private;
            own = !SameText(profile.StreamerTemplates, TemplatePresets.StreamerSafe);
        }
        else if (SameText(profile.Templates, TemplatePresets.Detailed))
        {
            style = StatusStyle.Detailed;
            own = false;
        }
        else if (SameText(profile.Templates, TemplatePresets.Minimal))
        {
            style = StatusStyle.Simple;
            own = false;
        }
        else
        {
            style = null;
            own = true;
        }

        SelectedStyle = style;
        HasOwnText = own;
        foreach (var card in Cards)
        {
            card.IsSelected = card.Style == style;
        }

        SelectedBig = CommonBig(profile, settings.Privacy.StreamerMode);
        SelectedSmall = profile.Visibility.ShowSmallImage ? CommonSmall(profile, settings.Privacy.StreamerMode) : SmallBadge.None;
        OnPropertyChanged(nameof(IsPrivate));
        OnPropertyChanged(nameof(ShowNoBigPictureNote));
        OnPropertyChanged(nameof(ShowScreenReadingOffStrip));
        if (Live is not null)
        {
            Refresh();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.SnapshotChanged -= OnSnapshotChanged;
            Controls.PropertyChanged -= OnControlsChanged;
            _clock.Dispose();
            _undoTimer?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static bool SameText(TemplateSet a, TemplateSet b) => string.Equals(TextFingerprint(a), TextFingerprint(b), StringComparison.Ordinal);

    /// <summary>The words and timers of a set, without pictures or buttons: two sets with the same words are the same style (design §4.1).</summary>
    private static string TextFingerprint(TemplateSet set) => string.Join(
        "\n",
        set.ByState.OrderBy(kv => kv.Key).Select(kv => string.Create(
            CultureInfo.InvariantCulture, $"{kv.Key}\u0001{kv.Value.Details}\u0001{kv.Value.State}\u0001{kv.Value.LargeText}\u0001{kv.Value.SmallText}\u0001{kv.Value.Timer}")));

    private static TemplateSet MapSlots(TemplateSet set, Func<PresenceTemplate, PresenceTemplate> change) =>
        set with { ByState = set.ByState.ToDictionary(kv => kv.Key, kv => change(kv.Value)) };

    private static TemplateSet WithPictures(TemplateSet set, BigPicture? big, SmallBadge? small) =>
        MapSlots(set, t => t with
        {
            LargeImage = big is { } b ? BigSlot(b) : t.LargeImage,
            SmallImage = small is { } s ? SmallSlot(s) : t.SmallImage,
        });

    /// <summary>Only these pictures may appear in the Private style: never the map or the character (they could give away what the user keeps private).</summary>
    private static bool IsPrivateSafe(ImageSlot slot) => slot.Source is ImageSource.AppLogo or ImageSource.None or ImageSource.RoleArtwork;

    private static ImageSlot BigSlot(BigPicture choice) => choice switch
    {
        BigPicture.MyCharacter => ImageSlot.Character,
        BigPicture.MyRole => ImageSlot.Role,
        BigPicture.TheMap => ImageSlot.Map,
        BigPicture.AppLogo => ImageSlot.AppLogo,
        _ => ImageSlot.None,
    };

    private static ImageSlot SmallSlot(SmallBadge choice) => choice switch
    {
        SmallBadge.Automatic => ImageSlot.Automatic,
        SmallBadge.MyRole => ImageSlot.Role,
        SmallBadge.AppLogo => ImageSlot.AppLogo,
        _ => ImageSlot.None,
    };

    private static BigPicture? CommonBig(PresenceProfile profile, bool streamer)
    {
        var sources = AllTemplates(profile, streamer).Select(t => t.LargeImage).Distinct().ToList();
        return sources.Count == 1 ? BigFrom(sources[0]) : null;
    }

    private static SmallBadge? CommonSmall(PresenceProfile profile, bool streamer)
    {
        var sources = AllTemplates(profile, streamer).Select(t => t.SmallImage).Distinct().ToList();
        return sources.Count == 1 ? SmallFrom(sources[0]) : null;
    }

    private static IEnumerable<PresenceTemplate> AllTemplates(PresenceProfile profile, bool streamer) =>
        streamer ? profile.StreamerTemplates.ByState.Values : profile.Templates.ByState.Values;

    private static BigPicture? BigFrom(ImageSlot slot) => slot.Source switch
    {
        ImageSource.CharacterArtwork => BigPicture.MyCharacter,
        ImageSource.RoleArtwork => BigPicture.MyRole,
        ImageSource.MapArtwork => BigPicture.TheMap,
        ImageSource.AppLogo => BigPicture.AppLogo,
        ImageSource.None => BigPicture.None,
        _ => null,
    };

    private static SmallBadge? SmallFrom(ImageSlot slot) => slot.Source switch
    {
        ImageSource.Automatic => SmallBadge.Automatic,
        ImageSource.RoleArtwork => SmallBadge.MyRole,
        ImageSource.AppLogo => SmallBadge.AppLogo,
        ImageSource.None => SmallBadge.None,
        _ => null,
    };

    private void ClearUndo()
    {
        _beforeStyle = null;
        _undoTimer?.Dispose();
        _undoTimer = null;
        StyleNote = null;
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => Dispatcher.Post(Refresh);

    private void OnControlsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PresenceControlsViewModel.Profiles))
        {
            OnPropertyChanged(nameof(HasSeveralSetups));
        }
    }

    private void Tick() => Refresh();

    private void Refresh()
    {
        var settings = Settings.Current;
        var live = LivePreview.From(_coordinator, settings, _time.GetUtcNow(), () => Controls.IsPaused = false);
        if (!live.SameAs(Live))
        {
            Live = live;
        }

        RefreshMiniPreviews(settings);
        OnPropertyChanged(nameof(ShowScreenReadingOffStrip));
    }

    /// <summary>Gives each style card the first two lines it would give Discord now, composed from the very inputs the real status uses.</summary>
    private void RefreshMiniPreviews(AppSettings settings)
    {
        var snapshot = _coordinator.Snapshot;
        var now = _time.GetUtcNow();
        var inputs = snapshot.Inputs ?? new PresenceInputs { Now = now, GameStartedUtc = snapshot.Game.StartedUtc };

        // "Right now" with the game closed would be nothing at all, so the cards show what each style would say while playing. No value is invented.
        if (inputs.State is PresenceState.NotRunning or PresenceState.Launching)
        {
            inputs = inputs with { State = PresenceState.Running, GameStartedUtc = inputs.GameStartedUtc ?? now };
        }

        var profile = settings.ActiveProfile;
        foreach (var card in Cards)
        {
            var preview = card.Style switch
            {
                StatusStyle.Private => PresenceComposer.Compose(inputs, profile, new PrivacyState(StreamerMode: true), settings.Discord.DisplayName),
                StatusStyle.Simple => PresenceComposer.Compose(inputs, profile with { Templates = TemplatePresets.Minimal }, new PrivacyState(), settings.Discord.DisplayName),
                _ => PresenceComposer.Compose(inputs, profile with { Templates = TemplatePresets.Detailed }, new PrivacyState(), settings.Discord.DisplayName),
            };
            card.SetLines(preview.Activity?.Details ?? string.Empty, preview.Activity?.State ?? string.Empty);
        }
    }
}
