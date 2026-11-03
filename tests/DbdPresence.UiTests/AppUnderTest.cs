using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace DbdPresence.UiTests;

/// <summary>
/// Launches the built DBD-Presence.exe with its own data folder (DBDP_DATA_ROOT, so tests never touch a real
/// profile) and exposes its main window for UIA-driven tests.
/// </summary>
internal sealed partial class AppUnderTest : IDisposable
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(15);
    private readonly Application _application;
    private readonly UIA3Automation _automation;
    private bool _exited;

    private AppUnderTest(Application application, UIA3Automation automation, Window mainWindow, string dataRoot)
    {
        _application = application;
        _automation = automation;
        MainWindow = mainWindow;
        DataRoot = dataRoot;
    }

    public Window MainWindow { get; }

    /// <summary>Gets the data folder this instance uses (settings under settings/, logs under data/).</summary>
    public string DataRoot { get; }

    /// <summary>Gets the settings file path for this instance.</summary>
    public string SettingsFile => Path.Combine(DataRoot, "settings", "settings.json");

    /// <summary>Directory where screenshots are written (CI uploads it as an artifact).</summary>
    public static string ScreenshotDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("DBDP_SCREENSHOT_DIR");
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(RepositoryRoot, "TestResults", "screenshots");
            }

            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbdPresence.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    /// <summary>Creates a fresh, empty data folder.</summary>
    public static string NewDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbdp-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Writes a settings document before launch (fields not given keep their defaults).</summary>
    public static void Seed(string dataRoot, string json)
    {
        var dir = Path.Combine(dataRoot, "settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
    }

    /// <summary>
    /// Launches the UI-test build of the app, unlocked (D32): a licence signed with the test key is written first and the app verifies it like any
    /// other. Use <see cref="LaunchLocked"/> for the normal build and its lock screen.
    /// </summary>
    public static AppUnderTest Launch(string? dataRoot = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        dataRoot ??= NewDataRoot();
        UiTestUnlock.Write(dataRoot);
        return Start(dataRoot, environment, "UiTest");
    }

    /// <summary>Launches the normal (Release or Debug) build of the app with nothing stored, so it shows the lock screen.</summary>
    public static AppUnderTest LaunchLocked(string? dataRoot = null, IReadOnlyDictionary<string, string>? environment = null) =>
        Start(dataRoot ?? NewDataRoot(), environment, typeof(AppUnderTest).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "BuildConfiguration").Value ?? "Debug");

    private static AppUnderTest Start(string dataRoot, IReadOnlyDictionary<string, string>? environment, string buildFolder)
    {
        var binDir = Path.Combine(RepositoryRoot, "src", "DbdPresence.App", "bin", buildFolder);
        var exe = Directory.GetFiles(binDir, "DBD-Presence.exe", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.Environment["DBDP_DATA_ROOT"] = dataRoot;
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        var application = Application.Launch(start);
        var automation = new UIA3Automation();
        var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30));
        if (window is null)
        {
            automation.Dispose();
            application.Kill();
            application.Dispose();
            throw new InvalidOperationException("Main window did not appear within 30 s.");
        }

        return new AppUnderTest(application, automation, window, dataRoot);
    }

    /// <summary>Finds an element by automation id, waiting for it to appear.</summary>
    public AutomationElement Find(string automationId) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), s_wait).Result
        ?? throw new InvalidOperationException($"Element '{automationId}' not found.");

    /// <summary>Finds an element by its accessible name, waiting for it to appear.</summary>
    public AutomationElement FindByName(string name) =>
        Retry.WhileNull(() => MainWindow.FindFirstDescendant(cf => cf.ByName(name)), s_wait).Result
        ?? throw new InvalidOperationException($"Element named '{name}' not found.");

    /// <summary>
    /// Clicks an element after bringing it on screen. A long page scrolls, and a click on an element below the fold lands on
    /// whatever is drawn at its old position, so the element is scrolled into view (and focused, which makes WPF bring it into
    /// view) before the click.
    /// </summary>
    public void ClickInView(string automationId)
    {
        var element = Find(automationId);
        element.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
        element.Focus();
        if (!Retry.WhileTrue(() => element.IsOffscreen, s_wait).Success)
        {
            throw new InvalidOperationException($"Element '{automationId}' stayed off screen.");
        }

        element.Click();
    }

    /// <summary>Returns the element if it is present now (no waiting).</summary>
    public AutomationElement? TryFind(string automationId) => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    /// <summary>
    /// Returns true when the element is in the tree and on screen. WPF keeps the automation peer of an element whose parent was
    /// collapsed after it had been shown, so "gone" means absent or off screen.
    /// </summary>
    public bool IsShown(string automationId) => TryFind(automationId) is { IsOffscreen: false };

    /// <summary>Opens a page through the navigation rail.</summary>
    public void Navigate(string key)
    {
        Find($"nav-{key}").AsListBoxItem().Select();
        Retry.WhileFalse(() => TryFind(PageId(key)) is not null, s_wait);
    }

    /// <summary>Skips first-run setup if it is showing.</summary>
    public void SkipFirstRun()
    {
        if (TryFind("FirstRunSkip") is { } skip && !skip.IsOffscreen)
        {
            skip.AsButton().Invoke();
            Retry.WhileTrue(() => TryFind("FirstRunSkip") is { IsOffscreen: false }, s_wait);
        }
    }

    /// <summary>Saves a PNG of the main window and returns its path.</summary>
    public string CaptureWindow(string name)
    {
        var path = Path.Combine(ScreenshotDirectory, $"{name}.png");
        Capture.Element(MainWindow).ToFile(path);
        return path;
    }

    /// <summary>Quits through Settings, About (the graceful path: clears Discord, writes settings) and waits for exit.</summary>
    public void Quit()
    {
        Navigate("settings");
        Find("QuitApp").AsButton().Invoke();
        _exited = WaitForExit(TimeSpan.FromSeconds(15));
        if (!_exited)
        {
            throw new InvalidOperationException("The app did not exit within 15 s of Quit.");
        }
    }

    /// <summary>Closes the main window (title-bar X) and waits for the process to exit.</summary>
    public void CloseWindowExpectingExit()
    {
        MainWindow.Patterns.Window.Pattern.Close();
        _exited = WaitForExit(TimeSpan.FromSeconds(15));
        if (!_exited)
        {
            throw new InvalidOperationException("The app did not exit within 15 s of closing its window.");
        }
    }

    /// <summary>Describes the display the test ran on (resolution and system DPI), for evidence records.</summary>
    public static string DescribeDisplay()
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        var dpi = GetDpiForSystem();
        return $"primary={width}x{height}; systemDpi={dpi} ({dpi * 100 / 96}% scaling)";
    }

    public void Dispose()
    {
        if (!_exited && !_application.HasExited)
        {
            _application.Kill();
            _ = WaitForExit(TimeSpan.FromSeconds(10));
        }

        _application.Dispose();
        _automation.Dispose();
        CopyLogs();
    }

    /// <summary>The folder that receives each test's app log (next to the screenshots, so it is in the windows-evidence artifact).</summary>
    public static string LogsDirectory => Path.Combine(Directory.GetParent(ScreenshotDirectory)!.FullName, "ui-logs");

    /// <summary>
    /// Copies the app's log folder (data/logs under the data root) to TestResults/ui-logs/&lt;test&gt;, so a crash in CI carries its log
    /// (Windows CI run 86 showed only the crash dialog). Runs after the app exited, when Serilog has released the file.
    /// </summary>
    private void CopyLogs()
    {
        var source = Path.Combine(DataRoot, "data", "logs");
        if (!Directory.Exists(source))
        {
            return;
        }

        var test = TestContext.Current.Test?.TestDisplayName ?? Path.GetFileName(DataRoot);
        var name = string.Concat(test.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '(' or ')' or '"' or ',' ? '_' : c));
        var target = Path.Combine(LogsDirectory, name.Length > 120 ? name[..120] : name);
        try
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The test result itself does not depend on the copy; say why the log is missing instead of failing the test.
            TestContext.Current.AddWarning($"The app log could not be copied to {target}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string PageId(string key) => key switch
    {
        "home" => "HomePage",
        "status" => "DiscordStatusPage",
        "characters" => "CharactersPage",
        "account" => "AccountPage",
        "settings" => "SettingsPage",
        "study" => "StudyPage",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown page."),
    };

    private bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(_application.ProcessId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
