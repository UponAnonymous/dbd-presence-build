// Covers: PLAN_V2 D25 (study recordings delete themselves 30 days after they were made), QUALITY_GATES C6 data collection,
// SAFETY_AND_PRIVACY section 3.2. The 30-day clean-up deletes only the study tool's own session folders, never follows links,
// stays inside the output root, reports what it deleted and what it could not, and runs on an injected clock.
using System.Globalization;
using System.Text.Json;
using DbdPresence.StudyCapture;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Core.Tests.Study;

public sealed class StudyRetentionTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 11, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbd-study-retention-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(s_now);

    public StudyRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ExpiredSessionIsDeletedAndReported_UnexpiredIsKept()
    {
        var old = MakeSession(s_now.AddDays(-31), frames: 3);
        var recent = MakeSession(s_now.AddDays(-29), frames: 2);

        var report = SessionRetention.Sweep(_root, _time);

        Assert.Equal([Path.GetFileName(old)], report.Deleted);
        Assert.Empty(report.Problems);
        Assert.Equal(1, report.Kept);
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(recent, "metadata.json")));
        Assert.Equal(2, Directory.GetFiles(recent, "frame-*.png").Length);
    }

    [Fact]
    public void ABoundary_IsKeptJustBeforeThirtyDaysAndDeletedAtThirtyDays()
    {
        var session = MakeSession(s_now.AddDays(-30).AddMinutes(1), frames: 1);

        Assert.Empty(SessionRetention.Sweep(_root, _time).Deleted);
        Assert.True(Directory.Exists(session));

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal([Path.GetFileName(session)], SessionRetention.Sweep(_root, _time).Deleted);
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public void TheInjectedClockDecidesExpiry_NotTheWallClock()
    {
        var session = MakeSession(s_now, frames: 1);

        Assert.Empty(SessionRetention.Sweep(_root, _time).Deleted);

        _time.Advance(TimeSpan.FromDays(31));

        Assert.Single(SessionRetention.Sweep(_root, _time).Deleted);
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public void TheRecordingDateIsTheMetadataStart_NotTheFolderName()
    {
        // The folder is named like an old session, but the tool's own metadata says it started recently: it is kept.
        var session = MakeSession(s_now.AddDays(-2), frames: 1, folderName: "2020-01-01_00-00-00");

        var report = SessionRetention.Sweep(_root, _time);

        Assert.Empty(report.Deleted);
        Assert.True(Directory.Exists(session));
    }

    [Fact]
    public void FoldersTheToolDidNotCreate_AreNeverTouched()
    {
        // Session-like name but no metadata.json.
        var noMetadata = Path.Combine(_root, "2020-01-01_00-00-00");
        Directory.CreateDirectory(noMetadata);
        File.WriteAllText(Path.Combine(noMetadata, "frame-00001.png"), "x");

        // Session-like name, metadata from something else (no marker).
        var foreign = Path.Combine(_root, "2020-02-02_00-00-00");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "metadata.json"), "{\"StartedUtc\":\"2020-02-02T00:00:00+00:00\",\"Tool\":\"SomethingElse\"}");

        // Session-like name, metadata that is not JSON.
        var garbage = Path.Combine(_root, "2020-03-03_00-00-00");
        Directory.CreateDirectory(garbage);
        File.WriteAllText(Path.Combine(garbage, "metadata.json"), "not json");

        // A name that does not look like a session, even with a valid marker.
        var named = MakeSession(s_now.AddDays(-90), frames: 1, folderName: "My Holiday Photos");

        // A loose file in the root.
        var loose = Path.Combine(_root, "2020-04-04_00-00-00.txt");
        File.WriteAllText(loose, "keep");

        var report = SessionRetention.Sweep(_root, _time);

        Assert.Empty(report.Deleted);

        // Since the 2026-11-02 reviews a folder that holds recording pictures but no readable metadata.json is reported (and still never touched); the others stay unmentioned.
        Assert.Equal(["2020-01-01_00-00-00"], report.Problems.Select(p => p.FolderName));
        Assert.True(File.Exists(Path.Combine(noMetadata, "frame-00001.png")));
        Assert.True(File.Exists(Path.Combine(foreign, "metadata.json")));
        Assert.True(File.Exists(Path.Combine(garbage, "metadata.json")));
        Assert.True(Directory.Exists(named));
        Assert.True(File.Exists(loose));
    }

    [Fact]
    public void AnExpiredFolderHoldingSomethingTheToolDidNotWrite_IsLeftIntactAndReported()
    {
        var session = MakeSession(s_now.AddDays(-40), frames: 2);
        File.WriteAllText(Path.Combine(session, "my-notes.txt"), "mine");

        var report = SessionRetention.Sweep(_root, _time);

        Assert.Empty(report.Deleted);
        var problem = Assert.Single(report.Problems);
        Assert.Equal(Path.GetFileName(session), problem.FolderName);
        Assert.Contains("my-notes.txt", problem.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(session, "my-notes.txt")));
        Assert.True(File.Exists(Path.Combine(session, "metadata.json")));
        Assert.Equal(2, Directory.GetFiles(session, "frame-*.png").Length);
    }

    [Fact]
    public void AnExpiredFolderHoldingASubfolder_IsLeftIntactAndReported()
    {
        var session = MakeSession(s_now.AddDays(-40), frames: 1);
        Directory.CreateDirectory(Path.Combine(session, "extra"));

        var report = SessionRetention.Sweep(_root, _time);

        Assert.Empty(report.Deleted);
        Assert.Single(report.Problems);
        Assert.True(Directory.Exists(Path.Combine(session, "extra")));
    }

    [Fact]
    public void ASessionFolderThatIsALink_IsNotFollowedAndItsTargetIsNeverTouched()
    {
        var outside = Path.Combine(Path.GetTempPath(), "dbd-study-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "frame-00001.png"), "precious");
            File.WriteAllText(Path.Combine(outside, "metadata.json"), MetadataJson(s_now.AddDays(-100)));
            Directory.CreateSymbolicLink(Path.Combine(_root, "2020-05-05_00-00-00"), outside);

            var report = SessionRetention.Sweep(_root, _time);

            Assert.Empty(report.Deleted);
            var problem = Assert.Single(report.Problems);
            Assert.Contains("link", problem.Reason, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(outside, "frame-00001.png")));
            Assert.True(File.Exists(Path.Combine(outside, "metadata.json")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void ALinkInsideAnExpiredSession_IsNotFollowedAndNothingInThatSessionIsDeleted()
    {
        var outside = Path.Combine(Path.GetTempPath(), "dbd-study-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var target = Path.Combine(outside, "precious.png");
            File.WriteAllText(target, "precious");
            var session = MakeSession(s_now.AddDays(-40), frames: 1);
            File.CreateSymbolicLink(Path.Combine(session, "frame-00002.png"), target);

            var report = SessionRetention.Sweep(_root, _time);

            Assert.Empty(report.Deleted);
            Assert.Single(report.Problems);
            Assert.True(File.Exists(target));
            Assert.True(File.Exists(Path.Combine(session, "metadata.json")));
            Assert.True(File.Exists(Path.Combine(session, "frame-00001.png")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void AnOutputRootThatIsALink_IsNotFollowed()
    {
        var real = Path.Combine(Path.GetTempPath(), "dbd-study-real-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "dbd-study-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        try
        {
            var session = Path.Combine(real, "2020-01-01_00-00-00");
            Directory.CreateDirectory(session);
            File.WriteAllText(Path.Combine(session, "metadata.json"), MetadataJson(s_now.AddDays(-100)));
            Directory.CreateSymbolicLink(link, real);

            var report = SessionRetention.Sweep(link, _time);

            Assert.Empty(report.Deleted);
            Assert.Single(report.Problems);
            Assert.True(File.Exists(Path.Combine(session, "metadata.json")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(real, recursive: true);
        }
    }

    [Fact]
    public void AFolderThatCannotBeDeleted_IsReportedNotIgnored()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var session = MakeSession(s_now.AddDays(-40), frames: 1);
        var mode = File.GetUnixFileMode(session);
        try
        {
            // Without write permission on the folder its files cannot be unlinked. (Running as root ignores permissions: then it deletes.)
            File.SetUnixFileMode(session, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var report = SessionRetention.Sweep(_root, _time);

            if (report.Deleted.Count == 0)
            {
                var problem = Assert.Single(report.Problems);
                Assert.Equal(Path.GetFileName(session), problem.FolderName);
                Assert.Contains("could not be deleted", problem.Reason, StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(report.Problems);
            }
        }
        finally
        {
            if (Directory.Exists(session))
            {
                File.SetUnixFileMode(session, mode);
            }
        }
    }

    [Fact]
    public void AMissingRoot_IsAnEmptyReport()
    {
        var report = SessionRetention.Sweep(Path.Combine(_root, "does-not-exist"), _time);

        Assert.Empty(report.Deleted);
        Assert.Empty(report.Problems);
        Assert.Equal(0, report.Kept);
    }

    [Fact]
    public void WithSeveralSessions_OnlyTheExpiredOnesAreDeleted()
    {
        var a = MakeSession(s_now.AddDays(-45), frames: 1);
        var b = MakeSession(s_now.AddDays(-31), frames: 1);
        var c = MakeSession(s_now.AddDays(-1), frames: 1);

        var report = SessionRetention.Sweep(_root, _time);

        Assert.Equal([Path.GetFileName(a), Path.GetFileName(b)], report.Deleted);
        Assert.True(Directory.Exists(c));
    }

    [Fact]
    public void WrittenMetadata_CarriesTheMarkerAndTheExpiryDate()
    {
        var started = s_now.AddDays(-10);
        var metadata = new SessionMetadata { ToolVersion = "1.0.0", StartedUtc = started };

        using var document = JsonDocument.Parse(metadata.ToJson());
        var root = document.RootElement;

        Assert.Equal(SessionRetention.ToolMarker, root.GetProperty("Tool").GetString());
        Assert.Equal(started.AddDays(30), root.GetProperty("ExpiresUtc").GetDateTimeOffset());
        Assert.Equal(SessionRetention.ExpiryOf(started), metadata.ExpiresUtc);
        Assert.Equal(30, SessionRetention.Days);
    }

    [Fact]
    public void ExpiryDate_IsAnIsoLocalDateThirtyDaysAfterTheStart()
    {
        var started = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        var text = SessionRetention.ExpiryDate(started);

        Assert.Equal(started.AddDays(30).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), text);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", text);
    }

    private static string MetadataJson(DateTimeOffset startedUtc) =>
        new SessionMetadata { ToolVersion = "1.0.0", StartedUtc = startedUtc }.ToJson();

    private string MakeSession(DateTimeOffset startedUtc, int frames, string? folderName = null)
    {
        var name = folderName ?? startedUtc.ToString(SessionRetention.NameFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        for (var i = 1; i <= frames; i++)
        {
            File.WriteAllBytes(Path.Combine(path, $"frame-{i:D5}.png"), [1, 2, 3]);
        }

        File.WriteAllText(Path.Combine(path, "metadata.json"), MetadataJson(startedUtc));
        return path;
    }
}
