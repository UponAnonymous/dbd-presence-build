// Covers: FR-AUTO-03 (the own-row state pictures come from a file on this PC; a missing or unusable file means none), NFR-PRIV-02 (the warning names no path),
// security review L3 (the size is checked before the file is read). BoundedFile is the shared reader.
using DbdPresence.Packs.Local;
using DbdPresence.Presentation.Detection;

namespace DbdPresence.Presentation.Tests.Detection;

public sealed class StatusTemplateFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbdp-status-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = [];

    private string FilePath => Path.Combine(_directory, "status-templates.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingFile_MeansNoPictures_WithoutAWarning()
    {
        Assert.Null(StatusTemplateFile.Load(FilePath, _warnings.Add));
        Assert.Empty(_warnings);
    }

    [Fact]
    public void AFileOverOneMegabyte_IsRefusedWithoutBeingRead()
    {
        _ = Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, new byte[(int)BoundedFile.TextLimitBytes + 1]);

        Assert.Null(StatusTemplateFile.Load(FilePath, _warnings.Add));

        var warning = Assert.Single(_warnings);
        Assert.Contains("larger than 1 MB", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotTheFormat_IsRefusedWithAPlainSentence()
    {
        _ = Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Null(StatusTemplateFile.Load(FilePath, _warnings.Add));

        var warning = Assert.Single(_warnings);
        Assert.StartsWith("Status icon pictures were not loaded", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedFile_ReadsAtTheLimit_AndRefusesOneByteMore()
    {
        _ = Directory.CreateDirectory(_directory);
        File.WriteAllBytes(FilePath, new byte[10]);

        Assert.Equal(10, BoundedFile.ReadAllBytes(FilePath, 10)!.Length);
        Assert.Null(BoundedFile.ReadAllBytes(FilePath, 9));
        Assert.Null(BoundedFile.ReadAllText(FilePath, 9));
        _ = Assert.Throws<FileNotFoundException>(() => BoundedFile.ReadAllBytes(Path.Combine(_directory, "missing.json"), 10));
    }
}
