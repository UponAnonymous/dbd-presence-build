using System.Globalization;
using DbdPresence.Study;

namespace DbdPresence.Eval;

/// <summary>The <c>export</c> and <c>import-labels</c> commands: the command-line side of <see cref="ReviewFiles"/>.</summary>
internal static class ReviewCommands
{
    /// <summary>The <c>export</c> command.</summary>
    public static int Export(string root, CommandLine line, EvalEnvironment env)
    {
        var folder = ReviewFiles.ResolveSession(root, line.Positional(0) ?? throw new FormatException("export needs the recording to export."));
        var limit = DefaultOr(line.Value("max-mb"), ReviewExport.DefaultLimitMegabytes, "--max-mb");
        return ReviewFiles.ExportFolder(folder, limit, env);
    }

    /// <summary>The <c>import-labels</c> command.</summary>
    public static int ImportLabels(string root, CommandLine line, EvalEnvironment env)
    {
        var folder = ReviewFiles.ResolveSession(root, line.Positional(0) ?? throw new FormatException("import-labels needs the recording."));
        var file = line.Positional(1) ?? throw new FormatException("import-labels needs the labels file.");
        var matches = ReviewFiles.ImportFile(folder, file, env);
        env.Out.WriteLine($"Imported the reviewer's labels for {Path.GetFileName(folder)}: {matches} matches.");
        env.Out.WriteLine("They do not count yet. Your quick spot-check comes first: run DbdPresence.Eval (no arguments) and choose 'Label my recordings'.");
        return 0;
    }

    private static int DefaultOr(string? text, int fallback, string name)
    {
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 8 and <= 2000
            ? value
            : throw new FormatException($"{name} must be a whole number of megabytes from 8 to 2000.");
    }
}
