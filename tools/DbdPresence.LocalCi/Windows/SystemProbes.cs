using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace DbdPresence.LocalCi.Windows;

/// <summary>Whether programs with given names are running. Only the names are looked at; no process is opened, read or touched.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessList : IProcessList
{
    public bool IsAnyRunning(IReadOnlyCollection<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        foreach (var name in processNames)
        {
            var found = Process.GetProcessesByName(name);
            try
            {
                if (found.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in found)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Shows a short message window (it closes itself) and writes the same words to the log. The windows are shown by one thread of their own that is connected to the owner's desktop when the
/// program starts, before any job's private window station exists, so a notice can never end up on a private desktop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MessageBoxNotifier : INotifier
{
    private const uint InformationIcon = 0x40;
    private const uint SetForeground = 0x10000;
    private const uint TopMost = 0x40000;
    private const uint ShownForMilliseconds = 60_000;

    private readonly ICiLog _log;
    private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
    private readonly Thread _worker;

    public MessageBoxNotifier(ICiLog log)
    {
        _log = log;
        _worker = new Thread(Work) { IsBackground = true, Name = "DBD Local CI notices" };
        _worker.Start();
    }

    public void Notify(string message)
    {
        _log.Info("Notice: " + message);
        _ = _messages.Writer.TryWrite(message);
    }

    private void Work()
    {
        // The first window call of this thread connects it to the owner's desktop; doing it now, under the lock, keeps it away from any private station for good.
        lock (PrivateDesktop.ProcessStationGate)
        {
            _ = NativeMethods.GetDesktopWindow();
        }

        while (_messages.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_messages.Reader.TryRead(out var message))
            {
                try
                {
                    _ = NativeMethods.MessageBoxTimeout(nint.Zero, message, "DBD Local CI", InformationIcon | SetForeground | TopMost, 0, ShownForMilliseconds);
                }
                catch (EntryPointNotFoundException)
                {
                    _log.Warn("This Windows has no timed message window; the notice is only in the log.");
                }
            }
        }
    }
}
