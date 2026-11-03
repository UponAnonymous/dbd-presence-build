// Covers: FR-LAB-01..04 wiring that cannot run on Linux (security review of automatic labelling L8, QA review L4): the relay adapter in the App maps every outcome of the
// online client to the same-named outcome of the labelling code (a swap would change stop-or-continue and the page sentence), and the destructive 30-day clean-up of
// study recordings (SessionRetention.Sweep) is called from exactly one place in the app and its libraries: the start of a recording in
// src/DbdPresence.Study/Recording/StudyRecorder.cs. The owner asked on 2026-11-02 for recording inside the app, and PLAN_V2 D25 requires the 30-day clean-up wherever recording
// happens, so the in-app recorder runs it once, in StartAsync, before it writes anything. No other file under App, Study, Presentation, Online or Platform.Windows may call it
// (the console recorder, outside src/, links that same file).
using System.Text.RegularExpressions;

namespace DbdPresence.ArchitectureTests;

public sealed partial class StudyAppWiringTests
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([ProductAssemblies.RepositoryRoot, .. parts]));

    private static string[] EnumMembers(string source, string enumName)
    {
        var body = Regex.Match(source, @"enum\s+" + enumName + @"\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline).Groups["body"].Value;
        return [.. Regex.Matches(body, @"^\s{4}([A-Z]\w*)\s*,?\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public void TheRelayAdapter_MapsEveryClientOutcomeToTheSameNamedLabellerOutcome_AndUnknownOnesToFailed()
    {
        var adapter = Source("src", "DbdPresence.App", "Composition", "RelayLabellerTransport.cs");
        var pairs = Regex.Matches(adapter, @"LabellingStatus\.(\w+)\s*=>\s*LabellerOutcome\.(\w+)").Select(m => (From: m.Groups[1].Value, To: m.Groups[2].Value)).ToList();
        var statuses = EnumMembers(Source("src", "DbdPresence.Online", "Study", "StudyLabellingClient.cs"), "LabellingStatus");
        var outcomes = EnumMembers(Source("src", "DbdPresence.Study", "AutoLabelling", "LabellerTransport.cs"), "LabellerOutcome");

        Assert.NotEmpty(statuses);
        Assert.All(pairs, p => Assert.Equal(p.From, p.To));
        Assert.Equal(statuses.Where(s => s != "Failed").Order(StringComparer.Ordinal), pairs.Select(p => p.From).Order(StringComparer.Ordinal));
        Assert.Contains("_ => LabellerOutcome.Failed", adapter, StringComparison.Ordinal);
        // The labelling code knows exactly the client's outcomes plus the run's own limit, which never comes from the relay.
        Assert.Equal(statuses.Append("RunLimitReached").Order(StringComparer.Ordinal), outcomes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OnlyTheRecorderStart_CallsTheRetentionSweep_EverythingElseInTheAppAndItsLibrariesIsForbidden()
    {
        Assert.True(SweepCall().IsMatch("PrintRetention(SessionRetention.Sweep(SessionFolder.Root, TimeProvider.System));"), "the pattern must catch the call the recorder makes");
        var offenders = new List<string>();
        foreach (var project in new[] { "DbdPresence.App", "DbdPresence.Study", "DbdPresence.Presentation", "DbdPresence.Online", "DbdPresence.Platform.Windows" })
        {
            var folder = Path.Combine(ProductAssemblies.RepositoryRoot, "src", project);
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
            {
                if (SweepCall().IsMatch(File.ReadAllText(file)) && !IsTheRecorder(file))
                {
                    offenders.Add(Path.GetRelativePath(ProductAssemblies.RepositoryRoot, file));
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static bool IsTheRecorder(string path) =>
        string.Equals(Path.GetRelativePath(ProductAssemblies.RepositoryRoot, path).Replace('\\', '/'), RecorderFile, StringComparison.Ordinal);

    [Fact]
    public void TheRecorder_CallsTheSweepExactlyOnce_AndOnlyInTheMethodThatStartsARecording()
    {
        var source = Source(RecorderFile.Split('/'));

        Assert.Single(SweepCall().Matches(source));
        var start = StartMethod().Match(source);
        Assert.True(start.Success, "the recorder must have a StartAsync method");
        Assert.True(SweepCall().IsMatch(start.Groups["body"].Value), "the sweep must be called inside StartAsync");
        Assert.False(StartMethod().IsMatch("public async Task StopAsync(string reason = \"x\")\n    {\n        SessionRetention.Sweep(root, time);\n    }\n"), "the pattern must not take another method for StartAsync");
    }

    [Fact]
    public void TheSweepPattern_CatchesTheEvasionsTheReviewNamed_AndNotTheDeclaration()
    {
        foreach (var call in new[]
        {
            "SessionRetention.Sweep(_root, _time);", "using static DbdPresence.StudyCapture.SessionRetention;", "using SR = DbdPresence.StudyCapture.SessionRetention;",
            "SR.Sweep(_root, time);", "Sweep(_root, time);", "var x = retention.Sweep(root, t);",
        })
        {
            Assert.True(SweepCall().IsMatch(call), call);
        }

        Assert.DoesNotMatch(SweepCall(), "public static RetentionReport Sweep(string root, TimeProvider time)");
    }

    [Fact]
    public void OnlyTheRecordingSection_StartsTheStudyRecorder_AndItChecksTheOwnerAndTheConfirmationFirst()
    {
        var callers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ProductAssemblies.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            if (!IsTheRecorder(file) && RecorderStart().IsMatch(File.ReadAllText(file)))
            {
                callers.Add(Path.GetRelativePath(ProductAssemblies.RepositoryRoot, file).Replace('\\', '/'));
            }
        }

        Assert.Equal(["src/DbdPresence.Presentation/Labelling/StudyViewModel.Recording.cs"], callers);
        Assert.True(RecorderStart().IsMatch("await _recorder.StartAsync();"), "the pattern must catch the call the page makes");
        Assert.True(RecorderStart().IsMatch("studyRecorder.StartAsync()"), "and a differently named variable");
        var page = Source("src", "DbdPresence.Presentation", "Labelling", "StudyViewModel.Recording.cs");
        var call = RecorderStart().Match(page).Index;
        Assert.True(page.IndexOf("_owner.IsOwner != true", StringComparison.Ordinal) is var owner and >= 0 && owner < call, "the owner check must come before the start");
        Assert.True(page.IndexOf("!RecordingConfirmed", StringComparison.Ordinal) is var confirmed and >= 0 && confirmed < call, "the confirmation check must come before the start");
        Assert.Contains("_mayRecord()", Source("src", "DbdPresence.Study", "Recording", "StudyRecorder.cs"), StringComparison.Ordinal);
        Assert.Contains("ownerState.IsOwner == true", Source("src", "DbdPresence.App", "Composition", "AppRoot.cs"), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\w*[Rr]ecorder\w*\s*\.\s*StartAsync\s*\(")]
    private static partial Regex RecorderStart();

    private const string RecorderFile = "src/DbdPresence.Study/Recording/StudyRecorder.cs";

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [GeneratedRegex(@"public async Task StartAsync\(Func<bool>\? confirmAfterCleanUp = null\)\s*\{(?<body>.*?)\n    \}\n", RegexOptions.Singleline)]
    private static partial Regex StartMethod();

    // Any call-looking use of the clean-up: the qualified name, a bare call (a `using static` or an alias), a call whose first argument is any root-like name (`_root`
    // too), or a `using static` of the class. The declaration itself (`RetentionReport Sweep(`) is not a call.
    [GeneratedRegex(@"\bSessionRetention\s*\.\s*Sweep\b|(?<!RetentionReport\s)(?<![\w])Sweep\s*\(|\.\s*Sweep\s*\(|using\s+static\s+[\w.]*SessionRetention|using\s+\w+\s*=\s*[\w.]*SessionRetention")]
    private static partial Regex SweepCall();
}
