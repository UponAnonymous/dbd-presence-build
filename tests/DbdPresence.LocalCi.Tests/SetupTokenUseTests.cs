// Covers: FR-CI-01 and FR-CI-06, local CI round 4 QA M-4 (R5, adopted from the reviewer's scratch test D7): the token the owner just pasted is the one the GitHub client uses to check it, before
// anything is saved; a token GitHub refuses is let go of again, so the saved one is used afterwards; the saved token is the one used when nothing is pasted. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class SetupTokenUseTests
{
    private const string Saved = "github_pat_11SAVEDSAVEDSAVEDSAVED_abcdefghijklmnopqrstuvwxyz";
    private const string Fresh = "github_pat_11FRESHFRESHFRESHFRESH_abcdefghijklmnopqrstuvwxyz";

    /// <summary>Mutant 405: the token the owner just pasted is the one the GitHub client uses for the check, before anything is saved.</summary>
    [Fact]
    public async Task ThePastedTokenIsTheOneUsedForTheCheck()
    {
        using var rig = new SetupRig(secrets: [SetupRig.GoodToken]);

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.NotEmpty(rig.Api.TokensSeen);
        Assert.Equal(SetupRig.GoodToken, rig.Api.TokensSeen[0]);
    }

    [Fact]
    public async Task WithASavedTokenAndNothingPasted_TheSavedOneIsUsed_ForEveryCall()
    {
        using var rig = new SetupRig(secrets: []);
        rig.Store.Save(new SecretText(Saved));

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.NotEmpty(rig.Api.TokensSeen);
        Assert.All(rig.Api.TokensSeen, t => Assert.Equal(Saved, t));
    }

    [Fact]
    public async Task ARefusedPastedToken_IsLetGoOf_SoTheNextTokenIsTheOneChecked_AndTheGoodOneIsTheOneKept()
    {
        using var rig = new SetupRig(secrets: ["github_pat_11BADBADBADBADBADBAD_abcdefghijklmnopqrstuvwxyz", Fresh]);
        rig.Api.RepoFailure = new GitHubApiException("Bad credentials", 401, isTransient: false);
        rig.Api.RepoFailureEndsAfter = 1;

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal("github_pat_11BADBADBADBADBADBAD_abcdefghijklmnopqrstuvwxyz", rig.Api.TokensSeen[0]);
        Assert.Equal(Fresh, rig.Api.TokensSeen[1]);
        Assert.All(rig.Api.TokensSeen.Skip(1), t => Assert.Equal(Fresh, t));
        Assert.Equal(Fresh, rig.Store.Load()!.Reveal());
    }

    [Fact]
    public async Task ARenewalTokenThatGitHubRefuses_IsLetGoOf_SoEveryLaterCallUsesTheSavedToken()
    {
        using var rig = new SetupRig(secrets: [Fresh]);
        rig.Store.Save(new SecretText(Saved));
        rig.Api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(3), IsPrivate: true);
        rig.Api.RepoCallsBeforeFailure = 1;
        rig.Api.RepoFailure = new GitHubApiException("Bad credentials", 401, isTransient: false);
        rig.Api.RepoFailureEndsAfter = 1;

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(Saved, rig.Api.TokensSeen[0]);
        Assert.Equal(Fresh, rig.Api.TokensSeen[1]);
        Assert.All(rig.Api.TokensSeen.Skip(2), t => Assert.Equal(Saved, t));
        Assert.True(rig.Api.TokensSeen.Count >= 3, "the test check should have asked GitHub after the renewal");
        Assert.Equal(Saved, rig.Store.Load()!.Reveal());
        Assert.Contains("GitHub did not accept that token", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARenewalTokenThatIsGood_ReplacesTheSavedOne_AfterItWasChecked()
    {
        using var rig = new SetupRig(secrets: [Fresh]);
        rig.Store.Save(new SecretText(Saved));
        rig.Api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(3), IsPrivate: true);

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(Saved, rig.Api.TokensSeen[0]);
        Assert.Equal(Fresh, rig.Api.TokensSeen[1]);
        Assert.Equal(Fresh, rig.Store.Load()!.Reveal());
        Assert.All(rig.Api.TokensSeen.Skip(1), t => Assert.Equal(Fresh, t));
    }

    [Fact]
    public async Task ASavedTokenThatStillWorks_IsKept_WhenNothingNewIsPasted()
    {
        using var rig = new SetupRig(secrets: []);
        rig.Store.Save(new SecretText(Saved));
        rig.Api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(3), IsPrivate: true);

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(Saved, rig.Store.Load()!.Reveal());
        Assert.Contains("Kept the saved token", rig.Console.AllText, StringComparison.Ordinal);
    }
}
