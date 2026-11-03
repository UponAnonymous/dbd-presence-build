namespace DbdPresence.Vision.Tests.Support;

/// <summary>
/// An invented 3 x 5 pixel font used only by the tests: capital letters, digits and a few marks. The glyphs are made up here and resemble no game
/// asset or real typeface. The renderer draws it at whole-pixel cell sizes and the fake text recogniser reads it back from the pixels, so the
/// geometry of a frame (which region the text falls in, at which frame size) is exercised exactly. Every glyph has ink in its first column, so
/// a line's left edge marks the start of its first glyph.
/// </summary>
internal static class TestFont
{
    public const int Width = 3;
    public const int Height = 5;

    /// <summary>Advance of one glyph in cells (the glyph plus one blank column).</summary>
    public const int Advance = 4;

    private static readonly Dictionary<char, string[]> s_glyphs = new()
    {
        ['A'] = ["010", "101", "111", "101", "101"],
        ['B'] = ["110", "101", "110", "101", "110"],
        ['C'] = ["011", "100", "100", "100", "011"],
        ['D'] = ["110", "101", "101", "101", "110"],
        ['E'] = ["111", "100", "110", "100", "111"],
        ['F'] = ["111", "100", "110", "100", "100"],
        ['G'] = ["011", "100", "101", "101", "011"],
        ['H'] = ["101", "101", "111", "101", "101"],
        ['I'] = ["111", "010", "010", "010", "111"],
        ['J'] = ["001", "001", "001", "101", "010"],
        ['K'] = ["101", "101", "110", "101", "101"],
        ['L'] = ["100", "100", "100", "100", "111"],
        ['M'] = ["101", "111", "111", "101", "101"],
        ['N'] = ["110", "101", "101", "101", "101"],
        ['O'] = ["010", "101", "101", "101", "010"],
        ['P'] = ["110", "101", "110", "100", "100"],
        ['Q'] = ["010", "101", "101", "110", "011"],
        ['R'] = ["110", "101", "110", "101", "101"],
        ['S'] = ["011", "100", "010", "001", "110"],
        ['T'] = ["111", "010", "010", "010", "010"],
        ['U'] = ["101", "101", "101", "101", "111"],
        ['V'] = ["101", "101", "101", "101", "010"],
        ['W'] = ["101", "101", "111", "111", "101"],
        ['X'] = ["101", "101", "010", "101", "101"],
        ['Y'] = ["101", "101", "010", "010", "010"],
        ['Z'] = ["111", "001", "010", "100", "111"],
        ['0'] = ["111", "101", "101", "101", "111"],
        ['1'] = ["010", "110", "010", "010", "111"],
        ['2'] = ["110", "001", "010", "100", "111"],
        ['3'] = ["110", "001", "010", "001", "110"],
        ['4'] = ["101", "101", "111", "001", "001"],
        ['5'] = ["111", "100", "110", "001", "110"],
        ['6'] = ["011", "100", "111", "101", "111"],
        ['7'] = ["111", "001", "010", "010", "010"],
        ['8'] = ["111", "101", "111", "101", "111"],
        ['9'] = ["111", "101", "111", "001", "110"],
        ['.'] = ["000", "000", "000", "000", "100"],
        ['-'] = ["000", "000", "111", "000", "000"],
        [':'] = ["000", "100", "000", "100", "000"],
        ['['] = ["110", "100", "100", "100", "110"],
        [']'] = ["011", "001", "001", "001", "011"],
        ['/'] = ["001", "001", "010", "100", "100"],
        [' '] = ["000", "000", "000", "000", "000"],
    };

    /// <summary>Gets every glyph as a 15-bit pattern (row-major, true = ink), keyed by character.</summary>
    public static IReadOnlyDictionary<char, bool[]> Patterns { get; } = s_glyphs.ToDictionary(g => g.Key, g => g.Value.SelectMany(r => r.Select(c => c == '1')).ToArray());

    /// <summary>Returns whether the font can draw the character (after upper-casing).</summary>
    public static bool Supports(char c) => s_glyphs.ContainsKey(char.ToUpperInvariant(c));

    /// <summary>Returns whether the cell of the glyph at (column, row) is ink.</summary>
    public static bool Ink(char c, int column, int row) => Patterns[char.ToUpperInvariant(c)][(row * Width) + column];
}
