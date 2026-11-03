using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>A third-party component and its licence.</summary>
/// <param name="Name">Component.</param>
/// <param name="Licence">Licence identifier.</param>
/// <param name="Source">Where its source and licence text are published (plain text; not opened by the app).</param>
public sealed record ThirdPartyNotice(string Name, string Licence, string Source);

/// <summary>About (UI_SPEC §2): version, build facts, the unsigned-build notice, the disclaimer and licences.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>Said above the uninstall button: the online account is deleted first, because it cannot be reached after the app is gone (RELEASE_CHECKLIST §6; no FR id yet).</summary>
    public const string UninstallText = "To remove DBD Presence, first delete your online account on the Account page if you want it gone. Then choose Uninstall: Windows opens Installed apps, where you pick DBD Presence and choose Uninstall. Your settings and logs stay on this PC unless you delete them.";

    /// <summary>Said when Windows did not open Installed apps.</summary>
    public const string UninstallFailedText = "Windows did not open Installed apps. Open Windows Settings, then Apps, then Installed apps, and choose Uninstall next to DBD Presence.";

    private readonly IInstalledAppsLauncher? _installedApps;

    /// <summary>Initializes a new instance of the <see cref="AboutViewModel"/> class.</summary>
    /// <param name="app">Build facts.</param>
    /// <param name="installedApps">Opens Windows' Installed apps page; null hides the uninstall button (tests that do not need it).</param>
    public AboutViewModel(AppInfo app, IInstalledAppsLauncher? installedApps = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        _installedApps = installedApps;
        Version = app.Version;
        Commit = app.Commit;
        SigningNotice = app.IsSigned
            ? "This build is code-signed."
            : "This build is not code-signed. Windows SmartScreen may warn before it runs; that warning is expected for unsigned builds.";
    }

    /// <summary>Gets a value indicating whether the uninstall button can do anything.</summary>
    public bool CanUninstall => _installedApps is not null;

    /// <summary>Gets the problem opening Installed apps, or null.</summary>
    [ObservableProperty]
    public partial string? UninstallError { get; private set; }

    /// <summary>Gets the product version.</summary>
    public string Version { get; }

    /// <summary>Gets the source commit.</summary>
    public string Commit { get; }

    /// <summary>Gets the code-signing statement.</summary>
    public string SigningNotice { get; }

    /// <summary>Gets the fan-app statement (FR-RPC-12), word for word as shown on the About page.</summary>
    public string UnofficialStatement { get; } =
        "Unofficial fan-made app — not affiliated with or endorsed by Behaviour Interactive. Dead by Daylight is a trademark of Behaviour Interactive Inc.";

    /// <summary>Gets what the app is and is not, in plain words.</summary>
    public IReadOnlyList<string> Disclaimer { get; } =
    [
        "DBD Presence is an unofficial fan project. It is not made, endorsed or supported by Behaviour Interactive or Discord.",
        "It never reads or changes the game's memory or files, injects code, or sends input. " + AppCopy.ProcessCheckOnly,
        "No anti-cheat system has approved this app. Low risk is not the same as no risk; use it at your own discretion.",
        AppCopy.NetworkStatement,
    ];

    /// <summary>Gets the third-party notices for shipped components.</summary>
    public IReadOnlyList<ThirdPartyNotice> ThirdParty { get; } =
    [
        new(".NET runtime and WPF", "MIT", "github.com/dotnet/runtime, github.com/dotnet/wpf"),
        new("CommunityToolkit.Mvvm", "MIT", "github.com/CommunityToolkit/dotnet"),
        new("H.NotifyIcon.Wpf", "MIT", "github.com/HavenDV/H.NotifyIcon"),
        new("Microsoft.Extensions.Hosting", "MIT", "github.com/dotnet/runtime"),
        new("Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File", "Apache-2.0", "github.com/serilog"),
    ];

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (_installedApps is null)
        {
            return;
        }

        var opened = await _installedApps.OpenInstalledAppsAsync().ConfigureAwait(true);
        UninstallError = opened ? null : UninstallFailedText;
    }
}
