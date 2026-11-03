using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbdPresence.Configuration;

/// <summary>Where the loaded settings came from.</summary>
public enum SettingsSource
{
    /// <summary>The settings file.</summary>
    File,

    /// <summary>A backup, because the settings file was missing or unreadable.</summary>
    Backup,

    /// <summary>Built-in defaults (first run, or nothing usable found).</summary>
    Defaults,
}

/// <summary>The result of loading settings.</summary>
/// <param name="Settings">Validated settings.</param>
/// <param name="Source">Where they came from.</param>
/// <param name="IsReadOnly">True when the file was written by a newer version and must not be overwritten.</param>
/// <param name="Issues">Per-field corrections made by validation.</param>
/// <param name="Notice">A user-facing message when something unusual happened (restore, newer file), else null.</param>
public sealed record SettingsLoadResult(
    AppSettings Settings, SettingsSource Source, bool IsReadOnly, IReadOnlyList<SettingsIssue> Issues, string? Notice);

/// <summary>
/// Loads and saves <c>settings.json</c> (ADR-0007): size-capped parsing, migration, validation, recovery
/// from up to <see cref="BackupCount"/> backups, atomic replace, and a session backup on the first save.
/// Not thread-safe: <see cref="SettingsService"/> serialises access.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>Number of rotating backups kept.</summary>
    public const int BackupCount = 5;

    /// <summary>Maximum accepted settings file size.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private readonly ISettingsFileSystem _fileSystem;
    private readonly SettingsPaths _paths;
    private readonly SettingsMigrator _migrator;
    private bool _sessionBackupTaken;

    /// <summary>Initializes a new instance of the <see cref="SettingsStore"/> class.</summary>
    public SettingsStore(ISettingsFileSystem fileSystem, SettingsPaths paths, SettingsMigrator? migrator = null)
    {
        _fileSystem = fileSystem;
        _paths = paths;
        _migrator = migrator ?? new SettingsMigrator();
    }

    /// <summary>Gets a value indicating whether saving is disabled (newer settings file).</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>Gets the path of backup <paramref name="index"/> (1 = newest).</summary>
    public string BackupPath(int index) => $"{_paths.SettingsFile}.bak{index}";

    /// <summary>Gets the paths of existing backups, newest first.</summary>
    public IReadOnlyList<string> ExistingBackups() =>
        [.. Enumerable.Range(1, BackupCount).Select(BackupPath).Where(_fileSystem.Exists)];

    /// <summary>Loads settings, recovering from corruption where possible. Never throws for bad content.</summary>
    public SettingsLoadResult Load()
    {
        var main = _paths.SettingsFile;
        var mainExists = _fileSystem.Exists(main);
        if (mainExists)
        {
            var parsed = TryParse(main);
            if (parsed.Outcome == MigrationOutcome.NewerThanSupported)
            {
                IsReadOnly = true;
                var defaults = SettingsValidator.Validate(new AppSettings());
                return new(defaults.Settings, SettingsSource.Defaults, IsReadOnly: true, defaults.Issues,
                    "Your settings were saved by a newer version of DBD Presence. They have not been changed; defaults are used until you update.");
            }

            if (parsed.Settings is not null)
            {
                var validated = SettingsValidator.Validate(parsed.Settings);
                return new(validated.Settings, SettingsSource.File, IsReadOnly: false, validated.Issues, null);
            }

            PreserveCorruptCopy(main);
            var restored = FromBackups($"Your settings file could not be read ({parsed.Error}).");
            if (restored is not null)
            {
                return restored;
            }

            var fresh = SettingsValidator.Validate(new AppSettings());
            return new(fresh.Settings, SettingsSource.Defaults, IsReadOnly: false, fresh.Issues,
                $"Your settings file could not be read ({parsed.Error}) and no usable backup exists. Defaults are in use; the unreadable file was kept as settings.json.corrupt.");
        }

        var fromBackup = FromBackups("Your settings file was missing.");
        if (fromBackup is not null)
        {
            return fromBackup;
        }

        var first = SettingsValidator.Validate(new AppSettings());
        return new(first.Settings, SettingsSource.Defaults, IsReadOnly: false, first.Issues, null);
    }

    /// <summary>Saves settings atomically (temp file + replace). Takes a backup of the previous file once per session.</summary>
    /// <exception cref="InvalidOperationException">The store is read-only.</exception>
    /// <exception cref="IOException">The file system rejected the write.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Settings were written by a newer version and are read-only in this session.");
        }

        var main = _paths.SettingsFile;
        var temp = $"{main}.tmp";
        _fileSystem.CreateDirectory(_paths.SettingsDirectory);
        _fileSystem.WriteAllTextDurable(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));

        if (!_sessionBackupTaken && _fileSystem.Exists(main))
        {
            RotateBackups(main);
            _sessionBackupTaken = true;
        }

        _fileSystem.Move(temp, main);
    }

    /// <summary>Restores a backup over the settings file (FR-PROF-03) and returns the loaded result.</summary>
    public SettingsLoadResult RestoreBackup(int index)
    {
        var backup = BackupPath(index);
        if (!_fileSystem.Exists(backup))
        {
            throw new FileNotFoundException("Backup not found.", backup);
        }

        var parsed = TryParse(backup);
        if (parsed.Settings is null)
        {
            throw new InvalidDataException($"Backup {index} is not usable: {parsed.Error}");
        }

        var validated = SettingsValidator.Validate(parsed.Settings);
        Save(validated.Settings);
        return new(validated.Settings, SettingsSource.Backup, IsReadOnly: false, validated.Issues, $"Restored settings from backup {index}.");
    }

    private SettingsLoadResult? FromBackups(string reason)
    {
        for (var i = 1; i <= BackupCount; i++)
        {
            var path = BackupPath(i);
            if (!_fileSystem.Exists(path))
            {
                continue;
            }

            var parsed = TryParse(path);
            if (parsed.Settings is not null)
            {
                var validated = SettingsValidator.Validate(parsed.Settings);
                return new(validated.Settings, SettingsSource.Backup, IsReadOnly: false, validated.Issues,
                    $"{reason} Restored from backup {i}.");
            }
        }

        return null;
    }

    private (AppSettings? Settings, MigrationOutcome Outcome, string? Error) TryParse(string path)
    {
        try
        {
            if (_fileSystem.Length(path) > MaxFileBytes)
            {
                return (null, MigrationOutcome.Invalid, "file larger than 1 MB");
            }

            if (JsonNode.Parse(_fileSystem.ReadAllText(path)) is not JsonObject document)
            {
                return (null, MigrationOutcome.Invalid, "not a JSON object");
            }

            var (outcome, _) = _migrator.Migrate(document);
            if (outcome != MigrationOutcome.Current)
            {
                return (null, outcome, outcome == MigrationOutcome.Invalid ? "missing or unsupported schema version" : "written by a newer version");
            }

            SettingsV2ToV3Migration.RepairDocument(document);
            var settings = document.Deserialize(SettingsJsonContext.Default.AppSettings);
            return settings is null ? (null, MigrationOutcome.Invalid, "empty document") : (settings, MigrationOutcome.Current, null);
        }
        catch (JsonException error)
        {
            return (null, MigrationOutcome.Invalid, $"invalid JSON: {error.Message}");
        }
        catch (IOException error)
        {
            return (null, MigrationOutcome.Invalid, $"read failed: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            return (null, MigrationOutcome.Invalid, $"access denied: {error.Message}");
        }
    }

    private void RotateBackups(string main)
    {
        _fileSystem.Delete(BackupPath(BackupCount));
        for (var i = BackupCount - 1; i >= 1; i--)
        {
            if (_fileSystem.Exists(BackupPath(i)))
            {
                _fileSystem.Move(BackupPath(i), BackupPath(i + 1));
            }
        }

        _fileSystem.Copy(main, BackupPath(1));
    }

    private void PreserveCorruptCopy(string main)
    {
        try
        {
            _fileSystem.Copy(main, $"{main}.corrupt");
        }
        catch (IOException)
        {
            // hygiene: allow-swallow keeping a diagnostic copy is best effort; the load result already reports the problem.
        }
    }
}
