namespace DbdPresence.Configuration;

/// <summary>Live settings: the current document, validated updates and change notification.</summary>
public interface ISettingsService
{
    /// <summary>Raised synchronously after a change was applied (on the thread that made it).</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Raised when a save failed.</summary>
    event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    /// <summary>Gets a value indicating whether changes are kept in memory only.</summary>
    bool IsReadOnly { get; }

    /// <summary>Gets the current settings.</summary>
    AppSettings Current { get; }

    /// <summary>Applies a change: validates, publishes and schedules a save. Returns validation issues.</summary>
    IReadOnlyList<SettingsIssue> Update(Func<AppSettings, AppSettings> change);
}
