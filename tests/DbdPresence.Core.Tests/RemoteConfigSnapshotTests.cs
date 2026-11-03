// Covers: FR-ONL-08 (announcement id per text, minimum-version comparison that never blocks presence: unreadable versions mean "no note").
using DbdPresence.Core.Online;

namespace DbdPresence.Core.Tests;

public sealed class RemoteConfigSnapshotTests
{
    private static RemoteConfigSnapshot With(string? banner = null, string? minimum = null) =>
        new(false, false, true, [], null, true, banner, minimum);

    [Fact]
    public void AnnouncementId_IsStablePerText_AndDiffersBetweenTexts()
    {
        var a = With(banner: "Maintenance on Friday.").AnnouncementId;
        var again = With(banner: "Maintenance on Friday.").AnnouncementId;
        var b = With(banner: "Maintenance on Saturday.").AnnouncementId;

        Assert.Equal(16, a!.Length);
        Assert.Equal(a, again);
        Assert.NotEqual(a, b);
        Assert.All(a, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.Null(With().AnnouncementId);
    }

    [Theory]
    [InlineData("0.1.0", "0.1.1", true)]
    [InlineData("0.1.0+abc1234", "0.1.0", false)]
    [InlineData("2.0.0-rc.1+abc", "2.0.1", true)]
    [InlineData("0.9.0", "0.10.0", true)]
    [InlineData("10.0.0", "9.99.99", false)]
    [InlineData("not a version", "1.0.0", false)]
    [InlineData("1", "2.0.0", false)]
    [InlineData("1.0.0", "x", false)]
    public void IsUpdateNeeded_ComparesNumericallyAndIgnoresSuffixes(string running, string minimum, bool expected) =>
        Assert.Equal(expected, With(minimum: minimum).IsUpdateNeeded(running));

    [Fact]
    public void WithoutAMinimum_NoUpdateIsEverNeeded()
    {
        Assert.False(With().IsUpdateNeeded("0.0.1"));
        Assert.False(RemoteConfigSnapshot.SafeDefaults.IsUpdateNeeded("0.0.1"));
    }
}
