using System.Globalization;
using DbdPresence.Core.Game;
using DbdPresence.Discord;

namespace DbdPresence.Presentation.Presence;

/// <summary>The single button of the status headline, and where it goes (design §3.2).</summary>
public enum HeroActionKind
{
    /// <summary>Opens the Account page.</summary>
    OpenAccount,

    /// <summary>Opens Settings where the data folder is shown.</summary>
    ShowWhereSaved,

    /// <summary>Resumes presence.</summary>
    Resume,

    /// <summary>Opens Settings, Advanced.</summary>
    OpenAdvancedSettings,
}

/// <summary>The headline's one button.</summary>
/// <param name="Label">The button text.</param>
/// <param name="Kind">What it does.</param>
public sealed record HeroAction(string Label, HeroActionKind Kind);

/// <summary>One of the two facts beside the headline.</summary>
/// <param name="Label">What it is ("Discord", "Game").</param>
/// <param name="Value">Its value ("Connected", "Running since 19:42").</param>
/// <param name="Tone">The tone of the small icon beside the value; the value is always words too.</param>
public sealed record HeroFact(string Label, string Value, StatusTone Tone);

/// <summary>
/// The status headline on Home (design §3.2): one state, one sentence of detail, at most one button, two facts. The tone is never the only signal:
/// <see cref="Icon"/> and the words differ in every state.
/// </summary>
/// <param name="Id">The state id from the design (H1 to H10), for tests and automation.</param>
/// <param name="Title">The headline.</param>
/// <param name="Detail">One plain sentence.</param>
/// <param name="Tone">The tone of the stripe and tint.</param>
/// <param name="Icon">The icon name; the view maps it to a glyph in one table.</param>
/// <param name="Action">The headline's button, or null.</param>
/// <param name="Facts">The Discord and game facts.</param>
public sealed record HeroStatus(string Id, string Title, string Detail, StatusTone Tone, string Icon, HeroAction? Action, IReadOnlyList<HeroFact> Facts);

/// <summary>What the headline needs; all of it is real, live state.</summary>
/// <param name="Snapshot">The coordinator's snapshot.</param>
/// <param name="Now">The current time (retry countdowns).</param>
/// <param name="Zone">The time zone for "since 19:42".</param>
/// <param name="AccessWithdrawn">A signed revocation was received.</param>
/// <param name="SettingsNotSaved">Settings are in memory only or the last save failed.</param>
/// <param name="GenericWhilePaused">"While paused: show that I'm playing" is on.</param>
/// <param name="ShowBreakWhenClosed">"Show Taking a break while the game is closed" is on.</param>
/// <param name="ConfirmedCharacter">The confirmed character, when the app has one and the user shows it.</param>
public sealed record HeroInputs(
    PresenceSnapshot Snapshot, DateTimeOffset Now, TimeZoneInfo Zone, bool AccessWithdrawn, bool SettingsNotSaved, bool GenericWhilePaused, bool ShowBreakWhenClosed, string? ConfirmedCharacter);

/// <summary>Chooses the headline (design §3.2): evaluated top to bottom, the first match wins.</summary>
public static class HeroStatusBuilder
{
    /// <summary>Builds the headline for the given state.</summary>
    /// <param name="inputs">The live state.</param>
    public static HeroStatus Build(HeroInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var snapshot = inputs.Snapshot;
        var health = snapshot.Connection;
        var game = snapshot.Game.State;
        var gameFact = GameFact(snapshot.Game, inputs.Zone);

        if (snapshot.IsConfigured && health.Status == RpcStatus.Disconnected && health.LastError is null && health.NextRetryUtc is null && health.LastAckUtc is null)
        {
            return new HeroStatus(
                "H1", "Starting up", "Connecting to Discord and looking for the game.", StatusTone.Neutral, "Progress", null,
                [new("Discord", "Checking…", StatusTone.Neutral), new("Game", "Checking…", StatusTone.Neutral)]);
        }

        var connected = health.Status == RpcStatus.Connected;
        if (inputs.AccessWithdrawn)
        {
            return new HeroStatus(
                "H2", "Your status is switched off", "Access for this account was withdrawn, so nothing is shown on Discord. The Account page explains why.", StatusTone.Problem, "Blocked",
                new HeroAction("Open Account", HeroActionKind.OpenAccount), [DiscordFact(snapshot), gameFact]);
        }

        if (inputs.SettingsNotSaved)
        {
            return new HeroStatus(
                "H3", "Your changes can't be saved", "They work until you close the app, then they're lost. Make sure the settings folder can be written to.", StatusTone.Problem, "Error",
                new HeroAction("Show where it's saved", HeroActionKind.ShowWhereSaved), [DiscordFact(snapshot), gameFact]);
        }

        if (snapshot.Paused)
        {
            return new HeroStatus(
                "H4", "Paused",
                inputs.GenericWhilePaused ? "Discord only shows that you're playing until you resume." : "Discord shows nothing until you resume.",
                StatusTone.Neutral, "Pause", new HeroAction("Resume", HeroActionKind.Resume), [DiscordFact(snapshot), gameFact]);
        }

        if (!snapshot.IsConfigured)
        {
            return new HeroStatus(
                "H5", "Finish setting up your own Discord app", "You turned on “Use my own Discord app” but its ID is missing or wrong, so nothing is sent.", StatusTone.Caution, "Warning",
                new HeroAction("Open Advanced settings", HeroActionKind.OpenAdvancedSettings), [new("Discord", "Not set up", StatusTone.Caution), gameFact]);
        }

        if (health.Status == RpcStatus.Disconnected)
        {
            return new HeroStatus(
                "H6", "Open Discord on this PC", $"DBD Presence connects by itself once Discord is open.{Retry(health, inputs.Now)}", StatusTone.Problem, "Plug", null,
                [new("Discord", "Not open", StatusTone.Problem), gameFact]);
        }

        if (!connected)
        {
            var problem = health.Status == RpcStatus.Faulted;
            return new HeroStatus(
                "H7", "Connecting to Discord", problem ? $"Discord didn't answer.{Retry(health, inputs.Now, "Trying again in")}" : "Waiting for Discord to answer.",
                StatusTone.Caution, "Progress", null, [new("Discord", "Connecting…", StatusTone.Caution), gameFact]);
        }

        var discord = new HeroFact("Discord", "Connected", StatusTone.Good);
        return game switch
        {
            GameState.Running => new HeroStatus(
                "H10", "You're live on Discord",
                inputs.ConfirmedCharacter is { Length: > 0 } character
                    ? $"Your friends can see you're playing as {character}."
                    : "Your friends can see that you're playing Dead by Daylight.",
                StatusTone.Good, "Check", null, [discord, gameFact]),
            GameState.Launching => new HeroStatus(
                "H9", "Dead by Daylight is starting", "Your status appears as soon as the game is running.", StatusTone.Caution, "Progress", null, [discord, gameFact]),
            _ => new HeroStatus(
                "H8", "Ready when you are",
                inputs.ShowBreakWhenClosed ? "Discord shows that you're taking a break." : "Start Dead by Daylight and your status appears on Discord.",
                StatusTone.Neutral, "Moon", null, [discord, gameFact]),
        };
    }

    private static HeroFact DiscordFact(PresenceSnapshot snapshot) => snapshot.Connection.Status switch
    {
        RpcStatus.Connected => new("Discord", "Connected", StatusTone.Good),
        RpcStatus.Connecting => new("Discord", "Connecting…", StatusTone.Caution),
        RpcStatus.Disconnected => new("Discord", "Not open", StatusTone.Problem),
        _ => new("Discord", "Connecting…", StatusTone.Caution),
    };

    private static HeroFact GameFact(GameLifecycle game, TimeZoneInfo zone) => game.State switch
    {
        GameState.Running => new("Game", game.StartedUtc is { } started ? $"Running since {Time(started, zone)}" : "Running", StatusTone.Good),
        GameState.Launching => new("Game", "Starting", StatusTone.Caution),
        _ => new("Game", "Not running", StatusTone.Neutral),
    };

    private static string Time(DateTimeOffset utc, TimeZoneInfo zone) => string.Create(CultureInfo.CurrentCulture, $"{TimeZoneInfo.ConvertTime(utc, zone):t}");

    private static string Retry(RpcHealth health, DateTimeOffset now, string lead = "Next try in")
    {
        if (health.NextRetryUtc is not { } retry)
        {
            return string.Empty;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling((retry - now).TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $" {lead} {seconds} s.");
    }
}
