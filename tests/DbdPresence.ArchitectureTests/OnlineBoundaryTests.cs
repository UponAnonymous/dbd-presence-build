// Covers: NFR-SEC-01, ADR-0016 (network boundary), PLAN_V2 §4.1: DbdPresence.Online is the only networking assembly, its host literals are
// the allow-listed ones, and custom certificate validation is banned everywhere.
namespace DbdPresence.ArchitectureTests;

public sealed class OnlineBoundaryTests
{
    [Fact]
    public void OnlineAssembly_ContainsOnlyAllowListedHostLiterals()
    {
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.Online"))
        {
            using var assembly = new AssemblyInspector(path);

            var violations = SafetyRules.HostLiterals(assembly, SafetyRules.AllowedOnlineHosts);

            Assert.True(violations.Count == 0, string.Join("\n  ", violations));
        }
    }

    [Fact]
    public void AllowedHosts_AreExactlyTheOwnersApiAndCdnHostsPlusDiscordAndLoopback()
    {
        string[] expected = ["127.0.0.1", "api-staging.arcavale.com", "api.arcavale.com", "cdn-staging.arcavale.com", "cdn.arcavale.com", "discord.com"];

        Assert.Equal(expected, SafetyRules.AllowedOnlineHosts.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OnlineAssembly_ReallyUsesTheNetworkCarveOut()
    {
        // Guards against a vacuous pass: the carve-out exists because Online references HTTP and sockets.
        foreach (var path in ProductAssemblies.PathsOf("DbdPresence.Online"))
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Contains("System.Net.Http", assembly.AssemblyReferences);
            Assert.Contains("System.Net.Sockets", assembly.AssemblyReferences);
            Assert.Empty(SafetyRules.Network(assembly));
        }
    }

    [Theory]
    [InlineData("DbdPresence.Core")]
    [InlineData("DbdPresence.Configuration")]
    [InlineData("DbdPresence.Discord")]
    [InlineData("DbdPresence.Vision")]
    [InlineData("DbdPresence.Packs")]
    [InlineData("DbdPresence.Presentation")]
    [InlineData("DbdPresence.Platform.Windows")]
    [InlineData("DbdPresence.ImageHelper")]
    [InlineData("DbdPresence.App")]
    public void EveryOtherAssembly_HasNoNetworkReferencesAndNoOnlineReferenceExceptTheApp(string project)
    {
        foreach (var path in ProductAssemblies.PathsOf(project))
        {
            using var assembly = new AssemblyInspector(path);

            Assert.Empty(SafetyRules.Network(assembly, new HashSet<string>(), []));
            Assert.True(
                project == "DbdPresence.App" || !assembly.AssemblyReferences.Contains("DbdPresence.Online"),
                $"{project} must not reference DbdPresence.Online");
        }
    }

    [Fact]
    public void HostLiteralRule_FlagsUnlistedHostsAndAcceptsListedOnes()
    {
        using var self = new AssemblyInspector(typeof(OnlineBoundaryTests).Assembly.Location);

        var violations = SafetyRules.HostLiterals(self, SafetyRules.AllowedOnlineHosts);

        Assert.Contains(violations, v => v.Contains("Hosts.Bad", StringComparison.Ordinal) && v.Contains("'evil.example.net'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Hosts.Bad", StringComparison.Ordinal) && v.Contains("'tracker.example.com'", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("Hosts.Good", StringComparison.Ordinal));
    }

    internal static class Hosts
    {
        internal static string[] Bad() => ["https://evil.example.net/x", "tracker.example.com"];

        internal static string[] Good() => ["https://api.arcavale.com/v1/config", "https://discord.com/oauth2/authorize", "http://127.0.0.1:1/callback"];
    }
}
