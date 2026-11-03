using System.Text.Json.Nodes;

namespace DbdPresence.Spikes;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = SpikeOptions.Parse(args, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(SpikeOptions.Usage);
            Prompt.WaitForEnter("Press Enter to close.");
            return 2;
        }

        if (options.Cadence)
        {
            return await CadenceTest.RunAsync(options).ConfigureAwait(false);
        }

        Console.WriteLine("Spike V2-S1. Nothing is uploaded; results are written next to this program.");
        Console.WriteLine("Look at the activity on a second device or a friend's view of your profile after each step.");
        var label = Prompt.Text("Which account is this run (for example main or second)?");
        var log = new ResultLog(label, options, DateTimeOffset.Now);
        log.Save();
        Console.WriteLine($"Results file: {log.FilePath}");

        try
        {
            var session = new IpcSession();
            try
            {
                if (await ConnectAsync(session, options, log).ConfigureAwait(false))
                {
                    await RunActivitySteps(session, options, log).ConfigureAwait(false);
                }
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            await RunLoopbackStep(options, log).ConfigureAwait(false);
        }
        finally
        {
            log.Save();
            Console.WriteLine();
            Console.WriteLine($"Done. Bring back: {log.FilePath}");
            Prompt.WaitForEnter("Press Enter to close.");
        }

        return 0;
    }

    private static async Task<bool> ConnectAsync(IpcSession session, SpikeOptions options, ResultLog log)
    {
        var (ok, sent, response) = await session.ConnectAsync(options.AppId, CancellationToken.None).ConfigureAwait(false);
        Show("Handshake", sent, response);
        log.Add(new StepResult("Handshake", sent, response, ok ? "-" : "Handshake failed; steps A to E skipped."));
        return ok;
    }

    private static async Task RunActivitySteps(IpcSession session, SpikeOptions options, ResultLog log)
    {
        await RunStep(session, log, "A plain text", new JsonObject { ["details"] = "Spike A plain text", ["state"] = "No image, no name" },
            () => $"activity visible (Q1): {Prompt.YesNo("Does your profile show an activity from this application?")}; text visible: {Prompt.YesNo("Does it show 'Spike A plain text'?")}").ConfigureAwait(false);

        await RunStep(session, log, "B large_image https URL", new JsonObject
        {
            ["details"] = "Spike B image",
            ["assets"] = new JsonObject { ["large_image"] = options.ImageUrl, ["large_text"] = "Spike B hover text" },
        },
            () => $"image visible (Q2): {Prompt.YesNo("Is the large image visible?")}; hover text shown: {Prompt.YesNo("Does hovering the image show 'Spike B hover text'?")}").ConfigureAwait(false);

        await RunStep(session, log, "C name override", new JsonObject { ["name"] = "Spike Name Test", ["details"] = "Spike C name" },
            () =>
            {
                var says = Prompt.YesNo("Does the header say 'Playing Spike Name Test'? (n if it shows the application's own name)");
                var note = Prompt.Text("What does the header say? (Enter to skip)");
                return $"name overrides Playing text (Q3): {says}; header text: {(note.Length == 0 ? "-" : note)}";
            }).ConfigureAwait(false);

        string[] expected = ["the application name", "the state text 'Spike D state'", "the details text 'Spike D details'"];
        for (var type = 0; type <= 2; type++)
        {
            var current = type;
            await RunStep(session, log, $"D status_display_type {type}", new JsonObject
            {
                ["details"] = "Spike D details",
                ["state"] = "Spike D state",
                ["status_display_type"] = type,
            },
                () => $"honoured (Q4), header shows {expected[current]}: {Prompt.YesNo($"Does the header/status line show {expected[current]}?")}").ConfigureAwait(false);
        }

        await RunStep(session, log, "E clear", null,
            () => $"activity gone: {Prompt.YesNo("Is the activity gone from the profile?")}").ConfigureAwait(false);
    }

    private static async Task RunStep(IpcSession session, ResultLog log, string name, JsonObject? activity, Func<string> ask)
    {
        var (sent, response) = await session.SetActivityAsync(activity, CancellationToken.None).ConfigureAwait(false);
        Show(name, sent, response);
        Prompt.WaitForEnter("Look at the profile now, then press Enter to answer the questions.");
        log.Add(new StepResult(name, sent, response, ask()));
    }

    private static async Task RunLoopbackStep(SpikeOptions options, ResultLog log)
    {
        Console.WriteLine();
        Console.WriteLine("--- F loopback redirect ---");
        Prompt.WaitForEnter("The browser will open a Discord authorization page. Press Enter to start.");
        var (sent, response) = await LoopbackTest.RunAsync(options.AppId, CancellationToken.None).ConfigureAwait(false);
        Show("F loopback redirect", sent, response);
        var accepted = Prompt.YesNo("Did Discord let you authorize (no 'invalid redirect_uri' page)? Type n if you saw that error.");
        log.Add(new StepResult("F loopback redirect (Q5)", sent, response, $"authorize page accepted the arbitrary port: {accepted}"));
    }

    private static void Show(string step, string sent, string response)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {step} ---");
        Console.WriteLine($"Sent:     {sent}");
        Console.WriteLine($"Response: {response}");
    }
}
