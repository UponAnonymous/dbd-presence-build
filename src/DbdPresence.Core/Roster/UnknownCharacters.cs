using DbdPresence.Core.Game;

namespace DbdPresence.Core.Roster;

/// <summary>
/// One character name read on screen in a character-name position (the TAB or scoreboard character line, or the killer row) that is
/// not in the vocabulary (owner decision D35). It is a fact about the game, not about a player: never a player name or an image.
/// </summary>
/// <param name="Name">The text as read on screen. The receiver folds it (<see cref="RosterNameFold"/>); only the folded form ever leaves the PC.</param>
/// <param name="Role">Killer or survivor, from the position the name was read in. <see cref="Role.None"/> is ignored.</param>
/// <param name="MatchKey">
/// Any value that is the same for every sighting in one match and different between matches (for example the engine's match
/// sequence number). It never leaves the PC: the receiver gives each match a fresh random id.
/// </param>
public sealed record UnknownCharacterSighting(string Name, Role Role, long MatchKey);

/// <summary>
/// Where detection hands unknown character names (D35). Implemented by <c>DbdPresence.Online</c>, which reports them only from the
/// owner's own app account (friends' apps never report), batched, rate-limited, and only while online features are allowed. The service
/// adds a character for everyone once the identical folded name was seen in 3 separate matches. Detection itself never changes because of
/// a report: a learned character reaches the vocabulary only through the signed roster.
/// </summary>
public interface IUnknownCharacterSink
{
    /// <summary>Records one sighting. Never throws for bad input and never blocks: unusable names are dropped, the network is used later.</summary>
    /// <param name="sighting">The sighting.</param>
    void Report(UnknownCharacterSighting sighting);
}

/// <summary>A sink that drops every sighting: the app before sign-in is wired, unit tests, and builds without online features.</summary>
public sealed class NullUnknownCharacterSink : IUnknownCharacterSink
{
    /// <summary>Gets the shared instance.</summary>
    public static NullUnknownCharacterSink Instance { get; } = new();

    /// <inheritdoc />
    public void Report(UnknownCharacterSighting sighting) => ArgumentNullException.ThrowIfNull(sighting);
}
