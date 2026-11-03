// Covers: NFR-SVC-05 (key custody: the release and recovery keys are made and used offline), SAFETY_AND_PRIVACY section 3 (owner-run tools are local
// only), NFR-SEC-01. The offline release-signing tool in tools/DbdPresence.ReleaseSigner has no network code, starts no process and has no native code:
// no networking references, no host or URL literals, no P/Invoke, no process start. It uses only the app's own signing rules (DbdPresence.Packs) and no
// package. The service never holds the release or recovery key; service/test/releases.test.ts checks that side.
namespace DbdPresence.ArchitectureTests;

public sealed class ReleaseSignerBoundaryTests
{
    private static readonly string[] s_bannedSourceTokens =
    [
        "System.Net", "HttpClient", "HttpListener", "Socket", "WebClient", "TcpClient", "UdpClient", "WebRequest", "NamedPipe",
        "http://", "https://", "ftp://", "wss://", "ws://", "Process.Start", "ProcessStartInfo", "System.Diagnostics.Process", "Process.GetProcess",
        "DllImport", "LibraryImport", "Assembly.Load", "Activator.CreateInstance", "Environment.GetEnvironmentVariable", "Clipboard",
    ];

    private static string ToolDirectory => Path.Combine(ProductAssemblies.RepositoryRoot, "tools", "DbdPresence.ReleaseSigner");

    [Fact]
    public void ReleaseSignerSource_ContainsNoNetworkProcessOrNativeCode()
    {
        var files = Directory.GetFiles(ToolDirectory, "*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(f => s_bannedSourceTokens
                .Where(t => File.ReadAllText(f).Contains(t, StringComparison.Ordinal))
                .Select(t => $"{Path.GetFileName(f)} contains '{t}'"))
            .ToList();

        Assert.True(violations.Count == 0, string.Join("\n  ", violations));
    }

    [Fact]
    public void ReleaseSignerProject_ReferencesOnlyThePacksLibrary_AndNoPackages()
    {
        var project = File.ReadAllText(Path.Combine(ToolDirectory, "DbdPresence.ReleaseSigner.csproj"));

        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        var references = System.Text.RegularExpressions.Regex.Matches(project, "ProjectReference Include=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToArray();
        Assert.Equal(["../../src/DbdPresence.Packs/DbdPresence.Packs.csproj"], references);
    }

    [Fact]
    public void BuiltReleaseSigner_SatisfiesTheSafetyRules_HasNoHostLiteralsAndNoOnlineOrNetworkReference()
    {
        var binDir = Path.Combine(ToolDirectory, "bin", ProductAssemblies.Configuration);
        var paths = Directory.Exists(binDir) ? Directory.GetFiles(binDir, "DbdPresence.ReleaseSigner.dll", SearchOption.AllDirectories) : [];
        Assert.True(paths.Length > 0, $"No built release signer under {binDir}. Build the solution first.");

        foreach (var path in paths)
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Empty(SafetyRules.All(assembly));
            Assert.Empty(SafetyRules.HostLiterals(assembly, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            Assert.Empty(assembly.PInvokes);
            Assert.DoesNotContain("DbdPresence.Online", assembly.AssemblyReferences);
            Assert.DoesNotContain(assembly.AssemblyReferences, r => SafetyRules.NetworkAssemblies.Contains(r));
            Assert.DoesNotContain(assembly.MemberReferences, m => m.ParentType == "System.Diagnostics.Process");
        }
    }

    [Fact]
    public void NoPrivateKeyIsCommitted_OnlyTheTestKeysUnderTestFixtures()
    {
        // Every PEM private key in the repository must be a published TEST key under tests/fixtures (never trusted by a release build).
        // The files are the ones git tracks or would add (security review 2026-10-25, L7): tracked dot-folders such as .github and
        // .claude are checked, ignored ones such as other agents' worktrees are not, and only regular files are read.
        var root = ProductAssemblies.RepositoryRoot;
        var files = Git(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Contains(files, f => f.EndsWith("ReleaseSignerBoundaryTests.cs", StringComparison.Ordinal));

        var offenders = new List<string>();
        foreach (var relative in files)
        {
            var info = new FileInfo(Path.Combine(root, relative));
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 2_000_000
                || info.Extension is ".png" or ".jpg" or ".ico" or ".dll" or ".exe" or ".zip" or ".nupkg")
            {
                continue;
            }

            if (File.ReadAllText(info.FullName).Contains("PRIVATE KEY-----", StringComparison.Ordinal) && !MayQuotePemMarker(relative))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, "Private keys outside tests/fixtures:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The only files outside tests/fixtures that may contain the PEM marker text: they write or check for it, they do not hold a key (final review LOW).</summary>
    private static readonly string[] s_pemMarkerQuoters =
    [
        Path.Combine("tests", "DbdPresence.ArchitectureTests", "ReleaseSignerBoundaryTests.cs"),
        Path.Combine("tests", "DbdPresence.ReleaseSigner.Tests", "ReleaseSignerTests.cs"),
        Path.Combine("tools", "DbdPresence.ReleaseSigner", "ReleaseKeys.cs"),
    ];

    private static bool MayQuotePemMarker(string relativePath)
    {
        var normalised = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var parts = normalised.Split(Path.DirectorySeparatorChar);
        return (parts.Length > 2 && parts[0] == "tests" && parts[1] == "fixtures") || s_pemMarkerQuoters.Contains(normalised, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("docs/notes.txt", false)]
    [InlineData("service/src/leak.ts", false)]
    [InlineData("tools/DbdPresence.ReleaseSigner/OtherFile.cs", false)]
    [InlineData("README.md", false)]
    [InlineData("tests/fixtures/signing/keys/test-content-1.private.pem", true)]
    [InlineData("tools/DbdPresence.ReleaseSigner/ReleaseKeys.cs", true)]
    [InlineData("tests/DbdPresence.ReleaseSigner.Tests/ReleaseSignerTests.cs", true)]
    public void OnlyNamedFiles_AndTheTestFixtures_MayContainThePemMarker(string relative, bool allowed)
    {
        Assert.Equal(allowed, MayQuotePemMarker(relative));
        Assert.Equal(allowed, MayQuotePemMarker(relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void GitIgnore_KeepsOfflinePrivateKeysOut_ButNotTheTestFixtureKeys()
    {
        // Security review 2026-10-25, L6: a private key file the signer writes into the repository by mistake is never added.
        var root = ProductAssemblies.RepositoryRoot;

        Assert.Equal("keys/release-prod-1.private.pem", Git(root, "check-ignore", "--no-index", "keys/release-prod-1.private.pem").Trim());
        Assert.Equal("tools/recovery-prod-1.private.pem", Git(root, "check-ignore", "--no-index", "tools/recovery-prod-1.private.pem").Trim());
        Assert.Equal(string.Empty, Git(root, [0, 1], "check-ignore", "--no-index", "tests/fixtures/signing/keys/test-content-1.private.pem").Trim());
    }

    private static string Git(string root, params string[] arguments) => Git(root, [0], arguments);

    private static string Git(string root, int[] exitCodes, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var git = System.Diagnostics.Process.Start(start)!;
        var output = git.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(exitCodes.Contains(git.ExitCode), $"git {string.Join(' ', arguments)} exited with {git.ExitCode}: {error}");
        return output.GetAwaiter().GetResult();
    }
}
