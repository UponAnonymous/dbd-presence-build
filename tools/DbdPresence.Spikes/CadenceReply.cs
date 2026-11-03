using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbdPresence.Spikes;

/// <summary>Turns Discord's raw answer to one SET_ACTIVITY into a short outcome word and a detail text for the log.</summary>
internal static class CadenceReply
{
    public const string Ok = "OK";
    public const string Error = "ERROR";
    public const string NoReply = "NO-REPLY";
    public const string Closed = "CLOSED";
    public const string SendFailed = "SEND-FAILED";
    public const string Unreadable = "UNREADABLE";

    /// <summary>Classifies the answer text returned by <see cref="IpcSession.SetActivityAsync"/>.</summary>
    public static (string Outcome, string Detail) Classify(string response)
    {
        if (response.StartsWith("No answer from Discord", StringComparison.Ordinal))
        {
            return (NoReply, response);
        }

        if (response.StartsWith("Discord closed the connection", StringComparison.Ordinal)
            || response.StartsWith("Discord sent an oversized frame", StringComparison.Ordinal))
        {
            return (Closed, response);
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(response);
        }
        catch (JsonException)
        {
            return (Unreadable, Shorten(response));
        }

        var evt = json?["evt"]?.ToString();
        if (string.Equals(evt, "ERROR", StringComparison.Ordinal))
        {
            var code = json?["data"]?["code"]?.ToString() ?? "no code";
            var message = json?["data"]?["message"]?.ToString() ?? "no message";
            var looksLikeRateLimit = code == "429" || message.Contains("rate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("limit", StringComparison.OrdinalIgnoreCase);
            return (Error, $"code {code}: {message}{(looksLikeRateLimit ? " (wording suggests a rate limit; judged from the text only)" : string.Empty)}");
        }

        return string.Equals(json?["cmd"]?.ToString(), "SET_ACTIVITY", StringComparison.Ordinal)
            ? (Ok, "accepted")
            : (Unreadable, Shorten(response));
    }

    private static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
