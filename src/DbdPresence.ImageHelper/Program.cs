using System.Globalization;

namespace DbdPresence.ImageHelper;

/// <summary>
/// Entry point of the image helper. Usage: <c>DbdPresence.ImageHelper.exe &lt;input-file&gt; &lt;output-png&gt;</c> (sanitise, scale to fit 1024)
/// or <c>DbdPresence.ImageHelper.exe fit &lt;512|1024&gt; &lt;input-file&gt; &lt;output-png&gt;</c> (picture fit mode, FR-PACK-12).
/// Exit codes are part of the contract with the launcher (FR-PACK-10): see <see cref="ExitCodes"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2)
        {
            ResourceWatchdog.Start();
            return ImageSanitizer.Run(args[0], args[1], Console.Error);
        }

        if (args.Length == 4 && args[0] == "fit"
            && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var edge)
            && (edge == ImageSanitizer.FitEdgeStandard || edge == ImageSanitizer.FitEdgeLarge))
        {
            ResourceWatchdog.Start();
            return ImageSanitizer.Run(args[2], args[3], Console.Error, edge);
        }

        Console.Error.WriteLine("usage: DbdPresence.ImageHelper <input-file> <output-png> | fit <512|1024> <input-file> <output-png>");
        return ExitCodes.BadInput;
    }
}
