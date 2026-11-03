using System.Text.Json.Nodes;

namespace DbdPresence.Configuration;

/// <summary>One schema step <c>vN → vN+1</c>: a pure transform of the JSON document (ADR-0007).</summary>
public interface ISettingsMigration
{
    /// <summary>Gets the version this migration upgrades from.</summary>
    int FromVersion { get; }

    /// <summary>Transforms a document at <see cref="FromVersion"/> into the next version, in place.</summary>
    void Migrate(JsonObject document);
}

/// <summary>Outcome of <see cref="SettingsMigrator.Migrate"/>.</summary>
public enum MigrationOutcome
{
    /// <summary>The document is at the current version (possibly after migrating).</summary>
    Current,

    /// <summary>The document was written by a newer app version; it must not be overwritten.</summary>
    NewerThanSupported,

    /// <summary>The document has no usable schema version.</summary>
    Invalid,
}

/// <summary>Applies ordered migrations to bring a settings document to <see cref="AppSettings.CurrentSchemaVersion"/>.</summary>
public sealed class SettingsMigrator
{
    private readonly Dictionary<int, ISettingsMigration> _steps;

    /// <summary>Initializes a new instance of the <see cref="SettingsMigrator"/> class.</summary>
    /// <param name="steps">Migrations, one per source version. <see langword="null"/> uses the production list
    /// (<see cref="SettingsV1ToV2Migration"/>, <see cref="SettingsV2ToV3Migration"/>).</param>
    public SettingsMigrator(IEnumerable<ISettingsMigration>? steps = null)
    {
        _steps = (steps ?? [new SettingsV1ToV2Migration(), new SettingsV2ToV3Migration()]).ToDictionary(s => s.FromVersion);
    }

    /// <summary>Migrates <paramref name="document"/> in place; returns the outcome and the versions applied.</summary>
    public (MigrationOutcome Outcome, IReadOnlyList<int> Applied) Migrate(JsonObject document, int currentVersion = AppSettings.CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document["schemaVersion"] is not JsonValue versionNode || !versionNode.TryGetValue(out int version) || version < 1)
        {
            return (MigrationOutcome.Invalid, []);
        }

        if (version > currentVersion)
        {
            return (MigrationOutcome.NewerThanSupported, []);
        }

        var applied = new List<int>();
        while (version < currentVersion)
        {
            if (!_steps.TryGetValue(version, out var step))
            {
                return (MigrationOutcome.Invalid, applied);
            }

            step.Migrate(document);
            applied.Add(version);
            version++;
            document["schemaVersion"] = version;
        }

        return (MigrationOutcome.Current, applied);
    }
}
