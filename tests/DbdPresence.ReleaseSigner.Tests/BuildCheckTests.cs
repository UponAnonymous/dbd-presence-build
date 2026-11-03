// Covers: threat-model review 2026-10-25 TM-02 (a staging-configured package is never signed into a production feed), FR-ONL-10 (release
// feeds), ADR-0016 (one compiled-in service per build), NFR-SVC-05. The packages here hold small stand-ins for DbdPresence.Online.dll made
// with System.Reflection.Emit: the host the build uses is a code literal (ldstr), the other one only an unused constant, as in the real
// ServiceEnvironment class.
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.ReleaseSigner.Tests;

/// <summary>Stand-ins for the two builds of DbdPresence.Online.dll.</summary>
internal static class OnlineAssemblies
{
    private const string ProductionHost = "api.arcavale.com";
    private const string StagingHost = "api-staging.arcavale.com";

    /// <summary>A production build: it loads the production host; the staging host is only an unused constant.</summary>
    public static byte[] Production { get; } = Build(ProductionHost, StagingHost);

    /// <summary>A staging build (the default <c>OnlineEnvironment</c>): it loads the staging host.</summary>
    public static byte[] Staging { get; } = Build(StagingHost, ProductionHost);

    /// <summary>An assembly whose code names neither host.</summary>
    public static byte[] Neither { get; } = Build("example", "example-too");

    private static byte[] Build(string usedHost, string unusedConstant)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("DbdPresence.Online"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("DbdPresence.Online");
        var type = module.DefineType("DbdPresence.Online.ServiceEnvironment", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        type.DefineField("OtherHost", typeof(string), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault)
            .SetConstant(unusedConstant);
        var il = type.DefineMethod("ApiHost", MethodAttributes.Public | MethodAttributes.Static, typeof(string), Type.EmptyTypes).GetILGenerator();
        il.Emit(OpCodes.Ldstr, usedHost);
        il.Emit(OpCodes.Ret);
        type.CreateType();
        using var stream = new MemoryStream();
        assembly.Save(stream);
        return stream.ToArray();
    }
}

public sealed class BuildCheckTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdp-buildcheck-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly StringWriter _out = new();
    private readonly Prompt _prompt = new();

    public BuildCheckTests() => Directory.CreateDirectory(_root);

    private string Keys => Path.Combine(_root, "keys");

    private string Packages => Path.Combine(_root, "vpk");

    public void Dispose()
    {
        _out.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Read_TellsTheBuildFromTheHostItsCodeUses_NotFromAnUnusedConstant()
    {
        Assert.Equal(PackagedService.Production, BuildCheck.Read(TestPackages.Write(Packages, "1.2.0", onlineAssembly: OnlineAssemblies.Production)));
        Assert.Equal(PackagedService.Staging, BuildCheck.Read(TestPackages.Write(Packages, "1.3.0", onlineAssembly: OnlineAssemblies.Staging)));
    }

    [Fact]
    public void Read_RefusesAPackageItCannotCheck()
    {
        var none = TestPackages.Write(Packages, "1.0.0");
        Assert.Contains("0 copies of DbdPresence.Online.dll", Assert.Throws<InvalidDataException>(() => BuildCheck.Read(none)).Message, StringComparison.Ordinal);

        var neither = TestPackages.Write(Packages, "1.1.0", onlineAssembly: OnlineAssemblies.Neither);
        Assert.Contains("neither", Assert.Throws<InvalidDataException>(() => BuildCheck.Read(neither)).Message, StringComparison.Ordinal);

        var junk = TestPackages.Write(Packages, "1.2.0", onlineAssembly: Encoding.ASCII.GetBytes("MZ not really an assembly"));
        Assert.Throws<InvalidDataException>(() => BuildCheck.Read(junk));

        var notZip = Path.Combine(Packages, "DbdPresence-1.3.0-stable-full.nupkg");
        File.WriteAllBytes(notZip, [1, 2, 3, 4]);
        Assert.Throws<InvalidDataException>(() => BuildCheck.Read(notZip));
    }

    [Fact]
    public void SignFeed_ForProduction_RefusesAStagingBuild_AndSignsNothing()
    {
        Make("release-prod-1");
        TestPackages.Write(Packages, "1.1.0", onlineAssembly: OnlineAssemblies.Production);
        TestPackages.Write(Packages, "1.2.0", onlineAssembly: OnlineAssemblies.Staging);

        var refused = Assert.Throws<SignerException>(() => SignFeed("production", "feed.json"));

        Assert.Contains("DbdPresence-1.2.0-stable-full.nupkg is a staging build", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Release workflow", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "feed.json")));
    }

    [Fact]
    public void SignFeed_RefusesAPackageWithoutTheOnlineAssembly_AndAWrongBuildForStaging()
    {
        Make("release-prod-1");
        TestPackages.Write(Packages, "1.2.0");
        Assert.Contains("cannot be checked", Assert.Throws<SignerException>(() => SignFeed("production", "feed.json")).Message, StringComparison.Ordinal);

        var staging = Path.Combine(_root, "staging-vpk");
        TestPackages.Write(staging, "1.2.0", onlineAssembly: OnlineAssemblies.Production);
        Assert.Contains("production build", Assert.Throws<SignerException>(() => SignFeed("staging", "staging.json", staging)).Message, StringComparison.Ordinal);
        Assert.Throws<SignerException>(() => SignFeed("somewhere", "other.json"));
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public void SignFeed_ChecksOnlyTheThreePackagesItLists()
    {
        Make("release-prod-1");
        TestPackages.Write(Packages, "0.9.0", onlineAssembly: OnlineAssemblies.Staging);
        foreach (var version in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            TestPackages.Write(Packages, version, onlineAssembly: OnlineAssemblies.Production);
        }

        _prompt.Answers.Enqueue(Passphrase);
        Assert.NotEmpty(SignFeed("production", "feed.json"));
    }

    [Fact]
    public void Verify_FailsWhenAProductionFeedListsAStagingBuild()
    {
        var release = Make("release-prod-1");
        var staging = Path.Combine(_root, "staging-vpk");
        TestPackages.Write(staging, "1.2.0", onlineAssembly: OnlineAssemblies.Staging);
        _prompt.Answers.Enqueue(Passphrase);
        SignFeed("staging", "feed.json", staging);
        var trust = Path.Combine(_root, "trust.json");
        File.WriteAllText(trust, $$"""{"staging":[{"kid":"release-prod-1","purposes":["releases"],"spki":"{{release.SpkiText}}"}],"production":[{"kid":"release-prod-1","purposes":["releases"],"spki":"{{release.SpkiText}}"}]}""");

        Assert.True(Commands().Verify(Path.Combine(_root, "feed.json"), trust, "staging", staging));
        Assert.False(Commands().Verify(Path.Combine(_root, "feed.json"), trust, "production", staging));
        Assert.Contains("WRONG BUILD", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_SignFeedNeedsTheSet()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, Cli.Run(["sign-feed", "--key", Path.Combine(Keys, "release-prod-1.private.pem"), "--channel", "stable", "--packages", Packages, "--out", Path.Combine(_root, "feed.json")], output, error, _prompt, _time));
        Assert.Contains("--set is required", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckBuild_PassesOnlyWhenEveryPackageIsABuildForTheSet()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        TestPackages.Write(Packages, "1.2.0", onlineAssembly: OnlineAssemblies.Production);
        string[] production = ["check-build", "--packages", Packages, "--channel", "stable", "--set", "production"];

        Assert.Equal(0, Cli.Run(production, output, error, _prompt, _time));
        Assert.Contains("DbdPresence-1.2.0-stable-full.nupkg: a production build", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Cli.Run(["check-build", "--packages", Packages, "--channel", "stable", "--set", "staging"], output, error, _prompt, _time));

        TestPackages.Write(Packages, "1.3.0", onlineAssembly: OnlineAssemblies.Staging);
        Assert.Equal(1, Cli.Run(production, output, error, _prompt, _time));
        Assert.Contains("DbdPresence-1.3.0-stable-full.nupkg: WRONG BUILD", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Cli.Run(["check-build", "--packages", Path.Combine(_root, "empty"), "--channel", "stable", "--set", "production"], output, error, _prompt, _time));
    }

    private Commands Commands() => new(_out, _prompt, _time);

    private PublicKeyEntry Make(string kid)
    {
        _prompt.Answers.Enqueue(Passphrase);
        _prompt.Answers.Enqueue(Passphrase);
        return Commands().KeyGen(OfflineKeyKind.Release, kid, Keys);
    }

    private string SignFeed(string set, string output, string? packages = null) =>
        Commands().SignFeed(Path.Combine(Keys, "release-prod-1.private.pem"), null, "stable", set, packages ?? Packages, Path.Combine(_root, output), null, null, null, null);

    private sealed class Prompt : IPassphrasePrompt
    {
        public Queue<string> Answers { get; } = new();

        public string Read(string prompt) => Answers.Count > 0 ? Answers.Dequeue() : throw new InvalidOperationException("No scripted answer for: " + prompt);
    }
}
