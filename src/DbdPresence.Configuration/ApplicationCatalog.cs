namespace DbdPresence.Configuration;

/// <summary>One official Discord application the user can pick (PLAN_V2 §2.2, FR-RPC-07).</summary>
/// <param name="Key">Stable catalog key stored in settings.</param>
/// <param name="ApplicationId">Discord Application ID (public identifier).</param>
/// <param name="Name">The application's name in Discord; used for the in-app preview when the user sets no display name.</param>
/// <param name="Description">The application's Discord description (unofficial-fan-app labelling, FR-RPC-12).</param>
public sealed record OfficialApplication(string Key, string ApplicationId, string Name, string Description);

/// <summary>
/// The list of official applications. The bundled list is the fallback; a signed remote configuration
/// (a later slice) supplies another implementation.
/// </summary>
public interface IApplicationCatalog
{
    /// <summary>Gets the applications, default first. Never empty.</summary>
    IReadOnlyList<OfficialApplication> Applications { get; }

    /// <summary>Returns the application with <paramref name="key"/>, or the default one when the key is unknown or empty.</summary>
    /// <param name="key">The catalog key from settings.</param>
    OfficialApplication Resolve(string? key);
}

/// <summary>The catalog built into the app: one application named after the game, labelled unofficial.</summary>
public sealed class BundledApplicationCatalog : IApplicationCatalog
{
    /// <summary>The Discord description of the bundled application (FR-RPC-12).</summary>
    public const string UnofficialDescription = "Unofficial fan-made presence — not affiliated with Behaviour Interactive";

    private static readonly OfficialApplication[] s_applications =
    [
        new(DiscordSettings.DefaultApplicationKey, "1563291325107937340", "Dead by Daylight", UnofficialDescription),
    ];

    /// <inheritdoc />
    public IReadOnlyList<OfficialApplication> Applications => s_applications;

    /// <inheritdoc />
    public OfficialApplication Resolve(string? key) =>
        s_applications.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal)) ?? s_applications[0];
}

/// <summary>The application a settings document points at.</summary>
/// <param name="ApplicationId">The ID to connect with, or null when the own-application option is on but no valid ID is entered.</param>
/// <param name="Name">The application's name for the preview.</param>
/// <param name="IsOwn">True when the user's own application is selected.</param>
public sealed record ResolvedApplication(string? ApplicationId, string Name, bool IsOwn)
{
    /// <summary>Gets a value indicating whether there is an ID to connect with.</summary>
    public bool IsConfigured => ApplicationId is not null;

    /// <summary>Resolves the application for <paramref name="discord"/>: the catalog entry unless the own-application option is on.</summary>
    /// <param name="discord">The Discord settings.</param>
    /// <param name="catalog">The application catalog.</param>
    public static ResolvedApplication From(DiscordSettings discord, IApplicationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentNullException.ThrowIfNull(catalog);
        if (discord.UseOwnApplication)
        {
            return new ResolvedApplication(
                SettingsValidator.IsConfiguredApplicationId(discord.OwnApplicationId) ? discord.OwnApplicationId : null,
                discord.ApplicationName,
                IsOwn: true);
        }

        var entry = catalog.Resolve(discord.SelectedApplication);
        return new ResolvedApplication(entry.ApplicationId, entry.Name, IsOwn: false);
    }
}
