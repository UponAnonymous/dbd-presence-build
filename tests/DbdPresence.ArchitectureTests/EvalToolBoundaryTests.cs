// Covers: SAFETY_AND_PRIVACY section 3 (owner-run tools are local only), NFR-SEC-01, NFR-PRIV-01, and DETECTION_VALIDATION section 16 (labels stay independent of the
// detectors under test). The unshipped tool in tools/DbdPresence.Eval has no network code and starts no process: no networking references, no host or URL
// literals, no P/Invoke of its own, no process start, no process inspection. Tools are outside src/, so they are checked here. The labelling code is also
// checked for independence: it must not use a detector, the text recogniser or the change gate. The same holds for the compact review file (PLAN_V2 D34,
// DETECTION_VALIDATION section 16.1): the code that makes it, cuts its crops and imports the labels made from it may not reference a detector, in source or in
// the built assembly, so a reviewer who labels from it never sees a detector's reading. The labelling pipeline now lives in the product library
// src/DbdPresence.Study (PLAN_AUTO_LABELLING AL-3); its source and built assembly get every rule below, and the product safety rules in ProductSafetyTests.
namespace DbdPresence.ArchitectureTests;

public sealed class EvalToolBoundaryTests
{
    private static readonly string[] s_bannedSourceTokens =
    [
        "System.Net", "HttpClient", "HttpListener", "Socket", "WebClient", "TcpClient", "UdpClient", "WebRequest", "NamedPipe",
        "http://", "https://", "ftp://", "wss://", "ws://", "Process.Start", "ProcessStartInfo", "System.Diagnostics.Process", "Process.GetProcess",
        "DllImport", "LibraryImport", "ReadProcessMemory", "OpenProcess", "Assembly.Load", "Activator.CreateInstance",
    ];

    /// <summary>
    /// The automatic labeller (PLAN_AUTO_LABELLING AL-4, folder AutoLabelling): it sees only the review file and the closed lists, so it gets the strictest rule too.
    /// </summary>
    private static readonly string[] s_labellerFiles =
    [
        "LabellerTransport.cs", "ImageSize.cs", "ReviewFileReader.cs", "LabelRequestBuilder.cs", "LabellerPrompt.cs", "AutoLabeller.cs", "AutoLabelRun.cs", "StudyText.cs", "StudyPrivacy.cs",
    ];

    /// <summary>The files that make labels from the owner's answers. None of them may use what the detectors read.</summary>
    private static readonly string[] s_independentFiles =
    [
        "SegmentProposer.cs", "ReviewPage.cs", "ReviewSessions.cs", "LabelFactory.cs", "ReviewModels.cs",
        "ReviewExport.cs", "ExportSampler.cs", "ExportCrops.cs", "ReviewFiles.cs", "JpegEncoder.cs",
        "StudyFiles.cs", "StudySessions.cs", "StudyState.cs", "FrameCompaction.cs", "StudyEnvironment.cs", "GuidedMenu.cs", "ReviewCommands.cs",
        .. s_labellerFiles,
        "StudyWorkbench.cs", "StudyPageModels.cs",
    ];

    /// <summary>The files that make the review file. Stricter than the rest: no word of the detector side may appear in their code at all.</summary>
    private static readonly string[] s_exportFiles =
        ["ReviewExport.cs", "ExportSampler.cs", "ExportCrops.cs", "ReviewFiles.cs", "JpegEncoder.cs", "GuidedMenu.cs", "ReviewCommands.cs", .. s_labellerFiles];

    private static readonly string[] s_exportBannedWords =
    [
        "Detector", "Detection", "Recogni", "Ocr", "Observation", "ChangeGate", "HudAnalyzer", "HudThresholds", "Thresholds", "Highlight", "Sampling",
        "Platform.Windows", "CreateRecognizer",
    ];

    /// <summary>The types whose methods may not call a detector (the review file and the labelling code).</summary>
    private static readonly string[] s_independentTypes =
    [
        "DbdPresence.Study.ReviewExport", "DbdPresence.Study.ExportSampler", "DbdPresence.Study.ExportCrops", "DbdPresence.Study.ReviewFiles", "DbdPresence.Eval.GuidedMenu",
        "DbdPresence.Eval.ReviewCommands", "DbdPresence.Study.JpegEncoder", "DbdPresence.Study.ReviewSessions", "DbdPresence.Study.LabelFactory", "DbdPresence.Study.SegmentProposer",
        "DbdPresence.Study.StudyFiles", "DbdPresence.Study.ReviewFileReader", "DbdPresence.Study.LabelRequestBuilder", "DbdPresence.Study.LabellerPrompt", "DbdPresence.Study.AutoLabeller",
        "DbdPresence.Study.AutoLabelRun", "DbdPresence.Study.ImageSize", "DbdPresence.Study.StudyWorkbench", "DbdPresence.Study.StudyText", "DbdPresence.Study.StudyPrivacy",
    ];

    private static readonly string[] s_calledMemberBans =
    [
        "HudDetection", "HudAnalyzer", "HudEvaluationAdapter", "Detector", "ITextRecognizer", "TextRecognition", "WindowsEnglishOcr", "ChangeGated", "Platform.Windows",
        "Observation", "Core.Detection.", "DetectionEvaluator",
    ];

    private static readonly string[] s_detectorTokens =
    [
        "HudDetectionPipeline", "HudEvaluationAdapter", "HudAnalyzer", "ChangeGatedTextReader", "ITextRecognizer", "IDetector", "ScreenStateDetector", "RoleCueDetector",
        "CharacterDetector", "MapTextDetector", "KillerNameDetector", "QueueModeDetector", "DetectionEvaluator", "WindowsEnglishOcr", "TextRecognitionResult",
    ];

    private static string ToolDirectory => Path.Combine(ProductAssemblies.RepositoryRoot, "tools", "DbdPresence.Eval");

    private static string LibraryDirectory => Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.Study");

    /// <summary>The source file of a labelling class: in the library, or in the tool for the console parts that stayed.</summary>
    private static string SourcePath(string name)
    {
        var inLibrary = Path.Combine(LibraryDirectory, name);
        var inLabeller = Path.Combine(LibraryDirectory, "AutoLabelling", name);
        return File.Exists(inLibrary) ? inLibrary : File.Exists(inLabeller) ? inLabeller : Path.Combine(ToolDirectory, name);
    }

    private static string[] BuiltPaths(string directory, string dll)
    {
        var binDir = Path.Combine(directory, "bin", ProductAssemblies.Configuration);
        var paths = Directory.Exists(binDir) ? Directory.GetFiles(binDir, dll, SearchOption.AllDirectories) : [];
        Assert.True(paths.Length > 0, $"No built {dll} under {binDir}. Build the solution first.");
        return paths;
    }

    [Fact]
    public void EvalToolAndStudyLibrarySource_ContainNoNetworkProcessOrNativeCode()
    {
        var files = Directory.GetFiles(ToolDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(LibraryDirectory, "*.cs", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(Path.Combine(LibraryDirectory, "AutoLabelling"), "*.cs", SearchOption.TopDirectoryOnly)).ToArray();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(f => s_bannedSourceTokens
                .Where(t => File.ReadAllText(f).Contains(t, StringComparison.Ordinal))
                .Select(t => $"{Path.GetFileName(f)} contains '{t}'"))
            .ToList();

        Assert.True(violations.Count == 0, string.Join("\n  ", violations));
    }

    [Fact]
    public void EvalToolProject_ReferencesOnlyCoreVisionStudyAndThePlatformAdapter_AndNoPackages()
    {
        var project = File.ReadAllText(Path.Combine(ToolDirectory, "DbdPresence.Eval.csproj"));

        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        var references = System.Text.RegularExpressions.Regex.Matches(project, "ProjectReference Include=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "../../src/DbdPresence.Core/DbdPresence.Core.csproj",
                "../../src/DbdPresence.Platform.Windows/DbdPresence.Platform.Windows.csproj",
                "../../src/DbdPresence.Study/DbdPresence.Study.csproj",
                "../../src/DbdPresence.Vision/DbdPresence.Vision.csproj",
            ],
            references);
    }

    [Fact]
    public void BuiltEvalTool_SatisfiesTheSafetyRules_HasNoHostLiteralsAndNoOnlineOrNetworkReference()
    {
        var paths = BuiltPaths(ToolDirectory, "DbdPresence.Eval.dll").Concat(BuiltPaths(LibraryDirectory, "DbdPresence.Study.dll"));

        foreach (var path in paths)
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Empty(SafetyRules.All(assembly));
            Assert.Empty(SafetyRules.HostLiterals(assembly, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            Assert.Empty(assembly.PInvokes);
            Assert.DoesNotContain("DbdPresence.Online", assembly.AssemblyReferences);
            Assert.DoesNotContain(assembly.AssemblyReferences, r => SafetyRules.NetworkAssemblies.Contains(r));
        }
    }

    [Fact]
    public void TheReviewPage_LoadsNothingAndSendsNothing()
    {
        // The page text is a constant in ReviewPage.cs. It must hold no address, no network call and no script or style from elsewhere.
        var page = File.ReadAllText(Path.Combine(LibraryDirectory, "ReviewPage.cs"));
        string[] banned = ["http", "fetch(", "XMLHttpRequest", "WebSocket", "sendBeacon", "<link", "src=\"//", "import(", "importScripts", "localStorage", "document.cookie"];

        foreach (var token in banned)
        {
            Assert.DoesNotContain(token, page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LabelProposalsAndLabels_NeverUseADetectorTheRecogniserOrTheChangeGate_SoTheyCannotBeCircular()
    {
        foreach (var name in s_independentFiles)
        {
            var text = File.ReadAllText(SourcePath(name));
            foreach (var token in s_detectorTokens)
            {
                Assert.False(text.Contains(token, StringComparison.Ordinal), $"{name} must stay independent of the detectors under test but uses {token}.");
            }
        }
    }

    [Fact]
    public void TheReviewFileCode_ReferencesNoDetector_NoRecogniser_AndNoThresholdOfTheHudProfile_OnlyItsPositions()
    {
        foreach (var name in s_exportFiles)
        {
            var code = CodeWithoutComments(File.ReadAllText(SourcePath(name)));
            foreach (var word in s_exportBannedWords)
            {
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{name} makes the review file and must not use '{word}'.");
            }
        }

        // The only HUD profile use is the crop positions, and only in ExportCrops.
        foreach (var name in s_exportFiles.Where(n => n != "ExportCrops.cs"))
        {
            Assert.DoesNotContain("HudProfile", File.ReadAllText(SourcePath(name)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBuiltReviewFileCode_CallsNoDetector()
    {
        // The library holds the review file code; the tool keeps the console parts (GuidedMenu, ReviewCommands). Both are checked.
        var paths = BuiltPaths(ToolDirectory, "DbdPresence.Eval.dll").Concat(BuiltPaths(LibraryDirectory, "DbdPresence.Study.dll"));
        foreach (var path in paths)
        {
            using var assembly = new AssemblyInspector(path);
            var bodies = assembly.MethodBodies().Where(b => s_independentTypes.Any(t => b.DeclaringType == t || b.DeclaringType.StartsWith(t + "/", StringComparison.Ordinal) || b.DeclaringType.StartsWith(t + "+", StringComparison.Ordinal))).ToList();
            Assert.NotEmpty(bodies);
            if (path.Contains("DbdPresence.Study", StringComparison.Ordinal))
            {
                Assert.Contains(bodies, b => b.DeclaringType.StartsWith("DbdPresence.Study.ReviewExport", StringComparison.Ordinal));
            }

            var violations = new List<string>();
            foreach (var body in bodies)
            {
                foreach (var instruction in body.Instructions.Where(i => i.Operand is System.Reflection.Metadata.EntityHandle))
                {
                    var target = assembly.Describe((System.Reflection.Metadata.EntityHandle)instruction.Operand!);
                    if (s_calledMemberBans.Any(b => target.Contains(b, StringComparison.Ordinal)))
                    {
                        violations.Add($"{body.DeclaringType}.{body.Name} calls {target}");
                    }
                }
            }

            Assert.True(violations.Count == 0, string.Join("\n  ", violations));
        }
    }

    [Fact]
    public void TheReviewFileRule_IsNotVacuous_ItFlagsADetectorAndARecogniserInSamples_AndTheListedFilesExist()
    {
        const string sample = "var d = new HudDetectors(profile); // a comment about a Thing is fine\nvar t = new WindowsEnglishOcr();";
        var code = CodeWithoutComments(sample);

        Assert.Contains(s_exportBannedWords, w => code.Contains(w, StringComparison.Ordinal));
        Assert.DoesNotContain("a comment", code, StringComparison.Ordinal);
        Assert.Contains(s_calledMemberBans, b => "DbdPresence.Platform.Windows.Ocr.WindowsEnglishOcr::.ctor".Contains(b, StringComparison.Ordinal));
        Assert.All(s_exportFiles, f => Assert.True(File.Exists(SourcePath(f)), $"{f} is listed but missing."));
    }

    private static string CodeWithoutComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.IndexOf("//", StringComparison.Ordinal) is var at and >= 0 ? line[..at] : line));

    [Fact]
    public void TheIndependenceRule_IsNotVacuous_ItFlagsADetectorInASample_AndTheListedFilesExist()
    {
        const string sample = "var d = new HudDetectionPipeline(profile, vocab, ocr, time);";

        Assert.Contains(s_detectorTokens, t => sample.Contains(t, StringComparison.Ordinal));
        Assert.All(s_independentFiles, f => Assert.True(File.Exists(SourcePath(f)), $"{f} is listed but missing."));
    }

    [Fact]
    public void TheLabellerSource_NamesNoProviderOrModel_AndTheBuiltLabellerTypesExist()
    {
        foreach (var name in s_labellerFiles)
        {
            var text = File.ReadAllText(SourcePath(name));
            Assert.DoesNotContain("claude", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("anthropic", text, StringComparison.OrdinalIgnoreCase);
        }

        using var assembly = new AssemblyInspector(BuiltPaths(LibraryDirectory, "DbdPresence.Study.dll")[0]);
        var types = assembly.MethodBodies().Select(b => b.DeclaringType).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DbdPresence.Study.AutoLabeller", types);
        Assert.Contains("DbdPresence.Study.LabelRequestBuilder", types);
    }

    [Fact]
    public void SourceTokenRule_IsNotVacuous_ItFlagsATokenInASample()
    {
        const string sample = "var c = new System.Net.Http.HttpClient();";

        Assert.Contains(s_bannedSourceTokens, t => sample.Contains(t, StringComparison.Ordinal));
    }
}
