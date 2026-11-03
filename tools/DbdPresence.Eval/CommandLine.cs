
namespace DbdPresence.Eval;

/// <summary>The options a command accepts: <c>--name value</c> pairs, bare <c>--flag</c>s and a fixed number of leading plain arguments. Anything else is a usage error.</summary>
internal sealed record CommandSpec(string[] Values, string[] Flags, int Positionals = 0);

/// <summary>
/// A parsed command line. Parsing is strict (unknown, repeated or value-less options are errors) and never echoes an option's value, so a value
/// such as the owner's name cannot leak into an error message or a log.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = [];

    private CommandLine(string command)
    {
        Command = command;
    }

    /// <summary>Gets the command name.</summary>
    public string Command { get; }

    /// <summary>Returns the value of an option, or null when it was not given.</summary>
    public string? Value(string name) => _values.GetValueOrDefault(name);

    /// <summary>Returns the plain argument at a position, or null when there was none.</summary>
    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;

    /// <summary>Returns whether a flag was given.</summary>
    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>Parses the arguments after the command name.</summary>
    public static bool TryParse(string command, IReadOnlyList<string> args, CommandSpec spec, out CommandLine? parsed, out string error)
    {
        var result = new CommandLine(command);
        parsed = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) && result._positionals.Count < spec.Positionals)
            {
                result._positionals.Add(arg);
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
            {
                error = "Unexpected argument. Options look like --name value.";
                return false;
            }

            var name = arg[2..];
            if (spec.Flags.Contains(name, StringComparer.Ordinal))
            {
                if (!result._flags.Add(name))
                {
                    error = $"--{name} was given twice.";
                    return false;
                }

                continue;
            }

            if (!spec.Values.Contains(name, StringComparer.Ordinal))
            {
                error = $"Unknown option --{name} for '{command}'.";
                return false;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"--{name} needs a value.";
                return false;
            }

            if (!result._values.TryAdd(name, args[++i]))
            {
                error = $"--{name} was given twice.";
                return false;
            }
        }

        if (result._positionals.Count < spec.Positionals)
        {
            error = $"'{command}' needs {spec.Positionals} argument{(spec.Positionals == 1 ? string.Empty : "s")} before the options. Run 'DbdPresence.Eval help'.";
            return false;
        }

        parsed = result;
        error = string.Empty;
        return true;
    }
}
