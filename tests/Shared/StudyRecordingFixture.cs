using System.Globalization;
using DbdPresence.Core.Capture;
using DbdPresence.Study;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Text;

namespace DbdPresence.TestSupport;

/// <summary>
/// Writes a small recording into a study folder the way the study tool leaves it (pictures, <c>metadata.json</c> with the tool's marker) and then takes in
/// reviewer labels for it, so its spot-check is due. MECHANICS ONLY: the pictures are flat colour; nothing here says anything about the game.
/// Shared by the Linux tests of the Study library and the Windows UI tests of the Study page.
/// </summary>
internal static class StudyRecordingFixture
{
    /// <summary>The scenes of the four pictures, one range each.</summary>
    public static readonly string[] Scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];

    /// <summary>Writes the recording <paramref name="name"/> (a start time such as <c>2026-10-24_19-30-12</c>) with four pictures and takes in labels from a reviewer.</summary>
    /// <param name="root">The study folder.</param>
    /// <param name="name">The recording's folder name.</param>
    /// <param name="now">The time the labels are taken in (the spot-check is due at once for a reviewer's labels).</param>
    /// <returns>The recording's folder.</returns>
    public static string AddReviewerLabelled(string root, string name, DateTimeOffset now)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var start = DateTimeOffset.ParseExact(name, SessionRetention.NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var metadata = new SessionMetadata
        {
            ToolVersion = "test",
            OsVersion = "test",
            OsBuild = 26100,
            StartedUtc = start,
            FirstFrameWidth = 320,
            FirstFrameHeight = 180,
            Dpi = 96,
            WindowMode = WindowDisplayMode.FullscreenOrBorderless,
            StopReason = "owner pressed Q",
        };
        metadata.DistinctResolutions.Add("320x180");
        for (var index = 1; index <= Scenes.Length; index++)
        {
            var bgra = new byte[320 * 180 * 4];
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = (byte)(40 * index);
                bgra[i + 1] = (byte)(255 - (40 * index));
                bgra[i + 2] = 90;
                bgra[i + 3] = 255;
            }

            var png = PngEncoder.Encode(320, 180, bgra);
            var file = $"frame-{index:D5}.png";
            File.WriteAllBytes(Path.Combine(folder, file), png);
            metadata.Frames.Add(new FrameRecord(index, file, start.AddSeconds(2 * (index - 1)), 320, 180, png.Length, "scheduled"));
        }

        File.WriteAllText(Path.Combine(folder, "metadata.json"), metadata.ToJson());
        _ = StudySessions.Prepare(folder, new PrepareOptions(), 0) ?? throw new InvalidOperationException("The fixture recording could not be prepared.");
        var plan = new ReviewPlan(name, [.. Scenes.Select((_, i) => new PlanSegment(i, i + 1, i + 1, "19:30:12", 2.0))]);
        File.WriteAllText(Path.Combine(folder, SessionFiles.Plan), plan.ToJson());
        var answers = new ReviewAnswers(
            ReviewAnswers.LabelsKind,
            name,
            [.. Scenes.Select((scene, i) => new SegmentAnswer(i, scene, i == 1))],
            [new MatchAnswer(1, "Survivor", "dwight-fairfield", "ormond/mount-ormond-resort", "Escaped", "the-lich", string.Empty)],
            null,
            LabelSources.Reviewer);
        _ = ReviewSessions.ImportReviewerLabels(folder, answers.ToJson(), Vocabulary.LoadEmbedded(), now);
        return folder;
    }
}
