namespace DbdPresence.UiTests;

/// <summary>
/// Stage A spike A8 (QUALITY_GATES): the real app launches on Windows CI, its window can be found via
/// UI Automation, and a screenshot plus display description are produced as evidence. Since D32 it starts the UI-test build unlocked.
/// </summary>
public sealed class ShellSmokeTests
{
    private readonly ITestOutputHelper _output;

    public ShellSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void App_Launches_MainWindowIsFoundAndCaptured()
    {
        using var app = AppUnderTest.Launch();

        Assert.Equal("DBD Presence", app.MainWindow.Title);
        Assert.NotNull(app.Find("ShellTitle"));

        var screenshot = app.CaptureWindow("a8-shell");
        var display = AppUnderTest.DescribeDisplay();
        File.WriteAllText(Path.Combine(AppUnderTest.ScreenshotDirectory, "a8-display.txt"), display);
        _output.WriteLine($"Screenshot: {screenshot}; display: {display}");

        Assert.True(new FileInfo(screenshot).Length > 0);
    }

    // UI_SPEC Home headline: a visible headline button always has a label (regression: an empty gold button showed in every state).
    [Fact]
    public void Home_HeadlineButtons_AreNeverVisibleWithoutText()
    {
        using var app = AppUnderTest.Launch();
        app.SkipFirstRun();
        app.Navigate("home");
        Assert.NotNull(app.Find("HeroTitle"));

        foreach (var id in new[] { "HeroResume", "HeroAction" })
        {
            if (!app.IsShown(id))
            {
                continue;
            }

            var label = app.TryFind(id)!.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text));
            Assert.False(string.IsNullOrWhiteSpace(label?.Name), $"The visible headline button '{id}' has no text.");
        }
    }
}
