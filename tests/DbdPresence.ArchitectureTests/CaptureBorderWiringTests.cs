// Covers: ADR-0003 update 2026-11-02 (owner request: no yellow capture border). Every place the app or the study tool starts a Windows capture of the
// game asks Windows to leave the border out (requestBorderless: true). Windows still decides, from the user's own setting (Settings, Privacy & security,
// Screenshot borders), so the user keeps the last word; the app's own capture indicator stays (SAFETY_AND_PRIVACY section 5). The adapter itself keeps
// false as its default, so the request is always an explicit choice at the call site.
using System.Text.RegularExpressions;

namespace DbdPresence.ArchitectureTests;

public sealed partial class CaptureBorderWiringTests
{
    [Fact]
    public void EveryCaptureOfTheGame_AsksWindowsToLeaveTheBorderOut()
    {
        var files = new[]
        {
            Path.Combine("src", "DbdPresence.App", "Composition", "AppRoot.cs"),
            Path.Combine("tools", "DbdPresence.StudyCapture", "Program.cs"),
        };
        var calls = 0;
        foreach (var file in files)
        {
            var source = File.ReadAllText(Path.Combine(ProductAssemblies.RepositoryRoot, file));
            foreach (Match call in CaptureConstruction().Matches(source))
            {
                calls++;
                Assert.True(call.Groups["args"].Value.Contains("requestBorderless: true", StringComparison.Ordinal), $"{file}: {call.Value}");
            }
        }

        Assert.Equal(3, calls);
    }

    [Fact]
    public void NoOtherProductFile_StartsAWindowsCapture()
    {
        var allowed = new[] { "AppRoot.cs", "WgcWindowCaptureSource.cs" };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ProductAssemblies.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || allowed.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            Assert.DoesNotMatch(CaptureConstruction(), File.ReadAllText(file));
        }
    }

    [GeneratedRegex(@"new\s+WgcWindowCaptureSource\s*\((?<args>[^;]*?)\)\)?,?\s*$", RegexOptions.Multiline)]
    private static partial Regex CaptureConstruction();
}
