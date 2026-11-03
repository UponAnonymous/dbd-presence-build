using DbdPresence.Core.Presence;

namespace DbdPresence.Discord.Tests.Support;

/// <summary>Builds <see cref="ValidatedActivity"/> instances the only legal way: through the validator.</summary>
internal static class TestActivities
{
    /// <summary>A well-formed Discord application id.</summary>
    public const string ApplicationId = "123456789012345678";

    /// <summary>Validates a draft and returns the sendable activity.</summary>
    public static ValidatedActivity From(ActivityDraft draft) => ActivityValidator.Validate(draft).Activity;

    /// <summary>A minimal activity that differs from every other by its details text.</summary>
    public static ValidatedActivity Simple(string details) => From(new ActivityDraft { Details = details });

    /// <summary>An activity that uses every field.</summary>
    public static ValidatedActivity Full() => From(new ActivityDraft
    {
        Kind = ActivityKind.Competing,
        Details = "In a trial",
        State = "3 generators left",
        StartTimestamp = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),
        LargeImage = "map_haddonfield",
        LargeText = "Haddonfield",
        SmallImage = "https://example.com/role.png",
        SmallText = "Survivor",
        Buttons = [new ButtonDraft("Watch live", "https://twitch.tv/example"), new ButtonDraft("Discord", "https://discord.gg/example")],
        StatusDisplay = StatusDisplay.Details,
        NameOverride = "Dead by Daylight",
    });
}
