// Covers (on Windows CI, real executable, UI-test build): FR-LAB-04 / D38 (the Study page exists only for the owner's own account: absent from the navigation for
// anyone else, present for the owner), the page's states (empty list; a recording whose spot-check is due), the spot-check by keyboard (focus, choose, Save refused
// while rows are unanswered, with its message), NFR-A11Y-01 (every control reachable and named).
// The owner is stood in for by the UI-test build's DBDP_STUDY_OWNER hook (honoured only with DBDP_DATA_ROOT; ARCHITECTURE section 8); the study folder is then
// <data root>/data/study. No labelling is started here, so nothing contacts the service. PENDING-ENV until the first Windows CI run: these tests have not been run.
using DbdPresence.TestSupport;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

namespace DbdPresence.UiTests;

public sealed class StudyPageTests
{
    private const string Recording = "2026-10-24_19-30-12";
    private static readonly Dictionary<string, string> s_owner = new() { ["DBDP_STUDY_OWNER"] = "1" };
    private readonly ITestOutputHelper _output;

    public StudyPageTests(ITestOutputHelper output) => _output = output;

    private static string Seeded() => """{ "schemaVersion": 2, "firstRunCompleted": true }""";

    [Fact]
    public void ForAnyoneButTheOwner_ThereIsNoStudyItemInTheNavigation_AndCtrl6DoesNothing()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded());
        using var app = AppUnderTest.Launch(root);

        Assert.NotNull(app.Find("nav-home"));
        Assert.Null(app.TryFind("nav-study"));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_6);
        Assert.Null(app.TryFind("StudyPage"));
        app.Quit();
    }

    [Fact]
    public void ForTheOwner_TheStudyPageIsInTheNavigation_AndShowsItsEmptyState()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded());
        using var app = AppUnderTest.Launch(root, s_owner);

        Assert.NotNull(app.Find("nav-study"));
        app.Navigate("study");

        // Before the one-time confirmation only the explanation and its button are there: no switch and no "Label now".
        Assert.NotNull(app.Find("StudyConfirmPanel"));
        Assert.Null(app.TryFind("StudyLabelNow"));
        var confirm = app.Find("StudyConfirm");
        confirm.Focus();
        Keyboard.Type(VirtualKeyShort.SPACE);
        Assert.True(Retry.WhileNull(() => app.TryFind("StudyLabelNow"), TimeSpan.FromSeconds(10)).Success);
        Assert.Null(app.TryFind("StudyConfirmPanel"));
        Assert.NotNull(app.Find("StudyLabelAfterPlay"));
        Assert.NotNull(app.Find("StudyPrivacy"));
        Assert.NotNull(app.Find("StudyEmpty"));
        Assert.Null(app.TryFind("StudySaveSpotCheck")); // no spot-check is open
        _output.WriteLine(app.CaptureWindow("study-empty"));
        app.Quit();
    }

    [Fact]
    public void ARecordingWithADueSpotCheck_IsListedInPlainWords_AndTheSpotCheckWorksByKeyboard()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded());
        _ = StudyRecordingFixture.AddReviewerLabelled(Path.Combine(root, "data", "study"), Recording, DateTimeOffset.UtcNow);
        using var app = AppUnderTest.Launch(root, s_owner);

        app.Navigate("study");
        var state = Retry.WhileNull(() => app.TryFind("StudyRowState"), TimeSpan.FromSeconds(20)).Result;
        Assert.NotNull(state);
        Assert.Contains("spot-check ready (about 2 minutes)", state!.Name, StringComparison.Ordinal);
        _output.WriteLine(app.CaptureWindow("study-list"));

        // Open the spot-check with the keyboard.
        var open = app.Find("StudyOpenSpotCheck");
        open.Focus();
        Keyboard.Type(VirtualKeyShort.ENTER);
        Assert.True(Retry.WhileNull(() => app.TryFind("StudySceneChoice"), TimeSpan.FromSeconds(20)).Success);
        Assert.NotNull(app.Find("StudySpotCheckTitle"));
        _output.WriteLine(app.CaptureWindow("study-spot-check"));

        // Choose a scene for the first row with the keyboard, then Save: the rows still open are refused with a message and nothing is saved.
        var first = app.Find("StudySceneChoice");
        first.Focus();
        Keyboard.Type(VirtualKeyShort.DOWN);
        var save = app.Find("StudySaveSpotCheck");
        save.Focus();
        Keyboard.Type(VirtualKeyShort.SPACE);
        var message = Retry.WhileNull(() => app.TryFind("StudySpotCheckMessage"), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(message);
        Assert.Contains("still need", message!.Name, StringComparison.Ordinal);
        Assert.NotNull(app.TryFind("StudySpotCheckTitle")); // still open

        // Not now closes it without saving; the recording is still due.
        app.Find("StudyCloseSpotCheck").Focus();
        Keyboard.Type(VirtualKeyShort.SPACE);
        Assert.True(Retry.WhileTrue(() => app.IsShown("StudySpotCheckTitle"), TimeSpan.FromSeconds(10)).Success);
        Assert.NotNull(app.Find("StudyOpenSpotCheck"));
        app.Quit();
    }
}
