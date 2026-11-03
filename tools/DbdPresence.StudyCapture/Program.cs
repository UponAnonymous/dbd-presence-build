using DbdPresence.Core.Capture;
using DbdPresence.Core.Game;
using DbdPresence.Platform.Windows.Capture;
using DbdPresence.Platform.Windows.Processes;
using DbdPresence.Study;

namespace DbdPresence.StudyCapture;

/// <summary>
/// Owner-run study tool (QUALITY_GATES C6 data collection, SAFETY_AND_PRIVACY section 3). It captures the Dead by Daylight window only,
/// through the same capture adapter the app uses, and saves PNG files in a folder on this PC. Nothing is uploaded: the tool has no
/// network code (architecture test StudyToolBoundaryTests). The recording itself is the shared <see cref="StudyRecorder"/> that the app's Study page also runs;
/// this program is only the console around it.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan s_wait = TimeSpan.FromMilliseconds(250);

    public static async Task<int> Main()
    {
        PrintIntro(TimeProvider.System);

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var monitor = new GameMonitor(new WindowsProcessSnapshotSource(), TimeProvider.System, new GameProcessMatcher());
        var recorder = new StudyRecorder(
            SessionFolder.Root,
            TimeProvider.System,
            () =>
            {
                monitor.PollOnce();
                return monitor.Current;
            },
            processId => new WgcFrameSource(new WgcWindowCaptureSource(processId, TimeProvider.System, requestBorderless: true)),
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            () => true);

        var exit = await RunAsync(recorder, cancel.Token).ConfigureAwait(false);
        if (exit is null)
        {
            return 0;
        }

        Prompt.WaitForEnter("Press Enter to close this window.");
        return exit.Value;
    }

    private static void PrintIntro(TimeProvider time)
    {
        const string Title = "DBD Presence study capture (test tool; the app's Study page can record too)";
        Console.WriteLine(Title);
        Console.WriteLine(new string('=', Title.Length));
        Console.WriteLine();
        Console.WriteLine("What this does");
        foreach (var line in StudyRecorderText.WhatItDoes("press Q"))
        {
            Console.WriteLine($"  - {line}");
        }

        Console.WriteLine();
        foreach (var line in StudyRecorderText.Where(SessionFolder.Root, Environment.GetEnvironmentVariable))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("PRIVACY - please read");
        foreach (var line in StudyRecorderText.Privacy(SessionRetention.ExpiryDate(time.GetUtcNow())))
        {
            Console.WriteLine($"  {line}");
        }

        Console.WriteLine();
    }

    private static async Task<int?> RunAsync(StudyRecorder recorder, CancellationToken cancellationToken)
    {
        var printedNotes = 0;
        var announcedRecording = false;
        var lastPictures = -1;

        // The clean-up of old recordings and its report come first, then the Y question, as they always have.
        await recorder.StartAsync(() =>
        {
            printedNotes = PrintNotes(recorder.Status, printedNotes);
            Console.WriteLine();
            return Prompt.Confirm("Type Y and press Enter to start, or just close this window to cancel: ");
        }).ConfigureAwait(false);
        if (recorder.Status.StopReason == "cancelled before it started")
        {
            return null;
        }

        if (recorder.Status.IsActive)
        {
            Console.WriteLine("Waiting for Dead by Daylight to start. Start the game now. Press Q to give up.");
        }

        while (recorder.Status.IsActive)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await recorder.StopAsync("owner pressed Ctrl+C").ConfigureAwait(false);
                break;
            }

            if (Prompt.QPressed())
            {
                await recorder.StopAsync("owner pressed Q").ConfigureAwait(false);
                break;
            }

            var status = recorder.Status;
            printedNotes = PrintNotes(status, printedNotes);
            if (status.State == StudyRecorderState.Recording)
            {
                if (!announcedRecording)
                {
                    announcedRecording = true;
                    Console.WriteLine($"The game is running. Saving pictures to: {status.Folder}");
                    Console.WriteLine("Play your matches now. Press Q in this window when you are done.");
                    Console.WriteLine($"These pictures are deleted automatically on {status.ExpiryDate} ({SessionRetention.Days} days from now). Delete the folder to remove them earlier.");
                }

                if (status.Pictures != lastPictures)
                {
                    lastPictures = status.Pictures;
                    Console.Write($"\r{status.Pictures} pictures saved ({status.Bytes / (1024 * 1024)} MB). Press Q to stop.   ");
                }
            }

            await Task.Delay(s_wait, CancellationToken.None).ConfigureAwait(false);
        }

        var final = recorder.Status;
        PrintNotes(final, printedNotes);
        Console.WriteLine();
        if (final.Folder is null)
        {
            // The notes above already say why nothing was saved (including "Stopped before the game started. Nothing was saved.").
            return final.StopReason == "stopped before the game started" ? 0 : 1;
        }

        Console.WriteLine(final.StopReason switch
        {
            "game closed" => "The game was closed.",
            "owner pressed Q" => "Stopped (Q).",
            "owner pressed Ctrl+C" => "Stopped (Ctrl+C).",
            "2 GB limit reached" => "This session reached the 2 GB limit, so capture stopped. Start the tool again for a new session.",
            _ => $"Stopped: {final.StopReason}.",
        });
        Console.WriteLine($"{final.Pictures} pictures ({final.Bytes / (1024 * 1024)} MB) saved in:");
        Console.WriteLine(final.Folder);
        Console.WriteLine("Next: look through the pictures, zip this folder and send it to the project owner privately.");
        Console.WriteLine($"This folder deletes itself on {final.ExpiryDate}; copy what you need before then, or delete the folder yourself earlier.");
        return final.Pictures == 0 ? 1 : 0;
    }

    private static int PrintNotes(StudyRecorderStatus status, int printed)
    {
        var notes = status.Notes ?? [];
        for (var i = printed; i < notes.Count; i++)
        {
            Console.WriteLine();
            Console.WriteLine(notes[i]);
        }

        return notes.Count;
    }

    /// <summary>Gives the recorder the Windows capture adapter's whole-window pictures.</summary>
    private sealed class WgcFrameSource(WgcWindowCaptureSource source) : IStudyFrameSource
    {
        public (int Width, int Height) LatestContentSize => source.LatestContentSize;

        public Task<FullFrameSample> CaptureFullFrameAsync(CancellationToken cancellationToken) => source.CaptureFullFrameAsync(cancellationToken);

        public void Dispose() => source.Dispose();
    }
}
