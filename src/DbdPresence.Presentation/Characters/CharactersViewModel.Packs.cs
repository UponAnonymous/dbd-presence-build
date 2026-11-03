using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Sync;

namespace DbdPresence.Presentation.Characters;

/// <summary>The Packs panel of the Characters page: the packs in the library, removing an imported pack and checking for new owner packs (design §5.5, §7.8, FR-ONL-07).</summary>
public sealed partial class CharactersViewModel
{
    private IPackSyncService? _sync;

    /// <summary>Gets the packs in the library: owner packs and imported packs.</summary>
    public ObservableCollection<PackRowViewModel> PackRows { get; } = [];

    /// <summary>Gets a value indicating whether this build can check for owner packs at all.</summary>
    public bool ShowSyncSection => _sync is not null;

    /// <summary>Gets "Last updated 24 Oct 2026, 14:03." or the sentence that nothing was downloaded yet.</summary>
    [ObservableProperty]
    public partial string SyncStatusText { get; private set; } = string.Empty;

    /// <summary>Gets the plain sentence about the latest check, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyncNote))]
    public partial string? SyncNote { get; private set; }

    /// <summary>Gets a value indicating whether <see cref="SyncNote"/> has text.</summary>
    public bool HasSyncNote => !string.IsNullOrEmpty(SyncNote);

    /// <summary>Gets a value indicating whether online features are available, so a check can run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForPacks), nameof(SyncUnavailable))]
    public partial bool CanSync { get; private set; }

    /// <summary>Gets a value indicating whether a check is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForPacks), nameof(CheckForPacksText))]
    public partial bool IsChecking { get; private set; }

    /// <summary>Gets a value indicating whether the check button is enabled.</summary>
    public bool CanCheckForPacks => CanSync && !IsChecking;

    /// <summary>Gets the check button text: "Check for updates" or "Checking…".</summary>
    public string CheckForPacksText => IsChecking ? "Checking…" : "Check for updates";

    /// <summary>Gets a value indicating whether the page says why checking is not available.</summary>
    public bool SyncUnavailable => ShowSyncSection && !CanSync;

    /// <summary>Gets why checking is not available (design §7.8, "Not signed in").</summary>
    public string SyncUnavailableText { get; } =
        "Updates to the default portraits need an account with online features. Sign in on the Account page. Imported packs work in your preview without it.";

    /// <summary>Checks for new owner packs now. The service decides how often that may happen.</summary>
    [RelayCommand]
    private async Task CheckForPacksAsync(CancellationToken cancellationToken)
    {
        if (_sync is null || !CanCheckForPacks)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = "Checking for updates…";
        var status = await _sync.SyncAsync(manual: true, cancellationToken).ConfigureAwait(false);
        Dispatcher.Post(() =>
        {
            RefreshSync();
            Reload();
            StatusMessage = status.LastOutcome switch
            {
                PackSyncOutcome.Updated => "The default portraits were updated.",
                PackSyncOutcome.UpToDate => "The default portraits are already up to date.",
                PackSyncOutcome.AlreadyRunning => "A check is already running.",
                _ => status.Note ?? "The check did not finish.",
            };
        });
    }

    private void StartSync(IPackSyncService? sync)
    {
        _sync = sync;
        if (sync is not null)
        {
            sync.Changed += OnSyncChanged;
        }

        RefreshSync();
    }

    private void StopSync()
    {
        if (_sync is not null)
        {
            _sync.Changed -= OnSyncChanged;
        }
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
        SyncNote = status.Note;
        SyncStatusText = status.LastSuccessAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"Updated {at.ToLocalTime():d MMM yyyy, HH:mm}.")
            : status.CanSync ? "The default portraits have not been downloaded yet." : string.Empty;
    }

    private void RefreshPackRows()
    {
        PackRows.Clear();
        foreach (var card in _library.Cards())
        {
            var imported = card.Origin == PackOrigin.Imported;
            var count = card.Entries.Count.ToString(CultureInfo.InvariantCulture);
            var creator = string.IsNullOrWhiteSpace(card.Info.Author) ? string.Empty : $" · Creator: {card.Info.Author}";
            var caption = imported
                ? $"Imported by you · {count} pictures{creator}"
                : $"Owner pack · {count} pictures{creator}";
            var confirm = $"Remove “{card.Info.Name}”? Its {count} pictures are removed from this PC. Characters that used them go back to the default portrait.";
            PackRows.Add(new PackRowViewModel(card.Info.Id, card.Info.Name, caption, imported, confirm, RemovePack));
        }
    }

    private void RemovePack(PackRowViewModel row)
    {
        if (!row.CanRemove || !_library.Imported.Any(p => string.Equals(p.Info.Id, row.Id, StringComparison.Ordinal)))
        {
            return;
        }

        _library.RemoveImported(row.Id);
        StatusMessage = $"Removed “{row.Name}”.";
        Reload();
    }
}

/// <summary>One pack in the Packs panel. Owner packs can only be updated, never removed; an imported pack can be removed after a confirmation.</summary>
public sealed partial class PackRowViewModel : ObservableObject
{
    private readonly Action<PackRowViewModel> _remove;

    internal PackRowViewModel(string id, string name, string caption, bool canRemove, string removeConfirmText, Action<PackRowViewModel> remove)
    {
        Id = id;
        Name = name;
        Caption = caption;
        CanRemove = canRemove;
        RemoveConfirmText = removeConfirmText;
        _remove = remove;
    }

    /// <summary>Gets the pack id; the automation id is <c>Pack-&lt;Id&gt;</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the pack name.</summary>
    public string Name { get; }

    /// <summary>Gets the line under the name: owner or imported, picture count and creator.</summary>
    public string Caption { get; }

    /// <summary>Gets a value indicating whether the pack can be removed (imported packs only).</summary>
    public bool CanRemove { get; }

    /// <summary>Gets the confirmation sentence.</summary>
    public string RemoveConfirmText { get; }

    /// <summary>Gets a value indicating whether the inline confirmation shows.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; private set; }

    /// <summary>Gets the automation name of the remove button.</summary>
    public string RemoveName => $"Remove pack {Name}";

    /// <summary>Shows the confirmation.</summary>
    [RelayCommand]
    private void RequestRemove() => IsConfirmingRemove = true;

    /// <summary>Removes the pack.</summary>
    [RelayCommand]
    private void ConfirmRemove() => _remove(this);

    /// <summary>Keeps the pack.</summary>
    [RelayCommand]
    private void Keep() => IsConfirmingRemove = false;
}
