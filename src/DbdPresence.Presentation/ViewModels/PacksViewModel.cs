using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Resolution;
using DbdPresence.Packs.Sync;
using DbdPresence.Presentation.Presence;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>The Packs page shows one of these at a time.</summary>
public enum PacksStage
{
    /// <summary>The pack cards, overrides and the Import button.</summary>
    Browse,

    /// <summary>An archive is being read and its images checked by the image helper.</summary>
    Reading,

    /// <summary>The mapping confirmation screen.</summary>
    Mapping,
}

/// <summary>
/// The Packs page (FR-PACK-06, FR-PACK-07, FR-PACK-10): pack cards with a grid preview, an "In use" badge and a switch per
/// category; per-image overrides; "Import pack…" with a mapping confirmation step; removal of imported packs. It never
/// decodes an image itself and uses no network: files come from the image helper. Nothing is bundled (D16): with no pack the page shows an empty state.
/// </summary>
public sealed partial class PacksViewModel : SettingsPageViewModel
{
    private static readonly string[] s_overrideKeys =
        ["role:killer", "role:survivor", "character:generic-killer", "character:generic-survivor", "map:generic"];

    private readonly PackLibrary _library;
    private readonly IFilePicker _picker;
    private readonly IPackSyncService? _sync;
    private readonly UploadsViewModel? _uploads;
    private readonly List<PackCardViewModel> _cardList = [];
    private string _libraryFingerprint = string.Empty;
    private CancellationTokenSource? _importCancel;

    /// <summary>Initializes a new instance of the <see cref="PacksViewModel"/> class.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="dispatcher">Runs UI updates on the UI thread.</param>
    /// <param name="library">The pack library.</param>
    /// <param name="picker">File dialogs.</param>
    /// <param name="sync">Pack sync, or null in builds without it.</param>
    /// <param name="uploads">Uploads (FR-ONL-09), or null in builds without them: own images then stay preview-only.</param>
    public PacksViewModel(ISettingsService settings, IUiDispatcher dispatcher, PackLibrary library, IFilePicker picker, IPackSyncService? sync = null, UploadsViewModel? uploads = null)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(picker);
        _library = library;
        _picker = picker;
        _sync = sync;
        _uploads = uploads;
        if (_sync is not null)
        {
            _sync.Changed += OnSyncChanged;
        }

        if (_uploads is not null)
        {
            _uploads.PropertyChanged += OnUploadsChanged;
            _library.SyncedChanged += OnLibraryChanged;
        }

        Overrides = [.. s_overrideKeys.Select(k => new OverrideRowViewModel(k, ChooseOverrideAsync, ResetOverride, _uploads is null ? null : UploadOverrideAsync))];
        Reload();
        RefreshSync();
    }

    /// <summary>Gets the uploads (policy, availability), or null in builds without uploads.</summary>
    public UploadsViewModel? Uploads => _uploads;

    /// <summary>Gets a value indicating whether this build offers uploads (the upload controls only exist then).</summary>
    public bool ShowUploads => _uploads is not null;

    /// <summary>Gets a value indicating whether this build syncs official packs (the sync section only exists then).</summary>
    public bool ShowSyncSection => _sync is not null;

    /// <summary>Gets "Last updated 24 Oct 2026, 14:03", or that nothing was downloaded yet.</summary>
    [ObservableProperty]
    public partial string SyncStatusText { get; private set; } = string.Empty;

    /// <summary>Gets the sentence about the latest check (could not reach the service, asked to wait, licensed images withdrawn), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyncNote))]
    public partial string? SyncNote { get; private set; }

    /// <summary>Gets a value indicating whether <see cref="SyncNote"/> has text.</summary>
    public bool HasSyncNote => !string.IsNullOrEmpty(SyncNote);

    /// <summary>Gets a value indicating whether the packs on this PC are older than the service's latest because the last check failed or the pack list expired.</summary>
    [ObservableProperty]
    public partial bool CouldNotRefresh { get; private set; }

    /// <summary>Gets a value indicating whether online features are available, so a check can run. Without them no check button shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForPacks), nameof(SyncUnavailable))]
    public partial bool CanSync { get; private set; }

    /// <summary>Gets a value indicating whether a check is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForPacks), nameof(CheckForPacksText))]
    public partial bool IsChecking { get; private set; }

    /// <summary>Gets a value indicating whether the "Check for new packs" button is usable.</summary>
    public bool CanCheckForPacks => CanSync && !IsChecking;

    /// <summary>Gets the button text.</summary>
    public string CheckForPacksText => IsChecking ? "Checking…" : "Check for new packs";

    /// <summary>Gets a value indicating whether the explanation for missing online features shows instead of the button.</summary>
    public bool SyncUnavailable => ShowSyncSection && !CanSync;

    /// <summary>Gets why official packs are not available right now.</summary>
    public string SyncUnavailableText { get; } =
        "Official packs need an active account with online features. Sign in on the Account page. Imported packs and your own images work without it.";

    /// <summary>Gets Behaviour's notice, shown above the cards while an official pack with Behaviour artwork is listed.</summary>
    [ObservableProperty]
    public partial string? BehaviourNotice { get; private set; }

    /// <summary>Checks for new packs now. The service decides how often that may happen (it can ask the app to wait).</summary>
    [RelayCommand]
    private async Task CheckForPacksAsync(CancellationToken cancellationToken)
    {
        if (_sync is null || !CanCheckForPacks)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = "Checking for new packs…";
        var status = await _sync.SyncAsync(manual: true, cancellationToken).ConfigureAwait(false);
        Dispatcher.Post(() =>
        {
            RefreshSync();
            Reload();
            StatusMessage = status.LastOutcome switch
            {
                PackSyncOutcome.Updated => "Official packs were updated.",
                PackSyncOutcome.UpToDate => "Official packs are already up to date.",
                PackSyncOutcome.AlreadyRunning => "A check is already running.",
                _ => status.Note ?? "The check did not finish.",
            };
        });
    }

    private void OnSyncChanged(object? sender, EventArgs e) => Dispatcher.Post(() =>
    {
        RefreshSync();
        Reload();
    });

    private void RefreshSync()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        var status = sync.Status;
        CanSync = status.CanSync;
        IsChecking = status.IsRunning;
        CouldNotRefresh = status.CouldNotRefresh;
        SyncNote = status.Note;
        SyncStatusText = status.LastSuccessAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"Last updated {at.ToLocalTime():d MMM yyyy, HH:mm}.")
            : status.CanSync ? "Official packs have not been downloaded yet." : string.Empty;
    }

    /// <summary>Gets the choices for the kind of an imported image.</summary>
    public IReadOnlyList<Choice<ImportSlotKind>> SlotKinds { get; } = ImportRowViewModel.SlotKinds;

    /// <summary>Gets the statement about what Discord can show (always visible above the pack grid).</summary>
    public string DiscordNote { get; } =
        "Imported packs and your own images are shown only in your preview. Discord shows images only from official packs; where none applies it shows no image.";

    /// <summary>Gets the text above the cards that explains official packs when this build can sync them.</summary>
    public string OfficialNote { get; } = "Official packs are downloaded from the DBD Presence service and checked on this PC before they are used.";

    /// <summary>Gets the text shown instead of the pack grid while there are no packs.</summary>
    public string EmptyStateText { get; } =
        "No image packs yet. Import a NightLight pack you've downloaded, or official packs appear here once available.";

    /// <summary>Gets the pack cards: imported packs by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPacks))]
    public partial IReadOnlyList<PackCardViewModel> Cards { get; private set; } = [];

    /// <summary>Gets a value indicating whether there is no pack to list (the empty state shows).</summary>
    public bool HasNoPacks => Cards.Count == 0;

    /// <summary>Gets the per-image override rows.</summary>
    public IReadOnlyList<OverrideRowViewModel> Overrides { get; }

    /// <summary>Gets the "Discord shows" rows for the Killer and Survivor icons.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PackPreviewRow> PreviewRows { get; private set; } = [];

    /// <summary>Gets the notice shown while a local image is in use ("Shown only in your preview; Discord shows no image for it").</summary>
    [ObservableProperty]
    public partial string? LocalImagesNote { get; private set; }

    /// <summary>Gets what is wrong with the imported packs on disk, if anything.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> LoadProblems { get; private set; } = [];

    /// <summary>Gets the current stage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsing), nameof(IsReading), nameof(IsMapping))]
    public partial PacksStage Stage { get; private set; }

    /// <summary>Gets a value indicating whether the cards are showing.</summary>
    public bool IsBrowsing => Stage == PacksStage.Browse;

    /// <summary>Gets a value indicating whether an archive is being read.</summary>
    public bool IsReading => Stage == PacksStage.Reading;

    /// <summary>Gets a value indicating whether the mapping confirmation screen is showing.</summary>
    public bool IsMapping => Stage == PacksStage.Mapping;

    /// <summary>Gets a progress or result sentence for the live region.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Gets the last problem, shown as an error.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Gets the images found in the archive being imported.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ImportRowViewModel> ImportRows { get; private set; } = [];

    /// <summary>Gets the entries of the archive that were left out, with reasons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkipped), nameof(SkippedSummary))]
    public partial IReadOnlyList<SkippedEntry> ImportSkipped { get; private set; } = [];

    /// <summary>Gets a value indicating whether some archive entries were left out.</summary>
    public bool HasSkipped => ImportSkipped.Count > 0;

    /// <summary>Gets the name of the archive being imported (shown on the mapping screen).</summary>
    [ObservableProperty]
    public partial string ImportSourceName { get; private set; } = string.Empty;

    /// <summary>Gets or sets the name of the pack to save.</summary>
    [ObservableProperty]
    public partial string ImportPackName { get; set; } = string.Empty;

    /// <summary>Gets or sets the creator text to show with the pack (as the pack names its creator).</summary>
    [ObservableProperty]
    public partial string ImportCreator { get; set; } = string.Empty;

    /// <summary>Gets the pack waiting for the user to confirm its removal, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemovalPrompt), nameof(IsRemovalPending))]
    public partial PackCardViewModel? PendingRemoval { get; private set; }

    /// <summary>Gets a value indicating whether a removal is waiting for confirmation.</summary>
    public bool IsRemovalPending => PendingRemoval is not null;

    /// <summary>Gets the question shown before a pack is removed.</summary>
    public string RemovalPrompt => PendingRemoval is { } pack
        ? $"Remove “{pack.Name}”? This deletes its images from this PC. You can import the file again later."
        : string.Empty;

    /// <summary>Picks a pack archive, reads it, and opens the mapping screen.</summary>
    [RelayCommand]
    private async Task ImportPackAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        var path = _picker.PickPackArchive();
        if (path is null)
        {
            return;
        }

        Stage = PacksStage.Reading;
        StatusMessage = "Reading the pack and checking each image…";
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _importCancel = source;
        ImportAnalysis analysis;
        try
        {
            analysis = await _library.AnalyseImportAsync(path, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Post(() =>
            {
                Stage = PacksStage.Browse;
                StatusMessage = "Import cancelled.";
            });
            return;
        }
        finally
        {
            _importCancel = null;
        }

        Dispatcher.Post(() => ShowAnalysis(path, analysis));
    }

    /// <summary>Stops reading the archive.</summary>
    [RelayCommand]
    private void CancelReading() => _importCancel?.Cancel();

    /// <summary>Confirms every row whose file name suggested a mapping. Each is still shown and can be changed.</summary>
    [RelayCommand]
    private void ConfirmSuggestions()
    {
        foreach (var row in ImportRows.Where(r => r.Item.Proposal is not null && r.CanConfirm))
        {
            row.IsConfirmed = true;
        }

        StatusMessage = $"{ImportRows.Count(r => r.IsConfirmed)} image(s) confirmed.";
    }

    /// <summary>Saves the confirmed images as a local pack. Images that were not confirmed are ignored.</summary>
    [RelayCommand]
    private void SaveImport()
    {
        var confirmed = ImportRows.Where(r => r.IsConfirmed && r.Key is not null).Select(r => new ConfirmedMapping(r.Item, r.Key!)).ToArray();
        if (confirmed.Length == 0)
        {
            ErrorMessage = "Confirm at least one image first. Images you do not confirm are not imported.";
            return;
        }

        var result = _library.SaveImport(ImportPackName, ImportCreator, ImportSourceName, confirmed);
        if (result.Pack is null)
        {
            ErrorMessage = result.Error;
            return;
        }

        ErrorMessage = null;
        FinishImport();
        StatusMessage = $"Saved “{result.Pack.Info.Name}” with {confirmed.Length} image(s). Choose where to use it below. Discord does not show imported packs.";
        Reload();
    }

    /// <summary>Hides the error message once the user has read it.</summary>
    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    /// <summary>Leaves the mapping screen without saving.</summary>
    [RelayCommand]
    private void CancelImport()
    {
        FinishImport();
        StatusMessage = "Import cancelled. Nothing was saved.";
    }

    /// <summary>Asks the user to confirm removing an imported pack.</summary>
    private void RequestRemoval(PackCardViewModel card)
    {
        PendingRemoval = card;
        StatusMessage = null;
    }

    /// <summary>Removes the pack the user confirmed, and returns any category that used it to no pack.</summary>
    [RelayCommand]
    private void ConfirmRemoval()
    {
        if (PendingRemoval is not { IsImported: true } pack)
        {
            return;
        }

        PendingRemoval = null;
        try
        {
            _library.RemoveImported(pack.Id);
        }
        catch (IOException ex)
        {
            ErrorMessage = $"The pack could not be removed ({ex.GetType().Name}). Close other programs using its folder and try again.";
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorMessage = $"The pack could not be removed ({ex.GetType().Name}). The app data folder is not writable.";
            return;
        }

        ErrorMessage = null;
        Apply(s => s with { Packs = ResetPack(s.Packs, pack.Id) });
        Reload();
        StatusMessage = $"Removed “{pack.Name}”.";
    }

    /// <summary>Keeps the pack.</summary>
    [RelayCommand]
    private void CancelRemoval() => PendingRemoval = null;

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        var fingerprint = string.Join('|', _library.Cards().Select(c => $"{c.Origin}:{c.Info.Id}:{c.Info.Name}:{c.Info.BehaviourArt}:{string.Join(',', c.Entries.Select(e => e.Sha256))}"));
        if (fingerprint != _libraryFingerprint)
        {
            _libraryFingerprint = fingerprint;
            RebuildCards();
        }

        var chosen = ChosenPacks(settings.Packs);
        foreach (var card in _cardList)
        {
            card.Refresh(chosen);
        }

        LoadProblems = _library.LoadProblems;
        RefreshOverrides(settings.Packs);
        RefreshPreview(settings.Packs);
    }

    private static Dictionary<PackCategory, string> ChosenPacks(PackSettings packs) => new()
    {
        [PackCategory.Killer] = packs.KillerPack,
        [PackCategory.Survivor] = packs.SurvivorPack,
        [PackCategory.Characters] = packs.CharactersPack,
        [PackCategory.Maps] = packs.MapsPack,
    };

    private static PackSettings ResetPack(PackSettings packs, string packId)
    {
        string Reset(string id) => string.Equals(id, packId, StringComparison.Ordinal) ? PackSettings.NoPack : id;
        return packs with
        {
            KillerPack = Reset(packs.KillerPack),
            SurvivorPack = Reset(packs.SurvivorPack),
            CharactersPack = Reset(packs.CharactersPack),
            MapsPack = Reset(packs.MapsPack),
        };
    }

    private void RebuildCards()
    {
        _cardList.Clear();
        foreach (var card in _library.Cards())
        {
            var tiles = card.Entries
                .OrderBy(e => e.Kind)
                .ThenBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new PackTile(PackLabels.ForKey(e.Key), _library.LocationOf(card, e)))
                .ToArray();
            _cardList.Add(new PackCardViewModel(card, tiles, OnCategoryChanged, RequestRemoval));
        }

        Cards = [.. _cardList];
        BehaviourNotice = _cardList.Any(c => c.HasBehaviourNotice) ? AppCopy.BehaviourNotice : null;
    }

    private void OnCategoryChanged(PackCategory category, bool chosen, PackCardViewModel card)
    {
        // Switching a pack off returns the category to no pack; switching one on makes it the active pack.
        var id = chosen ? card.Id : PackSettings.NoPack;
        Apply(s => s with
        {
            Packs = category switch
            {
                PackCategory.Killer => s.Packs with { KillerPack = id },
                PackCategory.Survivor => s.Packs with { SurvivorPack = id },
                PackCategory.Characters => s.Packs with { CharactersPack = id },
                _ => s.Packs with { MapsPack = id },
            },
        });
        var chosenNow = ChosenPacks(Settings.Current.Packs);
        foreach (var other in _cardList)
        {
            other.Refresh(chosenNow);
        }

        RefreshPreview(Settings.Current.Packs);
        StatusMessage = chosen
            ? $"{card.Name} is now used for {PackLabels.Name(category)}."
            : $"{PackLabels.Name(category)} uses no pack again.";
    }

    private void RefreshOverrides(PackSettings packs)
    {
        foreach (var row in Overrides)
        {
            var file = packs.Overrides.TryGetValue(row.Key, out var name) ? name : null;
            var path = file is null ? null : _library.OverridePath(file);
            row.Update(path is not null, path is not null ? new PackImageLocation(path) : null, _library.UploadUrlOf(file) is not null, _uploads is not null, _uploads?.CanUpload == true);
        }
    }

    private void RefreshPreview(PackSettings packs)
    {
        var choices = PackArtworkProvider.ChoicesOf(packs);
        var rows = new List<PackPreviewRow>();
        var anyLocal = packs.Overrides.Values.Any(f => _library.UploadUrlOf(f) is null);
        foreach (var (role, label) in new[] { ("killer", "Killer icon"), ("survivor", "Survivor icon") })
        {
            var selection = new ImageSelection(role, null, null, false);
            var preview = _library.ResolvePreview(selection, choices, packs.Overrides).Role;
            var discord = _library.ResolveDiscord(selection, choices, packs.Overrides).Role;
            anyLocal |= discord.LocalOnly;
            rows.Add(new PackPreviewRow(
                label,
                _library.LocationOf(preview),
                discord.Reference is null ? "No image" : preview.Source == ResolvedImageSource.UserOverride ? "Your uploaded image (unlisted)" : "Image from the pack service",
                discord.LocalOnly));
        }

        PreviewRows = rows;
        LocalImagesNote = anyLocal
            ? _uploads is null
                ? "Shown only in your preview; Discord shows no image for it."
                : "Shown only in your preview; Discord shows no image for it. Upload one of your own images below so Discord can show it."
            : null;
    }

    private void ShowAnalysis(string path, ImportAnalysis analysis)
    {
        ImportSkipped = analysis.Skipped;
        if (analysis.IsRejected)
        {
            Stage = PacksStage.Browse;
            ErrorMessage = analysis.RejectedReason;
            StatusMessage = null;
            return;
        }

        if (analysis.Items.Count == 0)
        {
            Stage = PacksStage.Browse;
            ErrorMessage = "No usable images were found in that file. " + (analysis.Skipped.Count > 0 ? $"{analysis.Skipped.Count} entries were skipped; the reasons are listed below." : string.Empty);
            StatusMessage = null;
            return;
        }

        var fileName = Path.GetFileName(path);
        ImportSourceName = fileName;
        ImportPackName = Path.GetFileNameWithoutExtension(fileName);
        ImportCreator = string.Empty;
        ImportRows = [.. analysis.Items.Select(i => new ImportRowViewModel(i))];
        Stage = PacksStage.Mapping;
        StatusMessage = $"{analysis.Items.Count} image(s) ready. Confirm what each one is for; images you do not confirm are ignored.";
    }

    private void FinishImport()
    {
        ImportRows = [];
        ImportSkipped = [];
        ImportPackName = string.Empty;
        ImportCreator = string.Empty;
        ImportSourceName = string.Empty;
        Stage = PacksStage.Browse;
    }

    private async Task ChooseOverrideAsync(OverrideRowViewModel row)
    {
        ErrorMessage = null;
        var path = _picker.PickImage();
        if (path is null)
        {
            return;
        }

        StatusMessage = "Checking the image…";
        var result = await _library.SaveOverrideAsync(row.Key, path, CancellationToken.None).ConfigureAwait(false);
        Dispatcher.Post(() =>
        {
            if (result.FileName is not { } file)
            {
                ErrorMessage = result.Error;
                StatusMessage = null;
                return;
            }

            var previous = Settings.Current.Packs.Overrides.TryGetValue(row.Key, out var old) ? old : null;
            Apply(s => s with { Packs = s.Packs with { Overrides = new Dictionary<string, string>(s.Packs.Overrides) { [row.Key] = file } } });
            DeleteIfUnused(previous);
            StatusMessage = $"Your image is now used for the {row.Label}. Discord does not show your own images.";
            Reload();
        });
    }

    private void ResetOverride(OverrideRowViewModel row)
    {
        var previous = Settings.Current.Packs.Overrides.TryGetValue(row.Key, out var old) ? old : null;
        Apply(s => s with { Packs = s.Packs with { Overrides = s.Packs.Overrides.Where(p => p.Key != row.Key).ToDictionary(p => p.Key, p => p.Value) } });
        DeleteIfUnused(previous);
        StatusMessage = $"The {row.Label} uses its pack image again.";
        Reload();
    }

    private async Task UploadOverrideAsync(OverrideRowViewModel row)
    {
        if (_uploads is null || !_uploads.CanUpload)
        {
            return;
        }

        ErrorMessage = null;
        var file = Settings.Current.Packs.Overrides.TryGetValue(row.Key, out var name) ? name : null;
        if (file is null || _library.ReadOverrideFile(file) is not { } png)
        {
            ErrorMessage = "Your image for this slot could not be read. Choose it again.";
            return;
        }

        StatusMessage = "Uploading your image…";
        var result = await _uploads.UploadAsync(png).ConfigureAwait(true);
        Dispatcher.Post(() =>
        {
            if (result.Succeeded)
            {
                StatusMessage = $"Your image for the {row.Label} is uploaded. Discord shows it at an unlisted address: not listed by the service, but anyone who sees your status can open it.";
                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = null;
            }

            Reload();
        });
    }

    private void OnUploadsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UploadsViewModel.CanUpload) or nameof(UploadsViewModel.Items))
        {
            Dispatcher.Post(() => RefreshOverrides(Settings.Current.Packs));
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => Dispatcher.Post(() =>
    {
        RefreshOverrides(Settings.Current.Packs);
        RefreshPreview(Settings.Current.Packs);
    });

    private void DeleteIfUnused(string? fileName)
    {
        if (fileName is not null && !Settings.Current.Packs.Overrides.Values.Contains(fileName, StringComparer.Ordinal))
        {
            _library.DeleteOverrideFile(fileName);
        }
    }

    /// <summary>Gets the text "n images skipped", for the summary line.</summary>
    public string SkippedSummary => string.Create(CultureInfo.InvariantCulture, $"{ImportSkipped.Count} entr{(ImportSkipped.Count == 1 ? "y was" : "ies were")} left out");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _importCancel?.Cancel();
            if (_sync is not null)
            {
                _sync.Changed -= OnSyncChanged;
            }

            if (_uploads is not null)
            {
                _uploads.PropertyChanged -= OnUploadsChanged;
                _library.SyncedChanged -= OnLibraryChanged;
            }
        }

        base.Dispose(disposing);
    }
}
