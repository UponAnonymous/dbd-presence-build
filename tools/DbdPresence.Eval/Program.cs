using DbdPresence.Platform.Windows.Ocr;

namespace DbdPresence.Eval;

/// <summary>
/// Owner-run evaluator and labelling helper (QUALITY_GATES C5/C6, DETECTION_VALIDATION sections 4, 6 and 12 to 16). It reads the study tool's recordings
/// from this PC, writes next to them, and uploads nothing: it has no network code (architecture test EvalToolBoundaryTests). This is the only
/// Windows-specific file; everything else is platform-neutral and tested on Linux with a fake recogniser.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var studyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DBD Presence Study");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var env = new EvalEnvironment
        {
            Out = Console.Out,
            Error = Console.Error,
            Time = TimeProvider.System,
            StudyRoot = studyRoot,
            DownloadsFolder = downloads,

            // The same folder the app resolves for its data (SettingsPaths.ForCurrentUser); the app reads status-templates.json from it when it starts.
            AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBDPresence.Data"),

            // The fake recogniser exists for the Linux tests only; this build offers Windows text recognition and nothing else.
            CreateRecognizer = kind => kind == OcrKind.Windows && WindowsEnglishOcr.IsEnglishAvailable() ? new WindowsEnglishOcr() : null,
            ReadLine = Console.ReadLine,
        };

        var guided = args.Length == 0;
        if (guided)
        {
            Console.WriteLine("DBD Presence: check your recordings (test tool, not part of the app)");
            Console.WriteLine("=====================================================================");
            Console.WriteLine("Everything stays on this PC. This tool uploads nothing and has no network code; a review file is a file you send yourself.");
            Console.WriteLine();
        }

        var exit = guided ? GuidedMenu.Run(env) : EvalApp.Run(args, env);
        if (guided)
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close this window.");
            Console.ReadLine();
        }

        return exit;
    }
}
