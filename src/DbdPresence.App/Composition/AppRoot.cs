using System.IO;
using DbdPresence.App.Hosting;
using DbdPresence.App.Theming;
using DbdPresence.App.Tray;
using DbdPresence.App.Updates;
using DbdPresence.Configuration;
using DbdPresence.Core.Diagnostics;
using DbdPresence.Core.Game;
using DbdPresence.Core.Online;
using DbdPresence.Core.Roster;
using DbdPresence.Discord;
using DbdPresence.Online;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using DbdPresence.Packs.Roster;
using DbdPresence.Packs.Signing;
using DbdPresence.Platform.Windows.Accounts;
using DbdPresence.Platform.Windows.Capture;
using DbdPresence.Platform.Windows.Images;
using DbdPresence.Platform.Windows.Ocr;
using DbdPresence.Platform.Windows.Processes;
using DbdPresence.Platform.Windows.Startup;
using DbdPresence.Presentation.Characters;
using DbdPresence.Presentation.Detection;
using DbdPresence.Presentation.Labelling;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.ViewModels;
using DbdPresence.Study;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Velopack.Locators;

namespace DbdPresence.App.Composition;

/// <summary>
/// Composes the app (ARCHITECTURE §6): the Generic Host runs the background services; the WPF objects are
/// created on the UI thread. Shutdown order: stop process monitoring, clear Discord (500 ms budget), write
/// settings, flush logs — all within 3 s.
/// </summary>
internal sealed class AppRoot : IDisposable
{
#if DBDP_UITEST
    private const string PackSyncDirectoryVariable = "DBDP_PACK_SYNC_DIR";
#endif
    private static readonly TimeSpan s_shutdownBudget = TimeSpan.FromSeconds(3);
    private readonly App _app;
    private readonly SettingsPaths _paths;
    private readonly SingleInstance _instance;
    private readonly string _version;
    private readonly List<IDisposable> _owned = [];
    private IHost? _host;
    private MainWindow? _window;
    private TrayController? _tray;
    private bool _quitting;

    public AppRoot(App app, SettingsPaths paths, SingleInstance instance, string version)
    {
        _app = app;
        _paths = paths;
        _instance = instance;
        _version = version;
    }

    public int Run()
    {
        _app.DispatcherUnhandledException += OnUnhandledUiException;
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("Unobserved background task failure: {Detail}", LogRedaction.Current.Describe(e.Exception));
        var time = TimeProvider.System;
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DBD-Presence.exe");

        // Program.Main ran VelopackApp first, so the locator describes this install. "Start with Windows" points at the launcher at the install
        // root, which survives updates, never at the versioned current\ folder (ADR-0006 open finding, fixed with the updater in V2-S6).
        var locator = VelopackLocator.Current;
        var startupExecutable = InstallLayout.StartupExecutable(locator, executable);
        var settings = Own(new SettingsService(new SettingsStore(new PhysicalSettingsFileSystem(), _paths), time));
        var theme = Own(new ThemeManager(_app.Resources, settings));
        var publisher = new PresencePublisher(new NamedPipeDiscordTransportFactory(), time);
        var monitor = new GameMonitor(new WindowsProcessSnapshotSource(), time, new GameProcessMatcher(settings.Current.Lifecycle.ExtraProcessNames));
        var bundledCatalog = new BundledApplicationCatalog();

        // Online layer (FR-ONL-*): sign-in is optional and presence never depends on it (D8). A redirected data root (UI tests) gets its own
        // device key so tests never touch the real one. Windows behaviour of the DPAPI and CNG stores is PENDING-ENV until the Windows CI run.
        var onlineDirectory = Path.Combine(_paths.DataDirectory, "online");
        var testRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootVariable) is { Length: > 0 };

        // Untrusted images are opened only by the image helper process (FR-PACK-10): imports, overrides and downloaded pack files.
        var imageLoader = new SafeImageLoader(AppContext.BaseDirectory, Path.Combine(_paths.DataDirectory, "image-work"), SafeImageLoader.DefaultTimeout);

        var packSyncTestDirectory = PackSyncTestDirectory(testRoot);
        // The embedded vocabulary is the append-only baseline of the signed roster (FR-ROS-04); the import roster is built from it.
        var embeddedVocabulary = Vocabulary.LoadEmbedded();
        var roster = VocabularyRoster.From(embeddedVocabulary);
        var online = OnlineServices.Create(new OnlineOptions(
            onlineDirectory,
            bundledCatalog.Applications[0].ApplicationId,
            new CngDeviceKeyStore(testRoot ? CngDeviceKeyStore.DefaultKeyName + ".test" : CngDeviceKeyStore.DefaultKeyName),
            new DpapiSecretStore(Path.Combine(onlineDirectory, "secrets")),
            new DiscordBrowserLauncher(),
            time,
            new SerilogOnlineLog(),
            imageLoader,
            Path.Combine(_paths.DataDirectory, "packs", "synced"),
            packSyncTestDirectory,
            AdditionalKeys(testRoot),
            roster));
        _owned.Add(online.Account);
        _owned.Add(online.Packs);
        _owned.Add(online.Uploads);

        // Signed updates (FR-ONL-10): Velopack over the release-key-verified feed; a copy that was not installed (development, UI tests) never checks.
        // The Microsoft Store package never updates itself (Store policy): there is no AppUpdater, nothing is checked, and the page says so.
        var packaged = PackageIdentity.IsPackaged;
        var updater = packaged ? null : new AppUpdater(online.Updates, InstallLayout.CurrentVersion(locator, _version), locator, time, new SerilogOnlineLog(), () => _app.Dispatcher.InvokeAsync(Quit));
        IAppUpdates appUpdates = updater is null ? new StoreManagedUpdates() : updater;
        var catalog = new RemoteApplicationCatalog(bundledCatalog, online.Config);

        // Image packs (FR-PACK-*): imports and overrides (no art is bundled, D16) under the app data folder, plus the verified synced packs
        // (FR-ONL-07). The library follows the sync service before any view model exists, so every listener sees the new packs.
        // Imported file names are matched to characters by code, then by name, against the embedded vocabulary (FR-PACK-07, PLAN_V2_S5B §2.7).
        var packLibrary = new PackLibrary(new PhysicalLocalPackStorage(), Path.Combine(_paths.DataDirectory, "packs"), imageLoader, roster);
        packLibrary.SetSynced(online.Packs.Synced);
        online.Packs.Changed += (_, _) => packLibrary.SetSynced(online.Packs.Synced);
        // The user's uploads (FR-ONL-09): an own image that was uploaded is shown in Discord at its unlisted address.
        packLibrary.SetUploads(online.Uploads.Current.UrlBySha256, online.Uploads.UploadHost);
        online.Uploads.Changed += (_, _) => packLibrary.SetUploads(online.Uploads.Current.UrlBySha256, online.Uploads.UploadHost);

        // The Characters page and the status artwork resolve a character's picture through one resolver, so the preview shows exactly what Discord gets
        // (D29). Own pictures are made smaller only by the image helper (FR-PACK-10): the SafeImageLoader is the picture fitter.
        var pictureStore = new PictureStore(new PhysicalLocalPackStorage(), Path.Combine(_paths.DataDirectory, "packs"), imageLoader, time);
        var pictureEnvironment = Own(new OnlinePictureEnvironment(online.Account, online.Uploads, packLibrary));
        var pictureResolver = new CharacterPictureResolver(packLibrary, pictureStore, pictureEnvironment, roster);
        var packArtwork = new PackArtworkProvider(packLibrary, pictureResolver);
        if (ImportedPackMigrationRunner.Run(settings, packLibrary, pictureStore) is { } migrated)
        {
            Log.Information("Moved {Count} imported-pack pictures into the picture model ({OverLimit} left out)", migrated.Migrated, migrated.OverLimit);
        }

        // Screen reading (FR-DET-25): off by default; it runs only when the user turned it on and the signed configuration allows capture. Every detector
        // stays Disabled for Discord (no setting asks for a tier, and the initial-guess HUD profile is refused above Disabled), so nothing it reads reaches
        // the activity. The game-version reader is not built, so no game version is passed (PENDING-ENV). Windows capture and text recognition against
        // the real game are PENDING-ENV.
        var vocabulary = MergedVocabulary(embeddedVocabulary, online.Roster.Current);
        var hudVocabulary = HudVocabulary.Create(vocabulary);
        // Power prompts learned on this PC only (FR-DET-22); the owner's account reports unknown character names through online.UnknownCharacters.
        var promptSink = PersistentPromptSink.Open(
            Path.Combine(_paths.DataDirectory, "learned-prompts.json"), hudVocabulary.IsKillerId, message => Log.Warning("{Message}", message));
        // The own-row state icons the owner's captures taught this PC (FR-AUTO-03); a missing or unusable file means no pictures, and the detector then reads nothing.
        var statusTemplates = LoadStatusTemplates(Path.Combine(_paths.DataDirectory, "status-templates.json"));
        var detection = new DetectionLoop(new DetectionLoopParts(
            settings,
            online.Config,
            monitor,
            processId => new WgcWindowCaptureSource(processId, time, requestBorderless: true),
            new WindowsEnglishOcr(),
            HudProfile.Catalogue,
            hudVocabulary,
            time,
            vocabulary,
            PromptSink: promptSink,
            StatusTemplates: statusTemplates,
            UnknownCharacters: online.UnknownCharacters,
            OtherPersonName: () => online.Account.Current.DisplayName));
        var coordinator = new PresenceCoordinator(settings, catalog, monitor, new DiscordPresenceSink(publisher), time, packArtwork, online.Account, detection);
        // A redirected data root (UI tests) gets its own startup entry so it can never remove the real one.
        var autostart = Environment.GetEnvironmentVariable(AppPaths.DataRootVariable) is { Length: > 0 }
            ? new AutostartRegistry("DBD Presence (test data root)")
            : new AutostartRegistry();

        // The Microsoft Store package starts with Windows through its manifest startup task; the HKCU Run key is redirected there and does not work.
        var packagedStartup = packaged ? new StartupTaskAutostart() : null;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<IHostLifetime, AppHostLifetime>();
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton<ISettingsService>(settings);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(monitor);
        builder.Services.AddSingleton(publisher);
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(detection);
        builder.Services.AddSingleton(autostart);
        builder.Services.AddSingleton<IAccountService>(online.Account);
        builder.Services.AddSingleton<IUploadService>(online.Uploads);
        builder.Services.AddSingleton(online);
        builder.Services.AddSingleton<IUnknownCharacterSink>(online.UnknownCharacters);
        builder.Services.AddSingleton(promptSink);

        // Started in this order and stopped in reverse: monitor first, then presence (clears Discord), then settings.
        builder.Services.AddHostedService(sp => new SettingsLifetimeService(settings, autostart, startupExecutable, packaged, sp.GetRequiredService<ILogger<SettingsLifetimeService>>()));
        builder.Services.AddHostedService<PresenceService>();
        builder.Services.AddHostedService<DetectionService>(); // after presence: it stops first, so capture is released before Discord is cleared
        builder.Services.AddHostedService<AccountRefreshService>();
        builder.Services.AddHostedService<PackSyncHostedService>();
        builder.Services.AddHostedService(_ => new UpdateHostedService(updater));
        builder.Services.AddHostedService<GameMonitorService>();
        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();

        var dispatcher = new WpfDispatcher(_app.Dispatcher);
        var info = new AppInfo(_version, CommitOf(_version), IsSigned: packaged, _paths.SettingsDirectory, _paths.DataDirectory, Path.Combine(AppContext.BaseDirectory, "privacy-notice.md"));
        var uploads = Own(new UploadsViewModel(online.Uploads, online.Account, settings, packLibrary, dispatcher));
        var account = Own(new AccountViewModel(online.Account, dispatcher, uploads: uploads));
        var controls = Own(new PresenceControlsViewModel(coordinator, settings, dispatcher, time));
        var studyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DBD Presence Study");
        var ownerState = new OwnerAccountState();
        var pinnedOwner = false;
#if DBDP_UITEST
        // UI-test build only, and only with a redirected data root: the account counts as the owner's own and the study folder is inside the test root.
        if (testRoot && Environment.GetEnvironmentVariable("DBDP_STUDY_OWNER") == "1")
        {
            pinnedOwner = true;
            studyFolder = Path.Combine(_paths.DataDirectory, "study");
            ownerState.Note(true);
        }
#endif
        // The Study page exists only for the owner's own game account: the flag comes from the normal account refresh (no call of its own) and is forgotten
        // when online features stop being allowed (signed out, no licence).
        online.Uploads.RosterReporterObserved += (_, isOwner) =>
        {
            if (!pinnedOwner)
            {
                ownerState.Note(isOwner);
            }
        };
        online.Account.Changed += (_, _) =>
        {
            if (!pinnedOwner && !online.Account.Current.OnlineFeaturesAllowed)
            {
                ownerState.Forget();
            }
        };
        var processes = new WindowsProcessSnapshotSource();
        var workbench = new StudyWorkbench(
            studyFolder,
            vocabulary,
            new RelayLabellerTransport(online.StudyLabelling),
            time,
            Path.Combine(_paths.DataDirectory, "study-runs.json"),
            message => Log.Warning("Study: {Message}", message));

        // The in-app study recorder (FR-LAB-06) saves to the same folder the labelling flow reads (studyFolder), through the same Windows capture adapter as the detection loop.
        // Windows behaviour is PENDING-ENV. Only the owner's Study page can start it.
        var studyRecorder = new StudyRecorder(
            studyFolder,
            time,
            () => monitor.Current,
            processId => new WgcStudyFrameSource(new WgcWindowCaptureSource(processId, time, requestBorderless: true)),
            _version,
            () => ownerState.IsOwner == true,
            warn: message => Log.Warning("Study recorder: {Message}", message));
        var study = Own(new StudyViewModel(workbench, ownerState, settings, monitor, dispatcher, time, () => StudyRecorderRunning(processes), studyRecorder));
        study.SpotCheckReady += (_, _) => _tray?.NotifySpotCheckReady();
        var detectionNames = new DetectionNames(vocabulary);
        var detectionCard = Own(new DetectionViewModel(settings, dispatcher, detection, detectionNames, new StudyFolderProgress(studyFolder, time)));
        var screenReading = Own(new ScreenReadingViewModel(settings, dispatcher, detection));
        var home = Own(new HomeViewModel(controls, coordinator, settings, dispatcher, time, detectionCard, screenReading, account));
        var appStartup = Own(new AppStartupViewModel(controls, settings, dispatcher, packagedStartup is not null ? packagedStartup : new RegistryAutostart(autostart, startupExecutable)));
        var privacy = Own(new PrivacyViewModel(settings, dispatcher, info));
        var studio = Own(new PresenceStudioViewModel(settings, catalog, dispatcher, time, packArtwork, coordinator));
        var status = Own(new DiscordStatusViewModel(controls, coordinator, settings, dispatcher, time, privacy, appStartup, studio, screenReading));
        var appearance = Own(new AppearanceViewModel(settings, dispatcher, theme));
        var updates = Own(new UpdatesViewModel(appUpdates, dispatcher));
        var settingsPage = new SettingsViewModel(controls, screenReading, appearance, appStartup, new AboutViewModel(info, new InstalledAppsAdapter()), privacy, detectionCard, updates);

        var characters = Own(new CharactersViewModel(
            settings, dispatcher, roster, packLibrary, pictureStore, new WindowsFilePicker(), pictureEnvironment, online.Uploads.StartPictureQueue(pictureStore, () => settings.Current.PictureRules), pictureResolver, online.Packs));
        // A roster that merges also goes to the live detection loop, which starts using the new characters between frames, without a restart (D35).
        online.Roster.Attach(new DetectionRosterConsumer(new RosterConsumer(embeddedVocabulary, characters, dispatcher), embeddedVocabulary, detection, detectionNames));
        var shell = Own(new ShellViewModel(
            controls,
            home,
            status,
            characters,
            account,
            settingsPage,
            settings.LoadResult.Notice,
            online.Config,
            settings,
            dispatcher,
            _version,
            study));
        var firstRun = Own(new FirstRunViewModel(settings, controls, account, screenReading, home));
        var unlock = Own(new UnlockViewModel(account, privacy));

        _window = new MainWindow(shell, firstRun, unlock, settings, theme, HideToTray, Quit);
        _tray = Own(new TrayController(controls, settings, ShowWindow, Quit, detection, studyRecorder));
        home.IsActive = false; // the window may start hidden in the tray; it turns the clock on when shown
        _instance.ActivationRequested += (_, _) => _app.Dispatcher.InvokeAsync(ShowWindow);
        _instance.ListenerFailed += (_, e) => Log.Warning("Second-launch activation is unavailable this session: {ExceptionType}", e.GetType().Name);
        _app.SessionEnding += (_, _) => Quit();

        // Start in the tray only when asked to and setup is done; otherwise the window is the way in.
        if (!settings.Current.Lifecycle.StartMinimised || firstRun.IsOpen || unlock.IsShown)
        {
            ShowWindow();
        }

        var exitCode = _app.Run();
        Stop();
        return exitCode;
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        _owned.Clear();
        _host?.Dispose();
    }

    /// <summary>The extra trusted keys: always none, except in the UI-test build with a redirected data root (D32; that build is never released).</summary>
    private static IReadOnlyDictionary<string, TrustedKey>? AdditionalKeys(bool testRoot)
    {
#if DBDP_UITEST
        return testRoot ? UiTestTrust.Keys : null;
#else
        _ = testRoot;
        return null;
#endif
    }

    /// <summary>The pack-sync test folder: always none, except in the UI-test build with a redirected data root (ARCHITECTURE §8, TM-10; that build is never released).</summary>
    private static string? PackSyncTestDirectory(bool testRoot)
    {
#if DBDP_UITEST
        return testRoot && Environment.GetEnvironmentVariable(PackSyncDirectoryVariable) is { Length: > 0 } syncDirectory ? syncDirectory : null;
#else
        _ = testRoot;
        return null;
#endif
    }

    /// <summary>
    /// Tells whether the study recorder tool is running, from the process list the app already reads (names only; no handle is opened). When the list cannot be
    /// read the answer is "running", so the automatic run waits instead of working on a recording that may still be written.
    /// </summary>
    private static bool StudyRecorderRunning(WindowsProcessSnapshotSource processes)
    {
        try
        {
            return processes.Snapshot().Any(p => string.Equals(p.Name, "DbdPresence.StudyCapture", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Log.Warning("The process list could not be read to look for the study recorder: {ExceptionType}", ex.GetType().Name);
            return true;
        }
    }

    private static OwnStatusTemplates? LoadStatusTemplates(string path) => StatusTemplateFile.Load(path, message => Log.Warning("{Message}", message));

    /// <summary>The detection vocabulary: the embedded one plus the cached signed roster, or the embedded one alone when the roster does not merge.</summary>
    private static Vocabulary MergedVocabulary(Vocabulary embedded, RosterDocument? roster)
    {
        if (roster is null)
        {
            return embedded;
        }

        try
        {
            return RosterVocabulary.Merge(embedded, roster);
        }
        catch (FormatException ex)
        {
            Log.Warning("The cached character list does not merge with the built-in one ({ExceptionType}); the built-in list is used", ex.GetType().Name);
            return embedded;
        }
    }

    private static string CommitOf(string informationalVersion)
    {
        // SourceLink-style versions end with "+<commit sha>".
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && informationalVersion.Length > plus + 7 ? informationalVersion[(plus + 1)..(plus + 8)] : "unknown";
    }

    private T Own<T>(T disposable)
        where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private void OnUnhandledUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // Logged locally with the user's name, profile path and computer name masked (NFR-PRIV-02); the user sees what happened and where details are, never a stack trace (UI_SPEC §5).
        Log.Fatal("Unhandled error on the UI thread: {Detail}", LogRedaction.Current.Describe(e.Exception));
        e.Handled = true;
        _ = System.Windows.MessageBox.Show(
            $"DBD Presence ran into an unexpected problem and will close. Your Discord status is cleared on exit.\n\nDetails were written to the log in {_paths.LogDirectory}.",
            "DBD Presence",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        Quit();
    }

    private void ShowWindow() => _window?.ShowAndActivate();

    private void HideToTray() => _tray?.ExplainCloseToTray();

    private void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            _window?.CloseForQuit();
        }
        finally
        {
            // Whatever happens to the window, the app must exit so the shutdown path clears Discord and saves.
            _app.Shutdown();
        }
    }

    private void Stop()
    {
        if (_host is null)
        {
            return;
        }

        using var budget = new CancellationTokenSource(s_shutdownBudget);
        try
        {
            _host.StopAsync(budget.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Shutdown exceeded {Budget} s; exiting anyway", s_shutdownBudget.TotalSeconds);
        }
#pragma warning disable CA1031 // Process exit boundary: any failure while stopping is logged; the process is ending regardless.
        catch (Exception ex)
        {
            Log.Error("A background service failed while stopping: {Detail}", LogRedaction.Current.Describe(ex));
        }
#pragma warning restore CA1031
    }

    /// <summary>The window, not a console, controls the app's lifetime.</summary>
    private sealed class AppHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
