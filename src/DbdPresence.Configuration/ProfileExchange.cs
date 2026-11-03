using System.Text.Json;
using System.Text.Json.Nodes;
using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration;

/// <summary>The result of importing a profile: either a validated profile (with corrections) or an error, never partial.</summary>
/// <param name="Profile">The validated profile, or null on error.</param>
/// <param name="Issues">Corrections made during validation.</param>
/// <param name="Error">Why the import was rejected, or null.</param>
public sealed record ProfileImportResult(PresenceProfile? Profile, IReadOnlyList<SettingsIssue> Issues, string? Error);

/// <summary>Profile export/import and diff preview (FR-PROF-02).</summary>
public static class ProfileExchange
{
    /// <summary>Maximum import size (characters).</summary>
    public const int MaxImportLength = 1024 * 1024;

    /// <summary>Serialises a profile for sharing or backup.</summary>
    public static string Export(PresenceProfile profile) =>
        JsonSerializer.Serialize(new ProfileExport { Profile = profile }, SettingsJsonContext.Default.ProfileExport);

    /// <summary>Parses, version-checks and validates an exported profile. Never throws for bad input.</summary>
    public static ProfileImportResult Import(string? json)
    {
        // Size first: never scan arbitrarily large input.
        if (json?.Length > MaxImportLength)
        {
            return Rejected("The file is larger than 1 MB.");
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return Rejected("The file is empty.");
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject document)
            {
                return Rejected("This is not a DBD Presence profile (expected a JSON object).");
            }

            if (document["kind"]?.GetValueKind() != JsonValueKind.String || (string?)document["kind"] != ProfileExport.ExpectedKind)
            {
                return Rejected("This is not a DBD Presence profile (missing kind \"dbd-presence-profile\").");
            }

            if (document["schemaVersion"] is not JsonValue versionNode || !versionNode.TryGetValue(out int version) || version < 1)
            {
                return Rejected("The profile has no valid schema version.");
            }

            if (version > AppSettings.CurrentSchemaVersion)
            {
                return Rejected("The profile was made by a newer version of DBD Presence. Update the app to import it.");
            }

            if (document["profile"] is JsonObject exported)
            {
                // Exports made before schema v3, and by builds of v3 before the switches existed, lack them and may still carry declared details.
                SettingsV2ToV3Migration.MigrateProfile(exported);
            }

            var export = document.Deserialize(SettingsJsonContext.Default.ProfileExport);
            if (export?.Profile is null)
            {
                return Rejected("The profile section is missing.");
            }

            var issues = new List<SettingsIssue>();
            var profile = SettingsValidator.ValidateProfile(export.Profile, "profile", issues);
            return new ProfileImportResult(profile, issues, null);
        }
        catch (JsonException error)
        {
            return Rejected($"The file is not valid JSON ({error.Message}).");
        }
        catch (InvalidOperationException error)
        {
            return Rejected($"The profile has unexpected content ({error.Message}).");
        }
    }

    /// <summary>
    /// Lists the settings paths that differ between two profiles (for the import preview), e.g.
    /// <c>visibility.showMap</c> or <c>templates.byState.Running.details</c>.
    /// </summary>
    public static IReadOnlyList<string> Diff(PresenceProfile current, PresenceProfile incoming)
    {
        var left = JsonSerializer.SerializeToNode(current, SettingsJsonContext.Default.PresenceProfile);
        var right = JsonSerializer.SerializeToNode(incoming, SettingsJsonContext.Default.PresenceProfile);
        var changes = new List<string>();
        Compare(left, right, string.Empty, changes);
        return changes;
    }

    private static void Compare(JsonNode? left, JsonNode? right, string path, List<string> changes)
    {
        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            foreach (var key in leftObject.Select(p => p.Key).Union(rightObject.Select(p => p.Key)).Order(StringComparer.Ordinal))
            {
                Compare(leftObject[key], rightObject[key], path.Length == 0 ? key : $"{path}.{key}", changes);
            }

            return;
        }

        if (!JsonNode.DeepEquals(left, right))
        {
            changes.Add(path);
        }
    }

    private static ProfileImportResult Rejected(string error) => new(null, [], error);
}
