namespace DbdPresence.ArchitectureTests;

/// <summary>Applies every <see cref="SafetyRules"/> rule to every built product assembly (NFR-SEC-01, NFR-SEC-02).</summary>
public sealed class ProductSafetyTests
{
    public static TheoryData<string, string> Assemblies => ProductAssemblies.All();

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void ProductAssembly_SatisfiesSafetyRules(string project, string relativePath)
    {
        using var assembly = new AssemblyInspector(ProductAssemblies.Absolute(relativePath));

        var violations = SafetyRules.All(assembly);

        Assert.True(violations.Count == 0, $"{project} ({relativePath}):\n  " + string.Join("\n  ", violations));
    }

    /// <summary>The image helper decodes untrusted files, so it must contain no network code and start no process (FR-PACK-10).</summary>
    [Fact]
    public void ImageHelper_HasNoNetworkReferencesAndStartsNoProcess()
    {
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.ImageHelper"))
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Empty(SafetyRules.Network(assembly));
            Assert.DoesNotContain(assembly.AssemblyReferences, r => r.StartsWith("System.Net", StringComparison.Ordinal));
            Assert.DoesNotContain(assembly.MemberReferences, m => m.ParentType == "System.Diagnostics.Process");
            Assert.DoesNotContain(assembly.AssemblyReferences, r => r.StartsWith("DbdPresence.", StringComparison.Ordinal));
        }
    }

    /// <summary>The helper turns untrusted bytes into pixels; it must not be able to load by location (Uri, BitmapImage, Uri-based decoders).</summary>
    [Fact]
    public void ImageHelper_HasNoUriBitmapImageOrUriDecoders()
    {
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.ImageHelper"))
        {
            using var assembly = new AssemblyInspector(path);

            var violations = SafetyRules.ImageHelper(assembly);

            Assert.True(violations.Count == 0, string.Join("\n  ", violations));
        }
    }

    /// <summary>
    /// Third-party libraries shipped with the app must not bring network code either (NFR-SEC-01).
    /// Any exception requires a written justification here.
    /// Exactly one exception exists: <c>Velopack</c> (ADR-0016, "Velopack exception"). It is the installer and
    /// update framework; its library references System.Net.Http for its own downloader, which the product never
    /// calls: since V2-S6 <c>UpdateManager</c> is used only with the custom <c>IUpdateSource</c>, whose downloads go
    /// through DbdPresence.Online (<see cref="Velopack_UpdatesOnlyThroughTheSignedSource"/>).
    /// The exemption matches the assembly name exactly and applies to nothing else; product assemblies are
    /// still checked by the other tests in this class.
    /// </summary>
    [Fact]
    public void ShippedThirdPartyLibraries_DoNotReferenceNetworkAssemblies()
    {
        var appOutputs = ProductAssemblies.PathsOf("DbdPresence.App").Select(Path.GetDirectoryName).Distinct();
        var offenders = new List<string>();
        foreach (var dir in appOutputs)
        {
            foreach (var dll in Directory.GetFiles(dir!, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (name.StartsWith("DbdPresence.", StringComparison.Ordinal) || name == "DBD-Presence"
                    || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.Windows.SDK", StringComparison.Ordinal)
                    || IsRuntimeFile(name))
                {
                    continue;
                }

                if (string.Equals(name, VelopackAssemblyName, StringComparison.Ordinal))
                {
                    // The named exception (ADR-0016). Product code is covered by the other tests, including
                    // Velopack_UpdatesOnlyThroughTheSignedSource below.
                    continue;
                }

                using var inspector = AssemblyInspector.TryOpen(dll);
                if (inspector is null)
                {
                    continue;
                }

                offenders.AddRange(inspector.AssemblyReferences.Where(SafetyRules.NetworkAssemblies.Contains).Select(r => $"{name} → {r}"));
            }
        }

        Assert.True(offenders.Count == 0, "Third-party libraries referencing network assemblies:\n  " + string.Join("\n  ", offenders.Distinct()));
    }

    /// <summary>
    /// The Velopack exception covers the installer hooks and, since V2-S6, the signed updater (FR-ONL-10, ADR-0016): Velopack's own
    /// network code must never run. Only the App references Velopack; no product code uses any <c>Velopack.Sources</c> type except the
    /// <c>IUpdateSource</c> interface (no GitHub, web, file or Flow source and no Velopack downloader); every <c>UpdateManager</c> is
    /// constructed in <c>DbdPresence.App.Updates</c> with the overload that takes an <c>IUpdateSource</c> (the other overloads build
    /// Velopack's own web or Flow source), including the base call of a subclass (<see cref="SafetyRules.UpdateManagerConstructions"/>);
    /// and downgrades are never switched on.
    /// </summary>
    [Fact]
    public void Velopack_UpdatesOnlyThroughTheSignedSource()
    {
        const string SourceInterface = "Velopack.Sources.IUpdateSource";
        var offenders = new List<string>();
        var constructions = 0;
        foreach (var (project, _) in ProductAssemblies.Projects)
        {
            foreach (var path in ProductAssemblies.PathsOf(project))
            {
                using var inspector = new AssemblyInspector(path);
                if (project != "DbdPresence.App" && inspector.AssemblyReferences.Contains(VelopackAssemblyName))
                {
                    offenders.Add($"{project} references Velopack; only DbdPresence.App may");
                }

                offenders.AddRange(inspector.TypeReferences
                    .Where(t => t.StartsWith("Velopack.Sources.", StringComparison.Ordinal) && t != SourceInterface)
                    .Select(t => $"{project} uses {t}"));

                offenders.AddRange(SafetyRules.UpdateManagerConstructions(inspector, out var found));
                constructions += found;
                foreach (var body in inspector.MethodBodies())
                {
                    for (var i = 0; i < body.Instructions.Length; i++)
                    {
                        var instruction = body.Instructions[i];
                        if (instruction.Operand is not System.Reflection.Metadata.EntityHandle handle)
                        {
                            continue;
                        }

                        var target = inspector.Describe(handle);
                        if (target == "Velopack.UpdateOptions::set_AllowVersionDowngrade" && (i == 0 || body.Instructions[i - 1].OpCode != System.Reflection.Metadata.ILOpCode.Ldc_i4_0))
                        {
                            offenders.Add($"{body.DeclaringType}::{body.Name} may switch version downgrades on");
                        }
                    }
                }
            }
        }

        Assert.True(constructions > 0, "No UpdateManager construction found: the rule would pass vacuously.");
        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders.Distinct()));
    }

    /// <summary>
    /// Velopack's apply-at-start installs a downloaded package without the re-check that "Restart to update" does (security review 2026-10-25,
    /// M3 and L9). Every call to <c>VelopackApp.SetAutoApplyOnStartup</c> must pass <c>false</c> (the argument is the instruction just before
    /// the call), and there must be one, because the default is on.
    /// </summary>
    [Fact]
    public void Velopack_AutoApplyOnStartup_IsSwitchedOff()
    {
        const string Setter = "Velopack.VelopackApp::SetAutoApplyOnStartup";
        var offenders = new List<string>();
        var calls = 0;
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.App"))
        {
            using var inspector = new AssemblyInspector(path);
            foreach (var body in inspector.MethodBodies())
            {
                for (var i = 0; i < body.Instructions.Length; i++)
                {
                    if (body.Instructions[i].Operand is not System.Reflection.Metadata.EntityHandle handle || inspector.Describe(handle) != Setter)
                    {
                        continue;
                    }

                    calls++;
                    if (i == 0 || body.Instructions[i - 1].OpCode != System.Reflection.Metadata.ILOpCode.Ldc_i4_0)
                    {
                        offenders.Add($"{body.DeclaringType}::{body.Name} does not pass false to SetAutoApplyOnStartup");
                    }
                }
            }
        }

        Assert.True(calls > 0, "No SetAutoApplyOnStartup call found in the App: apply-at-start would be on by default.");
        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    private const string VelopackAssemblyName = "Velopack";

    // Self-contained publish folders also contain the .NET runtime itself, which is not third-party code.
    private static bool IsRuntimeFile(string name) =>
        name is "System" or "mscorlib" or "netstandard" or "WindowsBase" or "PresentationCore" or "PresentationFramework" or "WinRT.Runtime"
        || name.StartsWith("Presentation", StringComparison.Ordinal) || name.StartsWith("UIAutomation", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.VisualBasic", StringComparison.Ordinal) || name.StartsWith("Microsoft.Win32", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.CSharp", StringComparison.Ordinal) || name.StartsWith("ReachFramework", StringComparison.Ordinal)
        || name.StartsWith("DirectWriteForwarder", StringComparison.Ordinal) || name.StartsWith("Accessibility", StringComparison.Ordinal);
}
