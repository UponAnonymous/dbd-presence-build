using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Core.Game;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Presentation.Characters;

/// <summary>One character tile of the list (design §5.4). The view binds to these properties and nothing else.</summary>
public sealed partial class CharacterTileViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="CharacterTileViewModel"/> class.</summary>
    /// <param name="code">The game's character code (<c>K27</c>).</param>
    /// <param name="id">The pack slug (<c>the-onryo</c>).</param>
    /// <param name="name">The display name.</param>
    /// <param name="role">Killer or survivor.</param>
    public CharacterTileViewModel(string code, string id, string name, Role role)
    {
        Code = code;
        Id = id;
        Name = name;
        Role = role;
    }

    /// <summary>Gets the character code; the automation id is <c>Character-&lt;Code&gt;</c>.</summary>
    public string Code { get; }

    /// <summary>Gets the pack slug.</summary>
    public string Id { get; }

    /// <summary>Gets the mapping key, <c>character:&lt;id&gt;</c>.</summary>
    public string Key => PictureKeys.Of(Id);

    /// <summary>Gets the display name.</summary>
    public string Name { get; }

    /// <summary>Gets the role.</summary>
    public Role Role { get; }

    /// <summary>Gets the list group header: "Killers" or "Survivors".</summary>
    public string Group => Role == Role.Killer ? "Killers" : "Survivors";

    /// <summary>Gets the role as the panel shows it: "Killer" or "Survivor".</summary>
    public string RoleText => Role == Role.Killer ? "Killer" : "Survivor";

    /// <summary>Gets the file the tile shows, or null (the view then shows the outline).</summary>
    [ObservableProperty]
    public partial string? PreviewPath { get; private set; }

    /// <summary>Gets the line under the name: Default, Imported, From my PC, My link, No picture or Not downloaded.</summary>
    [ObservableProperty]
    public partial string SourceCaption { get; private set; } = "No picture";

    /// <summary>Gets a value indicating whether the picture in use cannot reach Discord now (the eye mark).</summary>
    [ObservableProperty]
    public partial bool IsPreviewOnly { get; private set; }

    /// <summary>Gets the tooltip of the eye mark.</summary>
    [ObservableProperty]
    public partial string PreviewOnlyTooltip { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the user changed this character (a picture was added or chosen).</summary>
    [ObservableProperty]
    public partial bool IsChangedByMe { get; private set; }

    /// <summary>Gets a value indicating whether the character was added to the list lately (the New badge).</summary>
    [ObservableProperty]
    public partial bool IsNew { get; private set; }

    /// <summary>Gets a value indicating whether the tile shows the role icon or an outline instead of a character picture.</summary>
    [ObservableProperty]
    public partial bool ShowsRoleFallback { get; private set; } = true;

    /// <summary>Gets the automation name: "&lt;Name&gt;, &lt;role&gt;. Picture: &lt;source&gt;. &lt;Discord line&gt;." plus ", new" when new.</summary>
    [ObservableProperty]
    public partial string AutomationName { get; private set; } = string.Empty;

    internal void Update(
        string? previewPath, string sourceCaption, bool previewOnly, string tooltip, bool changed, bool isNew, bool fallback, string discordLine)
    {
        PreviewPath = previewPath;
        SourceCaption = sourceCaption;
        IsPreviewOnly = previewOnly;
        PreviewOnlyTooltip = tooltip;
        IsChangedByMe = changed;
        IsNew = isNew;
        ShowsRoleFallback = fallback;
        AutomationName = $"{Name}, {RoleText.ToLowerInvariant()}. Picture: {sourceCaption}. {discordLine}." + (isNew ? ", new" : string.Empty);
    }
}

/// <summary>One choice in the side panel's picture list: the default portrait, the owner picture in use, or one of the user's own (design §5.5).</summary>
public sealed partial class PictureOptionViewModel : ObservableObject
{
    private readonly Action<PictureOptionViewModel> _use;
    private readonly Action<PictureOptionViewModel> _requestRemove;
    private readonly Action<PictureOptionViewModel> _confirmRemove;
    private readonly Action<PictureOptionViewModel> _cancelRemove;

    internal PictureOptionViewModel(
        int index,
        string title,
        string sourceCaption,
        string discordLine,
        string? thumbnailPath,
        string? link,
        PictureUse? use,
        PictureItem? item,
        bool isInUse,
        bool canRemove,
        string removeConfirmText,
        Action<PictureOptionViewModel> useAction,
        Action<PictureOptionViewModel> requestRemove,
        Action<PictureOptionViewModel> confirmRemove,
        Action<PictureOptionViewModel> cancelRemove)
    {
        Index = index;
        Title = title;
        SourceCaption = sourceCaption;
        DiscordLine = discordLine;
        ThumbnailPath = thumbnailPath;
        Link = link;
        Use = use;
        Item = item;
        IsInUse = isInUse;
        CanRemove = canRemove;
        RemoveConfirmText = removeConfirmText;
        _use = useAction;
        _requestRemove = requestRemove;
        _confirmRemove = confirmRemove;
        _cancelRemove = cancelRemove;
    }

    /// <summary>Gets the position in the list; the automation ids are <c>Picture-&lt;Index&gt;</c> and <c>RemovePicture-&lt;Index&gt;</c>.</summary>
    public int Index { get; }

    /// <summary>Gets the title: Default portrait, the pack name, From my PC or My link.</summary>
    public string Title { get; }

    /// <summary>Gets the source line, for example "From your PC · only your status".</summary>
    public string SourceCaption { get; }

    /// <summary>Gets the Discord line, for example "On Discord · saved online, unlisted" or "Preview only · storage full".</summary>
    public string DiscordLine { get; }

    /// <summary>Gets the file of the 56 DIP thumbnail, or null (a link shows the link glyph; a missing file shows "Missing").</summary>
    public string? ThumbnailPath { get; }

    /// <summary>Gets the link, for a link picture.</summary>
    public string? Link { get; }

    /// <summary>Gets a value indicating whether the thumbnail is the labelled link tile.</summary>
    public bool IsLink => Link is not null;

    /// <summary>Gets what choosing this option stores: null for the default portrait.</summary>
    public PictureUse? Use { get; }

    /// <summary>Gets the user's own picture behind the option, or null for the default portrait and owner pictures.</summary>
    public PictureItem? Item { get; }

    /// <summary>Gets a value indicating whether this is the picture in use (the In use badge and the selected radio).</summary>
    [ObservableProperty]
    public partial bool IsInUse { get; private set; }

    /// <summary>Gets a value indicating whether the picture can be removed (own pictures only; the default portrait never).</summary>
    public bool CanRemove { get; }

    /// <summary>Gets a value indicating whether the option shows its inline confirmation.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; internal set; }

    /// <summary>Gets the confirmation sentence (design §7.4).</summary>
    public string RemoveConfirmText { get; }

    /// <summary>Gets the automation name of the option.</summary>
    public string AutomationName => $"Use {Title}. {SourceCaption}. {DiscordLine}.";

    /// <summary>Makes this the picture in use.</summary>
    [RelayCommand]
    private void Choose() => _use(this);

    /// <summary>Shows the inline confirmation.</summary>
    [RelayCommand]
    private void Remove() => _requestRemove(this);

    /// <summary>Removes the picture.</summary>
    [RelayCommand]
    private void ConfirmRemove() => _confirmRemove(this);

    /// <summary>Keeps the picture.</summary>
    [RelayCommand]
    private void KeepPicture() => _cancelRemove(this);
}

/// <summary>A picture of another pack that can be added to the selected character ("From another pack", design §5.5).</summary>
public sealed partial class PackPictureChoiceViewModel : ObservableObject
{
    private readonly Func<PackPictureChoiceViewModel, Task> _add;

    internal PackPictureChoiceViewModel(PicturePack pack, PicturePiece piece, bool isAdded, Func<PackPictureChoiceViewModel, Task> add)
    {
        Pack = pack;
        Piece = piece;
        IsAdded = isAdded;
        _add = add;
    }

    /// <summary>Gets the pack.</summary>
    public PicturePack Pack { get; }

    /// <summary>Gets the picture.</summary>
    public PicturePiece Piece { get; }

    /// <summary>Gets the pack name.</summary>
    public string PackName => Pack.Name;

    /// <summary>Gets the source line: "owner pack · everyone can use it" or "imported · only your status".</summary>
    public string SourceCaption => Pack.IsImported ? "Imported · only your status" : "Owner pack · everyone can use it";

    /// <summary>Gets the 48 DIP thumbnail file.</summary>
    public string? ThumbnailPath => Piece.LocalPath;

    /// <summary>Gets a value indicating whether the character already has the picture (the button then says "Added").</summary>
    public bool IsAdded { get; }

    /// <summary>Gets the button text: Add or Added.</summary>
    public string ActionText => IsAdded ? "Added" : "Add";

    /// <summary>Adds the picture and makes it the one in use.</summary>
    [RelayCommand]
    private Task AddAsync() => IsAdded ? Task.CompletedTask : _add(this);
}
