using System.Globalization;
using DbdPresence.Core.Game;
using DbdPresence.Discord;

namespace DbdPresence.Presentation.Presence;

/// <summary>How a status reads at a glance; the view pairs it with an icon so colour is never the only signal.</summary>
public enum StatusTone
{
    /// <summary>Neutral or idle.</summary>
    Neutral,

    /// <summary>Working as intended.</summary>
    Good,

    /// <summary>In progress or needs attention soon.</summary>
    Caution,

    /// <summary>Not working.</summary>
    Problem,
}

/// <summary>A status line with an optional explanation.</summary>
/// <param name="Text">Short status.</param>
/// <param name="Detail">Longer explanation or next action, if any.</param>
/// <param name="Tone">Tone for the icon and colour.</param>
public sealed record StatusLine(string Text, string? Detail, StatusTone Tone);

/// <summary>User-facing wording for connection and game status (UI_SPEC §5).</summary>
public static class StatusText
{
    /// <summary>Describes the Discord connection.</summary>
    public static StatusLine Connection(RpcHealth health, bool isConfigured, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (!isConfigured || health.Status == RpcStatus.NotConfigured)
        {
            return new StatusLine("Not configured", "Your own Discord application needs a valid Application ID. Enter it under Settings, Advanced, “Use my own Discord app”, or switch back to the shared application.", StatusTone.Caution);
        }

        return health.Status switch
        {
            RpcStatus.Connected => new StatusLine("Connected to Discord", health.Endpoint is null ? null : $"Using {health.Endpoint}.", StatusTone.Good),
            RpcStatus.Connecting => new StatusLine("Connecting to Discord…", null, StatusTone.Caution),
            RpcStatus.Faulted => new StatusLine("Connection problem — retrying", Retry(health, now), StatusTone.Problem),
            _ => new StatusLine("Discord not detected — retrying", Retry(health, now) ?? "Start the Discord desktop app; this app reconnects automatically.", StatusTone.Problem),
        };
    }

    /// <summary>Describes the game process state.</summary>
    public static StatusLine Game(GameLifecycle game, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(zone);
        return game.State switch
        {
            GameState.Launching => new StatusLine("Dead by Daylight is starting", "Only the launcher is running so far.", StatusTone.Caution),
            GameState.Running => new StatusLine("Dead by Daylight is running", Since(game, zone), StatusTone.Good),
            _ => new StatusLine("Dead by Daylight is not running", "Presence updates automatically when the game starts.", StatusTone.Neutral),
        };
    }

    private static string? Since(GameLifecycle game, TimeZoneInfo zone)
    {
        if (game.StartedUtc is not { } started)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(started, zone);
        var text = string.Create(CultureInfo.CurrentCulture, $"Since {local:t}");
        return game.StartIsApproximate ? $"{text} (approximate: when this app first saw the game)." : $"{text}.";
    }

    private static string? Retry(RpcHealth health, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(health.LastError))
        {
            parts.Add(health.LastError.TrimEnd('.') + ".");
        }

        if (health.NextRetryUtc is { } retry)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((retry - now).TotalSeconds));
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Next attempt in {seconds} s."));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
