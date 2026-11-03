// Covers: FR-CI-06 (no token, runner configuration or password in a log line, a message or the state file), local CI round 3: QA R2 (every shape of a GitHub token is removed, at the edges of the
// pattern), red-team L10 (the redactor keeps only the secrets that are current: a job's runner configuration is dropped when the job is over). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class RedactorTests
{
    private const string Body = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123456789_";

    /// <summary>R2: every prefix GitHub gives a token is recognised without being registered.</summary>
    [Theory]
    [InlineData("github_pat_")]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghu_")]
    [InlineData("ghs_")]
    [InlineData("ghr_")]
    public void ATokenOfEveryGitHubShape_IsRemovedWithoutBeingRegistered(string prefix)
    {
        var redactor = new Redactor();
        var token = prefix + Body;

        Assert.Equal("before [hidden] after", redactor.Redact($"before {token} after"));
        Assert.Equal("[hidden]", redactor.Redact(token));
        Assert.Equal("Authorization: Bearer [hidden]", redactor.Redact($"Authorization: Bearer {token}"));
        Assert.Equal("token=[hidden]&x=1", redactor.Redact($"token={token}&x=1"));
    }

    /// <summary>R2, the edge: 16 characters after the prefix are enough, 15 are not (the pattern is {16,}).</summary>
    [Theory]
    [InlineData("ghp_", 16, true)]
    [InlineData("ghp_", 15, false)]
    [InlineData("github_pat_", 16, true)]
    [InlineData("github_pat_", 15, false)]
    [InlineData("ghs_", 40, true)]
    public void TheShortestToken_IsExactlySixteenCharactersAfterThePrefix(string prefix, int length, bool hidden)
    {
        var redactor = new Redactor();
        var token = prefix + new string('A', length);

        var text = redactor.Redact("x " + token + " y");

        Assert.Equal(hidden ? "x [hidden] y" : "x " + token + " y", text);
    }

    [Fact]
    public void ATokenFollowedByPunctuation_LosesOnlyTheTokenCharacters()
    {
        var redactor = new Redactor();

        Assert.Equal("(see [hidden].)", redactor.Redact("(see ghp_AAAAAAAAAAAAAAAAAAAA.)"));
        Assert.Equal("[hidden]\n[hidden]", redactor.Redact("ghp_AAAAAAAAAAAAAAAAAAAA\ngho_BBBBBBBBBBBBBBBBBBBB"));
    }

    [Theory]
    [InlineData("ghx_AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("github_pat-AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ghp-AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("pat_AAAAAAAAAAAAAAAAAAAA")]
    public void TextThatIsNotAGitHubTokenShape_IsLeftAlone(string text) => Assert.Equal(text, new Redactor().Redact(text));

    [Fact]
    public void NullEmptyAndPlainTextPassThrough()
    {
        var redactor = new Redactor();

        Assert.Equal(string.Empty, redactor.Redact(string.Empty));
        Assert.Equal("plain words", redactor.Redact("plain words"));
        redactor.Register(string.Empty);
        Assert.Equal(0, redactor.SecretCount);
    }

    // ---- L10: only the current secrets are kept

    [Fact]
    public void AScopedSecret_IsHiddenWhileItsScopeIsOpen_AndForgottenWhenItEnds()
    {
        var redactor = new Redactor();
        var scope = redactor.RegisterScoped(new SecretText("one-jobs-configuration"));

        Assert.Equal("a [hidden] b", redactor.Redact("a one-jobs-configuration b"));
        Assert.Equal(1, redactor.SecretCount);

        scope.Dispose();

        Assert.Equal("a one-jobs-configuration b", redactor.Redact("a one-jobs-configuration b"));
        Assert.Equal(0, redactor.SecretCount);
    }

    [Fact]
    public void ThousandsOfJobs_DoNotMakeTheListGrow()
    {
        var redactor = new Redactor();
        redactor.Register("the-token-that-stays");

        for (var job = 0; job < 2000; job++)
        {
            using var scope = redactor.RegisterScoped(new SecretText($"configuration-of-job-{job}"));
            Assert.Equal(2, redactor.SecretCount);
        }

        Assert.Equal(1, redactor.SecretCount);
        Assert.Equal("[hidden] configuration-of-job-1", redactor.Redact("the-token-that-stays configuration-of-job-1"));
    }

    [Fact]
    public void ASecretRegisteredForGood_StaysAfterAScopeForTheSameValueEnds()
    {
        var redactor = new Redactor();
        var scope = redactor.RegisterScoped(new SecretText("same-value-everywhere"));
        redactor.Register("same-value-everywhere");

        scope.Dispose();

        Assert.Equal("[hidden]", redactor.Redact("same-value-everywhere"));
        Assert.Equal(1, redactor.SecretCount);
    }

    [Fact]
    public void ASecretRegisteredForGood_BeforeAScopeIsOpened_StaysToo()
    {
        var redactor = new Redactor();
        redactor.Register("kept-for-good");
        var scope = redactor.RegisterScoped(new SecretText("kept-for-good"));

        scope.Dispose();

        Assert.Equal("[hidden]", redactor.Redact("kept-for-good"));
    }

    [Fact]
    public void TwoScopesForOneValue_KeepItUntilBothHaveEnded()
    {
        var redactor = new Redactor();
        var first = redactor.RegisterScoped(new SecretText("shared-by-two"));
        var second = redactor.RegisterScoped(new SecretText("shared-by-two"));

        first.Dispose();
        Assert.Equal("[hidden]", redactor.Redact("shared-by-two"));
        second.Dispose();

        Assert.Equal("shared-by-two", redactor.Redact("shared-by-two"));
        Assert.Equal(0, redactor.SecretCount);
    }

    [Fact]
    public void DisposingAScopeTwice_DoesNotReleaseAnotherScopesHold()
    {
        var redactor = new Redactor();
        var first = redactor.RegisterScoped(new SecretText("shared-by-two"));
        var second = redactor.RegisterScoped(new SecretText("shared-by-two"));

        first.Dispose();
        first.Dispose();

        Assert.Equal("[hidden]", redactor.Redact("shared-by-two"));
        second.Dispose();
        Assert.Equal(0, redactor.SecretCount);
    }

    [Fact]
    public void ALongerScopedSecret_IsRemovedWhole_EvenWithAShorterOneInsideIt()
    {
        var redactor = new Redactor();
        redactor.Register("abc");
        using var scope = redactor.RegisterScoped(new SecretText("abc-def-long-configuration"));

        Assert.Equal("x [hidden] y", redactor.Redact("x abc-def-long-configuration y"));
    }

    [Fact]
    public async Task SecretsRegisteredAndForgottenFromManyThreads_NeverBreakARedaction()
    {
        var redactor = new Redactor();
        redactor.Register("permanent-secret-value");
        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(
            () =>
            {
                for (var i = 0; i < 500; i++)
                {
                    using var scope = redactor.RegisterScoped(new SecretText($"worker-{worker}-secret-{i}"));
                    var text = redactor.Redact($"x permanent-secret-value y worker-{worker}-secret-{i} z");
                    Assert.Equal("x [hidden] y [hidden] z", text);
                }
            },
            TestContext.Current.CancellationToken)).ToArray();

        await Task.WhenAll(workers).OrFail();

        Assert.Equal(1, redactor.SecretCount);
    }

    [Fact]
    public void NullSecretsAreRefused()
    {
        var redactor = new Redactor();

        Assert.Throws<ArgumentNullException>(() => redactor.Register((SecretText)null!));
        Assert.Throws<ArgumentNullException>(() => redactor.RegisterScoped(null!));
        Assert.Throws<ArgumentNullException>(() => redactor.Describe(null!));
    }
}
