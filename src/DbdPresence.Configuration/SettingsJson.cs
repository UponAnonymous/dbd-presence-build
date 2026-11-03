using System.Text.Json;
using System.Text.Json.Serialization;
using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration;

/// <summary>Source-generated JSON contract for settings and profile exports (no reflection at runtime).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    MaxDepth = 32)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProfileExport))]
[JsonSerializable(typeof(PresenceProfile))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>The profile export document (FR-PROF-02).</summary>
public sealed record ProfileExport
{
    /// <summary>The document kind marker.</summary>
    public const string ExpectedKind = "dbd-presence-profile";

    /// <summary>Gets the document kind.</summary>
    public string Kind { get; init; } = ExpectedKind;

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; init; } = AppSettings.CurrentSchemaVersion;

    /// <summary>Gets the exported profile.</summary>
    public PresenceProfile? Profile { get; init; }
}
