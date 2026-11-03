using DbdPresence.Core.Roster;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// Decides whether a line of text has the shape of a character name, which is the first condition for reporting it as an unknown character (D35).
/// The shape is deliberately narrow: letters, spaces, apostrophes, dots and hyphens only (so a player name with a digit or an underscore is never
/// a candidate), two to five words of at least two letters (a single word is the shape of most player names, so a one-word character is not learned), at least five letters in all, and a fold that the roster accepts
/// (<see cref="RosterNameFold.FoldForReport"/>). It says nothing about whether the text is a character: the other conditions (position, two lines,
/// not a player name on screen, not near a known name) are checked by <see cref="HudAnalyzer"/>.
/// </summary>
internal static class CharacterNameShape
{
    /// <summary>The fewest letters in a name (a shorter line is too little to tell from noise).</summary>
    public const int MinLetters = 5;

    /// <summary>The longest line that is considered.</summary>
    public const int MaxLength = 40;

    /// <summary>The fewest words in a name.</summary>
    public const int MinWords = 2;

    /// <summary>The most words in a name.</summary>
    public const int MaxWords = 5;

    /// <summary>The fewest letters in one word.</summary>
    public const int MinWordLetters = 2;

    /// <summary>Returns whether the line has the shape of a character name.</summary>
    /// <param name="line">One recognised line.</param>
    public static bool IsShaped(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var text = line.Trim();
        if (text.Length is < MinLetters or > MaxLength || !char.IsLetter(text[0]) || !char.IsLetter(text[^1]))
        {
            return false;
        }

        var previousMark = false;
        foreach (var c in text)
        {
            var mark = c is '\'' or '’' or '‘' or '.' or '-';
            if (!(char.IsLetter(c) || c == ' ' || mark) || (mark && previousMark))
            {
                return false;
            }

            previousMark = mark;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < MinWords or > MaxWords || words.Count(w => w.Count(char.IsLetter) >= MinWordLetters) < MinWords || words.Any(w => !IsWord(w)))
        {
            return false;
        }

        return text.Count(char.IsLetter) >= MinLetters && RosterNameFold.FoldForReport(text) is not null;
    }

    /// <summary>A word has at least two letters; a single letter is allowed as an initial that ends in a dot (<c>S.</c>).</summary>
    private static bool IsWord(string word)
    {
        var letters = word.Count(char.IsLetter);
        return letters >= MinWordLetters || (letters == 1 && word.EndsWith('.'));
    }
}
