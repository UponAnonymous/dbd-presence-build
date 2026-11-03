using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Settings;
using DbdPresence.Core.Templates;
using DbdPresence.Packs.Local;
using DbdPresence.Presentation.Presence;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>Which template set the Studio edits.</summary>
public enum TemplateTarget
{
    /// <summary>The text of the Detailed and Simple styles ("My usual style").</summary>
    Normal,

    /// <summary>The text of the Private style.</summary>
    Streamer,
}

/// <summary>What Discord's member list shows next to your name (FR-RPC-11); a real value so lists can select it.</summary>
public enum MemberListDisplay
{
    /// <summary>Leave it to Discord (the field is omitted).</summary>
    DiscordDefault,

    /// <summary>The application name.</summary>
    Name,

    /// <summary>The state line.</summary>
    State,

    /// <summary>The details line.</summary>
    Details,
}

/// <summary>A state in the Studio's state list.</summary>
/// <param name="State">The state.</param>
/// <param name="Name">Display name.</param>
/// <param name="Reachability">When the state can be reached.</param>
/// <param name="Badge">Badge text for <paramref name="Reachability"/>.</param>
public sealed record StudioState(PresenceState State, string Name, Reachability Reachability, string Badge)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A chip in "Add a detail".</summary>
/// <param name="Token">The text to insert, e.g. "{role}".</param>
/// <param name="Label">The plain name on the chip, e.g. "Role".</param>
/// <param name="Description">When the detail is available.</param>
public sealed record TokenChip(string Token, string Label, string Description)
{
    /// <summary>Gets the spoken name of the chip.</summary>
    public string AutomationName => $"Insert detail: {Label}";
}

/// <summary>
/// The text editor under Discord status, Advanced (design §4.3, FR-TPL-*): edit what each moment says, see live counters and syntax errors, and
/// preview the exact payload. The preview uses the live values only (D29): nothing is invented.
/// </summary>
public sealed partial class PresenceStudioViewModel : SettingsPageViewModel
{
    private readonly IApplicationCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly PackArtworkProvider? _packs;
    private readonly PresenceCoordinator? _coordinator;
    private bool _loadingButtons;

    /// <summary>Initializes a new instance of the <see cref="PresenceStudioViewModel"/> class.</summary>
    public PresenceStudioViewModel(
        ISettingsService settings, IApplicationCatalog catalog, IUiDispatcher dispatcher, TimeProvider time, PackArtworkProvider? packs = null,
        PresenceCoordinator? coordinator = null)
        : base(settings, dispatcher)
    {
        _packs = packs;
        _coordinator = coordinator;
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(time);
        _catalog = catalog;
        _time = time;
        States = [.. Enum.GetValues<PresenceState>().Select(s => new StudioState(s, StudioText.StateName(s), StudioText.ReachabilityOf(s), StudioText.Badge(StudioText.ReachabilityOf(s))))];
        Details = new TemplateFieldViewModel("Top line", isRequired: true, text => EditTemplate(t => t with { Details = text }));
        StateLine = new TemplateFieldViewModel("Second line", isRequired: false, text => EditTemplate(t => t with { State = text }));
        LargeText = new TemplateFieldViewModel("Text when someone points at the big picture", isRequired: false, text => EditTemplate(t => t with { LargeText = text }));
        SmallText = new TemplateFieldViewModel("Text when someone points at the small badge", isRequired: false, text => EditTemplate(t => t with { SmallText = text }));
        Preview = ActivityPreview.From(null, string.Empty, new(), time.GetUtcNow());

        // Open on the situation Discord shows now (D29), in the template set that is live.
        var liveState = coordinator?.Snapshot.State ?? PresenceState.Running;
        SelectedState = States.First(s => s.State == liveState);
        Target = Settings.Current.Privacy.StreamerMode ? TemplateTarget.Streamer : TemplateTarget.Normal;
        if (coordinator is not null)
        {
            coordinator.SnapshotChanged += OnSnapshotChanged;
        }

        Reload();
    }

    /// <summary>Gets the states, in lifecycle order.</summary>
    public IReadOnlyList<StudioState> States { get; }

    /// <summary>Gets the template set choices.</summary>
    public IReadOnlyList<Choice<TemplateTarget>> Targets { get; } =
        [new(TemplateTarget.Normal, "My usual style"), new(TemplateTarget.Streamer, "Private style")];

    /// <summary>Gets the image source choices for the big picture.</summary>
    public IReadOnlyList<Choice<ImageSource>> ImageSources { get; } =
    [
        new(ImageSource.None, "No picture"), new(ImageSource.AppLogo, "App logo"), new(ImageSource.RoleArtwork, "My role"),
        new(ImageSource.CharacterArtwork, "My character"),
        new(ImageSource.MapArtwork, "The map"), new(ImageSource.Fixed, "A picture link"),
    ];

    /// <summary>Gets the image source choices for the small badge: the big picture's, with Automatic (FR-AUTO-01; it is for the badge only).</summary>
    public IReadOnlyList<Choice<ImageSource>> SmallImageSources { get; } =
    [
        new(ImageSource.Automatic, "Automatic"), new(ImageSource.None, "No picture"), new(ImageSource.AppLogo, "App logo"), new(ImageSource.RoleArtwork, "My role"),
        new(ImageSource.CharacterArtwork, "My character"),
        new(ImageSource.MapArtwork, "The map"), new(ImageSource.Fixed, "A picture link"),
    ];

    /// <summary>Gets the words under the small badge choice about Automatic.</summary>
    public string AutomaticSmallBadgeNote { get; } =
        "Automatic shows your role icon. It changes to the hook, down or match result picture when the app is sure of that moment and your pack has the picture.";

    /// <summary>Gets the timer choices.</summary>
    public IReadOnlyList<Choice<TimerSource>> TimerSources { get; } =
        [new(TimerSource.None, "None"), new(TimerSource.GameSession, "Since the game started"), new(TimerSource.Match, "Since the match started")];

    /// <summary>Gets the activity type choices (FR-RPC-10).</summary>
    public IReadOnlyList<Choice<ActivityKind>> ActivityKinds { get; } =
        [new(ActivityKind.Playing, "Playing"), new(ActivityKind.Competing, "Competing"), new(ActivityKind.Watching, "Watching"), new(ActivityKind.Listening, "Listening")];

    /// <summary>Gets the status display choices (FR-RPC-11; what Discord shows next to your name in the member list).</summary>
    public IReadOnlyList<Choice<MemberListDisplay>> StatusDisplays { get; } = MemberListChoices.All;

    /// <summary>Gets the chips of "Add a detail", one per detail the text can use.</summary>
    public IReadOnlyList<TokenChip> Tokens { get; } =
        [.. TemplateTokens.Known.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new TokenChip($"{{{t.Key}}}", DetailLabel(t.Key), t.Value))];

    /// <summary>Gets the details field.</summary>
    public TemplateFieldViewModel Details { get; }

    /// <summary>Gets the state line field.</summary>
    public TemplateFieldViewModel StateLine { get; }

    /// <summary>Gets the large image text field.</summary>
    public TemplateFieldViewModel LargeText { get; }

    /// <summary>Gets the small image text field.</summary>
    public TemplateFieldViewModel SmallText { get; }

    /// <summary>Gets or sets which template set is being edited.</summary>
    [ObservableProperty]
    public partial TemplateTarget Target { get; set; }

    /// <summary>Gets or sets the state being edited.</summary>
    [ObservableProperty]
    public partial StudioState SelectedState { get; set; }

    /// <summary>Gets or sets the large image source.</summary>
    [ObservableProperty]
    public partial ImageSource LargeImageSource { get; set; }

    /// <summary>Gets or sets the large image key or URL (for <see cref="ImageSource.Fixed"/>).</summary>
    [ObservableProperty]
    public partial string LargeImageValue { get; set; } = string.Empty;

    /// <summary>Gets the problem with <see cref="LargeImageValue"/>, if any (it is not saved until valid).</summary>
    [ObservableProperty]
    public partial string? LargeImageError { get; private set; }

    /// <summary>Gets or sets the small image source.</summary>
    [ObservableProperty]
    public partial ImageSource SmallImageSource { get; set; }

    /// <summary>Gets or sets the small image key or URL.</summary>
    [ObservableProperty]
    public partial string SmallImageValue { get; set; } = string.Empty;

    /// <summary>Gets the problem with <see cref="SmallImageValue"/>, if any.</summary>
    [ObservableProperty]
    public partial string? SmallImageError { get; private set; }

    /// <summary>Gets or sets the timer source.</summary>
    [ObservableProperty]
    public partial TimerSource Timer { get; set; }

    /// <summary>Gets or sets the first button label.</summary>
    [ObservableProperty]
    public partial string Button1Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the first button URL.</summary>
    [ObservableProperty]
    public partial string Button1Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the second button label.</summary>
    [ObservableProperty]
    public partial string Button2Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the second button URL.</summary>
    [ObservableProperty]
    public partial string Button2Url { get; set; } = string.Empty;

    /// <summary>Gets the problem with the buttons, if any (invalid buttons are not saved).</summary>
    [ObservableProperty]
    public partial string? ButtonsError { get; private set; }

    /// <summary>Gets or sets the activity type.</summary>
    [ObservableProperty]
    public partial ActivityKind ActivityKind { get; set; }

    /// <summary>Gets or sets the status display.</summary>
    [ObservableProperty]
    public partial MemberListDisplay StatusDisplay { get; set; }

    /// <summary>Gets the preview card.</summary>
    [ObservableProperty]
    public partial ActivityPreview Preview { get; private set; }

    /// <summary>Gets which template the preview used and why.</summary>
    [ObservableProperty]
    public partial string FallbackExplanation { get; private set; } = string.Empty;

    /// <summary>Gets the composer's notes for the preview.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> PreviewNotes { get; private set; } = [];

    /// <summary>Gets a note about when the selected state appears on Discord.</summary>
    [ObservableProperty]
    public partial string ReachabilityNote { get; private set; } = string.Empty;

    /// <summary>Gets one plain sentence, with a fix button where there is one, for each common reason something is missing from the preview (D29).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PreviewHint> Hints { get; private set; } = [];

    /// <summary>Gets a value indicating whether the preview is the activity Discord shows right now (the situation and template set being edited are the live ones).</summary>
    [ObservableProperty]
    public partial bool IsEditingLive { get; private set; }

    /// <summary>Gets a note saying which situation is being edited and whether Discord shows it right now.</summary>
    [ObservableProperty]
    public partial string LiveStateNote { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the large image needs a key or URL.</summary>
    public bool LargeImageNeedsValue => LargeImageSource == ImageSource.Fixed;

    /// <summary>Gets a value indicating whether the small image needs a key or URL.</summary>
    public bool SmallImageNeedsValue => SmallImageSource == ImageSource.Fixed;

    /// <summary>Gets a value indicating whether the note about a small image without a large image is shown (FR-ART-01).</summary>
    public bool ShowsSmallImageNeedsLargeNote => LargeImageSource == ImageSource.None;

    /// <summary>Gets the note shown under Small image while Large image is "No image".</summary>
    public string SmallImageNeedsLargeNote => ShowsSmallImageNeedsLargeNote ? "Discord shows a small badge only on top of a big picture. With no big picture, Discord shows its own app icon instead." : string.Empty;

    /// <summary>Restores all the text of the set being edited to its standard style (Detailed, or the Private text).</summary>
    [RelayCommand]
    private void ResetAllText()
    {
        var source = Target == TemplateTarget.Streamer ? TemplatePresets.StreamerSafe : TemplatePresets.Detailed;
        EditSet(_ => source);
    }

    /// <summary>Restores the selected moment from the standard style of the set being edited.</summary>
    [RelayCommand]
    private void ResetState()
    {
        var source = Target == TemplateTarget.Streamer ? TemplatePresets.StreamerSafe : TemplatePresets.Detailed;
        var state = SelectedState.State;
        EditSet(set =>
        {
            var map = set.ByState.ToDictionary();
            if (source.ByState.TryGetValue(state, out var original))
            {
                map[state] = original;
            }
            else
            {
                map.Remove(state);
            }

            return set with { ByState = map };
        });
    }

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        var profile = settings.ActiveProfile;
        ActivityKind = profile.ActivityKind;
        StatusDisplay = MemberListChoices.FromStored(profile.StatusDisplay);

        var template = CurrentTemplate(profile);
        Details.Load(template.Details);
        StateLine.Load(template.State);
        LargeText.Load(template.LargeText);
        SmallText.Load(template.SmallText);
        LargeImageSource = template.LargeImage.Source;
        SmallImageSource = template.SmallImage.Source;
        if (template.LargeImage.Source == ImageSource.Fixed && LargeImageError is null)
        {
            LargeImageValue = template.LargeImage.Value ?? string.Empty;
        }

        if (template.SmallImage.Source == ImageSource.Fixed && SmallImageError is null)
        {
            SmallImageValue = template.SmallImage.Value ?? string.Empty;
        }

        Timer = template.Timer;
        LoadButtons(template.Buttons);
        RefreshPreview(settings);
    }

    partial void OnTargetChanged(TemplateTarget value) => ReloadForSelection();

    partial void OnSelectedStateChanged(StudioState value) => ReloadForSelection();

    partial void OnLargeImageSourceChanged(ImageSource value)
    {
        OnPropertyChanged(nameof(LargeImageNeedsValue));
        OnPropertyChanged(nameof(ShowsSmallImageNeedsLargeNote));
        CommitImage(isLarge: true);
    }

    partial void OnLargeImageValueChanged(string value) => CommitImage(isLarge: true);

    partial void OnSmallImageSourceChanged(ImageSource value)
    {
        OnPropertyChanged(nameof(SmallImageNeedsValue));
        CommitImage(isLarge: false);
    }

    partial void OnSmallImageValueChanged(string value) => CommitImage(isLarge: false);

    partial void OnTimerChanged(TimerSource value) => EditTemplate(t => t with { Timer = value });

    partial void OnButton1LabelChanged(string value) => CommitButtons();

    partial void OnButton1UrlChanged(string value) => CommitButtons();

    partial void OnButton2LabelChanged(string value) => CommitButtons();

    partial void OnButton2UrlChanged(string value) => CommitButtons();

    partial void OnActivityKindChanged(ActivityKind value) => Apply(s => WithActiveProfile(s, p => p with { ActivityKind = value }));

    partial void OnStatusDisplayChanged(MemberListDisplay value)
    {
        var display = MemberListChoices.ToStored(value);
        Apply(s => WithActiveProfile(s, p => p with { StatusDisplay = display }));
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _coordinator is not null)
        {
            _coordinator.SnapshotChanged -= OnSnapshotChanged;
        }

        base.Dispose(disposing);
    }

    /// <summary>The plain name of a detail (design §8: a detail, never a token).</summary>
    private static string DetailLabel(string token) => token switch
    {
        TemplateTokens.Role => "Role",
        TemplateTokens.Character => "Character",
        TemplateTokens.Map => "Map",
        TemplateTokens.Realm => "Realm",
        TemplateTokens.Generators => "Generators left",
        TemplateTokens.Mode => "Game mode",
        TemplateTokens.Result => "Match result",
        TemplateTokens.Duration => "Time played",
        TemplateTokens.StatusDead => "Survivors out",
        TemplateTokens.StatusHooked => "Survivors on hooks",
        TemplateTokens.HookCount => "Hook count",
        _ => token,
    };

    private static string? ImageProblem(string value) =>
        ActivityValidator.IsValidImageReference(value.Trim())
            ? null
            : "Use a link that starts with https:// (up to 256 characters), or the name of a picture in your own Discord app (a–z, 0–9, _ - .).";

    private void OnSnapshotChanged(object? sender, EventArgs e) => Dispatcher.Post(() => RefreshPreview(Settings.Current));

    private PresenceTemplate CurrentTemplate(PresenceProfile profile)
    {
        var set = Target == TemplateTarget.Streamer ? profile.StreamerTemplates : profile.Templates;
        return set.ByState.TryGetValue(SelectedState.State, out var template) ? template : new PresenceTemplate { LargeImage = ImageSlot.AppLogo };
    }

    private void ReloadForSelection()
    {
        // A pending invalid entry belongs to the previous state; drop it rather than carry it across.
        LargeImageError = null;
        SmallImageError = null;
        ButtonsError = null;
        Reload();
    }

    private void EditSet(Func<TemplateSet, TemplateSet> change)
    {
        var target = Target;
        Apply(s => WithActiveProfile(s, p => target == TemplateTarget.Streamer
            ? p with { StreamerTemplates = change(p.StreamerTemplates) }
            : p with { Templates = change(p.Templates) }));
    }

    private void EditTemplate(Func<PresenceTemplate, PresenceTemplate> change)
    {
        if (IsLoading)
        {
            return;
        }

        var state = SelectedState.State;
        EditSet(set =>
        {
            var map = set.ByState.ToDictionary();
            var current = map.TryGetValue(state, out var existing) ? existing : new PresenceTemplate { LargeImage = ImageSlot.AppLogo };
            map[state] = change(current);
            return set with { ByState = map };
        });
        RefreshPreview(Settings.Current);
    }

    private void CommitImage(bool isLarge)
    {
        if (IsLoading)
        {
            return;
        }

        var source = isLarge ? LargeImageSource : SmallImageSource;
        var value = isLarge ? LargeImageValue : SmallImageValue;
        string? problem = null;
        ImageSlot? slot = source switch
        {
            ImageSource.Fixed when (problem = ImageProblem(value)) is null => new ImageSlot(ImageSource.Fixed, value.Trim()),
            ImageSource.Fixed => null,
            _ => new ImageSlot(source),
        };

        if (isLarge)
        {
            LargeImageError = problem;
        }
        else
        {
            SmallImageError = problem;
        }

        if (slot is not null)
        {
            EditTemplate(t => isLarge ? t with { LargeImage = slot } : t with { SmallImage = slot });
        }
    }

    private void LoadButtons(IReadOnlyList<ButtonTemplate> buttons)
    {
        if (ButtonsError is not null || SameButtons(buttons, EnteredButtons()))
        {
            return;
        }

        _loadingButtons = true;
        try
        {
            Button1Label = buttons.Count > 0 ? buttons[0].Label : string.Empty;
            Button1Url = buttons.Count > 0 ? buttons[0].Url : string.Empty;
            Button2Label = buttons.Count > 1 ? buttons[1].Label : string.Empty;
            Button2Url = buttons.Count > 1 ? buttons[1].Url : string.Empty;
        }
        finally
        {
            _loadingButtons = false;
        }
    }

    private List<ButtonTemplate> EnteredButtons() =>
    [
        .. new[] { (Button1Label, Button1Url), (Button2Label, Button2Url) }
            .Where(b => !string.IsNullOrWhiteSpace(b.Item1) || !string.IsNullOrWhiteSpace(b.Item2))
            .Select(b => new ButtonTemplate(b.Item1.Trim(), b.Item2.Trim())),
    ];

    private static bool SameButtons(IReadOnlyList<ButtonTemplate> a, IReadOnlyList<ButtonTemplate> b) => a.SequenceEqual(b);

    private void CommitButtons()
    {
        if (IsLoading || _loadingButtons)
        {
            return;
        }

        var buttons = EnteredButtons();
        var problems = new List<string>();
        foreach (var (button, index) in buttons.Select((b, i) => (b, i + 1)))
        {
            if (button.Label.Length == 0)
            {
                problems.Add($"Button {index} needs a label.");
            }
            else if (!TextFit.Fits(button.Label, DiscordLimits.ButtonLabelMaxChars, DiscordLimits.ButtonLabelMaxBytes))
            {
                problems.Add($"Button {index} label is too long (Discord allows {DiscordLimits.ButtonLabelMaxChars} characters).");
            }

            if (!ActivityValidator.IsHttpsUrl(button.Url, DiscordLimits.ButtonUrlMax))
            {
                problems.Add($"Button {index} needs an https:// link.");
            }
        }

        ButtonsError = problems.Count == 0 ? null : string.Join(" ", problems) + " Not saved until fixed.";
        if (problems.Count == 0)
        {
            EditTemplate(t => t with { Buttons = buttons });
        }
    }

    private void RefreshPreview(AppSettings settings)
    {
        var profile = settings.ActiveProfile;
        var state = SelectedState.State;
        var now = _time.GetUtcNow();
        var snapshot = _coordinator?.Snapshot;
        var liveState = snapshot?.State ?? PresenceState.Running;
        var editedStreamer = Target == TemplateTarget.Streamer;
        var liveStreamer = settings.Privacy.StreamerMode;
        var paused = snapshot?.Paused ?? false;
        var editingLive = state == liveState && editedStreamer == liveStreamer;
        IsEditingLive = editingLive;

        // The situation being edited, composed from the real role, settings and game. Nothing is invented: what is not known is missing.
        var editProfile = profile with { WhenNotRunning = NotRunningBehavior.ShowTemplate };
        var live = snapshot?.Inputs ?? new PresenceInputs { Now = now, GameStartedUtc = snapshot?.Game.StartedUtc };
        var inputs = live with { State = state, Now = now };
        var artwork = _packs?.Apply(settings, editProfile, inputs.DetectedRole, inputs.CharacterName) ?? profile.Artwork;
        editProfile = editProfile with { Artwork = artwork };
        var composition = PresenceComposer.Compose(inputs, editProfile, new PrivacyState(StreamerMode: editedStreamer), settings.Discord.DisplayName);

        // The live situation uses the coordinator's own composed activity, so the card is the payload Discord gets (while paused: the
        // payload it will get on resume), not a second computation.
        var useSnapshot = editingLive && snapshot is not null;
        var shown = useSnapshot ? (paused ? snapshot!.ActivityIfNotPaused : snapshot!.Composition.Activity) : composition.Activity;
        var shownArtwork = useSnapshot ? snapshot!.Artwork ?? artwork : artwork;
        Func<string, PackReferenceMatch?>? match = _packs is { } packs ? r => packs.Match(r) : null;
        Preview = ActivityPreview.From(shown, ResolvedApplication.From(settings.Discord, _catalog).Name, shownArtwork, now, match);
        PreviewNotes = useSnapshot
            ? [.. snapshot!.Composition.Notes, .. snapshot.Composition.Issues.Select(i => i.Reason)]
            : [.. composition.Notes, .. composition.Issues.Select(i => i.Reason)];
        FallbackExplanation = composition.TemplateState switch
        {
            null => "Nothing applies to this moment, so Discord would show nothing.",
            { } used when used == state => $"Discord is using your “{StudioText.StateName(state)}” text.",
            { } used => $"“{StudioText.StateName(state)}” is empty or needs a detail the app isn't sure of, so your “{StudioText.StateName(used)}” text is used.",
        };
        ReachabilityNote = StudioText.ReachabilityOf(state) == Reachability.Always
            ? state == PresenceState.NotRunning
                ? "Shown only when “Show Taking a break while the game is closed” is on."
                : "Shown whenever this moment applies."
            : "Needs screen reading. Until the app is sure where you are, Discord shows your “Playing (no screen reading)” text.";

        var liveSet = liveStreamer ? "Private style" : "my usual style";
        var editedSet = editedStreamer ? "Private style" : "my usual style";
        LiveStateNote = editingLive
            ? paused ? "Editing what Discord shows now. You're paused, so this is what shows when you resume." : "Editing what Discord shows now."
            : $"Editing: {StudioText.StateName(state)} ({editedSet}) · Discord shows: {StudioText.StateName(liveState)} ({liveSet}{(paused ? ", paused" : string.Empty)}). "
                + "Changes here may not appear until that moment happens.";

        Hints = PreviewHints.Build(
            new PreviewHintInputs(paused, profile.WhenPaused == PauseBehavior.Generic, editingLive && liveState == PresenceState.NotRunning, shown is not null, Preview),
            () => _coordinator?.SetPaused(false));

        var shortened = composition.Issues.Where(i => i.Action == ValidationAction.Truncated).Select(i => i.Field).ToHashSet();
        var own = composition.TemplateState == state ? composition.Activity : null;
        Details.Evaluate(own?.Details, shortened.Contains(ActivityField.Details) && own is not null);
        StateLine.Evaluate(own?.State, shortened.Contains(ActivityField.State) && own is not null);
        LargeText.Evaluate(own?.LargeText, shortened.Contains(ActivityField.LargeText) && own is not null);
        SmallText.Evaluate(own?.SmallText, shortened.Contains(ActivityField.SmallText) && own is not null);
    }
}
