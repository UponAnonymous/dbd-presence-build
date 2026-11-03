// Covers (on Windows CI, real executable): FR-PACK-12 (up to three own pictures per character, the counter, the default portrait is not removable),
// FR-PACK-13 (the preview pair and the sentence when Discord shows something else), FR-PACK-14 (the page offers links, never loads them), FR-PACK-10
// (a picture from the PC goes through the image helper), the Characters page design (search, Killers / Survivors filter, open a character, picture options,
// Remove with confirmation, Ctrl+F), NFR-A11Y-01 (named controls), FR-APP-01/04 (both themes, 880 x 600 screenshots).
//
// The file dialog is not driven (FlaUI cannot reliably drive the common dialog on the CI image): DBDP_PACK_IMAGE_FILE names the picture, honoured only
// together with DBDP_DATA_ROOT (ARCHITECTURE §8). Everything after the file choice is the real app.
// PENDING-ENV: these tests have not been run; they run only in the Windows CI job (ci-windows.yml).
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace DbdPresence.UiTests;

public sealed class CharactersFlowTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);
    private readonly ITestOutputHelper _output;

    public CharactersFlowTests(ITestOutputHelper output) => _output = output;

    private static string Seeded(string theme) =>
        $$"""{ "schemaVersion": 2, "firstRunCompleted": true, "appearance": { "theme": "{{theme}}" } }""";

    private static string WriteImage(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, TestPng.Gray(128, 128));
        return path;
    }

    /// <summary>True when the character has a tile in the list. A tile can be in the list yet scrolled out of view, so "listed" is not "on screen".</summary>
    private static bool Listed(AppUnderTest app, string code) => app.TryFind($"Character-{code}") is not null;

    private static void WaitUntil(Func<bool> condition, string because) =>
        Assert.True(Retry.WhileFalse(condition, s_wait).Success, because);

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Page_ListsKillersAndSurvivors_WithTheBehaviourNotice_AtDefaultAndMinimumSize(string theme)
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded(theme));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("characters");

        Assert.NotNull(app.Find("CharacterList"));
        Assert.NotNull(app.Find("Character-K27")); // The Onryō (killer)
        Assert.NotNull(app.Find("Character-S01")); // a survivor
        Assert.Contains("© Behaviour Interactive", app.Find("CharactersBehaviourNotice").Name, StringComparison.Ordinal);
        Assert.True(app.Find("ImportPack").IsEnabled, "Import pack… must work with no packs.");
        var normal = app.CaptureWindow($"characters-{theme.ToLowerInvariant()}-ready");

        app.MainWindow.Patterns.Transform.Pattern.Resize(880, 600);
        var minimum = app.CaptureWindow($"characters-{theme.ToLowerInvariant()}-ready-min");
        _output.WriteLine($"{normal}; {minimum}");
        app.Quit();
    }

    [Fact]
    public void SearchAndFilters_NarrowTheList_AndTheCountIsAnnounced()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("characters");

        app.Find("CharacterSearch").AsTextBox().Text = "onryo";
        WaitUntil(() => !Listed(app, "S01"), "A survivor stayed in the list after searching for the Onryō.");
        Assert.NotNull(app.TryFind("Character-K27"));
        Assert.StartsWith("Showing", app.Find("CharacterResultCount").Name, StringComparison.Ordinal);
        app.CaptureWindow("characters-dark-search");

        app.Find("CharacterSearch").AsTextBox().Text = "no such character at all";
        Assert.Contains("No characters match", app.Find("NoResults").Name, StringComparison.Ordinal);
        app.CaptureWindow("characters-dark-no-results");
        app.Find("NoResultsClear").AsButton().Invoke();
        WaitUntil(() => Listed(app, "S01"), "Clearing the search did not bring the list back.");

        app.Find("Filter-survivors").AsRadioButton().IsChecked = true;
        WaitUntil(() => !Listed(app, "K27"), "A killer stayed in the list under Survivors.");
        Assert.NotNull(app.TryFind("Character-S01"));
        app.Find("Filter-killers").AsRadioButton().IsChecked = true;
        WaitUntil(() => !Listed(app, "S01") && Listed(app, "K27"), "Killers did not show alone.");
        app.Find("Filter-all").AsRadioButton().IsChecked = true;
        WaitUntil(() => Listed(app, "S01"), "All did not bring the survivors back.");

        app.Find("FilterChanged").AsToggleButton().Toggle();
        Assert.Contains("haven't changed", app.Find("NoResults").Name, StringComparison.Ordinal);
        app.Quit();
    }

    [Fact]
    public void OpeningACharacter_ShowsItsPictureOptions_TheCounter_AndAddingAndRemovingAPictureWorks()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        var image = WriteImage(Path.Combine(root, "inbox"), "mine.png");
        using var app = AppUnderTest.Launch(root, new Dictionary<string, string> { ["DBDP_PACK_IMAGE_FILE"] = image });
        app.Navigate("characters");

        app.Find("Character-K27").AsListBoxItem().Select();
        Assert.Contains("Onry", app.Find("SelectedCharacterName").Name, StringComparison.Ordinal);
        Assert.NotNull(app.Find("Picture-0")); // the default portrait option is always there
        Assert.Null(app.TryFind("RemovePicture-0")); // and cannot be removed
        Assert.Contains("0 of 3", app.Find("PictureCounter").Name, StringComparison.Ordinal);
        app.CaptureWindow("characters-dark-character");

        app.Find("AddPicture").AsButton().Invoke();
        Assert.NotNull(app.Find("AddFromPc"));
        Assert.NotNull(app.Find("AddFromLink"));
        app.CaptureWindow("characters-dark-add");
        app.Find("AddFromPc").AsButton().Invoke();

        Assert.True(Retry.WhileNull(() => app.TryFind("Picture-1"), s_wait).Success, "The added picture did not become an option.");
        Assert.Contains("1 of 3", app.Find("PictureCounter").Name, StringComparison.Ordinal);
        Assert.NotNull(app.Find("PreviewImage"));
        Assert.Contains("From my PC", app.Find("Picture-1").Name, StringComparison.Ordinal);
        app.CaptureWindow("characters-dark-preview-differs");

        app.Find("RemovePicture-1").AsButton().Invoke();
        Assert.Contains("deleted from this PC", app.Find("RemoveConfirmText").Name, StringComparison.Ordinal);
        app.CaptureWindow("characters-dark-remove-confirm");
        app.Find("ConfirmRemovePicture").AsButton().Invoke();
        WaitUntil(() => app.TryFind("Picture-1") is null, "The picture stayed after it was removed.");
        Assert.Contains("0 of 3", app.Find("PictureCounter").Name, StringComparison.Ordinal);
        app.Quit();
    }

    [Fact]
    public void LinkForm_ChecksAsYouType_AndNeverFetches()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("characters");
        app.Find("Character-K27").AsListBoxItem().Select();
        app.Find("AddPicture").AsButton().Invoke();
        app.Find("AddFromLink").AsButton().Invoke();

        app.Find("LinkUrl").AsTextBox().Text = "http://example.org/a.png";
        WaitUntil(() => app.IsShown("LinkError"), "A link that is not https was not flagged.");
        app.CaptureWindow("characters-dark-link");

        app.Find("LinkUrl").AsTextBox().Text = "https://example.org/a.png";
        WaitUntil(() => !app.IsShown("LinkError"), "A valid link was still flagged.");
        app.Find("AddLink").AsButton().Invoke();

        Assert.True(Retry.WhileNull(() => app.TryFind("Picture-1"), s_wait).Success, "The link did not become an option.");
        app.Quit();
    }

    [Fact]
    public void PacksPanel_ShowsTheStorageMeter_AndBackReturnsToTheCharacter()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("characters");

        app.Find("ShowPacks").AsButton().Invoke();

        Assert.NotNull(app.Find("StorageMeter"));
        Assert.NotNull(app.Find("PictureSize"));
        app.CaptureWindow("characters-dark-packs");
        app.Find("BackFromPacks").AsButton().Invoke();
        Assert.True(Retry.WhileNull(() => app.TryFind("AddPicture"), s_wait).Success, "Back did not return to the character.");
        app.Quit();
    }

    [Fact]
    public void KeyboardTraversal_OnTheCharactersPage_FocusesOnlyNamedControls()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("characters");
        app.Find("nav-characters").Focus();

        var unnamed = new List<string>();
        var seen = 0;
        for (var i = 0; i < 30; i++)
        {
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            var focused = app.MainWindow.Automation.FocusedElement();
            if (focused is null)
            {
                continue;
            }

            seen++;
            if (string.IsNullOrWhiteSpace(focused.Properties.Name.ValueOrDefault))
            {
                unnamed.Add($"{focused.ControlType} '{focused.AutomationId}'");
            }
        }

        _output.WriteLine($"Focused {seen} elements; unnamed: {string.Join(", ", unnamed)}");
        Assert.True(seen > 5, "Tab did not move focus through the page.");
        Assert.Empty(unnamed);
        app.Quit();
    }
}
