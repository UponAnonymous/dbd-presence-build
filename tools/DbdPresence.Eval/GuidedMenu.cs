using System.Globalization;
using DbdPresence.Study;

namespace DbdPresence.Eval;

/// <summary>The menu shown when the owner double-clicks the tool: label by hand, or make a review file to send.</summary>
internal static class GuidedMenu
{
    /// <summary>Shows the menu and runs the owner's choice. Returns the exit code of that choice.</summary>
    public static int Run(EvalEnvironment env)
    {
        env.Out.WriteLine("What do you want to do?");
        env.Out.WriteLine("  1. Do the quick spot-checks that are waiting (or label a recording yourself)");
        env.Out.WriteLine("  2. Make a review file to send (the usual step after you have played)");
        env.Out.WriteLine("  3. Teach the app your status icons (after you labelled your own icon in some practice recordings)");
        env.Out.WriteLine("Type 1, 2 or 3 and press Enter (just Enter means 1).");
        var choice = env.ReadLine()?.Trim() ?? string.Empty;
        env.Out.WriteLine();
        switch (choice)
        {
            case "" or "1":
                return EvalApp.Run(["label", "--ask-new"], env);
            case "2":
                return MakeReviewFile(env);
            case "3":
                return StatusIconTeaching.Run(env.StudyRoot, null, env);
            default:
                env.Error.WriteLine("That was not 1, 2 or 3, so nothing was done. Run the tool again.");
                return 2;
        }
    }

    private static int MakeReviewFile(EvalEnvironment env)
    {
        var folders = StudySessions.RecordingFolders(env.StudyRoot).Reverse().ToList();
        if (folders.Count == 0)
        {
            env.Error.WriteLine($"No recordings were found in {env.StudyRoot}. Start the recorder (DbdPresence.StudyCapture), play, then run this again.");
            return 1;
        }

        var fresh = folders.Where(IsNew).ToList();
        env.Out.WriteLine("Which recording? The newest is first.");
        for (var i = 0; i < folders.Count; i++)
        {
            env.Out.WriteLine($"  {i + 1}. {Path.GetFileName(folders[i])}{Note(folders[i])}");
        }

        env.Out.WriteLine(fresh.Count switch
        {
            0 => "Type its number and press Enter (just Enter means 1).",
            1 => "Type its number and press Enter (just Enter makes the review file for the one new recording).",
            _ => string.Create(CultureInfo.InvariantCulture, $"Type its number and press Enter (just Enter makes review files for all {fresh.Count} new recordings, one after another)."),
        });
        var text = env.ReadLine()?.Trim() ?? string.Empty;
        if (text.Length == 0 && fresh.Count > 0)
        {
            return ExportAll(fresh, env);
        }

        var number = text.Length == 0 ? 1 : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (number < 1 || number > folders.Count)
        {
            env.Error.WriteLine("That was not one of the numbers, so nothing was done. Run the tool again.");
            return 2;
        }

        env.Out.WriteLine();
        return ExportOne(folders[number - 1], env) ? 0 : 1;
    }

    /// <summary>A recording nobody has dealt with yet: it has no review file and no labels.</summary>
    private static bool IsNew(string folder) =>
        ReviewExport.ExistingFiles(folder).Count == 0 && !File.Exists(Path.Combine(folder, SessionFiles.Answers));

    private static int ExportAll(List<string> folders, EvalEnvironment env)
    {
        var failed = new List<string>();
        foreach (var folder in folders)
        {
            env.Out.WriteLine();
            if (!ExportOne(folder, env))
            {
                failed.Add(Path.GetFileName(folder));
            }
        }

        if (folders.Count > 1)
        {
            env.Out.WriteLine();
            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Made review files for {folders.Count - failed.Count} of {folders.Count} new recordings. Send every file listed above."));
        }

        if (failed.Count > 0)
        {
            env.Error.WriteLine($"No review file was made for: {string.Join(", ", failed)} (the reason is above).");
            return 1;
        }

        return 0;
    }

    private static bool ExportOne(string folder, EvalEnvironment env)
    {
        try
        {
            return ReviewFiles.ExportFolder(folder, ReviewExport.DefaultLimitMegabytes, env) == 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            env.Error.WriteLine($"{Path.GetFileName(folder)}: {ex.Message}");
            return false;
        }
    }

    private static string Note(string folder)
    {
        if (ReviewExport.ExistingFiles(folder).Count > 0)
        {
            return " (a review file was made)";
        }

        return File.Exists(Path.Combine(folder, SessionFiles.Answers)) ? " (labelled)" : string.Empty;
    }
}
