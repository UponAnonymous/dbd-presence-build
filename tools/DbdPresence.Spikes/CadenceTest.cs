using System.Globalization;
using System.Text.Json.Nodes;

namespace DbdPresence.Spikes;

/// <summary>
/// Discord update cadence (docs/evidence/detection-speed-and-confidence.md, plan step 1). Sends SET_ACTIVITY with a counter in
/// "details" at several fixed gaps, logs when each was sent and exactly how Discord answered, and then asks the owner which
/// counter values actually appeared on the profile. It only talks to the local Discord pipe. It measures what Discord accepts and
/// shows; it never guesses a limit.
/// </summary>
internal static class CadenceTest
{
    private static readonly TimeProvider s_time = TimeProvider.System;

    public static async Task<int> RunAsync(SpikeOptions options)
    {
        var totalSends = options.IntervalsSeconds.Count * options.UpdatesPerBlock;
        var estimate = options.IntervalsSeconds.Sum(i => i * (options.UpdatesPerBlock - 1)) + (options.RestSeconds * (options.IntervalsSeconds.Count - 1));
        Console.WriteLine("Discord update cadence test. Nothing is uploaded; this program only talks to the Discord app on this PC.");
        Console.WriteLine();
        Console.WriteLine("What it does: it sets your Discord activity to 'Update 1', 'Update 2', and so on, at different speeds,");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"one speed at a time ({string.Join(", ", options.IntervalsSeconds.Select(i => CadenceLog.Number(i) + " s"))} between updates, {options.UpdatesPerBlock} updates each, {options.RestSeconds} s rest between speeds)."));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"That is {totalSends} updates and takes about {estimate / 60.0:0.#} minutes."));
        Console.WriteLine();
        Console.WriteLine("Before you start: keep the Discord desktop app open, and make sure no game or other app is also setting your activity.");
        Console.WriteLine("Watch your profile while it runs: use a second account or friend that can see you, or your own profile popout");
        Console.WriteLine("(click your name at the bottom left of Discord). Write down which 'Update' numbers you actually see appear.");
        Prompt.WaitForEnter("Ready? Press Enter to start.");

        var startedAt = s_time.GetLocalNow();
        var log = new CadenceLog(options, startedAt);
        var session = new IpcSession();
        try
        {
            if (!await ConnectAsync(session, options).ConfigureAwait(false))
            {
                return 1;
            }

            var counter = 0;
            var runStart = s_time.GetTimestamp();
            for (var b = 0; b < options.IntervalsSeconds.Count; b++)
            {
                var block = new CadenceBlock(b + 1, options.IntervalsSeconds[b]) { Planned = options.UpdatesPerBlock };
                log.Blocks.Add(block);
                if (b > 0 && options.RestSeconds > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Resting {options.RestSeconds} s before the next speed, so the previous updates do not count against it.");
                    await Task.Delay(TimeSpan.FromSeconds(options.RestSeconds)).ConfigureAwait(false);
                }

                Console.WriteLine();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"--- Block {block.Number} of {options.IntervalsSeconds.Count}: one update every {CadenceLog.Number(block.IntervalSeconds)} s ---"));
                var blockStart = s_time.GetTimestamp();
                for (var k = 0; k < options.UpdatesPerBlock; k++)
                {
                    var wait = TimeSpan.FromSeconds(k * block.IntervalSeconds) - s_time.GetElapsedTime(blockStart);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait).ConfigureAwait(false);
                    }

                    counter++;
                    var update = await SendOneAsync(session, counter, block, runStart).ConfigureAwait(false);
                    block.Updates.Add(update);
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"[{update.SentAt:HH:mm:ss.fff}] sent 'Update {update.Counter}' -> {update.Outcome} after {CadenceLog.Number(update.ReplyMilliseconds)} ms: {update.Detail}"));
                    if (update.Outcome is CadenceReply.Closed or CadenceReply.SendFailed)
                    {
                        Console.WriteLine("The connection to Discord was lost. Trying to connect again.");
                        await session.DisposeAsync().ConfigureAwait(false);
                        session = new IpcSession();
                        if (!await ConnectAsync(session, options).ConfigureAwait(false))
                        {
                            Console.WriteLine("Could not reconnect; the run stops here and reports what it has.");
                            return await FinishAsync(session, log, stoppedEarly: true).ConfigureAwait(false);
                        }
                    }
                }
            }

            return await FinishAsync(session, log, stoppedEarly: false).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            log.Save();
            Console.WriteLine();
            Console.WriteLine($"Results file: {log.FilePath}");
            Prompt.WaitForEnter("Press Enter to close.");
        }
    }

    private static async Task<bool> ConnectAsync(IpcSession session, SpikeOptions options)
    {
        var (ok, _, response) = await session.ConnectAsync(options.AppId, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(ok ? "Connected to Discord." : $"Could not connect: {response}");
        return ok;
    }

    private static async Task<CadenceUpdate> SendOneAsync(IpcSession session, int counter, CadenceBlock block, long runStart)
    {
        var activity = new JsonObject
        {
            ["details"] = $"Update {counter}",
            ["state"] = string.Create(CultureInfo.InvariantCulture, $"Block {block.Number}: every {CadenceLog.Number(block.IntervalSeconds)} s"),
        };
        var sentAt = s_time.GetLocalNow();
        var elapsed = s_time.GetElapsedTime(runStart).TotalSeconds;
        var timer = s_time.GetTimestamp();
        string outcome;
        string detail;
        try
        {
            var (_, response) = await session.SetActivityAsync(activity, CancellationToken.None).ConfigureAwait(false);
            (outcome, detail) = CadenceReply.Classify(response);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            (outcome, detail) = (CadenceReply.SendFailed, $"{ex.GetType().Name}: {ex.Message}");
        }

        var replyMs = s_time.GetElapsedTime(timer).TotalMilliseconds;
        return new CadenceUpdate(counter, block.Number, block.IntervalSeconds, sentAt, elapsed, replyMs, outcome, detail);
    }

    private static async Task<int> FinishAsync(IpcSession session, CadenceLog log, bool stoppedEarly)
    {
        try
        {
            await session.SetActivityAsync(null, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("Your activity was cleared.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Console.WriteLine($"Could not clear the activity ({ex.GetType().Name}); it will disappear when this program closes.");
        }

        Console.WriteLine();
        Console.WriteLine(stoppedEarly ? "The run stopped early. Answer for what you saw before it stopped." : "All blocks are done.");
        Console.WriteLine("Now tell me what you saw on the profile. Type the Update numbers that appeared, separated by commas");
        Console.WriteLine("(for example 1,2,4,6), or 'all' or 'none'. Press Enter to skip a question.");
        foreach (var block in log.Blocks)
        {
            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Block {block.Number}: one update every {CadenceLog.Number(block.IntervalSeconds)} s, Update {block.Range}."));
            block.SeenText = Prompt.Text("Which numbers did you see appear?");
            block.DelayText = Prompt.Text("About how many seconds after a send did the change show? (Enter to skip)");
        }

        Console.WriteLine();
        Console.WriteLine("Summary (Accepted means Discord's reply said OK; only your 'You saw' column says what was shown):");
        Console.WriteLine();
        Console.Write(log.SummaryTable());
        return 0;
    }
}
