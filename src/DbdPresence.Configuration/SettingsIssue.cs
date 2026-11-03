namespace DbdPresence.Configuration;

/// <summary>A setting that was invalid and has been reset or corrected, with a user-facing explanation.</summary>
/// <param name="Path">Dotted path of the setting, e.g. <c>discord.applicationId</c>.</param>
/// <param name="Message">What was wrong and what was done.</param>
public sealed record SettingsIssue(string Path, string Message);
