// Covers: ADR-0007 live apply (changes publish immediately), debounced single writer, save failure surfaced (no silent loss).
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Configuration.Tests;

public sealed class SettingsServiceTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeTimeProvider _time = new();

    private SettingsService NewService() => new(new SettingsStore(_fs, new SettingsPaths("/cfg", "/data")), _time);

    [Fact]
    public void Update_PublishesValidatedSettingsImmediately()
    {
        using var service = NewService();
        SettingsChangedEventArgs? seen = null;
        service.Changed += (_, e) => seen = e;

        var issues = service.Update(s => s with { Appearance = s.Appearance with { FontScalePercent = 999 } });

        Assert.NotNull(seen);
        Assert.Equal(100, seen.Current.Appearance.FontScalePercent);
        Assert.Equal(seen.Current, service.Current);
        Assert.Single(issues);
    }

    [Fact]
    public void Updates_AreDebouncedIntoOneWrite()
    {
        using var service = NewService();

        for (var i = 0; i < 10; i++)
        {
            service.Update(s => s with { FirstRunCompleted = !s.FirstRunCompleted });
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(0, _fs.Writes);
        _time.Advance(SettingsService.SaveDelay);
        Assert.Equal(1, _fs.Writes);
    }

    [Fact]
    public void Dispose_FlushesPendingChanges()
    {
        var service = NewService();
        service.Update(s => s with { FirstRunCompleted = true });

        service.Dispose();

        Assert.Equal(1, _fs.Writes);
        Assert.True(new SettingsStore(_fs, new SettingsPaths("/cfg", "/data")).Load().Settings.FirstRunCompleted);
    }

    [Fact]
    public void SaveFailure_IsReported_AndRetriedOnNextChange()
    {
        using var service = NewService();
        Exception? failure = null;
        service.SaveFailed += (_, e) => failure = e.Error;
        _fs.FailNextWrite = new IOException("disk full");

        service.Update(s => s with { FirstRunCompleted = true });
        _time.Advance(SettingsService.SaveDelay);
        service.Update(s => s with { Lifecycle = s.Lifecycle with { StartMinimised = true } });
        _time.Advance(SettingsService.SaveDelay);

        Assert.IsType<IOException>(failure);
        Assert.Equal(1, _fs.Writes);
    }

    [Fact]
    public void ChangeMadeDuringASave_IsNotLost_AndReadersAreNotBlocked()
    {
        using var service = NewService();
        service.Update(s => s with { FirstRunCompleted = true });
        AppSettings? readDuringWrite = null;
        _fs.DuringWrite = () =>
        {
            _fs.DuringWrite = null;
            // Another thread (the UI) must be able to read while the disk write is in progress.
            var reader = Task.Run(() => service.Current);
            readDuringWrite = reader.Wait(TimeSpan.FromSeconds(5)) ? reader.Result : null;
            service.Update(s => s with { Discord = s.Discord with { ApplicationName = "Changed mid-save" } });
        };

        var file = new SettingsPaths("/cfg", "/data").SettingsFile; // platform path separator
        service.Flush();
        Assert.NotNull(readDuringWrite);
        Assert.DoesNotContain("Changed mid-save", _fs.Files[file], StringComparison.Ordinal);

        service.Flush();
        Assert.Contains("Changed mid-save", _fs.Files[file], StringComparison.Ordinal);
    }
}
