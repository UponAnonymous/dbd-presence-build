using DbdPresence.LocalCi.Windows;

// Owner's local CI program (D40; docs/owner/free-ci.md). Usage: DbdLocalCi help.
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("DBD Local CI runs on Windows 10 or 11 only.");
    return 1;
}

return await LocalCiApplication.RunAsync(args).ConfigureAwait(false);
