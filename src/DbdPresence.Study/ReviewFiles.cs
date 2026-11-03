using System.Globalization;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Text;

namespace DbdPresence.Study;

/// <summary>
/// The operations around the compact review file (PLAN_V2 D34, DETECTION_VALIDATION §16.1): makes it and takes in the labels made from it. No detector, recogniser or HUD reading is involved anywhere here.
/// </summary>
internal static class ReviewFiles
{
    /// <summary>The words printed whenever a review file is made: it shows other people's names.</summary>
    internal const string ToolPrivacyWarning =
        "The review file shows other players' names and anything else that was on your screen. Send it only to the developer's private session, never post it in public. It deletes itself with the recording, 30 days after the recording was made.";

    /// <summary>Finds a recording's folder from its name (for example 2026-10-24_19-30-12) or the path of its folder.</summary>
    /// <exception cref="FormatException">There is no such recording in the study folder.</exception>
    public static string ResolveSession(string root, string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        var trimmed = argument.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var searchRoot = root;
        var name = trimmed;
        if (Directory.Exists(trimmed))
        {
            var full = Path.GetFullPath(trimmed);
            searchRoot = Path.GetDirectoryName(full) ?? root;
            name = Path.GetFileName(full);
        }

        return StudySessions.RecordingFolders(searchRoot).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal))
            ?? throw new FormatException($"No study recording named '{name}' was found in {searchRoot}.");
    }

    /// <summary>Prepares the recording if needed and makes its review file. Returns 0 when the file was made.</summary>
    public static int ExportFolder(string folder, int limitMegabytes, StudyEnvironment env)
    {
        var parent = Path.GetDirectoryName(folder) ?? env.StudyRoot;
        env.Out.WriteLine($"Making the review file for {Path.GetFileName(folder)}. This takes a few minutes for a long recording.");
        env.Out.WriteLine("PRIVACY: " + env.PrivacyNotice);
        if (!File.Exists(Path.Combine(folder, SessionFiles.Session)))
        {
            var prepared = StudySessions.PreparedFolders(parent).Count;
            if (StudySessions.Prepare(folder, new PrepareOptions(), prepared) is null)
            {
                throw new FormatException("That recording holds no pictures, so there is nothing to export.");
            }
        }

        StudyFiles.RequireFrames([DatasetLoader.LoadSession(folder)]);
        FrameCompaction.RunReported(folder, env);
        var result = ReviewExport.Make(folder, new ExtensionFrameDecoder(), env.Time, limitMegabytes, m => env.Out.WriteLine("  " + m));
        env.Out.WriteLine();
        if (result.Files.Count == 1)
        {
            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Done: {result.Files[0]}"));
        }
        else
        {
            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Done. The review file is in {result.Files.Count} pieces so that each one can be attached (each is under {ReviewExport.PartLimitBytes / (1024 * 1024)} MB):"));
            foreach (var file in result.Files)
            {
                env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {file}  ({new FileInfo(file).Length / (1024.0 * 1024.0):0.0} MB)"));
            }
        }

        env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {result.Bytes / (1024.0 * 1024.0):0.0} MB in all, {result.Segments} parts of the recording, {result.Pictures} pictures, {result.Crops} close-ups of text."));
        if (result.LeftOutPictures > 0 || result.LeftOutCrops > 0)
        {
            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  The file reached its size limit of {limitMegabytes} MB, so {result.LeftOutPictures} pictures and {result.LeftOutCrops} close-ups were left out (the in-between pictures and the longest stretches go first)."));
        }

        env.Out.WriteLine(result.Files.Count == 1
            ? "Send that one file. Do not send the recording folder."
            : string.Create(CultureInfo.InvariantCulture, $"Send all {result.Files.Count} pieces, one after another. Do not send the recording folder."));
        env.Out.WriteLine("PRIVACY: " + env.PrivacyNotice);
        return 0;
    }

    /// <summary>
    /// Takes in labels made from a session's review file for sessions the owner has not labelled and that have no answers yet: the guided flow calls this so
    /// the owner only has to save the file the developer sends back. Problems are told and the session is left as it was.
    /// </summary>
    public static void ImportWaiting(string root, StudyEnvironment env)
    {
        foreach (var folder in StudySessions.PreparedFolders(root))
        {
            var id = Path.GetFileName(folder);
            if (File.Exists(Path.Combine(folder, SessionFiles.Answers)))
            {
                continue;
            }

            var found = StudyFiles.FindSaved(ReviewExport.ReviewerLabelsName(id), [env.DownloadsFolder, folder, root], DateTime.MinValue);
            if (found is null)
            {
                continue;
            }

            try
            {
                var matches = ImportFile(folder, found, env);
                env.Out.WriteLine($"Found the reviewer's labels for {id} ({matches} matches). They count only after your quick spot-check, which comes next.");
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException)
            {
                env.Error.WriteLine($"The reviewer's labels for {id} could not be used: {ex.Message} Nothing was changed.");
            }
        }
    }

    /// <summary>Tells whether a session is waiting for a reviewer: its review file was made and nobody has labelled it.</summary>
    public static bool IsWaitingForReviewer(string folder) =>
        ReviewExport.ExistingFiles(folder).Count > 0 && !File.Exists(Path.Combine(folder, SessionFiles.Answers));

    public static int ImportFile(string folder, string file, StudyEnvironment env, Vocabulary? vocabulary = null)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            throw new FormatException($"There is no file {file}.");
        }

        if (info.Length > 8L * 1024 * 1024)
        {
            throw new FormatException("The labels file is larger than 8 MB, so it is not a labels file.");
        }

        return ReviewSessions.ImportReviewerLabels(folder, File.ReadAllText(file), vocabulary ?? Vocabulary.LoadEmbedded(), env.Time.GetUtcNow());
    }
}
