// Covers: FR-DET-22 (killer identity from power prompts, learned on this PC only), D31 (no names stored, no network), SAFETY_AND_PRIVACY (local data only).
using DbdPresence.Core.Detection;
using DbdPresence.Presentation.Detection;

namespace DbdPresence.Presentation.Tests.Detection;

public sealed class PersistentPromptSinkTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbdp-prompts-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = [];

    private string FilePath => Path.Combine(_directory, "learned-prompts.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFileStartsEmptyWithoutWarning()
    {
        var sink = PersistentPromptSink.Open(FilePath, _ => true, _warnings.Add);

        Assert.Equal(0, sink.Store.Count);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void ObservationsAreSavedAndLoadedAgain()
    {
        static bool IsKiller(string id) => id is "the-onryo" or "the-trapper";
        var sink = PersistentPromptSink.Open(FilePath, IsKiller, _warnings.Add);
        sink.PromptsAssociated(new PromptAssociation("the-onryo", ["VILE DARKNESS"], s_now));
        sink.PromptsAssociated(new PromptAssociation("the-onryo", ["VILE DARKNESS"], s_now));
        sink.PromptsAssociated(new PromptAssociation("the-trapper", [], s_now)); // a match as another killer without the text: the negative evidence trust needs
        sink.KillerNotRecognised(new UnmatchedKillerPrompts(["OTHER PROMPT"], s_now));

        var reopened = PersistentPromptSink.Open(FilePath, IsKiller, _warnings.Add);

        Assert.Empty(_warnings);
        Assert.Equal(1, reopened.Store.Count);
        Assert.Equal(1, reopened.Store.UnrecognisedKillerMatches);
        var trusted = Assert.Single(reopened.Store.Trusted());
        Assert.Equal("the-onryo", trusted.KillerId);
        Assert.Equal(2, trusted.Matches);
    }

    [Fact]
    public void OnlyTrustedPromptsAreWrittenToDisk_TextSeenOnceStaysInMemory()
    {
        // Round-2 QA review F7: text held once may be anything on screen, so it never reaches the file.
        static bool IsKiller(string id) => id is "the-onryo" or "the-trapper";
        var sink = PersistentPromptSink.Open(FilePath, IsKiller, _warnings.Add);
        sink.PromptsAssociated(new PromptAssociation("the-onryo", ["VILE DARKNESS", "SEEN ONCE ONLY"], s_now));
        sink.PromptsAssociated(new PromptAssociation("the-onryo", ["VILE DARKNESS"], s_now));
        sink.PromptsAssociated(new PromptAssociation("the-trapper", [], s_now));

        var file = File.ReadAllText(FilePath);

        Assert.Contains("VILE DARKNESS", file, StringComparison.Ordinal);
        Assert.DoesNotContain("SEEN ONCE ONLY", file, StringComparison.Ordinal);
        Assert.Equal(2, sink.Store.Count);
    }

    [Fact]
    public void AFileOverOneMegabyte_IsNotReadAtAll()
    {
        // Round-2 security review L3: the size is checked before the file is read.
        _ = Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, new byte[(1024 * 1024) + 1]);

        var sink = PersistentPromptSink.Open(FilePath, _ => true, _warnings.Add);

        Assert.Equal(0, sink.Store.Count);
        var warning = Assert.Single(_warnings);
        Assert.Contains("larger than 1 MB", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptFileIsLoggedPlainlyAndReplacedByAnEmptyStore()
    {
        _ = Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json at all");

        var sink = PersistentPromptSink.Open(FilePath, _ => true, _warnings.Add);

        Assert.Equal(0, sink.Store.Count);
        var warning = Assert.Single(_warnings);
        Assert.Contains("could not be read", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("not json", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FileNamingAnUnknownKillerIsRefused()
    {
        var first = PersistentPromptSink.Open(FilePath, _ => true, _warnings.Add);
        first.PromptsAssociated(new PromptAssociation("someone-else", ["VILE DARKNESS"], s_now));

        var second = PersistentPromptSink.Open(FilePath, id => id == "the-onryo", _warnings.Add);

        Assert.Equal(0, second.Store.Count);
        _ = Assert.Single(_warnings);
    }

    [Fact]
    public void UnrecognisedKillerRaisesTheSeamOnlyForSubscribers()
    {
        var sink = PersistentPromptSink.Open(FilePath, _ => true, _warnings.Add);
        sink.KillerNotRecognised(new UnmatchedKillerPrompts(["NOBODY SUBSCRIBED"], s_now));
        var seen = new List<UnmatchedKillerPrompts>();
        sink.Reported += (_, report) => seen.Add(report);

        sink.KillerNotRecognised(new UnmatchedKillerPrompts(["SOMEONE SUBSCRIBED"], s_now));

        Assert.Equal(["SOMEONE SUBSCRIBED"], Assert.Single(seen).Prompts);
    }

    [Fact]
    public void UnwritableLocationIsLoggedAndTheStoreStaysInMemory()
    {
        _ = Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "blocked");
        _ = Directory.CreateDirectory(blocked);
        var sink = PersistentPromptSink.Open(Path.Combine(blocked, "x.json"), _ => true, _warnings.Add);
        Directory.Delete(blocked);
        File.WriteAllText(blocked, "a file where the folder should be");

        sink.PromptsAssociated(new PromptAssociation("the-onryo", ["VILE DARKNESS"], s_now));

        _ = Assert.Single(_warnings);
        Assert.Equal(1, sink.Store.Count);
    }
}
