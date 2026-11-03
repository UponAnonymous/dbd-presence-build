using System.Diagnostics;

namespace DbdPresence.ImageHelper;

/// <summary>
/// Stops the helper when it uses too much memory or runs too long (ADR-0013). The caller also has its own time limit; this guard
/// works from inside because the project has no job-object interop (ADR-0016), so memory cannot be capped from outside.
/// The helper is crash and parser isolation, not a privilege boundary.
/// </summary>
internal static class ResourceWatchdog
{
    public const long MaxWorkingSetBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan MaxRunTime = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>True when either limit is exceeded.</summary>
    public static bool IsOverLimit(long workingSetBytes, TimeSpan elapsed) => workingSetBytes > MaxWorkingSetBytes || elapsed > MaxRunTime;

    public static void Start()
    {
        var clock = Stopwatch.StartNew();
        var thread = new Thread(() => Watch(clock)) { IsBackground = true, Name = "resource-watchdog" };
        thread.Start();
    }

    private static void Watch(Stopwatch clock)
    {
        while (true)
        {
            if (IsOverLimit(Environment.WorkingSet, clock.Elapsed))
            {
                Console.Error.WriteLine("resource limit exceeded");
                Environment.Exit(ExitCodes.ResourceLimit);
            }

            Thread.Sleep(PollInterval);
        }
    }
}
