using System.Globalization;
using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// Makes text from outside (the runner's output, error messages) safe to write as one log line (security review 2026-10-30, low findings): no control characters,
/// so a line cannot fake further log lines or move a terminal's cursor, and a fixed length limit.
/// </summary>
public static class LogText
{
    /// <summary>The longest log line, in characters.</summary>
    public const int MaximumLength = 2000;

    public static string Clean(string? text, int maximumLength = MaximumLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(Math.Min(text.Length, maximumLength + 40));
        var shown = 0;
        foreach (var c in text)
        {
            if (shown >= maximumLength)
            {
                cleaned.Append(string.Create(CultureInfo.InvariantCulture, $" [{text.Length - shown} more characters not logged]"));
                break;
            }

            if (c is '\t')
            {
                cleaned.Append(' ');
            }
            else if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                cleaned.Append('?');
            }
            else
            {
                cleaned.Append(c);
            }

            shown++;
        }

        return cleaned.ToString();
    }
}

/// <summary>Lets a limited number of lines through, then stops (a job cannot fill the disk through its log). Counts what it dropped.</summary>
public sealed class LineBudget
{
    private readonly int _limit;
    private int _seen;

    public LineBudget(int limit)
    {
        _limit = limit;
    }

    public int Dropped => Math.Max(0, _seen - _limit);

    /// <summary>True for each of the first <c>limit</c> lines.</summary>
    public bool Allow() => Interlocked.Increment(ref _seen) <= _limit;
}
