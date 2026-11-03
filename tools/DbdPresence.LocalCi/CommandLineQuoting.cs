using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>Turns a program and its arguments into one Windows command line (the rules of the C run time: backslashes before a quote are doubled).</summary>
public static class CommandLineQuoting
{
    /// <summary>The longest command line <c>CreateProcessWithLogonW</c> accepts, in characters (Microsoft Win32 documentation).</summary>
    public const int LogonCommandLineLimit = 1024;

    public static string Join(string program, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        ArgumentNullException.ThrowIfNull(arguments);
        var line = new StringBuilder();
        Append(line, program, alwaysQuote: true);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Append(line, argument, alwaysQuote: false);
        }

        return line.ToString();
    }

    private static void Append(StringBuilder line, string argument, bool alwaysQuote)
    {
        if (argument.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A command line cannot hold a NUL character.", nameof(argument));
        }

        var needsQuotes = alwaysQuote || argument.Length == 0 || argument.AsSpan().IndexOfAny(" \t\n\v\"") >= 0;
        if (!needsQuotes)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                line.Append('\\', (backslashes * 2) + 1);
                line.Append('"');
            }
            else
            {
                line.Append('\\', backslashes);
                line.Append(c);
            }

            backslashes = 0;
        }

        line.Append('\\', backslashes * 2);
        line.Append('"');
    }
}
