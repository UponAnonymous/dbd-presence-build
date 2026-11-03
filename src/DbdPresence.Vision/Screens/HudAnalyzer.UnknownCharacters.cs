using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Prompts;
using DbdPresence.Vision.Text;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Screens;

/// <summary>A character name read in a character-name position that the closed lists do not contain (D35).</summary>
/// <param name="Name">The text as read.</param>
/// <param name="Role">Survivor for a survivor row or lobby pair; killer for the last row of TAB or the scoreboard.</param>
internal sealed record UnknownCharacterCandidate(string Name, GameRole Role);

/// <summary>
/// The unknown-character reading (owner decision D35, ADR-0017). It is a pure function of one frame's text. A line is a candidate only when all of
/// these hold:
/// <list type="bullet">
/// <item>it is the first line of a row of TAB match details or the scoreboard that has at least two lines (character over player name), or the first line
/// of a pre-match lobby pair that has two lines; a row of one line may be a player's name alone, so it is never a candidate (the same rule as the killer name, review U5 F7);</item>
/// <item>the row names no character of the closed lists anywhere (no span matches, none is ambiguous), and the line is not near a known name (it could be a misread of one);</item>
/// <item>the same screen shows at least one row whose first line is a known character with a second line below it, and the row has as many lines as that one: the
/// evidence that the first line of a row really is the character position in this layout (a row that lost its character line to an OCR miss has fewer lines);</item>
/// <item>the line has the shape of a character name (<see cref="CharacterNameShape"/>), and contains none of the names on the screen (every other line of every row,
/// the lobby's player list, the owner's name and the Discord display name), compared as letters-only skeletons like the power prompt learning path (<see cref="NameSkeletonGuard"/>).</item>
/// </list>
/// The role comes from the position only: the last of the five rows is the killer row, the others are survivors. Lines are never kept or logged here.
/// </summary>
internal sealed partial class HudAnalyzer
{
    /// <summary>A candidate whose normalised distance to a known name is below this is taken for a misreading of that name (an initial guess, not measured).</summary>
    public const double NearKnownDistance = 0.26;

    /// <summary>Reads the unknown character names of one frame.</summary>
    /// <param name="crops">The frame's regions and text.</param>
    /// <param name="context">The detection context (the owner's name).</param>
    /// <param name="screen">The screen the pipeline found.</param>
    /// <param name="otherPersonName">Another name of a person (the Discord display name), or null.</param>
    /// <returns>The candidates; none while the owner's name is unknown.</returns>
    public IReadOnlyList<UnknownCharacterCandidate> ReadUnknownCharacters(FrameCrops crops, DetectionContext context, ScreenKind screen, string? otherPersonName = null)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(context);
        if (context.OwnerName is null)
        {
            // Without the owner's name the owner's own player name cannot be excluded, and it repeats in every match (round-2 review M1).
            return [];
        }

        return screen switch
        {
            ScreenKind.MatchDetails => UnknownInRows(crops, context, scoreboard: false, otherPersonName),
            ScreenKind.Scoreboard => UnknownInRows(crops, context, scoreboard: true, otherPersonName),
            ScreenKind.PreMatchLobby => UnknownInLobby(crops, context, otherPersonName),
            _ => [],
        };
    }

    private List<UnknownCharacterCandidate> UnknownInRows(FrameCrops crops, DetectionContext context, bool scoreboard, string? otherPersonName)
    {
        var rows = new List<(int Index, IReadOnlyList<string> Lines, ParsedRow Parsed)>(HudProfile.PlayerRows);
        for (var i = 0; i < HudProfile.PlayerRows; i++)
        {
            var text = crops.GetText(scoreboard ? HudRegionIds.ScoreRow(i) : HudRegionIds.TabRow(i));
            rows.Add((i, AnchorText.Lines(text), ParseRow(text)));
        }

        var found = new List<UnknownCharacterCandidate>();
        var knownLineCounts = rows
            .Where(r => r.Lines.Count >= 2 && r.Parsed.CharacterId is not null && !r.Parsed.Ambiguous && r.Parsed.Chosen?.Line == 0)
            .Select(r => r.Lines.Count)
            .ToHashSet();
        var open = rows.Where(r => knownLineCounts.Contains(r.Lines.Count) && r.Parsed.CharacterId is null && !r.Parsed.Ambiguous).ToList();
        if (open.Count == 0)
        {
            return found;
        }

        var names = new List<string>();
        foreach (var (_, lines, parsed) in rows)
        {
            var characterLine = parsed.Chosen?.Line ?? 0;
            for (var l = 0; l < lines.Count; l++)
            {
                if (l != characterLine)
                {
                    names.Add(lines[l]);
                }
            }
        }

        AddOtherNames(names, context, otherPersonName);
        var forbidden = NameSkeletonGuard.ForbiddenForms(names);
        foreach (var (index, lines, _) in open)
        {
            if (IsUnknownCharacterLine(lines[0], forbidden))
            {
                // The last of the five rows is the killer row; the four above it are survivors.
                found.Add(new UnknownCharacterCandidate(lines[0], index == HudProfile.PlayerRows - 1 ? GameRole.Killer : GameRole.Survivor));
            }
        }

        return found;
    }

    private List<UnknownCharacterCandidate> UnknownInLobby(FrameCrops crops, DetectionContext context, string? otherPersonName)
    {
        var columns = new List<IReadOnlyList<string>>(HudProfile.LobbyColumns);
        for (var c = 0; c < HudProfile.LobbyColumns; c++)
        {
            columns.Add(AnchorText.Lines(crops.GetText(HudRegionIds.LobbyPair(c))));
        }

        var found = new List<UnknownCharacterCandidate>();
        var knownLineCounts = columns
            .Where(lines => lines.Count >= 2 && _vocabulary.Survivors.Match(lines[0]).IsAccepted && !IsKnownCharacter(lines[1]))
            .Select(lines => lines.Count)
            .ToHashSet();
        var open = columns.Where(lines => knownLineCounts.Contains(lines.Count)).ToList();
        if (open.Count == 0)
        {
            return found;
        }

        var names = new List<string>();
        foreach (var lines in columns)
        {
            names.AddRange(lines.Skip(1));
        }

        names.AddRange(AnchorText.Lines(crops.GetText(HudRegionIds.KillerList)));
        AddOtherNames(names, context, otherPersonName);
        var forbidden = NameSkeletonGuard.ForbiddenForms(names);
        foreach (var lines in open)
        {
            // A second line that is itself a character name is anonymous mode: the line below is not a player name, so the pair is not understood.
            if (!IsKnownCharacter(lines[1]) && IsUnknownCharacterLine(lines[0], forbidden))
            {
                found.Add(new UnknownCharacterCandidate(lines[0], GameRole.Survivor));
            }
        }

        return found;
    }

    /// <summary>
    /// The owner's name and the signed-in Discord display name, which are on screen as a person's name whatever else the frame shows. The trial column (<c>hud.row.*</c>) is deliberately not used here:
    /// on these screens it overlaps the rows themselves, so it would hold the character names too.
    /// </summary>
    private static void AddOtherNames(List<string> names, DetectionContext context, string? otherPersonName)
    {
        if (context.OwnerName is { } owner)
        {
            names.Add(owner);
        }

        if (!string.IsNullOrWhiteSpace(otherPersonName))
        {
            names.Add(otherPersonName);
        }
    }

    private bool IsUnknownCharacterLine(string line, HashSet<string> forbidden) =>
        CharacterNameShape.IsShaped(line) && !IsNearKnownCharacter(line) && !NameSkeletonGuard.ContainsPersonName(line, forbidden);

    private bool IsKnownCharacter(string line) => _vocabulary.Survivors.Match(line).IsAccepted || _vocabulary.Killers.Match(line).IsAccepted;

    /// <summary>Whether the line is, or is close to, a name of the closed lists (a misreading of a known character is never a new one).</summary>
    private bool IsNearKnownCharacter(string line)
    {
        foreach (var matcher in new[] { _vocabulary.Killers, _vocabulary.Survivors })
        {
            var match = matcher.Match(line);
            if (match.Outcome is MatchOutcome.Accepted or MatchOutcome.Ambiguous or MatchOutcome.GroupOnly || match.Distance < NearKnownDistance)
            {
                return true;
            }
        }

        return false;
    }
}
