using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Prompts;

/// <summary>
/// The name protection shared by every path that learns text from the screen (power prompts, unknown character names): a text that contains a name
/// read elsewhere on the same frame is never kept. Names are compared as letters-only OCR skeletons, so digits, underscores, spacing and one or two
/// OCR misreads do not hide a player name (<c>DeathSlinger77</c> and <c>DEATHSLINGER</c> have the same skeleton; <c>Shadow_Wolf</c> is the same as
/// <c>ShadowWolf</c>). Nothing here is persisted or logged.
/// </summary>
internal static class NameSkeletonGuard
{
    private const int MinTokenLetters = 3;

    /// <summary>
    /// The letters-only skeletons of every name read on the frame: each word, and the words of one source joined (a name such as <c>Shadow_Wolf</c>
    /// is two words to the reader but one string in the player column). Digits, underscores and punctuation are not letters.
    /// </summary>
    /// <param name="sources">Texts that name people, as read.</param>
    public static HashSet<string> ForbiddenForms(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var words = LettersOnly(source).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => TextNormalizer.Skeleton(w)).Where(w => w.Length > 0).ToArray();
            foreach (var word in words)
            {
                if (word.Length >= MinTokenLetters)
                {
                    forms.Add(word);
                }
            }

            var joined = string.Concat(words);
            if (words.Length > 1 && joined.Length >= MinTokenLetters)
            {
                forms.Add(joined);
            }
        }

        return forms;
    }

    /// <summary>Returns whether any run of words of <paramref name="candidate"/> (words separated by spaces) is, within the OCR tolerance, one of the forbidden forms.</summary>
    /// <param name="candidate">The text to check.</param>
    /// <param name="forbidden">The forms from <see cref="ForbiddenForms"/>.</param>
    public static bool ContainsPersonName(string candidate, HashSet<string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(forbidden);
        var skeletons = LettersOnly(candidate).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => TextNormalizer.Skeleton(w)).ToArray();
        for (var start = 0; start < skeletons.Length; start++)
        {
            var joined = string.Empty;
            for (var end = start; end < skeletons.Length; end++)
            {
                joined += skeletons[end];
                if (NamesAPerson(joined, forbidden))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string LettersOnly(string text)
    {
        var chars = text.Select(c => char.IsLetter(c) ? c : ' ').ToArray();
        return new string(chars);
    }

    /// <summary>How many edits a form of this length may differ by and still be taken for the same name (OCR misreads; short names must match exactly).</summary>
    private static int Tolerance(int length) => length >= 8 ? 2 : length >= 5 ? 1 : 0;

    private static bool NamesAPerson(string skeleton, HashSet<string> forbidden)
    {
        if (skeleton.Length < MinTokenLetters)
        {
            return false;
        }

        if (forbidden.Contains(skeleton))
        {
            return true;
        }

        foreach (var form in forbidden)
        {
            var tolerance = Tolerance(form.Length);
            if (tolerance > 0 && Math.Abs(form.Length - skeleton.Length) <= tolerance && TextNormalizer.Levenshtein(skeleton, form) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }
}
