// Covers (on Windows CI, real executable): FR-PACK-07 (import: the pictures are matched to characters by file name, the user reviews them and confirms
// before anything is saved), FR-PACK-10 (images opened by the image helper), FR-PACK-12 (imported pictures become the character's own pictures), the
// Characters page import review and result banner with Undo, D16 (no bundled pack), FR-APP-01/04 (both themes, 880 x 600 screenshots). The pack
// management that used to be its own page is the Packs panel of the Characters page (see PacksSyncFlowTests).
//
// The import tests do not drive the Windows file dialog: DBDP_PACK_IMPORT_FILE names the archive, honoured only together with DBDP_DATA_ROOT
// (ARCHITECTURE §8). Everything after the file choice (image helper, review, save, tiles) is the real app.
// PENDING-ENV: these tests have not been run; they run only in the Windows CI job (ci-windows.yml).
using System.IO.Compression;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace DbdPresence.UiTests;

public sealed class PacksFlowTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);
    private readonly ITestOutputHelper _output;

    public PacksFlowTests(ITestOutputHelper output) => _output = output;

    private static string Seeded(string theme) =>
        $$"""{ "schemaVersion": 2, "firstRunCompleted": true, "appearance": { "theme": "{{theme}}" } }""";

    /// <summary>Writes a small zip of real PNG files: one with a character code in its name and one with no name the app can place.</summary>
    private static string WriteFixtureZip(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, size) in new[] { ("K27_TheOnryo.png", 96), ("mystery-picture.png", 64) })
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(TestPng.Gray(size, size));
        }

        return path;
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void ImportingAPack_ShowsTheReview_ThenTheResultAndAnImportedTile(string theme)
    {
        var tag = theme.ToLowerInvariant();
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded(theme));
        var zip = WriteFixtureZip(Path.Combine(root, "inbox"), "onryo-set.zip");
        using var app = AppUnderTest.Launch(root, new Dictionary<string, string> { ["DBDP_PACK_IMPORT_FILE"] = zip });
        app.Navigate("characters");

        app.Find("ImportPack").AsButton().Invoke();

        Assert.True(Retry.WhileNull(() => app.TryFind("ImportReview"), s_wait).Success, "The import review did not appear.");
        Assert.Equal("Import 1 picture", app.Find("ConfirmImport").Name);
        app.CaptureWindow($"characters-{tag}-review");

        // Nothing is imported until the user confirms.
        app.Find("ConfirmImport").AsButton().Invoke();
        var result = Retry.WhileNull(() => app.TryFind("ImportResult"), s_wait).Result;
        Assert.NotNull(result);
        Assert.Contains("Imported 1 pictures", result.Name, StringComparison.Ordinal);
        Assert.Contains("Imported", app.Find("Character-K27").Name, StringComparison.Ordinal);
        var imported = app.CaptureWindow($"characters-{tag}-imported");
        app.MainWindow.Patterns.Transform.Pattern.Resize(880, 600);
        var importedMin = app.CaptureWindow($"characters-{tag}-imported-min");
        _output.WriteLine($"{imported}; {importedMin}");
        app.Quit();

        Assert.Contains("character:the-onryo", File.ReadAllText(app.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void UndoingTheImport_RemovesThePicturesAgain()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        var zip = WriteFixtureZip(Path.Combine(root, "inbox"), "onryo-set.zip");
        using var app = AppUnderTest.Launch(root, new Dictionary<string, string> { ["DBDP_PACK_IMPORT_FILE"] = zip });
        app.Navigate("characters");
        app.Find("ImportPack").AsButton().Invoke();
        Retry.WhileNull(() => app.TryFind("ImportReview"), s_wait);
        app.Find("ConfirmImport").AsButton().Invoke();
        Retry.WhileNull(() => app.TryFind("ImportResult"), s_wait);

        app.Find("UndoImport").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => !app.Find("Character-K27").Name.Contains("Imported", StringComparison.Ordinal), s_wait).Success, "The tile still says Imported after Undo.");
        app.Quit();
    }

    [Fact]
    public void ImportedPicture_SurvivesARestart()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        var zip = WriteFixtureZip(Path.Combine(root, "inbox"), "onryo-set.zip");
        using (var first = AppUnderTest.Launch(root, new Dictionary<string, string> { ["DBDP_PACK_IMPORT_FILE"] = zip }))
        {
            first.Navigate("characters");
            first.Find("ImportPack").AsButton().Invoke();
            Retry.WhileNull(() => first.TryFind("ImportReview"), s_wait);
            first.Find("ConfirmImport").AsButton().Invoke();
            Assert.True(Retry.WhileNull(() => first.TryFind("ImportResult"), s_wait).Success);
            first.Quit();
        }

        using var again = AppUnderTest.Launch(root);
        again.Navigate("characters");

        Assert.Contains("Imported", again.Find("Character-K27").Name, StringComparison.Ordinal);
        again.Quit();
    }

    [Fact]
    public void ARejectedFile_ShowsAClearError_AndChangesNothing()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        var bad = Path.Combine(root, "inbox", "not-a-zip.zip");
        File.WriteAllText(bad, "this is not a zip archive");
        using var app = AppUnderTest.Launch(root, new Dictionary<string, string> { ["DBDP_PACK_IMPORT_FILE"] = bad });
        app.Navigate("characters");

        app.Find("ImportPack").AsButton().Invoke();

        var error = Retry.WhileNull(() => app.TryFind("CharactersError"), s_wait).Result;
        Assert.NotNull(error);
        Assert.Contains("Nothing was imported", error.Name, StringComparison.Ordinal);
        Assert.Null(app.TryFind("ImportReview"));
        app.CaptureWindow("characters-dark-import-error");
        app.Quit();
    }
}
