using DbdPresence.Core.Presence;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>The member-list display options (FR-RPC-11) and their mapping to the stored <see cref="StatusDisplay"/>, shared by the pages that offer them.</summary>
public static class MemberListChoices
{
    /// <summary>Gets the choices in display order.</summary>
    public static IReadOnlyList<Choice<MemberListDisplay>> All { get; } =
    [
        new(MemberListDisplay.DiscordDefault, "Discord's default"), new(MemberListDisplay.Name, "Application name"),
        new(MemberListDisplay.State, "State line"), new(MemberListDisplay.Details, "Details line"),
    ];

    /// <summary>Maps a stored value to its choice (null = Discord's default).</summary>
    /// <param name="stored">The profile's status display.</param>
    public static MemberListDisplay FromStored(StatusDisplay? stored) => stored switch
    {
        StatusDisplay.Name => MemberListDisplay.Name,
        StatusDisplay.State => MemberListDisplay.State,
        StatusDisplay.Details => MemberListDisplay.Details,
        _ => MemberListDisplay.DiscordDefault,
    };

    /// <summary>Maps a choice to the stored value (Discord's default is stored as null, so the field is omitted).</summary>
    /// <param name="choice">The selected choice.</param>
    public static StatusDisplay? ToStored(MemberListDisplay choice) => choice switch
    {
        MemberListDisplay.Name => StatusDisplay.Name,
        MemberListDisplay.State => StatusDisplay.State,
        MemberListDisplay.Details => StatusDisplay.Details,
        _ => null,
    };
}
