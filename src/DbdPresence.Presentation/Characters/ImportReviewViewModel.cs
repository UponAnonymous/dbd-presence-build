using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Presentation.Characters;

/// <summary>A character the user can pick for a file in the import review.</summary>
/// <param name="Id">The pack slug.</param>
/// <param name="Code">The game's code.</param>
/// <param name="Name">The display name.</param>
public sealed record CharacterChoice(string Id, string Code, string Name)
{
    /// <summary>Gets the first item of the character box, standing for "no character": the file is not imported.</summary>
    public static CharacterChoice None { get; } = new(string.Empty, string.Empty, "Don't import");

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Which section of the review a file is in (design §5.6).</summary>
public enum ImportSection
{
    /// <summary>Matched by code: ticked, collapsed.</summary>
    Matched,

    /// <summary>Needs a choice: a suggestion from the name, or nothing.</summary>
    NeedsChoice,

    /// <summary>The matched character already has three pictures.</summary>
    Full,

    /// <summary>Not imported, with a reason.</summary>
    NotImported,
}

/// <summary>What to do with a file whose character already has three pictures.</summary>
/// <param name="ReplaceSha256">The picture to replace, or null to skip the file.</param>
/// <param name="Label">"Skip this picture" or "Replace “&lt;title&gt;”".</param>
public sealed record ReplaceChoice(string? ReplaceSha256, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One file in the import review.</summary>
public sealed partial class ImportReviewRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly bool _ready;

    internal ImportReviewRowViewModel(
        int index, PictureProposal proposal, ImportSection section, CharacterChoice? preset, IReadOnlyList<ReplaceChoice> replaceChoices, string caption, Action changed)
    {
        _changed = changed;
        Index = index;
        Proposal = proposal;
        Section = section;
        Character = preset;
        SelectedCharacter = preset;
        ReplaceChoices = replaceChoices;
        SelectedReplace = replaceChoices.Count > 0 ? replaceChoices[0] : null;
        Caption = caption;
        Included = section == ImportSection.Matched;
        _ready = true;
    }

    /// <summary>Gets the position, for the automation ids <c>ImportChoice-&lt;Index&gt;</c> and <c>ImportMatched-&lt;Index&gt;</c>.</summary>
    public int Index { get; }

    /// <summary>Gets the matcher's proposal.</summary>
    public PictureProposal Proposal { get; }

    /// <summary>Gets the section.</summary>
    public ImportSection Section { get; }

    /// <summary>Gets the file name.</summary>
    public string FileName => Proposal.Item.DisplayName;

    /// <summary>Gets the clean PNG the image helper returned, for the 64 DIP thumbnail.</summary>
    public byte[] Png => Proposal.Item.Png;

    /// <summary>Gets the character the file was matched or suggested to, or null.</summary>
    public CharacterChoice? Character { get; }

    /// <summary>Gets the caption: "Suggested from the file name: …", "No suggestion", the conflict sentence or the reason it is not imported.</summary>
    public string Caption { get; }

    /// <summary>Gets the replace choices of a file in the <see cref="ImportSection.Full"/> section (first: skip).</summary>
    public IReadOnlyList<ReplaceChoice> ReplaceChoices { get; }

    /// <summary>Gets or sets the character chosen for a file that needs a choice; null means "Don't import".</summary>
    [ObservableProperty]
    public partial CharacterChoice? SelectedCharacter { get; set; }

    /// <summary>Gets or sets the character box's value: <see cref="CharacterChoice.None"/> stands for no character.</summary>
    public CharacterChoice SelectedOrNone
    {
        get => SelectedCharacter ?? CharacterChoice.None;
        set => SelectedCharacter = ReferenceEquals(value, CharacterChoice.None) ? null : value;
    }

    /// <summary>Gets or sets whether a matched file is imported (the "Import" check box).</summary>
    [ObservableProperty]
    public partial bool Included { get; set; }

    /// <summary>Gets or sets the choice for a file whose character is full.</summary>
    [ObservableProperty]
    public partial ReplaceChoice? SelectedReplace { get; set; }

    /// <summary>Gets the automation name of the combo or check box.</summary>
    public string AutomationName => Section == ImportSection.Matched
        ? $"Import {FileName} as {Character?.Name}"
        : $"Character for {FileName}";

    /// <summary>Gets the character this file will be saved for, or null when it is left out.</summary>
    public CharacterChoice? Assigned => Section switch
    {
        ImportSection.Matched => Included ? Character : null,
        ImportSection.NeedsChoice => SelectedCharacter,
        ImportSection.Full => SelectedReplace?.ReplaceSha256 is null ? null : Character,
        _ => null,
    };

    partial void OnSelectedCharacterChanged(CharacterChoice? value)
    {
        OnPropertyChanged(nameof(SelectedOrNone));
        Changed();
    }

    partial void OnIncludedChanged(bool value) => Changed();

    partial void OnSelectedReplaceChanged(ReplaceChoice? value) => Changed();

    private void Changed()
    {
        if (_ready)
        {
            _changed();
        }
    }
}

/// <summary>
/// The import review (design §5.6, FR-PACK-07, FR-PACK-17): files matched to characters by code, files that need a choice, characters that already
/// have three pictures, and files that are not imported, with the storage line. Nothing is saved until <see cref="ConfirmCommand"/>.
/// </summary>
public sealed partial class ImportReviewViewModel : ObservableObject
{
    private readonly Func<ImportReviewViewModel, Task> _confirm;
    private readonly Action _cancel;
    private readonly Func<ImportItem, bool> _matchesOwnerCopy;
    private readonly long _usedBytes;
    private readonly long _limitBytes;
    private readonly bool _signedIn;
    private readonly bool _rulesDeclined;

    internal ImportReviewViewModel(
        string archiveName,
        IReadOnlyList<PictureProposal> proposals,
        IReadOnlyList<SkippedEntry> skipped,
        CharacterRoster roster,
        IReadOnlyDictionary<string, CharacterPictures> existing,
        Func<ImportItem, bool> matchesOwnerCopy,
        long usedBytes,
        long limitBytes,
        bool signedIn,
        bool rulesDeclined,
        Func<ImportReviewViewModel, Task> confirm,
        Action cancel)
    {
        ArchiveName = archiveName;
        SkippedFiles = skipped;
        _matchesOwnerCopy = matchesOwnerCopy;
        _usedBytes = usedBytes;
        _limitBytes = limitBytes;
        _signedIn = signedIn;
        _rulesDeclined = rulesDeclined;
        _confirm = confirm;
        _cancel = cancel;
        Choices = [.. roster.Characters.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => new CharacterChoice(c.Id, c.Code, c.Name))];
        PackName = Path.GetFileNameWithoutExtension(archiveName);
        var rows = new List<ImportReviewRowViewModel>();
        foreach (var proposal in proposals)
        {
            rows.Add(MakeRow(rows.Count, proposal, existing));
        }

        Rows = rows;
        NeedsChoiceHeading = Heading("Need a choice", rows.Count(r => r.Section == ImportSection.NeedsChoice));
        FullHeading = Heading("Characters that already have 3 of your pictures", rows.Count(r => r.Section == ImportSection.Full));
        MatchedHeading = Heading("Matched", rows.Count(r => r.Section == ImportSection.Matched));
        NotImportedHeading = Heading("Not imported", rows.Count(r => r.Section == ImportSection.NotImported) + skipped.Count);
        Recalculate();
    }

    /// <summary>Gets the file name of the archive.</summary>
    public string ArchiveName { get; }

    /// <summary>Gets every character the user can pick.</summary>
    public IReadOnlyList<CharacterChoice> Choices { get; }

    /// <summary>Gets the items of the character box: "Don't import", then every character.</summary>
    public IReadOnlyList<CharacterChoice> ChoicesWithNone => [CharacterChoice.None, .. Choices];

    /// <summary>Gets all files in review order.</summary>
    public IReadOnlyList<ImportReviewRowViewModel> Rows { get; }

    /// <summary>Gets the files that need a choice.</summary>
    public IEnumerable<ImportReviewRowViewModel> NeedsChoice => Rows.Where(r => r.Section == ImportSection.NeedsChoice);

    /// <summary>Gets the files whose character already has three pictures.</summary>
    public IEnumerable<ImportReviewRowViewModel> Full => Rows.Where(r => r.Section == ImportSection.Full);

    /// <summary>Gets "Need a choice (3)", or an empty text when no file needs a choice (the section is then hidden).</summary>
    public string NeedsChoiceHeading { get; }

    /// <summary>Gets "Characters that already have 3 of your pictures (1)", or an empty text.</summary>
    public string FullHeading { get; }

    /// <summary>Gets "Matched (41)", or an empty text.</summary>
    public string MatchedHeading { get; }

    /// <summary>Gets "Not imported (5)", or an empty text.</summary>
    public string NotImportedHeading { get; }

    /// <summary>Gets the files matched by code.</summary>
    public IEnumerable<ImportReviewRowViewModel> Matched => Rows.Where(r => r.Section == ImportSection.Matched);

    /// <summary>Gets the archive entries the reader or the image helper left out, with the reason (maps, not pictures, too large, …).</summary>
    public IReadOnlyList<SkippedEntry> SkippedFiles { get; }

    /// <summary>Gets the files that are not imported, with the reason as <see cref="ImportReviewRowViewModel.Caption"/>.</summary>
    public IEnumerable<ImportReviewRowViewModel> NotImported => Rows.Where(r => r.Section == ImportSection.NotImported);

    /// <summary>Gets or sets the pack name the user may edit (shown with the result).</summary>
    [ObservableProperty]
    public partial string PackName { get; set; }

    /// <summary>Gets or sets the creator text (optional).</summary>
    [ObservableProperty]
    public partial string Creator { get; set; } = string.Empty;

    /// <summary>Gets or sets the "After importing" choice: true uses the imported pictures, false keeps the current choices.</summary>
    [ObservableProperty]
    public partial bool UseImported { get; set; } = true;

    /// <summary>Gets the badge "41 matched".</summary>
    [ObservableProperty]
    public partial string MatchedBadge { get; private set; } = string.Empty;

    /// <summary>Gets the badge "3 need a choice".</summary>
    [ObservableProperty]
    public partial string NeedsChoiceBadge { get; private set; } = string.Empty;

    /// <summary>Gets the badge "1 character is full".</summary>
    [ObservableProperty]
    public partial string FullBadge { get; private set; } = string.Empty;

    /// <summary>Gets the badge "5 not imported".</summary>
    [ObservableProperty]
    public partial string NotImportedBadge { get; private set; } = string.Empty;

    /// <summary>Gets how many pictures will be imported.</summary>
    [ObservableProperty]
    public partial int ImportCount { get; private set; }

    /// <summary>Gets the primary button text: "Import 42 pictures", "Import 1 picture" or "Import pictures".</summary>
    [ObservableProperty]
    public partial string ImportButtonText { get; private set; } = "Import pictures";

    /// <summary>Gets a value indicating whether Import is enabled (at least one picture).</summary>
    public bool CanImport => ImportCount > 0;

    /// <summary>Gets "Choose at least one picture." while nothing is selected, otherwise null.</summary>
    [ObservableProperty]
    public partial string? ImportHint { get; private set; }

    /// <summary>Gets the storage estimate of the selection.</summary>
    [ObservableProperty]
    public partial StorageEstimate Estimate { get; private set; } = new(0, 0, 0, 0, true);

    /// <summary>Gets the storage line (design §7.7): what the import saves online and what is left.</summary>
    [ObservableProperty]
    public partial string StorageLine { get; private set; } = string.Empty;

    /// <summary>Gets the note above the buttons: ready, signed out, or rules declined.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>Gets the files that will be imported, each with the character it goes to and the picture it replaces (or null).</summary>
    internal IReadOnlyList<(ImportItem Item, CharacterChoice Character, string? ReplaceSha256)> Selection => [.. Rows
        .Where(r => r.Assigned is not null)
        .Select(r => (r.Proposal.Item, r.Assigned!, r.Section == ImportSection.Full ? r.SelectedReplace?.ReplaceSha256 : null))];

    /// <summary>Imports the selected pictures.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task ConfirmAsync() => _confirm(this);

    /// <summary>Closes the review without importing.</summary>
    [RelayCommand]
    private void Cancel() => _cancel();

    partial void OnUseImportedChanged(bool value) => Recalculate();

    private ImportReviewRowViewModel MakeRow(int index, PictureProposal proposal, IReadOnlyDictionary<string, CharacterPictures> existing)
    {
        CharacterChoice? Choice(RosterCharacter? c) => c is null ? null : Choices.FirstOrDefault(x => x.Id == c.Id);
        switch (proposal.Outcome)
        {
            case MatchOutcome.ByCode:
                return new ImportReviewRowViewModel(index, proposal, ImportSection.Matched, Choice(proposal.Character), [], string.Empty, Recalculate);
            case MatchOutcome.ByName:
                return new ImportReviewRowViewModel(
                    index, proposal, ImportSection.NeedsChoice, Choice(proposal.Character), [], $"Suggested from the file name: {proposal.Character!.Name}", Recalculate);
            case MatchOutcome.Conflict:
                return new ImportReviewRowViewModel(index, proposal, ImportSection.NeedsChoice, null, [], proposal.Reason ?? "No suggestion", Recalculate);
            case MatchOutcome.SameCharacter:
                return new ImportReviewRowViewModel(
                    index, proposal, ImportSection.NeedsChoice, null, [], "Another file of this pack already matched that character.", Recalculate);
            case MatchOutcome.Unmatched:
                return new ImportReviewRowViewModel(index, proposal, ImportSection.NeedsChoice, null, [], "No suggestion", Recalculate);
            case MatchOutcome.NoFreeSlot:
                var items = existing.GetValueOrDefault(PictureKeys.Of(proposal.Character!.Id))?.Items ?? [];
                var replace = new List<ReplaceChoice> { new(null, "Skip this picture") };
                replace.AddRange(items.Select((item, i) => new ReplaceChoice(item.Sha256, $"Replace “{TitleOf(item, i)}”")));
                return new ImportReviewRowViewModel(
                    index, proposal, ImportSection.Full, Choice(proposal.Character), replace, $"{proposal.Character.Name} already has 3 of your pictures.", Recalculate);
            default:
                return new ImportReviewRowViewModel(index, proposal, ImportSection.NotImported, null, [], proposal.Reason ?? "Not imported.", Recalculate);
        }
    }

    private static string Heading(string title, int count) =>
        count == 0 ? string.Empty : $"{title} ({count.ToString(CultureInfo.InvariantCulture)})";

    internal static string TitleOf(PictureItem item, int index) => item.Origin switch
    {
        PictureOrigin.Link => "My link",
        PictureOrigin.Import => $"Imported picture {(index + 1).ToString(CultureInfo.InvariantCulture)}",
        _ => "From my PC",
    };

    private void Recalculate()
    {
        var selection = Selection;
        ImportCount = selection.Count;
        ImportButtonText = ImportCount switch
        {
            0 => "Import pictures",
            1 => "Import 1 picture",
            _ => $"Import {ImportCount.ToString(CultureInfo.InvariantCulture)} pictures",
        };
        ImportHint = ImportCount == 0 ? "Choose at least one picture." : null;
        ImportCommand_NotifyCanExecute();
        OnPropertyChanged(nameof(CanImport));
        MatchedBadge = $"{Count(ImportSection.Matched)} matched";
        NeedsChoiceBadge = $"{Count(ImportSection.NeedsChoice)} need a choice";
        var full = Rows.Count(r => r.Section == ImportSection.Full);
        FullBadge = full == 1 ? "1 character is full" : $"{full.ToString(CultureInfo.InvariantCulture)} characters are full";
        NotImportedBadge = $"{(Rows.Count(r => r.Section == ImportSection.NotImported) + SkippedFiles.Count).ToString(CultureInfo.InvariantCulture)} not imported";
        Estimate = StorageEstimator.Estimate(
            [.. selection.Select(s => new EstimateItem(s.Item.Png.Length, UseImported, !_matchesOwnerCopy(s.Item)))], _usedBytes, _limitBytes);
        StorageLine = PictureTexts.ImportStorageLine(Estimate, _usedBytes, _limitBytes, _signedIn);
        Note = _rulesDeclined
            ? "Imported pictures stay in the preview only because the picture rules aren't accepted."
            : _signedIn
                ? "Imported pictures are saved online at unlisted addresses when you import them. Only your status uses them."
                : "Imported pictures appear in your preview. They're saved online, so Discord can show them, when you sign in on the Account page.";
        return;

        string Count(ImportSection section) => Rows.Count(r => r.Section == section).ToString(CultureInfo.InvariantCulture);
    }

    private void ImportCommand_NotifyCanExecute() => ConfirmCommand.NotifyCanExecuteChanged();
}
