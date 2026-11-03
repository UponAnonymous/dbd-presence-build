using System.Globalization;
using System.Text;

namespace DbdPresence.Spikes;

/// <summary>Collects step results and writes them as a markdown table next to the executable. Nothing is uploaded.</summary>
internal sealed class ResultLog(string accountLabel, SpikeOptions options, DateTimeOffset startedAt)
{
    private readonly List<StepResult> _steps = [];

    public string FilePath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        $"spike-results-{startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md");

    public void Add(StepResult step)
    {
        _steps.Add(step);
        Save();
    }

    public void Save()
    {
        var text = new StringBuilder();
        text.AppendLine("# Spike V2-S1 results");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Account: {Cell(accountLabel)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Application ID: {options.AppId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Image URL: {options.ImageUrl}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Started: {startedAt.ToString("O", CultureInfo.InvariantCulture)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- OS: {Environment.OSVersion}");
        text.AppendLine();
        text.AppendLine("| Step | Sent | Discord response | Owner observation |");
        text.AppendLine("| --- | --- | --- | --- |");
        foreach (var step in _steps)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(step.Step)} | {Cell(step.Sent)} | {Cell(step.DiscordResponse)} | {Cell(step.Observation)} |");
        }

        File.WriteAllText(FilePath, text.ToString(), new UTF8Encoding(false));
    }

    private static string Cell(string value) =>
        string.IsNullOrEmpty(value)
            ? "-"
            : value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
