using System.Globalization;
using System.Text;

namespace DbdPresence.Spikes;

/// <summary>One SET_ACTIVITY the cadence mode sent.</summary>
internal sealed record CadenceUpdate(
    int Counter,
    int Block,
    double IntervalSeconds,
    DateTimeOffset SentAt,
    double ElapsedSeconds,
    double ReplyMilliseconds,
    string Outcome,
    string Detail);

/// <summary>One group of updates sent at the same gap, plus what the owner reported seeing for it.</summary>
internal sealed class CadenceBlock(int number, double intervalSeconds)
{
    public int Number { get; } = number;

    public double IntervalSeconds { get; } = intervalSeconds;

    public List<CadenceUpdate> Updates { get; } = [];

    public int Planned { get; set; }

    /// <summary>Gets or sets the counter values the owner said appeared, exactly as typed (empty when skipped).</summary>
    public string SeenText { get; set; } = string.Empty;

    /// <summary>Gets or sets how long after a send the owner said the change showed up, as typed (empty when skipped).</summary>
    public string DelayText { get; set; } = string.Empty;

    public string Range => Updates.Count == 0 ? "-" : $"{Updates[0].Counter} to {Updates[^1].Counter}";
}

/// <summary>Builds the end-of-run summary and writes the cadence results as markdown next to the executable. Nothing is uploaded.</summary>
internal sealed class CadenceLog(SpikeOptions options, DateTimeOffset startedAt)
{
    public List<CadenceBlock> Blocks { get; } = [];

    public string FilePath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        $"cadence-results-{startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md");

    /// <summary>Plain-text summary with one row per block, for the console and the results file.</summary>
    public string SummaryTable()
    {
        var text = new StringBuilder();
        text.AppendLine("Block  Gap     Updates sent   Counters     Accepted  Errors  No reply/closed  Reply ms (median / max)  You saw");
        foreach (var block in Blocks)
        {
            var replies = block.Updates.Where(u => u.Outcome is CadenceReply.Ok or CadenceReply.Error).Select(u => u.ReplyMilliseconds).Order().ToArray();
            var median = replies.Length == 0
                ? "-"
                : Number(replies.Length % 2 == 1 ? replies[replies.Length / 2] : (replies[(replies.Length / 2) - 1] + replies[replies.Length / 2]) / 2);
            var max = replies.Length == 0 ? "-" : Number(replies[^1]);
            var accepted = block.Updates.Count(u => u.Outcome == CadenceReply.Ok);
            var errors = block.Updates.Count(u => u.Outcome == CadenceReply.Error);
            var silent = block.Updates.Count(u => u.Outcome is CadenceReply.NoReply or CadenceReply.Closed or CadenceReply.SendFailed or CadenceReply.Unreadable);
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{block.Number,-6} {Number(block.IntervalSeconds) + " s",-7} {block.Updates.Count + " of " + block.Planned,-14} {block.Range,-12} {accepted,-9} {errors,-7} {silent,-16} {median + " / " + max,-24} {(block.SeenText.Length == 0 ? "not given" : block.SeenText)}"));
        }

        return text.ToString();
    }

    public void Save()
    {
        var text = new StringBuilder();
        text.AppendLine("# Discord update cadence results");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Application ID: {options.AppId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Started: {startedAt.ToString("O", CultureInfo.InvariantCulture)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- OS: {Environment.OSVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Gaps tried (s): {string.Join(", ", options.IntervalsSeconds.Select(Number))}; updates per block: {options.UpdatesPerBlock}; rest between blocks: {options.RestSeconds} s");
        text.AppendLine("- Method: SET_ACTIVITY over the local Discord pipe, details = \"Update N\"; N counts up over the whole run. \"Accepted\" means Discord's reply said OK; it does not prove the change was shown to others.");
        text.AppendLine();
        text.AppendLine("## Summary");
        text.AppendLine();
        text.AppendLine("```");
        text.Append(SummaryTable());
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("## Owner notes");
        text.AppendLine();
        foreach (var block in Blocks)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Block {block.Number} (gap {Number(block.IntervalSeconds)} s, counters {block.Range}): saw \"{Cell(block.SeenText)}\"; delay before the change showed: \"{Cell(block.DelayText)}\"");
        }

        text.AppendLine();
        text.AppendLine("## Every update");
        text.AppendLine();
        text.AppendLine("| Counter | Block | Gap (s) | Sent at (local) | Seconds since start | Reply (ms) | Outcome | Detail |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var update in Blocks.SelectMany(b => b.Updates))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {update.Counter} | {update.Block} | {Number(update.IntervalSeconds)} | {update.SentAt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} | {Number(update.ElapsedSeconds)} | {Number(update.ReplyMilliseconds)} | {update.Outcome} | {Cell(update.Detail)} |");
        }

        File.WriteAllText(FilePath, text.ToString(), new UTF8Encoding(false));
    }

    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Cell(string value) =>
        string.IsNullOrEmpty(value)
            ? "-"
            : value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
