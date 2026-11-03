// Covers: FR-LIFE-01 (opt-in Run key, stable path, stale entry self-heal). Uses a scratch key, never the real Run key.
using DbdPresence.Platform.Windows.Startup;
using Microsoft.Win32;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class AutostartRegistryTests : IDisposable
{
    private readonly string _key = $@"Software\DBDPresenceTests\{Guid.NewGuid():N}";
    private readonly AutostartRegistry _registry;

    public AutostartRegistryTests() => _registry = new AutostartRegistry("DBD Presence Test", _key);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    [Fact]
    public void EnableThenDisable_RoundTrips()
    {
        var exe = Environment.ProcessPath!;

        _registry.Enable(exe);
        Assert.Equal(exe, _registry.RegisteredExecutable());
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
        {
            Assert.Equal($"\"{exe}\" {AutostartRegistry.AutostartArgument}", key!.GetValue("DBD Presence Test"));
        }

        _registry.Disable();
        Assert.Null(_registry.RegisteredExecutable());
    }

    [Fact]
    public void Reconcile_RemovesStaleEntry_AndRegistersCurrentPath()
    {
        _registry.Enable(@"C:\does\not\exist\DBD-Presence.exe");

        var removedStale = _registry.Reconcile(shouldStartWithWindows: true, Environment.ProcessPath!);

        Assert.True(removedStale);
        Assert.Equal(Environment.ProcessPath, _registry.RegisteredExecutable());
    }

    [Fact]
    public void Reconcile_SettingOff_RemovesEntry()
    {
        _registry.Enable(Environment.ProcessPath!);

        _registry.Reconcile(shouldStartWithWindows: false, Environment.ProcessPath!);

        Assert.Null(_registry.RegisteredExecutable());
    }
}
