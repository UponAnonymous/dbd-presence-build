// Covers: NFR-PRIV-02 (logs carry no user name, profile path or machine name), security review L2. Pure: identity values are passed in; no real user is involved.
using DbdPresence.Core.Diagnostics;

namespace DbdPresence.Core.Tests.Diagnostics;

public sealed class LogRedactionTests
{
    private static readonly LogRedaction s_redaction = new(@"C:\Users\Alice Smith", "alice.smith", "ALICE-PC");

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\Alice Smith\AppData\Roaming\DBD Presence\settings.json'.", "AppData")]
    [InlineData(@"Access to the path 'c:\users\alice smith\x' is denied.", "x")]
    [InlineData("Access to the path 'C:/Users/Alice Smith/x' is denied.", "x")]
    public void TheProfilePath_IsReplaced_WholeAndWithoutCase(string message, string kept)
    {
        var text = s_redaction.Redact(message);

        Assert.DoesNotContain("Alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);
        Assert.Contains(kept, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUserNameAndTheMachineName_AreMaskedWherever_TheyAppear()
    {
        var text = s_redaction.Redact("alice.smith on ALICE-PC failed; Alice-pc again");

        Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<user> on <machine> failed; <machine> again", text);
    }

    [Theory]
    [InlineData(@"D:\Users\Someone Else\Documents\file.png")]
    [InlineData("/home/someone/file.png")]
    [InlineData("/Users/someone/file.png")]
    public void AnyOtherUserFolder_IsMasked_EvenWhenItIsNotTheCurrentUser(string message)
    {
        var text = s_redaction.Redact(message);

        Assert.DoesNotContain("omeone", text, StringComparison.Ordinal);
        Assert.Contains("<user>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANetworkHost_IsMasked()
    {
        var text = s_redaction.Redact(@"Cannot open \\fileserver01\share\pic.png");

        Assert.DoesNotContain("fileserver01", text, StringComparison.Ordinal);
        Assert.Contains(@"\\<host>\share", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextWithoutIdentity_IsUnchanged_AndNullGivesEmpty()
    {
        Assert.Equal("The roster could not be saved: IOException", s_redaction.Redact("The roster could not be saved: IOException"));
        Assert.Equal(string.Empty, s_redaction.Redact(null));
        Assert.Equal(string.Empty, s_redaction.Redact(string.Empty));
    }

    [Fact]
    public void ShortOrMissingIdentityValues_AreNotUsed_SoCommonLettersAreNotMasked()
    {
        var redaction = new LogRedaction(null, "ab", "");

        Assert.Equal("a table about abc", redaction.Redact("a table about abc"));
    }

    [Fact]
    public void Describe_KeepsTypeNames_Messages_AndFirstFrames_WithoutTheIdentity()
    {
        Exception error;
        try
        {
            try
            {
                throw new FileNotFoundException(@"Could not find 'C:\Users\Alice Smith\data.json' for alice.smith");
            }
            catch (FileNotFoundException inner)
            {
                throw new InvalidOperationException("Loading failed on ALICE-PC", inner);
            }
        }
        catch (InvalidOperationException caught)
        {
            error = caught;
        }

        var text = s_redaction.Describe(error);

        Assert.StartsWith("InvalidOperationException: Loading failed on <machine> <- FileNotFoundException: Could not find '%USERPROFILE%\\data.json' for <user>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Describe_KeepsTypeNames", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NamesEveryMemberOfAnAggregate_AndStopsAtTheDepthLimit()
    {
        var aggregate = new AggregateException(new IOException("one for alice.smith"), new TimeoutException("two"));
        var deep = new InvalidOperationException("level0", new InvalidOperationException("level1", new InvalidOperationException("level2", new InvalidOperationException("level3", new InvalidOperationException("level4", new InvalidOperationException("level5"))))));

        var text = s_redaction.Describe(aggregate);

        Assert.Contains("IOException: one for <user>", text, StringComparison.Ordinal);
        Assert.Contains("TimeoutException: two", text, StringComparison.Ordinal);
        Assert.Contains("level4", s_redaction.Describe(deep), StringComparison.Ordinal);
        Assert.DoesNotContain("level5", s_redaction.Describe(deep), StringComparison.Ordinal);
        _ = Assert.Throws<ArgumentNullException>(() => s_redaction.Describe(null!));
    }

    [Fact]
    public void TheLiveInstance_MasksTheRealUserOfThisTestRun()
    {
        var user = Environment.UserName;
        if (user.Length < 3)
        {
            return;
        }

        Assert.DoesNotContain(user, LogRedaction.Current.Redact($"failed for {user}"), StringComparison.OrdinalIgnoreCase);
    }
}
