using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>Opens the file pickers (the Windows file dialog in the app; a fake in tests). Both return null when the user cancels.</summary>
public interface IFilePicker
{
    /// <summary>Asks for a pack archive (.zip) to import.</summary>
    string? PickPackArchive();

    /// <summary>Asks for an image file (PNG, JPEG or WebP).</summary>
    string? PickImage();
}

/// <summary>The four places a pack can be chosen for (FR-PACK-06).</summary>
public enum PackCategory
{
    /// <summary>The Killer role icon.</summary>
    Killer,

    /// <summary>The Survivor role icon.</summary>
    Survivor,

    /// <summary>Character images.</summary>
    Characters,

    /// <summary>Map images.</summary>
    Maps,
}

/// <summary>One image in a pack card's grid preview.</summary>
/// <param name="Label">What the image is, in words.</param>
/// <param name="Location">Where the UI loads it from, or null when the file is missing.</param>
public sealed record PackTile(string Label, PackImageLocation? Location)
{
    /// <summary>Gets the text shown in place of a picture whose file is missing (empty when the picture is there).</summary>
    public string MissingText => Location is null ? PackLabels.Missing : string.Empty;

    /// <summary>Gets the accessible name: the label, plus that the picture is missing when it is.</summary>
    public string AccessibleName => Location is null ? $"{Label}, {PackLabels.Missing.ToLowerInvariant()}" : Label;
}

/// <summary>Words for pack images.</summary>
public static class PackLabels
{
    /// <summary>The stand-in text for an image whose file is missing.</summary>
    public const string Missing = "Missing";

    /// <summary>The display name of a category.</summary>
    public static string Name(PackCategory category) => category switch
    {
        PackCategory.Killer => "Killer",
        PackCategory.Survivor => "Survivor",
        PackCategory.Characters => "Characters",
        _ => "Maps",
    };

    /// <summary>A label for a mapping key such as <c>character:the-trapper</c>.</summary>
    public static string ForKey(string key) => key switch
    {
        "role:killer" => "Killer icon",
        "role:survivor" => "Survivor icon",
        "app:mark" => "App mark",
        "app:mode-generic" => "Game mode tile",
        "map:generic" => "Generic map tile",
        "character:generic-killer" => "Generic Killer character",
        "character:generic-survivor" => "Generic Survivor character",
        _ => Pretty(key),
    };

    private static string Pretty(string key)
    {
        if (!PackKey.TryParse(key, out var kind, out var slug))
        {
            return key;
        }

        var words = string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(w[0])}{w[1..]}")));
        return kind switch
        {
            PackEntryKind.Character => $"Character: {words}",
            PackEntryKind.Map => $"Map: {words}",
            PackEntryKind.Role => $"Role: {words}",
            _ => words,
        };
    }
}

/// <summary>"Use for Killer": a pack's switch for one category. Choosing it makes this pack the active one for the category.</summary>
public sealed partial class PackCategoryChoiceViewModel : ObservableObject
{
    private readonly Action<PackCategory, bool> _changed;
    private bool _loading;

    /// <summary>Initializes a new instance of the <see cref="PackCategoryChoiceViewModel"/> class.</summary>
    public PackCategoryChoiceViewModel(PackCategory category, string packName, bool isAvailable, Action<PackCategory, bool> changed)
    {
        Category = category;
        _changed = changed;
        Name = PackLabels.Name(category);
        AutomationName = $"Use {packName} for {Name}";
        IsAvailable = isAvailable;
        HelpText = isAvailable ? $"Show this pack's {Name.ToLowerInvariant()} images." : $"This pack has no {Name.ToLowerInvariant()} image.";
    }

    /// <summary>Gets the category.</summary>
    public PackCategory Category { get; }

    /// <summary>Gets the label.</summary>
    public string Name { get; }

    /// <summary>Gets the accessible name, for example "Use My pack for Killer".</summary>
    public string AutomationName { get; }

    /// <summary>Gets the tooltip and help text.</summary>
    public string HelpText { get; }

    /// <summary>Gets a value indicating whether the pack has an image for this category.</summary>
    public bool IsAvailable { get; }

    /// <summary>Gets or sets a value indicating whether this pack is the active one for the category.</summary>
    [ObservableProperty]
    public partial bool IsChosen { get; set; }

    /// <summary>Gets a value indicating whether the user can change the switch.</summary>
    public bool CanToggle => IsAvailable;

    /// <summary>Sets the value from settings without raising a change.</summary>
    public void Load(bool chosen)
    {
        _loading = true;
        try
        {
            IsChosen = chosen;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIsChosenChanged(bool value)
    {
        if (!_loading)
        {
            _changed(Category, value);
        }
    }
}

/// <summary>A pack card on the Packs page: grid preview, "In use" badge and per-category switches.</summary>
public sealed partial class PackCardViewModel : ObservableObject
{
    /// <summary>Most images shown in a card's grid; the rest are counted.</summary>
    public const int MaxTiles = 24;

    /// <summary>Initializes a new instance of the <see cref="PackCardViewModel"/> class.</summary>
    public PackCardViewModel(PackCard card, IReadOnlyList<PackTile> tiles, Action<PackCategory, bool, PackCardViewModel> changed, Action<PackCardViewModel> remove)
    {
        ArgumentNullException.ThrowIfNull(card);
        Id = card.Info.Id;
        Name = card.Info.Name;
        Author = card.Info.Author;
        Credit = card.Info.Credit;
        LicenceNote = card.Info.LicenceNote;
        IsImported = card.Origin == PackOrigin.Imported;
        Kind = IsImported ? "Imported by you" : "Official pack";
        BehaviourNotice = card.Info.BehaviourArt && !IsImported ? AppCopy.BehaviourNotice : null;
        var holders = card.Entries.Where(e => e.Licensed is not null).Select(e => e.Licensed!.Holder).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        LicensedNote = holders.Length == 0 ? null : "Includes licensed images owned by others: " + string.Join(", ", holders) + ".";
        ImageCount = card.Entries.Count;
        Tiles = [.. tiles.Take(MaxTiles)];
        MoreCount = Math.Max(0, tiles.Count - MaxTiles);
        Categories =
        [
            .. Enum.GetValues<PackCategory>().Select(c => new PackCategoryChoiceViewModel(
                c, Name, Provides(card, c), (category, chosen) => changed(category, chosen, this))),
        ];
        RemoveCommand = new RelayCommand(() => remove(this), () => IsImported);
        Summary = $"{ImageCount.ToString(CultureInfo.InvariantCulture)} image{(ImageCount == 1 ? string.Empty : "s")}";
    }

    /// <summary>Gets the pack id.</summary>
    public string Id { get; }

    /// <summary>Gets the pack name.</summary>
    public string Name { get; }

    /// <summary>Gets the creator text (for imports, as the user typed it).</summary>
    public string Author { get; }

    /// <summary>Gets the credit line, for example the source file of an import.</summary>
    public string? Credit { get; }

    /// <summary>Gets the licence note.</summary>
    public string LicenceNote { get; }

    /// <summary>Gets a value indicating whether the user imported this pack.</summary>
    public bool IsImported { get; }

    /// <summary>Gets Behaviour's notice when the pack's signed index flags Behaviour artwork; otherwise null.</summary>
    public string? BehaviourNotice { get; }

    /// <summary>Gets a value indicating whether the Behaviour notice shows on this card.</summary>
    public bool HasBehaviourNotice => BehaviourNotice is not null;

    /// <summary>Gets the sentence naming the rights holders of licensed images in this pack, or null when it has none.</summary>
    public string? LicensedNote { get; }

    /// <summary>Gets a value indicating whether the licensed-images sentence shows.</summary>
    public bool HasLicensedNote => LicensedNote is not null;

    /// <summary>Gets a value indicating whether the card has a credit line.</summary>
    public bool HasCredit => !string.IsNullOrWhiteSpace(Credit);

    /// <summary>Gets where the pack comes from, in words.</summary>
    public string Kind { get; }

    /// <summary>Gets the number of images.</summary>
    public int ImageCount { get; }

    /// <summary>Gets "n images".</summary>
    public string Summary { get; }

    /// <summary>Gets the images shown in the grid.</summary>
    public IReadOnlyList<PackTile> Tiles { get; }

    /// <summary>Gets how many images are not shown in the grid.</summary>
    public int MoreCount { get; }

    /// <summary>Gets a value indicating whether the grid leaves images out.</summary>
    public bool HasMore => MoreCount > 0;

    /// <summary>Gets the "and n more" text.</summary>
    public string MoreText => string.Create(CultureInfo.InvariantCulture, $"and {MoreCount} more image{(MoreCount == 1 ? string.Empty : "s")}");

    /// <summary>Gets the per-category switches.</summary>
    public IReadOnlyList<PackCategoryChoiceViewModel> Categories { get; }

    /// <summary>Gets the command that asks to remove this pack (imported packs only).</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>Gets a value indicating whether any category uses this pack (the "In use" badge).</summary>
    [ObservableProperty]
    public partial bool InUse { get; private set; }

    /// <summary>Gets what the badge says: "In use" or "In use for Killer, Maps".</summary>
    [ObservableProperty]
    public partial string InUseText { get; private set; } = string.Empty;

    /// <summary>Gets the accessible name of the card.</summary>
    public string AutomationName => $"{Name}, {Kind.ToLowerInvariant()}, {Summary}";

    /// <summary>Updates the badge and switches from the current choices.</summary>
    public void Refresh(IReadOnlyDictionary<PackCategory, string> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var used = Categories.Where(c => chosen.TryGetValue(c.Category, out var id) && id == Id).ToArray();
        foreach (var choice in Categories)
        {
            choice.Load(used.Contains(choice));
        }

        InUse = used.Length > 0;
        InUseText = used.Length == 0
            ? string.Empty
            : used.Length == Categories.Count ? "In use" : "In use for " + string.Join(", ", used.Select(c => c.Name));
    }

    private static bool Provides(PackCard card, PackCategory category) => category switch
    {
        PackCategory.Killer => card.Entries.Any(e => e.Key == "role:killer"),
        PackCategory.Survivor => card.Entries.Any(e => e.Key == "role:survivor"),
        PackCategory.Characters => card.Entries.Any(e => e.Kind == PackEntryKind.Character),
        _ => card.Entries.Any(e => e.Kind == PackEntryKind.Map),
    };
}

/// <summary>What an imported image is for, as the user picks it on the mapping screen (FR-PACK-07).</summary>
public enum ImportSlotKind
{
    /// <summary>Not mapped: the image is ignored.</summary>
    None,

    /// <summary>The Killer role icon.</summary>
    Killer,

    /// <summary>The Survivor role icon.</summary>
    Survivor,

    /// <summary>A character portrait; the name says which.</summary>
    Character,

    /// <summary>A map image; the name says which.</summary>
    Map,
}

/// <summary>
/// One image on the mapping confirmation screen. The proposal from the file name is pre-filled but never applied: an
/// image is imported only when the user ticks "Import this image" for it (FR-PACK-07).
/// </summary>
public sealed partial class ImportRowViewModel : ObservableObject
{
    private bool _settling;

    /// <summary>Initializes a new instance of the <see cref="ImportRowViewModel"/> class.</summary>
    public ImportRowViewModel(ImportItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        DisplayName = item.DisplayName;
        Png = item.Png;
        if (item.Proposal is { } proposal)
        {
            _settling = true;
            (SlotKind, SlotName) = proposal.Kind switch
            {
                ProposalKind.Role when proposal.Slug == "killer" => (ImportSlotKind.Killer, string.Empty),
                ProposalKind.Role => (ImportSlotKind.Survivor, string.Empty),
                ProposalKind.Character => (ImportSlotKind.Character, proposal.Slug),
                _ => (ImportSlotKind.Map, proposal.Slug),
            };
            _settling = false;
            SuggestionText = $"Suggested from the file name: {PackLabels.ForKey(proposal.Key)}. Not applied until you confirm it.";
        }
        else
        {
            SuggestionText = "No suggestion. Choose what this image is for, or leave it out.";
        }
    }

    /// <summary>Gets the choices for <see cref="SlotKind"/>.</summary>
    public static IReadOnlyList<Choice<ImportSlotKind>> SlotKinds { get; } =
    [
        new(ImportSlotKind.None, "Don't import"), new(ImportSlotKind.Killer, "Killer icon"), new(ImportSlotKind.Survivor, "Survivor icon"),
        new(ImportSlotKind.Character, "Character"), new(ImportSlotKind.Map, "Map"),
    ];

    /// <summary>Gets the normalised image.</summary>
    public ImportItem Item { get; }

    /// <summary>Gets the sanitised file name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the clean PNG for the thumbnail.</summary>
    public byte[] Png { get; }

    /// <summary>Gets the accessible name of the row.</summary>
    public string AutomationName => $"Image {DisplayName}";

    /// <summary>Gets the note about the suggestion.</summary>
    public string SuggestionText { get; }

    /// <summary>Gets or sets what the image is for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Key), nameof(NeedsName), nameof(CanConfirm))]
    public partial ImportSlotKind SlotKind { get; set; }

    /// <summary>Gets or sets the character or map name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Key), nameof(CanConfirm))]
    public partial string SlotName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user confirmed importing this image for <see cref="Key"/>.</summary>
    [ObservableProperty]
    public partial bool IsConfirmed { get; set; }

    /// <summary>Gets what is wrong with the choice, if anything.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>Gets a value indicating whether the name field applies.</summary>
    public bool NeedsName => SlotKind is ImportSlotKind.Character or ImportSlotKind.Map;

    /// <summary>Gets a value indicating whether the choice names a valid slot.</summary>
    public bool CanConfirm => Key is not null;

    /// <summary>Gets the mapping key such as <c>character:the-trapper</c>, or null while the choice is incomplete.</summary>
    public string? Key
    {
        get
        {
            switch (SlotKind)
            {
                case ImportSlotKind.Killer:
                    return "role:killer";
                case ImportSlotKind.Survivor:
                    return "role:survivor";
                case ImportSlotKind.Character:
                case ImportSlotKind.Map:
                    var slug = MappingProposer.Slugify(SlotName);
                    return PackKey.IsSlug(slug) ? $"{(SlotKind == ImportSlotKind.Character ? "character" : "map")}:{slug}" : null;
                default:
                    return null;
            }
        }
    }

    partial void OnIsConfirmedChanged(bool value)
    {
        if (_settling)
        {
            return;
        }

        if (value && Key is null)
        {
            _settling = true;
            IsConfirmed = false;
            _settling = false;
            Problem = NeedsName ? "Type a name first." : "Choose what this image is for first.";
            return;
        }

        Problem = null;
    }

    partial void OnSlotKindChanged(ImportSlotKind value) => UnconfirmIfIncomplete();

    partial void OnSlotNameChanged(string value) => UnconfirmIfIncomplete();

    private void UnconfirmIfIncomplete()
    {
        if (!_settling && IsConfirmed && Key is null)
        {
            _settling = true;
            IsConfirmed = false;
            _settling = false;
        }
    }
}

/// <summary>A per-image override row: the user's own picture for one slot, shown only in the preview unless the user uploaded it (FR-ONL-09).</summary>
public sealed partial class OverrideRowViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="OverrideRowViewModel"/> class.</summary>
    /// <param name="key">The mapping key.</param>
    /// <param name="choose">Picks a file.</param>
    /// <param name="reset">Removes the override.</param>
    /// <param name="upload">Uploads the override so Discord can show it, or null when this build has no uploads.</param>
    public OverrideRowViewModel(string key, Func<OverrideRowViewModel, Task> choose, Action<OverrideRowViewModel> reset, Func<OverrideRowViewModel, Task>? upload = null)
    {
        Key = key;
        Label = PackLabels.ForKey(key);
        ChooseCommand = new AsyncRelayCommand(() => choose(this));
        ResetCommand = new RelayCommand(() => reset(this), () => HasOverride);
        UploadCommand = new AsyncRelayCommand(() => upload is null ? Task.CompletedTask : upload(this), () => upload is not null && CanUpload);
        AutomationName = $"Your own image for {Label}";
    }

    /// <summary>Gets the command that uploads the image so Discord shows it.</summary>
    public IAsyncRelayCommand UploadCommand { get; }

    /// <summary>Gets a value indicating whether this image is uploaded, so Discord shows it at its unlisted address.</summary>
    [ObservableProperty]
    public partial bool IsUploaded { get; private set; }

    /// <summary>Gets a value indicating whether the Upload button shows (an own image that is not uploaded yet, in a build with uploads).</summary>
    [ObservableProperty]
    public partial bool ShowUpload { get; private set; }

    /// <summary>Gets a value indicating whether the Upload button is usable now (policy accepted, online, not blocked, under the limit).</summary>
    [ObservableProperty]
    public partial bool CanUpload { get; private set; }

    /// <summary>Gets the mapping key.</summary>
    public string Key { get; }

    /// <summary>Gets the label, for example "Killer icon".</summary>
    public string Label { get; }

    /// <summary>Gets the accessible name of the row.</summary>
    public string AutomationName { get; }

    /// <summary>Gets the command that picks a file.</summary>
    public IAsyncRelayCommand ChooseCommand { get; }

    /// <summary>Gets the command that removes the override.</summary>
    public IRelayCommand ResetCommand { get; }

    /// <summary>Gets a value indicating whether the user's own image is set.</summary>
    [ObservableProperty]
    public partial bool HasOverride { get; private set; }

    /// <summary>Gets what the preview shows for this slot now.</summary>
    [ObservableProperty]
    public partial PackImageLocation? Current { get; private set; }

    /// <summary>Gets the line saying where the current image comes from.</summary>
    [ObservableProperty]
    public partial string CurrentText { get; private set; } = string.Empty;

    /// <summary>Updates the row.</summary>
    /// <param name="hasOverride">Whether the user's own image is set.</param>
    /// <param name="current">What the preview shows.</param>
    /// <param name="uploaded">Whether that image is uploaded.</param>
    /// <param name="uploadsOffered">Whether this build offers uploads.</param>
    /// <param name="canUpload">Whether an upload may start now.</param>
    public void Update(bool hasOverride, PackImageLocation? current, bool uploaded = false, bool uploadsOffered = false, bool canUpload = false)
    {
        HasOverride = hasOverride;
        Current = current;
        IsUploaded = hasOverride && uploaded;
        ShowUpload = hasOverride && !IsUploaded && uploadsOffered;
        CanUpload = ShowUpload && canUpload;
        CurrentText = !hasOverride ? "No image" : IsUploaded ? "Your own image, uploaded: Discord shows it (unlisted)" : "Your own image (shown only in your preview)";
        ResetCommand.NotifyCanExecuteChanged();
        UploadCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>What the preview and Discord show for one slot (the "Discord shows" panel).</summary>
/// <param name="Label">The slot, for example "Killer icon".</param>
/// <param name="Preview">What your preview shows.</param>
/// <param name="Discord">What Discord is told, in words.</param>
/// <param name="LocalOnly">True when the preview image is on this PC and Discord shows no image for it.</param>
public sealed record PackPreviewRow(string Label, PackImageLocation? Preview, string Discord, bool LocalOnly);
