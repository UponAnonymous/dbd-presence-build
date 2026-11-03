// Covers: FR-RPC-04 (limits), FR-RPC-09 (buttons), FR-ART-01 (image references), FR-RPC-10 (activity type).
using DbdPresence.Core.Presence;

namespace DbdPresence.Core.Tests.Presence;

public sealed class ActivityValidatorTests
{
    private static ActivityValidationResult Validate(ActivityDraft draft) => ActivityValidator.Validate(draft);

    [Fact]
    public void Validate_TypicalDraft_PassesUnchanged()
    {
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var result = Validate(new ActivityDraft
        {
            Kind = ActivityKind.Competing,
            Details = "In a trial",
            State = "3 gens left",
            StartTimestamp = start,
            LargeImage = "role_killer",
            LargeText = "Killer",
            SmallImage = "https://example.com/a.png",
            SmallText = "App",
            Buttons = [new ButtonDraft("My stream", "https://twitch.tv/x")],
        });

        Assert.Empty(result.Issues);
        var activity = result.Activity;
        Assert.Equal(ActivityKind.Competing, activity.Kind);
        Assert.Equal("In a trial", activity.Details);
        Assert.Equal("3 gens left", activity.State);
        Assert.Equal(start, activity.StartTimestamp);
        Assert.Equal("role_killer", activity.LargeImage);
        Assert.Equal("https://example.com/a.png", activity.SmallImage);
        Assert.Equal(new ActivityButton("My stream", "https://twitch.tv/x"), Assert.Single(activity.Buttons));
    }

    [Theory]
    [InlineData("a")]
    [InlineData(" b ")]
    public void Validate_TextBelowMinimum_IsOmittedWithIssue(string details)
    {
        var result = Validate(new ActivityDraft { Details = details });

        Assert.Null(result.Activity.Details);
        Assert.Contains(result.Issues, i => i.Field == ActivityField.Details && i.Action == ValidationAction.Omitted);
    }

    [Fact]
    public void Validate_LongText_IsTruncatedWithIssue()
    {
        var result = Validate(new ActivityDraft { Details = new string('x', 300) });

        Assert.True(result.Activity.Details!.Length <= DiscordLimits.TextMax);
        Assert.Contains(result.Issues, i => i.Field == ActivityField.Details && i.Action == ValidationAction.Truncated);
    }

    [Fact]
    public void Validate_InternalWhitespace_IsCollapsed()
    {
        Assert.Equal("In a lobby", Validate(new ActivityDraft { Details = "  In   a\tlobby " }).Activity.Details);
    }

    [Theory]
    [InlineData("Role_Killer")]
    [InlineData("has space")]
    [InlineData("http://insecure.example/a.png")]
    [InlineData("file:///C:/img.png")]
    [InlineData("C:\\img.png")]
    [InlineData("https://")]
    public void Validate_InvalidImageReference_IsOmitted(string image)
    {
        var result = Validate(new ActivityDraft { Details = "Hi there", LargeImage = image, LargeText = "Text" });

        Assert.Null(result.Activity.LargeImage);
        Assert.Null(result.Activity.LargeText);
        Assert.Contains(result.Issues, i => i.Field == ActivityField.LargeImage);
        Assert.Contains(result.Issues, i => i.Field == ActivityField.LargeText);
    }

    [Fact]
    public void Validate_ImageReferenceOverLengthLimit_IsOmitted()
    {
        var result = Validate(new ActivityDraft { Details = "Hi there", LargeImage = "https://example.com/" + new string('a', 300) });

        Assert.Null(result.Activity.LargeImage);
    }

    [Fact]
    public void Validate_MoreThanTwoButtons_KeepsFirstTwo()
    {
        var result = Validate(new ActivityDraft
        {
            Details = "Hi there",
            Buttons = [new("One", "https://a.example"), new("Two", "https://b.example"), new("Three", "https://c.example")],
        });

        Assert.Equal(["One", "Two"], result.Activity.Buttons.Select(b => b.Label));
        Assert.Contains(result.Issues, i => i.Field == ActivityField.Button);
    }

    [Theory]
    [InlineData("", "https://a.example")]
    [InlineData("This label is definitely longer than 32", "https://a.example")]
    [InlineData("ééééééééééééééééé", "https://a.example")] // 17 chars, 34 bytes > 31
    [InlineData("Watch", "http://a.example")]
    [InlineData("Watch", "javascript:alert(1)")]
    [InlineData("Watch", "https://a.example/ with space")]
    public void Validate_InvalidButton_IsOmitted(string label, string url)
    {
        var result = Validate(new ActivityDraft { Details = "Hi there", Buttons = [new(label, url)] });

        Assert.Empty(result.Activity.Buttons);
        Assert.Contains(result.Issues, i => i.Field == ActivityField.Button);
    }

    [Fact]
    public void Validate_ButtonLabelAtByteLimit_IsAccepted()
    {
        var label = new string('a', DiscordLimits.ButtonLabelMaxBytes);

        Assert.Single(Validate(new ActivityDraft { Details = "Hi there", Buttons = [new(label, "https://a.example")] }).Activity.Buttons);
    }

    [Fact]
    public void Validate_UndefinedActivityKind_FallsBackToPlaying()
    {
        Assert.Equal(ActivityKind.Playing, Validate(new ActivityDraft { Details = "Hi there", Kind = (ActivityKind)1 }).Activity.Kind);
    }

    [Fact]
    public void Validate_TimestampBeforeEpoch_IsOmitted()
    {
        var result = Validate(new ActivityDraft { Details = "Hi there", StartTimestamp = DateTimeOffset.UnixEpoch.AddSeconds(-1) });

        Assert.Null(result.Activity.StartTimestamp);
    }

    [Fact]
    public void ValidatedActivity_EqualityIsStructural()
    {
        ActivityDraft Draft() => new() { Details = "Same", Buttons = [new("Go", "https://a.example")] };

        var a = Validate(Draft()).Activity;
        var b = Validate(Draft()).Activity;
        var c = Validate(Draft() with { State = "Different" }).Activity;

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData("  a   b  ", "a b")]
    [InlineData("x\u202Ey", "xy")]
    [InlineData("tab\there\r\nnow", "tab here now")]
    [InlineData("\u0000\u200B", "")]
    [InlineData(null, "")]
    public void ActivityText_Clean_DropsInvisibleControlsAndCollapsesWhitespace(string? raw, string expected)
    {
        Assert.Equal(expected, ActivityText.Clean(raw));
    }
}
