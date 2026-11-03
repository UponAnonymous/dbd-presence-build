using System.Text.Json.Nodes;

namespace DbdPresence.Configuration;

/// <summary>
/// Schema v1 to v2 (ADR-0007 addendum, FR-RPC-07): the user-entered <c>discord.applicationId</c> becomes the Advanced
/// own-application fallback (<c>ownApplicationId</c> with <c>useOwnApplication</c> on), so a working v1 setup keeps
/// connecting to the same application. New v2 fields are left to their defaults.
/// </summary>
public sealed class SettingsV1ToV2Migration : ISettingsMigration
{
    /// <inheritdoc />
    public int FromVersion => 1;

    /// <inheritdoc />
    public void Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document["discord"] is not JsonObject discord)
        {
            return;
        }

        // The new fields are written explicitly rather than left to deserialisation defaults.
        discord.Remove("applicationId", out var node);
        discord["displayName"] = null;
        discord["selectedApplication"] = DiscordSettings.DefaultApplicationKey;
        discord["useOwnApplication"] = false;
        discord["ownApplicationId"] = string.Empty;
        if (node is JsonValue value && value.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
        {
            discord["ownApplicationId"] = id.Trim();
            discord["useOwnApplication"] = true;
        }
    }
}
