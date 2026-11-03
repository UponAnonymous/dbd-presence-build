using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using DbdPresence.Packs.Roster;
using DbdPresence.Packs.Sync;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Characters;

/// <summary>The list filter (design §5.3).</summary>
public enum CharacterFilter
{
    /// <summary>Every character.</summary>
    All,

    /// <summary>Killers only.</summary>
    Killers,

    /// <summary>Survivors only.</summary>
    Survivors,
}

/// <summary>What the side panel shows (design §4).</summary>
public enum CharacterPanelMode
{
    /// <summary>The selected character: previews and pictures.</summary>
    Character,

    /// <summary>The ways to add a picture.</summary>
    AddPicture,

    /// <summary>The link form.</summary>
    AddLink,

    /// <summary>Packs, online storage and picture size.</summary>
    Packs,
}

/// <summary>What the page shows below the header.</summary>
public enum CharactersStage
{
    /// <summary>The toolbar, the list and the side panel.</summary>
    Browse,

    /// <summary>An archive is being read and its pictures opened by the image helper.</summary>
    Reading,

    /// <summary>The import review replaces the toolbar and the body.</summary>
    Review,
}

/// <summary>Which line the status strip shows (design §6.1).</summary>
public enum CharactersStatusKind
{
    /// <summary>Ready: default portraits are online, own pictures are saved when added (S1).</summary>
    Ready,

    /// <summary>Signed out (S2).</summary>
    SignedOut,

    /// <summary>First run, signed out, no default portraits yet (S2b).</summary>
    FirstRun,

    /// <summary>Picture rules declined (S6).</summary>
    RulesDeclined,

    /// <summary>Online storage full (S7).</summary>
    StorageFull,

    /// <summary>Saving online is off for this account (S8-style).</summary>
    SavingOff,
}

/// <summary>
/// The Characters page (docs/design/characters-page.md, FR-PACK-06, FR-PACK-07, FR-PACK-10, FR-PACK-12..17, S5b-5). The list shows every killer and
/// survivor of the roster, filtered and searched; each has a picture source, a Discord line and up to three own pictures besides the default
/// portrait. Untrusted images are opened and made smaller only by the image helper (ADR-0013), links are only text (FR-PACK-14), and the page
/// uses no network: saving online is a plug-in (<see cref="IPictureUploadSink"/>, S5b-6). The view only binds.
/// </summary>
public sealed partial class CharactersViewModel : SettingsPageViewModel
{
    private readonly PackLibrary _library;

    /// <summary>Counts the steps that use the Reading stage (reading an archive, saving the confirmed pictures), so a late progress report of an earlier step is ignored.</summary>
    private int _readingRun;
    private readonly PictureStore _store;
    private readonly IFilePicker _picker;
    private readonly IPictureEnvironment _environment;
    private readonly IPictureUploadSink? _sink;
    private readonly CharacterPictureResolver _resolver;
    private readonly List<CharacterTileViewModel> _all = [];
    private readonly List<PictureItem> _pendingDeletes = [];
    private CharacterRoster _roster;
    private IReadOnlySet<string> _newCodes = new HashSet<string>(StringComparer.Ordinal);
    private bool _newDismissed;
    private PictureContext? _context;
    private ImportUndo? _undo;
    private CancellationTokenSource? _importCancel;
    private bool _wasSignedIn;
    private bool _confirming;

    /// <summary>Initializes a new instance of the <see cref="CharactersViewModel"/> class.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="dispatcher">Runs UI updates on the UI thread.</param>
    /// <param name="roster">The characters of the page: the shipped list plus synced additions.</param>
    /// <param name="library">The pack library (owner and imported packs).</param>
    /// <param name="store">Saves the user's own pictures.</param>
    /// <param name="picker">File dialogs.</param>
    /// <param name="environment">Account and sync facts; null for a build without online features.</param>
    /// <param name="uploadSink">Where saved pictures are reported for saving online (S5b-6); null while there is none.</param>
    /// <param name="resolver">The resolver the presence artwork also uses (D29); null makes one for this page.</param>
    /// <param name="sync">Checks for new owner packs (FR-ONL-07); null for a build without online features.</param>
    public CharactersViewModel(
        ISettingsService settings,
        IUiDispatcher dispatcher,
        CharacterRoster roster,
        PackLibrary library,
        PictureStore store,
        IFilePicker picker,
        IPictureEnvironment? environment = null,
        IPictureUploadSink? uploadSink = null,
        CharacterPictureResolver? resolver = null,
        IPackSyncService? sync = null)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(picker);
        _roster = roster;
        _library = library;
        _store = store;
        _picker = picker;
        _environment = environment ?? new OfflinePictureEnvironment();
        _sink = uploadSink;
        _resolver = resolver ?? new CharacterPictureResolver(library, store, _environment, roster);
        StartSync(sync);
        _environment.Changed += OnEnvironmentChanged;
        library.SyncedChanged += OnEnvironmentChanged;
        BuildTiles();
        SelectedTile = _all.FirstOrDefault(t => t.Role == Role.Killer) ?? _all.FirstOrDefault();
        _wasSignedIn = _environment.SignedIn;
        Reload();
    }

    /// <summary>Raised when the page asks the shell to open another page; the argument is the page key (for example <c>account</c>).</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>Gets the text of the Behaviour notice under the header (design §7.1).</summary>
    public string BehaviourNotice { get; } = AppCopy.BehaviourNotice;

    /// <summary>Gets the filtered, searched list: killers first, then survivors, each by name. The view groups by <see cref="CharacterTileViewModel.Group"/>.</summary>
    public ObservableCollection<CharacterTileViewModel> Tiles { get; } = [];

    /// <summary>Gets or sets the search text (name, code or id).</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Gets or sets the role filter.</summary>
    [ObservableProperty]
    public partial CharacterFilter Filter { get; set; } = CharacterFilter.All;

    /// <summary>Gets or sets a value indicating whether only characters the user changed are listed.</summary>
    [ObservableProperty]
    public partial bool ChangedByMeOnly { get; set; }

    /// <summary>Gets the filter button text "All 92".</summary>
    [ObservableProperty]
    public partial string AllFilterText { get; private set; } = string.Empty;

    /// <summary>Gets the filter button text "Killers 40".</summary>
    [ObservableProperty]
    public partial string KillersFilterText { get; private set; } = string.Empty;

    /// <summary>Gets the filter button text "Survivors 52".</summary>
    [ObservableProperty]
    public partial string SurvivorsFilterText { get; private set; } = string.Empty;

    /// <summary>Gets "Showing 3 of 92 characters" while a filter or search is active, otherwise null (a live region).</summary>
    [ObservableProperty]
    public partial string? ResultCountText { get; private set; }

    /// <summary>Gets the sentence shown instead of the list when nothing matches, or null.</summary>
    [ObservableProperty]
    public partial string? NoResultsText { get; private set; }

    /// <summary>Gets or sets the selected character.</summary>
    [ObservableProperty]
    public partial CharacterTileViewModel? SelectedTile { get; set; }

    /// <summary>Gets the stage of the page.</summary>
    [ObservableProperty]
    public partial CharactersStage Stage { get; private set; }

    /// <summary>Gets what the side panel shows.</summary>
    [ObservableProperty]
    public partial CharacterPanelMode PanelMode { get; private set; }

    /// <summary>Gets the status strip kind.</summary>
    [ObservableProperty]
    public partial CharactersStatusKind StatusKind { get; private set; }

    /// <summary>Gets the status strip sentence (design §7.2).</summary>
    [ObservableProperty]
    public partial string StatusStripText { get; private set; } = string.Empty;

    /// <summary>Gets the status strip caption, including "12.4 of 60 MB used".</summary>
    [ObservableProperty]
    public partial string StatusStripCaption { get; private set; } = string.Empty;

    /// <summary>Gets the file of the "Your preview" picture of the selected character, or null.</summary>
    [ObservableProperty]
    public partial string? PreviewImagePath { get; private set; }

    /// <summary>Gets the link of the preview when the picture in use is a link (shown as a labelled tile, never loaded), or null.</summary>
    [ObservableProperty]
    public partial string? PreviewLink { get; private set; }

    /// <summary>Gets the file of the "Discord shows" picture, found from the address Discord is given (D29), or null.</summary>
    [ObservableProperty]
    public partial string? DiscordImagePath { get; private set; }

    /// <summary>Gets the address Discord is given for the selected character, or null for no picture. Shown to nobody; for tests and the hover text.</summary>
    [ObservableProperty]
    public partial string? DiscordReference { get; private set; }

    /// <summary>Gets a value indicating whether Discord shows the same picture as the preview (the pair collapses into one).</summary>
    [ObservableProperty]
    public partial bool PreviewAndDiscordSame { get; private set; } = true;

    /// <summary>Gets the sentence under the pair explaining the difference, or null.</summary>
    [ObservableProperty]
    public partial string? DifferenceText { get; private set; }

    /// <summary>Gets the pack name when the pictures come from a pack with Behaviour artwork, so the hover text carries the label; otherwise false.</summary>
    [ObservableProperty]
    public partial bool DiscordPictureIsBehaviourArt { get; private set; }

    /// <summary>Gets the picture options of the selected character: the default portrait, the owner picture in use, then the user's own (at most 4 without an owner pick).</summary>
    public ObservableCollection<PictureOptionViewModel> PictureOptions { get; } = [];

    /// <summary>Gets "Your pictures: 2 of 3".</summary>
    [ObservableProperty]
    public partial string CounterText { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether another picture can be added (fewer than three own pictures).</summary>
    [ObservableProperty]
    public partial bool CanAddPicture { get; private set; } = true;

    /// <summary>Gets the sentence shown instead of the Add button when the character has three pictures.</summary>
    [ObservableProperty]
    public partial string? FullText { get; private set; }

    /// <summary>Gets the sentence for a character without a default portrait, or null.</summary>
    [ObservableProperty]
    public partial string? NoDefaultText { get; private set; }

    /// <summary>Gets "The selected picture is the one your status uses." (design §7.4).</summary>
    public string HelperText { get; } = "The selected picture is the one your status uses.";

    /// <summary>Gets the pictures of other packs that can be added to the selected character.</summary>
    public ObservableCollection<PackPictureChoiceViewModel> PackPictures { get; } = [];

    /// <summary>Gets "None of your other packs has a picture of X." or null when some do.</summary>
    [ObservableProperty]
    public partial string? NoPackPicturesText { get; private set; }

    /// <summary>Gets the help under "From my PC…" with the size and the free online space.</summary>
    [ObservableProperty]
    public partial string PcHelpText { get; private set; } = string.Empty;

    /// <summary>Gets the help under "From a link…".</summary>
    public string LinkHelpText { get; } = "Nothing is stored online for a link.";

    /// <summary>Gets the help in the link form.</summary>
    public string LinkFormHelp { get; } = "Use a direct link to a PNG or JPEG. The app never opens the link; Discord loads it.";

    /// <summary>Gets the warning in the link form (design §7.5).</summary>
    public string LinkWarning { get; } = "The site that hosts the picture can see when Discord loads it, and can change the picture at any time.";

    /// <summary>Gets or sets the text of the link box; checked as the user types.</summary>
    [ObservableProperty]
    public partial string LinkText { get; set; } = string.Empty;

    /// <summary>Gets the link error, or null while the link is empty or acceptable.</summary>
    [ObservableProperty]
    public partial string? LinkError { get; private set; }

    /// <summary>Gets "Checking the picture…" or "Saving online…" while the helper works, otherwise null (a live region).</summary>
    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    /// <summary>Gets the last message about a change (live region): "X now uses Y.", "Removed.".</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Gets the last error about an add or remove (live region), or null.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Gets the review of the import in progress, or null.</summary>
    [ObservableProperty]
    public partial ImportReviewViewModel? ImportReview { get; private set; }

    /// <summary>Gets "Reading Hollow-Ink.zip…" while an archive is read.</summary>
    [ObservableProperty]
    public partial string? ReadingText { get; private set; }

    /// <summary>Gets the sentence after an import (design §7.6), or null.</summary>
    [ObservableProperty]
    public partial string? ImportResultText { get; private set; }

    /// <summary>Gets the import error, or null.</summary>
    [ObservableProperty]
    public partial string? ImportError { get; private set; }

    /// <summary>Gets a value indicating whether the last import can be undone.</summary>
    public bool CanUndoImport => _undo is not null;

    /// <summary>Gets a value indicating whether the picture rules panel (P1) shows: signed in, rules not answered, and own pictures exist.</summary>
    [ObservableProperty]
    public partial bool ShowRulesPanel { get; private set; }

    /// <summary>Gets the P1 count line when pictures are waiting, or null.</summary>
    [ObservableProperty]
    public partial string? RulesCountText { get; private set; }

    /// <summary>Gets the rules the panel lists.</summary>
    public IReadOnlyList<string> RuleLines { get; } =
    [
        "Use only pictures you made or are allowed to use.",
        "Nothing offensive, hateful, sexual or illegal.",
        "The owner may remove any picture.",
    ];

    /// <summary>Gets a value indicating whether the user declined the rules (own pictures stay in the preview).</summary>
    [ObservableProperty]
    public partial bool RulesDeclined { get; private set; }

    /// <summary>Gets "12.4 of 60 MB used".</summary>
    [ObservableProperty]
    public partial string StorageText { get; private set; } = string.Empty;

    /// <summary>Gets the share of the online storage used, 0 to 1 (the meter).</summary>
    [ObservableProperty]
    public partial double StorageFraction { get; private set; }

    /// <summary>Gets the storage caption "12.4 of 60 MB used · 57 pictures".</summary>
    [ObservableProperty]
    public partial string StorageCaption { get; private set; } = string.Empty;

    /// <summary>Gets how many own pictures stay in the preview only because the online storage is full.</summary>
    [ObservableProperty]
    public partial int StorageFullCount { get; private set; }

    /// <summary>Gets how many own pictures there are on this PC (links excluded).</summary>
    [ObservableProperty]
    public partial int OwnPictureCount { get; private set; }

    /// <summary>Gets the sizes the user can choose: Standard (512 × 512) and Large (1024 × 1024).</summary>
    public IReadOnlyList<Choice<int>> PictureSizes { get; } =
    [
        new(PictureLimits.DefaultSize, "Standard (512 × 512)"),
        new(PictureLimits.LargeSize, "Large (1024 × 1024)"),
    ];

    /// <summary>Gets or sets the size of pictures saved from now on.</summary>
    [ObservableProperty]
    public partial Choice<int> SelectedPictureSize { get; set; } = new(PictureLimits.DefaultSize, "Standard (512 × 512)");

    /// <summary>Gets the help under the size box.</summary>
    public string PictureSizeHelp { get; } =
        "Large pictures look sharper in Discord's profile pop-out but use more of your 60 MB. Applies to pictures saved from now on.";

    /// <summary>Gets the one-time notice about what the settings migration left out, or null.</summary>
    [ObservableProperty]
    public partial string? MigrationNoticeText { get; private set; }

    /// <summary>Gets "New in the list: A and B." or null.</summary>
    [ObservableProperty]
    public partial string? NewCharactersText { get; private set; }

    /// <summary>Replaces the roster after a sync added characters (D27). Characters in <paramref name="newCodes"/> get the New badge.</summary>
    /// <param name="roster">The merged roster.</param>
    /// <param name="newCodes">Codes added lately, or null for none.</param>
    public void SetRoster(CharacterRoster roster, IReadOnlySet<string>? newCodes = null)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var selectedCode = SelectedTile?.Code;
        _roster = roster;
        _resolver.SetRoster(roster);
        _newCodes = newCodes ?? new HashSet<string>(StringComparer.Ordinal);
        _newDismissed = false;
        BuildTiles();
        SelectedTile = _all.FirstOrDefault(t => t.Code == selectedCode) ?? _all.FirstOrDefault(t => t.Role == Role.Killer) ?? _all.FirstOrDefault();
        Reload();
    }

    /// <summary>Opens the character whose name was clicked in the New banner.</summary>
    /// <param name="code">The character code.</param>
    public void SelectByCode(string code)
    {
        if (_all.FirstOrDefault(t => t.Code == code) is { } tile)
        {
            SearchText = string.Empty;
            Filter = CharacterFilter.All;
            ChangedByMeOnly = false;
            SelectedTile = tile;
        }
    }

    /// <summary>Asks the shell to open the Account page (the status strip button when signed out).</summary>
    [RelayCommand]
    private void GoToAccount() => NavigateRequested?.Invoke(this, "account");

    /// <summary>Hides the New banner.</summary>
    [RelayCommand]
    private void DismissNew()
    {
        _newDismissed = true;
        NewCharactersText = null;
    }

    /// <summary>Clears the search box and the filter chip.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        ChangedByMeOnly = false;
    }

    /// <summary>Shows the ways to add a picture.</summary>
    [RelayCommand]
    private void ShowAddPicture()
    {
        ErrorMessage = null;
        PanelMode = CharacterPanelMode.AddPicture;
        RefreshPackPictures(Settings.Current);
    }

    /// <summary>Shows the link form.</summary>
    [RelayCommand]
    private void ShowAddLink()
    {
        ErrorMessage = null;
        LinkText = string.Empty;
        PanelMode = CharacterPanelMode.AddLink;
    }

    /// <summary>Shows the Packs panel (storage, packs, picture size).</summary>
    [RelayCommand]
    private void ShowPacks()
    {
        ErrorMessage = null;
        PanelMode = CharacterPanelMode.Packs;
    }

    /// <summary>Goes back to the selected character (Escape).</summary>
    [RelayCommand]
    private void Back()
    {
        ErrorMessage = null;
        PanelMode = CharacterPanelMode.Character;
    }

    /// <summary>Adds a picture from a file the user chooses. The image helper opens it and makes it smaller (FR-PACK-10).</summary>
    [RelayCommand]
    private async Task AddFromPcAsync(CancellationToken cancellationToken)
    {
        if (SelectedTile is not { } tile || !CanAddPicture || BusyText is not null)
        {
            return; // a second click while the first picture is still being checked starts nothing
        }

        var path = _picker.PickImage();
        if (path is null)
        {
            return;
        }

        ErrorMessage = null;
        BusyText = "Checking the picture…";
        var size = Settings.Current.PictureSize;
        PictureIntakeResult result;
        try
        {
            result = await _store.AddFileAsync(path, size, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Post(() => BusyText = null);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new PictureIntakeResult(null, $"That picture could not be added ({ex.GetType().Name}). Try another file.");
        }

        Dispatcher.Post(() => FinishAdd(tile, result));
    }

    /// <summary>Adds the link in the link box (checked, never fetched; FR-PACK-14).</summary>
    [RelayCommand]
    private void AddLink()
    {
        if (SelectedTile is not { } tile)
        {
            return;
        }

        var result = _store.AddLink(LinkText);
        FinishAdd(tile, result);
    }

    /// <summary>Accepts the picture rules; pictures waiting are then reported for saving online.</summary>
    [RelayCommand]
    private void AcceptRules()
    {
        Apply(s => s with { PictureRules = PictureRules.Accepted });
        ReportPending();
    }

    private void ReportPending()
    {
        foreach (var (key, item) in OwnFilePictures(Settings.Current))
        {
            if (!_environment.SavedUrlBySha256.ContainsKey(item.Sha256))
            {
                ReportSaved(key, item, Settings.Current);
            }
        }
    }

    /// <summary>Keeps own pictures in the preview only.</summary>
    [RelayCommand]
    private void DeclineRules() => Apply(s => s with { PictureRules = PictureRules.Declined });

    /// <summary>Reopens the rules panel after they were declined.</summary>
    [RelayCommand]
    private void ReviewRules() => Apply(s => s with { PictureRules = PictureRules.Unset });

    /// <summary>Hides the one-time migration notice.</summary>
    [RelayCommand]
    private void DismissMigrationNotice() => Apply(s => s with { PictureNotice = null });

    /// <summary>Asks for a pack archive, reads it in the image helper and opens the review.</summary>
    [RelayCommand]
    private async Task ImportPackAsync()
    {
        if (Stage == CharactersStage.Reading)
        {
            return; // an import is already being read
        }

        var path = _picker.PickPackArchive();
        if (path is null)
        {
            return;
        }

        FinishUndoWindow();
        ImportError = null;
        ImportResultText = null;
        var name = Path.GetFileName(path);
        _importCancel?.Dispose();
        _importCancel = new CancellationTokenSource();
        var token = _importCancel.Token;
        Stage = CharactersStage.Reading;
        var run = ++_readingRun;
        ReadingText = $"Reading {name}… Each picture is opened by a separate helper program and saved again as a clean PNG.";
        var progress = new Progress<ImportProgress>(p => Dispatcher.Post(() =>
        {
            // Progress reports arrive later than the work they describe; one that comes after this read ended (or while the
            // confirm step saves the pictures, which also uses the Reading stage) must not overwrite the newer text.
            if (Stage == CharactersStage.Reading && run == _readingRun)
            {
                ReadingText = $"Reading {name}… picture {p.Done.ToString(CultureInfo.InvariantCulture)} of {p.Total.ToString(CultureInfo.InvariantCulture)}.";
            }
        }));
        try
        {
            var analysis = await _library.AnalyseImportAsync(path, token, progress).ConfigureAwait(false);
            Dispatcher.Post(() => OpenReview(name, analysis));
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Post(() =>
            {
                Stage = CharactersStage.Browse;
                ReadingText = null;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Dispatcher.Post(() =>
            {
                Stage = CharactersStage.Browse;
                ReadingText = null;
                ImportError = $"Couldn't read {name}. The archive could not be opened ({ex.GetType().Name}). Nothing was imported.";
            });
        }
    }

    /// <summary>Stops reading the archive.</summary>
    [RelayCommand]
    private void CancelReading() => _importCancel?.Cancel();

    /// <summary>Undoes the last import: the pictures it added go, the pictures it replaced and the choices it changed come back.</summary>
    [RelayCommand]
    private void UndoImport()
    {
        if (_undo is not { } undo)
        {
            return;
        }

        var before = undo.Before;
        Apply(s => s with { Pictures = RestorePictures(s.Pictures, before) });
        foreach (var item in undo.Added)
        {
            DeleteIfUnused(item);
        }

        _pendingDeletes.Clear();
        _undo = null;
        ImportResultText = null;
        StatusMessage = $"Import undone. Nothing from {undo.PackName} is left on this PC or online.";
        OnPropertyChanged(nameof(CanUndoImport));
    }

    /// <summary>Hides the import result and makes it final: pictures that were replaced are deleted now.</summary>
    [RelayCommand]
    private void DismissImportResult()
    {
        FinishUndoWindow();
        ImportResultText = null;
        ImportError = null;
    }

    /// <summary>Hides the import error.</summary>
    [RelayCommand]
    private void DismissImportError() => ImportError = null;

    /// <summary>Releases subscriptions and makes a pending import final.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _environment.Changed -= OnEnvironmentChanged;
            _library.SyncedChanged -= OnEnvironmentChanged;
            StopSync();
            _importCancel?.Dispose();
            FinishUndoWindow();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        _context = BuildContext(settings);
        SelectedPictureSize = PictureSizes.FirstOrDefault(c => c.Value == settings.PictureSize) ?? PictureSizes[0];
        foreach (var tile in _all)
        {
            UpdateTile(tile, settings);
        }

        ApplyFilter();
        RefreshStorage(settings); // counts the pictures kept back by a full storage, which the status strip reads
        RefreshStatus(settings);
        RefreshRules(settings);
        RefreshNotices(settings);
        RefreshDetail(settings);
        RefreshPackRows();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFilterChanged(CharacterFilter value) => ApplyFilter();

    partial void OnChangedByMeOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedTileChanged(CharacterTileViewModel? value)
    {
        PanelMode = CharacterPanelMode.Character;
        ErrorMessage = null;
        if (value is not null && !IsLoading)
        {
            RefreshDetail(Settings.Current);
        }
    }

    partial void OnLinkTextChanged(string value)
    {
        var check = LinkPolicy.Check(value);
        LinkError = string.IsNullOrWhiteSpace(value) || check.IsValid ? null : check.Message;
    }

    partial void OnSelectedPictureSizeChanged(Choice<int> value)
    {
        if (value is not null && value.Value != Settings.Current.PictureSize)
        {
            Apply(s => s with { PictureSize = value.Value });
        }
    }

    private static string Fold(string value) => CharacterPictureResolver.Fold(value);

    private static Dictionary<string, CharacterPictures> RestorePictures(
        IReadOnlyDictionary<string, CharacterPictures> now, IReadOnlyDictionary<string, CharacterPictures?> before)
    {
        var result = new Dictionary<string, CharacterPictures>(now, StringComparer.Ordinal);
        foreach (var (key, pictures) in before)
        {
            if (pictures is null)
            {
                result.Remove(key);
            }
            else
            {
                result[key] = pictures;
            }
        }

        return result;
    }

    private void OnEnvironmentChanged(object? sender, EventArgs e) => Dispatcher.Post(() =>
    {
        var signedIn = _environment.SignedIn;
        Reload();
        if (signedIn && !_wasSignedIn && Settings.Current.PictureRules == PictureRules.Accepted)
        {
            ReportPending(); // pictures added while signed out are handed to the queue now
        }

        _wasSignedIn = signedIn;
    });

    private void BuildTiles()
    {
        _all.Clear();
        foreach (var c in _roster.Characters.OrderBy(c => c.Role == Role.Killer ? 0 : 1).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _all.Add(new CharacterTileViewModel(c.Code, c.Id, c.Name, c.Role));
        }
    }

    private PictureContext BuildContext(AppSettings settings) => _resolver.BuildContext(settings);

    private PictureResolution Resolve(CharacterTileViewModel tile, CharacterPictures? pictures) =>
        PictureResolver.Resolve(tile.Key, tile.Role, pictures, _context!);

    private void UpdateTile(CharacterTileViewModel tile, AppSettings settings)
    {
        var pictures = settings.Pictures.GetValueOrDefault(tile.Key);
        var resolution = Resolve(tile, pictures);
        var preview = resolution.Preview;
        var firstRun = _library.Synced is null && !_environment.SignedIn;
        var caption = preview.Source switch
        {
            PictureSource.OwnFile or PictureSource.Missing => pictures?.Items.FirstOrDefault(i => i.Sha256 == pictures.Use?.Sha256)?.Origin == PictureOrigin.Import
                ? "Imported"
                : "From my PC",
            PictureSource.OwnLink => "My link",
            PictureSource.ChosenOwnerPack or PictureSource.CharactersPack => preview.LocalOnly ? "Imported" : preview.PackName ?? "Default",
            PictureSource.DefaultPack => "Default",
            _ => firstRun ? "Not downloaded" : "No picture",
        };
        var showsPicture = preview.Source is not (PictureSource.None or PictureSource.RoleIcon or PictureSource.GenericTile or PictureSource.FallbackPack);
        var why = resolution.Discord.WhyNotChosen;
        var line = why is { } w
            ? PictureTexts.DiscordLine(w)
            : resolution.Discord.Reference is not null ? "On Discord" : "No picture on Discord";
        tile.Update(
            preview.LocalPath,
            caption,
            resolution.DiffersFromPreview,
            PictureTexts.PreviewOnlyTooltip(why),
            pictures is { Items.Count: > 0 } || pictures?.Use is not null,
            _newCodes.Contains(tile.Code),
            !showsPicture,
            line);
    }

    private void ApplyFilter()
    {
        var search = Fold(SearchText);
        bool Matches(CharacterTileViewModel t) =>
            (Filter == CharacterFilter.All || (Filter == CharacterFilter.Killers) == (t.Role == Role.Killer))
            && (!ChangedByMeOnly || t.IsChangedByMe)
            && (search.Length == 0 || Fold(t.Name).Contains(search, StringComparison.Ordinal) || Fold(t.Id).Contains(search, StringComparison.Ordinal)
                || t.Code.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        var shown = _all.Where(Matches).ToList();
        if (!Tiles.SequenceEqual(shown))
        {
            Tiles.Clear();
            foreach (var tile in shown)
            {
                Tiles.Add(tile);
            }
        }

        var killers = _all.Count(t => t.Role == Role.Killer);
        AllFilterText = $"All {_all.Count.ToString(CultureInfo.InvariantCulture)}";
        KillersFilterText = $"Killers {killers.ToString(CultureInfo.InvariantCulture)}";
        SurvivorsFilterText = $"Survivors {(_all.Count - killers).ToString(CultureInfo.InvariantCulture)}";
        var filtered = search.Length > 0 || Filter != CharacterFilter.All || ChangedByMeOnly;
        ResultCountText = filtered
            ? $"Showing {shown.Count.ToString(CultureInfo.InvariantCulture)} of {_all.Count.ToString(CultureInfo.InvariantCulture)} characters"
            : null;
        NoResultsText = shown.Count > 0
            ? null
            : search.Length > 0 ? $"No characters match “{SearchText.Trim()}”." : "You haven't changed any characters yet.";
    }

    private void RefreshStatus(AppSettings settings)
    {
        var limit = _environment.LimitBytes;
        var used = PictureTexts.StorageUsed(_environment.UsedBytes, limit);
        var firstRun = _library.Synced is null && !_environment.SignedIn;
        StatusKind = settings.PictureRules == PictureRules.Declined
            ? CharactersStatusKind.RulesDeclined
            : firstRun
                ? CharactersStatusKind.FirstRun
                : !_environment.SignedIn
                    ? CharactersStatusKind.SignedOut
                    : _environment.UploadsBlocked
                        ? CharactersStatusKind.SavingOff
                        : StorageFullCount > 0 ? CharactersStatusKind.StorageFull : CharactersStatusKind.Ready;
        StatusStripText = StatusKind switch
        {
            CharactersStatusKind.RulesDeclined => "Your own pictures stay in the preview because the picture rules aren't accepted.",
            CharactersStatusKind.FirstRun =>
                "Default portraits download when you sign in. Until then Discord shows no character picture, and your own pictures appear in the preview only.",
            CharactersStatusKind.SignedOut =>
                "While you're signed out, Discord shows default portraits only. Your own pictures appear in the preview here and are saved online when you sign in.",
            CharactersStatusKind.SavingOff =>
                "Saving pictures online is paused. Pictures already online keep working; new ones stay in the preview until it's back on.",
            CharactersStatusKind.StorageFull =>
                $"Your online storage is full: {used}. {StorageFullCount.ToString(CultureInfo.InvariantCulture)} of your pictures stay in the preview only, so Discord shows the default portrait for them. Remove pictures you don't use to free space; the rest are saved then.",
            _ => "Default portraits are online for everyone. Your own pictures are saved online at an unlisted address when you add or import them, so Discord can show them.",
        };
        StatusStripCaption = StatusKind == CharactersStatusKind.Ready
            ? $"Only your status uses your pictures, but anyone who sees your status can see them. {used}."
            : string.Empty;
    }

    private void RefreshStorage(AppSettings settings)
    {
        var limit = _environment.LimitBytes;
        var files = settings.Pictures.Values.SelectMany(p => p.Items).Where(i => i.File is not null).DistinctBy(i => i.Sha256).ToList();
        OwnPictureCount = files.Count;
        StorageText = PictureTexts.StorageUsed(_environment.UsedBytes, limit);
        StorageFraction = limit <= 0 ? 0 : Math.Clamp((double)_environment.UsedBytes / limit, 0, 1);
        StorageCaption = $"{StorageText} · {_environment.SavedUrlBySha256.Count.ToString(CultureInfo.InvariantCulture)} pictures";
        StorageFullCount = 0;
        if (_context is not null)
        {
            foreach (var (key, pictures) in settings.Pictures)
            {
                var tile = _all.FirstOrDefault(t => t.Key == key);
                if (tile is null)
                {
                    continue;
                }

                foreach (var item in pictures.Items.Where(i => i.File is not null))
                {
                    var trial = new CharacterPictures(pictures.Items, PictureUse.Personal(item.Sha256));
                    if (Resolve(tile, trial).Discord.WhyNotChosen == PictureOnlineStatus.StorageFull)
                    {
                        StorageFullCount++;
                    }
                }
            }
        }
    }

    private void RefreshRules(AppSettings settings)
    {
        RulesDeclined = settings.PictureRules == PictureRules.Declined;
        var waiting = OwnFilePictures(settings).Where(p => !_environment.SavedUrlBySha256.ContainsKey(p.Item.Sha256)).ToList();
        ShowRulesPanel = _environment.SignedIn && settings.PictureRules == PictureRules.Unset && OwnFilePictures(settings).Any();
        var bytes = waiting.Sum(p => _store.BytesOf(p.Item));
        RulesCountText = ShowRulesPanel && waiting.Count > 0
            ? $"{waiting.Count.ToString(CultureInfo.InvariantCulture)} of your pictures on this PC ({PictureTexts.Megabytes(bytes)} MB) are saved online when you accept."
            : null;
    }

    private void RefreshNotices(AppSettings settings)
    {
        MigrationNoticeText = settings.PictureNotice is { HasContent: true } notice ? NoticeText(notice) : null;
        var added = _all.Where(t => _newCodes.Contains(t.Code)).Select(t => t.Name).ToList();
        NewCharactersText = added.Count == 0 || _newDismissed ? null : $"New in the list: {string.Join(" and ", added)}.";
        PcHelpText = _environment.SignedIn && settings.PictureRules != PictureRules.Declined
            ? $"PNG or JPEG, up to 8 MB. It's resized to {settings.PictureSize.ToString(CultureInfo.InvariantCulture)} × {settings.PictureSize.ToString(CultureInfo.InvariantCulture)} and compressed (usually 100–400 KB), then saved online for your status. {PictureTexts.Megabytes(Math.Max(0, _environment.LimitBytes - _environment.UsedBytes))} MB of {(_environment.LimitBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB free."
            : $"PNG or JPEG, up to 8 MB. It's resized to {settings.PictureSize.ToString(CultureInfo.InvariantCulture)} × {settings.PictureSize.ToString(CultureInfo.InvariantCulture)} and shown in your preview; it's saved online when you sign in.";
    }

    private static string NoticeText(PictureMigrationNotice notice)
    {
        var parts = new List<string>();
        if (notice.GenericOverrides.Count > 0)
        {
            parts.Add($"Pictures you set for role icons and generic tiles ({string.Join(", ", notice.GenericOverrides)}) were not carried over: only owner packs fill those now.");
        }

        if (notice.OverLimit > 0)
        {
            parts.Add($"{notice.OverLimit.ToString(CultureInfo.InvariantCulture)} pictures were left out because a character can have 3 of your pictures.");
        }

        return string.Join(" ", parts);
    }

    private static IEnumerable<(string Key, PictureItem Item)> OwnFilePictures(AppSettings settings) =>
        settings.Pictures.SelectMany(p => p.Value.Items.Where(i => i.File is not null).Select(i => (p.Key, i)));

    private void RefreshDetail(AppSettings settings)
    {
        PictureOptions.Clear();
        if (SelectedTile is not { } tile || _context is null)
        {
            PreviewImagePath = null;
            PreviewLink = null;
            DiscordImagePath = null;
            DiscordReference = null;
            DifferenceText = null;
            return;
        }

        var pictures = settings.Pictures.GetValueOrDefault(tile.Key) ?? CharacterPictures.Empty;
        var resolution = Resolve(tile, pictures);
        PreviewImagePath = resolution.Preview.LocalPath;
        PreviewLink = resolution.Preview.Link;
        DiscordReference = resolution.Discord.Reference;
        DiscordImagePath = resolution.Discord.Reference is { } reference ? _library.MatchReference(reference)?.Location?.Path : null;
        PreviewAndDiscordSame = !resolution.DiffersFromPreview;
        DiscordPictureIsBehaviourArt = resolution.Discord.BehaviourArt;
        DifferenceText = resolution.Discord.WhyNotChosen is { } why
            ? PictureTexts.Difference(why)
            : resolution.Preview.LocalOnly ? "This picture is from a pack you imported, so only your preview shows it. Discord shows the default portrait." : null;
        CounterText = $"Your pictures: {pictures.Items.Count.ToString(CultureInfo.InvariantCulture)} of {PictureLimits.MaxOwnPerCharacter.ToString(CultureInfo.InvariantCulture)}";
        CanAddPicture = pictures.Items.Count < PictureLimits.MaxOwnPerCharacter;
        FullText = CanAddPicture ? null : "This character has 3 of your pictures. Remove one to add another.";

        BuildOptions(tile, pictures);
        if (PanelMode == CharacterPanelMode.AddPicture)
        {
            RefreshPackPictures(settings);
        }
    }

    private void BuildOptions(CharacterTileViewModel tile, CharacterPictures pictures)
    {
        var defaultPiece = _context!.Pieces.FirstOrDefault(p =>
            p.Key == tile.Key && _context.Packs.FirstOrDefault(k => k.Id == p.PackId) is { IsDefault: true, IsImported: false });
        var notDownloaded = _library.Synced is null && !_environment.SignedIn;
        NoDefaultText = defaultPiece is null && !notDownloaded
            ? "No default portrait yet. The default pack adds one automatically when it's available; until then Discord shows no picture for this character."
            : null;
        var index = 0;
        PictureOptions.Add(Option(
            index++,
            "Default portrait",
            defaultPiece is not null ? "Owner pack · everyone can use it"
                : notDownloaded ? "Downloads when you sign in" : "No default portrait yet",
            defaultPiece?.Url is not null ? "On Discord" : notDownloaded ? "No picture yet" : "No picture",
            defaultPiece?.LocalPath,
            null,
            null,
            null,
            pictures.Use is null,
            false,
            string.Empty));
        if (pictures.Use is { Kind: PictureUseKind.Owner } owner
            && _context.Pieces.FirstOrDefault(p => p.PackId == owner.Pack && p.EntryId == owner.Entry) is { } piece)
        {
            var pack = _context.Packs.FirstOrDefault(p => p.Id == piece.PackId);
            PictureOptions.Add(Option(
                index++, pack?.Name ?? "Pack picture", $"{pack?.Name} · owner pack · everyone can use it", piece.Url is not null ? "On Discord" : "Preview only",
                piece.LocalPath, null, owner, null, true, false, string.Empty));
        }

        for (var i = 0; i < pictures.Items.Count; i++)
        {
            var item = pictures.Items[i];
            var inUse = pictures.Use is { Kind: PictureUseKind.Personal } use && use.Sha256 == item.Sha256;
            var trial = Resolve(tile, new CharacterPictures(pictures.Items, PictureUse.Personal(item.Sha256))).Discord;
            var line = item.File is not null && _store.PathOf(item) is null
                ? "Missing from this PC"
                : trial.Source switch
                {
                    DiscordSource.OwnerCopy => "On Discord (same as the owner's copy)",
                    DiscordSource.Link => "Discord loads it from your link",
                    DiscordSource.SavedOnline => PictureTexts.DiscordLine(PictureOnlineStatus.Saved),
                    _ => PictureTexts.DiscordLine(trial.WhyNotChosen ?? PictureOnlineStatus.Saving),
                };
            var caption = item.Origin switch
            {
                PictureOrigin.Link => "Your link · only your status",
                PictureOrigin.Import => "Imported · only your status",
                _ => "From your PC · only your status",
            };
            var savedOnline = _environment.SavedUrlBySha256.ContainsKey(item.Sha256);
            var confirm = item.Link is not null
                ? "Remove this link?"
                : savedOnline
                    ? $"Remove this picture? It's deleted from this PC and online, freeing {PictureTexts.Megabytes(_store.BytesOf(item))} MB. Discord may show a cached copy for a while."
                    : "Remove this picture? It's deleted from this PC.";
            PictureOptions.Add(Option(
                index++,
                ImportReviewViewModel.TitleOf(item, i),
                caption,
                line,
                item.File is not null ? _store.PathOf(item) : null,
                item.Link,
                PictureUse.Personal(item.Sha256),
                item,
                inUse,
                true,
                confirm));
        }
    }

    private PictureOptionViewModel Option(
        int index, string title, string caption, string line, string? thumbnail, string? link, PictureUse? use, PictureItem? item, bool inUse, bool canRemove, string confirmText) =>
        new(index, title, caption, line, thumbnail, link, use, item, inUse, canRemove, confirmText, ChooseOption, option => option.IsConfirmingRemove = true, ConfirmRemove, option => option.IsConfirmingRemove = false);

    private void RefreshPackPictures(AppSettings settings)
    {
        PackPictures.Clear();
        if (SelectedTile is not { } tile || _context is null)
        {
            NoPackPicturesText = null;
            return;
        }

        var pictures = settings.Pictures.GetValueOrDefault(tile.Key) ?? CharacterPictures.Empty;
        foreach (var piece in _context.Pieces.Where(p => p.Key == tile.Key))
        {
            var pack = _context.Packs.First(p => p.Id == piece.PackId);
            if (pack.IsDefault)
            {
                continue;
            }

            var added = pack.IsImported
                ? pictures.Items.Any(i => i.Sha256 == piece.Sha256)
                : pictures.Use is { Kind: PictureUseKind.Owner } u && u.Pack == piece.PackId && u.Entry == piece.EntryId;
            PackPictures.Add(new PackPictureChoiceViewModel(pack, piece, added, AddFromPackAsync));
        }

        NoPackPicturesText = PackPictures.Count == 0 ? $"None of your other packs has a picture of {tile.Name}." : null;
    }

    private Task AddFromPackAsync(PackPictureChoiceViewModel choice)
    {
        if (SelectedTile is not { } tile)
        {
            return Task.CompletedTask;
        }

        if (!choice.Pack.IsImported)
        {
            ApplySlots(tile, PictureSlots.Use(Settings.Current.Pictures.GetValueOrDefault(tile.Key), PictureUse.Owner(choice.Pack.Id, choice.Piece.EntryId)));
            StatusMessage = $"{tile.Name} now uses {choice.Pack.Name}.";
            return Task.CompletedTask;
        }

        var item = choice.Piece.LocalPath is { } path ? _store.AdoptFile(path) : null;
        FinishAdd(tile, item is null
            ? new PictureIntakeResult(null, "That picture could not be copied. It may be missing from this PC.")
            : new PictureIntakeResult(item, null));
        return Task.CompletedTask;
    }

    private void FinishAdd(CharacterTileViewModel tile, PictureIntakeResult result)
    {
        BusyText = null;
        if (result.Item is not { } item)
        {
            ErrorMessage = result.Error;
            return;
        }

        var slots = PictureSlots.Add(Settings.Current.Pictures.GetValueOrDefault(tile.Key), tile.Key, item, makeInUse: true);
        if (!slots.Succeeded)
        {
            // A new file nobody uses is not kept.
            if (slots.Problem != SlotProblem.Duplicate)
            {
                DeleteIfUnused(item);
            }

            ErrorMessage = slots.Message;
            return;
        }

        ErrorMessage = null;
        ApplySlots(tile, slots);
        StatusMessage = result.Notice ?? $"{tile.Name} now uses {ImportReviewViewModel.TitleOf(item, slots.Pictures.Items.Count - 1)}.";
        PanelMode = CharacterPanelMode.Character;
        ReportSaved(tile.Key, item, Settings.Current);
    }

    private void ApplySlots(CharacterTileViewModel tile, SlotResult slots)
    {
        if (!slots.Succeeded)
        {
            ErrorMessage = slots.Message;
            return;
        }

        var key = tile.Key;
        var pictures = slots.Pictures;
        Apply(s => s with { Pictures = WithPictures(s.Pictures, key, pictures) });
    }

    private static Dictionary<string, CharacterPictures> WithPictures(IReadOnlyDictionary<string, CharacterPictures> all, string key, CharacterPictures pictures)
    {
        var result = new Dictionary<string, CharacterPictures>(all, StringComparer.Ordinal);
        if (pictures.Items.Count == 0 && pictures.Use is null)
        {
            result.Remove(key);
        }
        else
        {
            result[key] = pictures;
        }

        return result;
    }

    // The upload gate (PLAN_V2_S5B §2.6): nothing is handed to the saving-online queue before the picture rules are accepted and the user is signed in.
    // The queue is told again when either becomes true (AcceptRules and the sign-in change in Reload), so a picture added earlier is not forgotten.
    private void ReportSaved(string key, PictureItem item, AppSettings settings)
    {
        if (item.File is null || _sink is null || settings.PictureRules != PictureRules.Accepted || !_environment.SignedIn)
        {
            return;
        }

        var inUse = settings.Pictures.GetValueOrDefault(key)?.Use is { Kind: PictureUseKind.Personal } use && use.Sha256 == item.Sha256;
        _sink.Enqueue(new PictureUploadRequest(key, item.Sha256, _store.BytesOf(item), inUse));
    }

    private void ChooseOption(PictureOptionViewModel option)
    {
        if (SelectedTile is not { } tile || option.IsInUse)
        {
            return;
        }

        var slots = PictureSlots.Use(Settings.Current.Pictures.GetValueOrDefault(tile.Key), option.Use);
        if (!slots.Succeeded)
        {
            ErrorMessage = slots.Message;
            return;
        }

        ApplySlots(tile, slots);
        StatusMessage = $"{tile.Name} now uses {option.Title}.";
        if (option.Item is { } item)
        {
            ReportSaved(tile.Key, item, Settings.Current);
        }
    }

    private void ConfirmRemove(PictureOptionViewModel option)
    {
        if (SelectedTile is not { } tile || option.Item is not { } item)
        {
            return;
        }

        var before = Settings.Current.Pictures.GetValueOrDefault(tile.Key);
        var wasInUse = before?.Use is { Kind: PictureUseKind.Personal } use && use.Sha256 == item.Sha256;
        var slots = PictureSlots.Remove(before, item.Sha256);
        if (!slots.Succeeded)
        {
            ErrorMessage = slots.Message;
            return;
        }

        var freed = _environment.SavedUrlBySha256.ContainsKey(item.Sha256) ? _store.BytesOf(item) : 0;
        ApplySlots(tile, slots);
        DeleteIfUnused(item);
        StatusMessage = wasInUse
            ? $"Removed. {tile.Name} now uses the default portrait."
            : freed > 0 ? $"Removed. {PictureTexts.Megabytes(freed)} MB freed." : "Removed.";
    }

    private void DeleteIfUnused(PictureItem item)
    {
        if (Settings.Current.Pictures.Values.Any(p => p.Items.Any(i => i.Sha256 == item.Sha256)))
        {
            return;
        }

        if (!ReferencedByOverride(item))
        {
            _store.Delete(item);
        }

        _sink?.Remove(item.Sha256);
    }

    // An older per-image choice (settings packs.overrides) may still point at this file, which lives in the same folders: it stays (review F3).
    private bool ReferencedByOverride(PictureItem item) =>
        item.File is { } file && Settings.Current.Packs.Overrides.Values.Any(v => string.Equals(v, file, StringComparison.Ordinal));

    private void OpenReview(string archiveName, ImportAnalysis analysis)
    {
        ReadingText = null;
        if (analysis.IsRejected || analysis.Items.Count == 0)
        {
            Stage = CharactersStage.Browse;
            ImportError = analysis.IsRejected
                ? $"Couldn't read {archiveName}. {analysis.RejectedReason} Nothing was imported."
                : $"{archiveName} has no pictures this app can use, so nothing was imported.";
            return;
        }

        var settings = Settings.Current;
        var proposals = PictureMatcher.Match(analysis.Items, _roster, settings.Pictures);
        var ownerShas = _context!.Pieces.Where(p => p.Url is not null).SelectMany(p => new[] { p.Sha256, p.SourceSha256 }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        ImportReview = new ImportReviewViewModel(
            archiveName,
            proposals,
            analysis.Skipped,
            _roster,
            settings.Pictures,
            item => ownerShas.Contains(item.Sha256),
            _environment.UsedBytes,
            _environment.LimitBytes,
            _environment.SignedIn,
            settings.PictureRules == PictureRules.Declined,
            ConfirmImportAsync,
            CancelReview);
        Stage = CharactersStage.Review;
    }

    private void CancelReview()
    {
        ImportReview = null;
        Stage = CharactersStage.Browse;
    }

    private async Task ConfirmImportAsync(ImportReviewViewModel review)
    {
        if (_confirming)
        {
            return; // a second click while the pictures are being saved starts nothing
        }

        _confirming = true;
        var settings = Settings.Current;
        var size = settings.PictureSize;
        var pictures = new Dictionary<string, CharacterPictures>(settings.Pictures, StringComparer.Ordinal);
        var before = new Dictionary<string, CharacterPictures?>(StringComparer.Ordinal);
        var added = new List<(string Key, PictureItem Item)>();
        var replaced = new List<PictureItem>();
        var failed = 0;
        var useImported = review.UseImported;
        var changedCharacters = new HashSet<string>(StringComparer.Ordinal);
        var selection = review.Selection.ToList();
        _importCancel?.Dispose();
        _importCancel = new CancellationTokenSource();
        var token = _importCancel.Token;
        Stage = CharactersStage.Reading;
        _readingRun++;
        var stoppedEarly = false;
        try
        {
            for (var done = 0; done < selection.Count; done++)
            {
                if (token.IsCancellationRequested)
                {
                    stoppedEarly = true;
                    break;
                }

                var progressText = $"Saving picture {(done + 1).ToString(CultureInfo.InvariantCulture)} of {selection.Count.ToString(CultureInfo.InvariantCulture)}…";
                Dispatcher.Post(() => ReadingText = progressText);
                var (item, character, replaceSha) = selection[done];
                var key = PictureKeys.Of(character.Id);
                PictureIntakeResult intake;
                try
                {
                    intake = await _store.AddBytesAsync(item.Png, size, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stoppedEarly = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    intake = new PictureIntakeResult(null, $"The picture could not be made smaller ({ex.GetType().Name}).");
                }

                if (intake.Item is not { } picture)
                {
                    failed++;
                    continue;
                }

                var current = pictures.GetValueOrDefault(key);
                if (replaceSha is not null && current?.Items.FirstOrDefault(i => i.Sha256 == replaceSha) is { } old)
                {
                    current = PictureSlots.Remove(current, replaceSha).Pictures;
                    replaced.Add(old);
                }

                var slots = PictureSlots.Add(current, key, picture, useImported);
                if (!slots.Succeeded)
                {
                    failed++;
                    DeleteFileIfNew(picture, settings.Pictures);
                    continue;
                }

                before.TryAdd(key, settings.Pictures.GetValueOrDefault(key));
                pictures[key] = slots.Pictures;
                added.Add((key, picture));
                changedCharacters.Add(key);
            }
        }
        finally
        {
            // Whatever happened above, the page leaves the saving state and the review is closed (review F8).
            var stopped = stoppedEarly;
            Dispatcher.Post(() =>
            {
                _confirming = false;
                ReadingText = null;
                FinishImport(review, pictures, before, added, replaced, changedCharacters.Count, failed, useImported);
                if (stopped)
                {
                    ImportResultText += " You stopped it early; the rest was not imported.";
                }
            });
        }
    }

    private void DeleteFileIfNew(PictureItem picture, IReadOnlyDictionary<string, CharacterPictures> existing)
    {
        if (!existing.Values.Any(p => p.Items.Any(i => i.Sha256 == picture.Sha256)) && !ReferencedByOverride(picture))
        {
            _store.Delete(picture);
        }
    }

    private void FinishImport(
        ImportReviewViewModel review,
        Dictionary<string, CharacterPictures> pictures,
        Dictionary<string, CharacterPictures?> before,
        List<(string Key, PictureItem Item)> added,
        List<PictureItem> replaced,
        int characters,
        int failed,
        bool useImported)
    {
        Apply(s => s with { Pictures = pictures });
        foreach (var (key, item) in added)
        {
            ReportSaved(key, item, Settings.Current);
        }

        var name = string.IsNullOrWhiteSpace(review.PackName) ? review.ArchiveName : review.PackName.Trim();
        _undo = new ImportUndo(name, before, [.. added.Select(a => a.Item)]);
        _pendingDeletes.AddRange(replaced);
        var count = added.Count.ToString(CultureInfo.InvariantCulture);
        ImportResultText = useImported
            ? $"Imported {count} pictures from {name}. {characters.ToString(CultureInfo.InvariantCulture)} characters now use them."
            : $"Imported {count} pictures from {name}. Your current choices are unchanged.";
        if (failed > 0)
        {
            ImportResultText += $" {failed.ToString(CultureInfo.InvariantCulture)} could not be imported.";
        }

        ImportReview = null;
        Stage = CharactersStage.Browse;
        OnPropertyChanged(nameof(CanUndoImport));
    }

    private void FinishUndoWindow()
    {
        foreach (var item in _pendingDeletes)
        {
            DeleteIfUnused(item);
        }

        _pendingDeletes.Clear();
        if (_undo is not null)
        {
            _undo = null;
            OnPropertyChanged(nameof(CanUndoImport));
        }
    }

    private sealed record ImportUndo(string PackName, IReadOnlyDictionary<string, CharacterPictures?> Before, IReadOnlyList<PictureItem> Added);
}
