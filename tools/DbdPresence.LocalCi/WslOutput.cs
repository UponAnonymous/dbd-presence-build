using System.Text;
using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>Reads what wsl.exe prints. Its own messages usually come as UTF-16 text (with NUL characters between the letters when read as bytes), but can be UTF-8; the text is plain and never trusted.</summary>
public static partial class WslOutput
{
    /// <summary>
    /// Decodes bytes that are either UTF-16 or UTF-8 text: a byte order mark decides, otherwise a NUL byte in the first bytes means UTF-16 (little endian, what Windows writes), otherwise UTF-8. Reading
    /// the wrong way would turn a list of names into noise that parses as an empty list, so the choice is made from the bytes, not assumed.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        var head = bytes[..Math.Min(bytes.Length, 32)];
        var oddNul = 0;
        var evenNul = 0;
        for (var i = 0; i < head.Length; i++)
        {
            if (head[i] == 0)
            {
                if ((i & 1) == 1)
                {
                    oddNul++;
                }
                else
                {
                    evenNul++;
                }
            }
        }

        if (oddNul > 0 && oddNul >= evenNul)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        return evenNul > 0 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The distribution names in the output of <c>wsl --list --quiet</c>: one per line. Only a plausible name counts (letters, digits, dot, hyphen and underscore, at most 64 characters, not starting with
    /// a symbol): a warning or notice that WSL prints between them is not a distribution, and treating it as one would make a clean-up try to remove it for ever.
    /// </summary>
    public static IReadOnlyList<string> ParseDistributions(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return
        [
            .. StripNuls(output)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimStart('﻿').Trim())
                .Where(l => PlausibleName().IsMatch(l)),
        ];
    }

    /// <summary>
    /// The text <c>wsl --version</c> printed, for the status: without NUL characters and carriage returns, the first twelve non-empty lines of at most 200 characters each, one per line. A line that is longer
    /// is not a version line and is left out.
    /// </summary>
    public static string VersionText(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return string.Join('\n', StripNuls(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Length <= 200).Take(12));
    }

    /// <summary>The last non-empty line of the output without NUL characters, for an error message; "no message" when there is none.</summary>
    public static string LastLine(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return StripNuls(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no message";
    }

    /// <summary>The text without NUL characters (UTF-16 text read as UTF-8 has one after every letter).</summary>
    public static string StripNuls(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\0", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlausibleName();
}
