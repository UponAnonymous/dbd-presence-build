namespace DbdPresence.ArchitectureTests;

/// <summary>
/// FR-DET-01 / ADR-0005: detectors emit observations and nothing else. The vision assembly (detectors, evaluator, vocabulary) may use the
/// observation types of Core but must never reach the engine, the publication policy, the composer or the presence types: only the engine
/// confirms values and only the policy decides what reaches Discord.
/// </summary>
public sealed class DetectionBoundaryTests
{
    private static readonly string[] s_bannedExact =
    [
        "DbdPresence.Core.Detection.MatchStateEngine",
        "DbdPresence.Core.Detection.MatchSnapshot",
        "DbdPresence.Core.Detection.PublicationPolicy",
        "DbdPresence.Core.Detection.PublicationResult",
        "DbdPresence.Core.Detection.PublicationOptions",
    ];

    private static readonly string[] s_bannedPrefixes =
    [
        "DbdPresence.Core.Composition.",
        "DbdPresence.Core.Presence.",
        "DbdPresence.Core.Templates.",
        "DbdPresence.Core.Settings.",
        "DbdPresence.Discord.",
    ];

    [Fact]
    public void VisionAssembly_NeverReferencesTheEnginePublicationPolicyOrComposer()
    {
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.Vision"))
        {
            using var assembly = new AssemblyInspector(path);

            var offenders = assembly.TypeReferences
                .Where(t => s_bannedExact.Contains(t, StringComparer.Ordinal) || s_bannedPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.True(offenders.Length == 0, "Detection code must only emit observations, but references: " + string.Join(", ", offenders));
        }
    }

    [Fact]
    public void VisionAssembly_DoesUseTheObservationTypes_SoTheRuleAboveIsNotVacuous()
    {
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.Vision"))
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Contains("DbdPresence.Core.Detection.Observation", assembly.TypeReferences);
            Assert.Contains("DbdPresence.Core.Detection.ContextKey", assembly.TypeReferences);
        }
    }

    [Fact]
    public void CoreEngine_DoesNotReferenceVisionOrAnyImagingApi()
    {
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.Core"))
        {
            using var assembly = new AssemblyInspector(path);

            Assert.DoesNotContain(assembly.AssemblyReferences, r => r.StartsWith("DbdPresence.Vision", StringComparison.Ordinal));
            Assert.DoesNotContain(assembly.AssemblyReferences, r => r is "System.Drawing" or "System.Drawing.Common" or "System.Drawing.Primitives");
        }
    }

    [Fact]
    public void VisionAssembly_UsesNoOperatingSystemImagingApi()
    {
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.Vision"))
        {
            using var assembly = new AssemblyInspector(path);

            Assert.DoesNotContain(assembly.AssemblyReferences, r => r is "System.Drawing" or "System.Drawing.Common" or "System.Drawing.Primitives" or "WindowsBase" or "PresentationCore");
            Assert.DoesNotContain(assembly.TypeReferences, t => t.StartsWith("System.Windows.Media.", StringComparison.Ordinal) || t.StartsWith("Windows.Graphics.", StringComparison.Ordinal) || t.StartsWith("Windows.Media.", StringComparison.Ordinal));
        }
    }
}
