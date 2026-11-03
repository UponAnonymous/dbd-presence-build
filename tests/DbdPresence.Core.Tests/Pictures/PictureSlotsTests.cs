// Covers: FR-PACK-12 (default portrait always there; at most 3 own pictures; own pictures only on character keys; every saved file under 2 MB),
// D28 (owner rules), O-1 and O-3.
using DbdPresence.Core.Pictures;

namespace DbdPresence.Core.Tests.Pictures;

public sealed class PictureSlotsTests
{
    private const string Key = "character:the-onryo";
    private static readonly DateTimeOffset s_when = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static PictureItem File(char c, long bytes = 1000, PictureOrigin origin = PictureOrigin.Pc)
    {
        var sha = new string(c, 64);
        return new PictureItem(sha, null, bytes, sha + ".png", null, origin, s_when);
    }

    [Fact]
    public void Add_FourthOwnPicture_IsRefusedWithAReason()
    {
        var pictures = CharacterPictures.Empty;
        foreach (var c in "abc")
        {
            var result = PictureSlots.Add(pictures, Key, File(c), makeInUse: false);
            Assert.True(result.Succeeded);
            pictures = result.Pictures;
        }

        var fourth = PictureSlots.Add(pictures, Key, File('d'), makeInUse: true);

        Assert.False(fourth.Succeeded);
        Assert.Equal(SlotProblem.Full, fourth.Problem);
        Assert.Equal("This character has 3 of your pictures. Remove one to add another.", fourth.Message);
        Assert.Equal(3, fourth.Pictures.Items.Count);
        Assert.Same(pictures, fourth.Pictures);
    }

    [Fact]
    public void Add_LinksCountAgainstTheSameThree()
    {
        var pictures = PictureSlots.Add(null, Key, File('a'), true).Pictures;
        pictures = PictureSlots.Add(pictures, Key, PictureSlots.LinkItem("https://example.com/a.png", s_when)!, false).Pictures;
        pictures = PictureSlots.Add(pictures, Key, PictureSlots.LinkItem("https://example.com/b.png", s_when)!, false).Pictures;

        Assert.Equal(SlotProblem.Full, PictureSlots.Add(pictures, Key, File('b'), false).Problem);
    }

    [Theory]
    [InlineData("role:killer")]
    [InlineData("role:survivor")]
    [InlineData("map:hawkins")]
    [InlineData("app:mark")]
    [InlineData("character:generic")]
    [InlineData("character:generic-killer")]
    [InlineData("character:generic-survivor")]
    [InlineData("character:")]
    [InlineData("character:Not Valid")]
    [InlineData("the-onryo")]
    public void Add_RefusesKeysThatAreNotACharacter(string key)
    {
        var result = PictureSlots.Add(null, key, File('a'), true);

        Assert.Equal(SlotProblem.NotACharacterKey, result.Problem);
        Assert.Empty(result.Pictures.Items);
    }

    [Fact]
    public void Add_EnforcesTheTwoMegabyteRule_UnderTheLimitPasses()
    {
        Assert.True(PictureSlots.Add(null, Key, File('a', PictureLimits.MaxFileBytes - 1), true).Succeeded);
        Assert.Equal(SlotProblem.InvalidPicture, PictureSlots.Add(null, Key, File('b', PictureLimits.MaxFileBytes), true).Problem);
        Assert.Equal(SlotProblem.InvalidPicture, PictureSlots.Add(null, Key, File('c', PictureLimits.MaxFileBytes + 1), true).Problem);
        Assert.Equal(SlotProblem.InvalidPicture, PictureSlots.Add(null, Key, File('d', -1), true).Problem);
        Assert.False(PictureLimits.FitsFileLimit(PictureLimits.MaxFileBytes));
        Assert.False(PictureLimits.FitsFileLimit(0));
        Assert.True(PictureLimits.FitsFileLimit(PictureLimits.MaxFileBytes - 1));
    }

    [Fact]
    public void Add_RefusesBadHashFileNameAndLinkMix()
    {
        var badHash = File('a') with { Sha256 = "xyz" };
        var wrongFile = File('a') with { File = new string('b', 64) + ".png" };
        var fileWithLink = File('a') with { Link = "https://example.com/a.png" };
        var linkWithFile = PictureSlots.LinkItem("https://example.com/a.png", s_when)! with { File = new string('a', 64) + ".png" };

        Assert.All([badHash, wrongFile, fileWithLink, linkWithFile], item => Assert.Equal(SlotProblem.InvalidPicture, PictureSlots.Add(null, Key, item, true).Problem));
    }

    [Fact]
    public void Add_SamePictureTwice_IsADuplicate_AndAddingMakesItInUseWhenAsked()
    {
        var first = PictureSlots.Add(null, Key, File('a'), makeInUse: true);
        var again = PictureSlots.Add(first.Pictures, Key, File('a'), true);
        var second = PictureSlots.Add(first.Pictures, Key, File('b'), makeInUse: false);

        Assert.Equal(PictureUse.Personal(new string('a', 64)), first.Pictures.Use);
        Assert.Equal(SlotProblem.Duplicate, again.Problem);
        Assert.Equal(PictureUse.Personal(new string('a', 64)), second.Pictures.Use);
        Assert.Equal(2, second.Pictures.Items.Count);
    }

    [Fact]
    public void Remove_PictureInUse_ReturnsToTheDefaultPortrait()
    {
        var pictures = PictureSlots.Add(PictureSlots.Add(null, Key, File('a'), true).Pictures, Key, File('b'), false).Pictures;

        var removed = PictureSlots.Remove(pictures, new string('a', 64));

        Assert.True(removed.Succeeded);
        Assert.Null(removed.Pictures.Use);
        Assert.Equal([new string('b', 64)], removed.Pictures.Items.Select(i => i.Sha256));
    }

    [Fact]
    public void Remove_OtherPicture_KeepsTheChoice_AndUnknownIsNotFound()
    {
        var pictures = PictureSlots.Add(PictureSlots.Add(null, Key, File('a'), true).Pictures, Key, File('b'), false).Pictures;

        var removed = PictureSlots.Remove(pictures, new string('b', 64));
        var missing = PictureSlots.Remove(pictures, new string('z', 64));

        Assert.Equal(PictureUse.Personal(new string('a', 64)), removed.Pictures.Use);
        Assert.Equal(SlotProblem.NotFound, missing.Problem);
    }

    [Fact]
    public void Use_DefaultPortraitIsAlwaysAvailable_OwnerEntryAndUnknownPictureAreChecked()
    {
        var pictures = PictureSlots.Add(null, Key, File('a'), true).Pictures;

        Assert.Null(PictureSlots.Use(pictures, null).Pictures.Use);
        Assert.Equal(SlotProblem.NotFound, PictureSlots.Use(pictures, PictureUse.Personal(new string('q', 64))).Problem);
        Assert.Equal(PictureUse.Owner("p", "e"), PictureSlots.Use(pictures, PictureUse.Owner("p", "e")).Pictures.Use);
        Assert.Equal(SlotProblem.InvalidPicture, PictureSlots.Use(pictures, new PictureUse(PictureUseKind.Owner, null, "", null)).Problem);
    }

    [Fact]
    public void LinkItem_StoresTheNormalisedLinkWithNoFileAndNoBytes()
    {
        var item = PictureSlots.LinkItem("  HTTPS://Example.com/a.png ", s_when)!;

        Assert.Equal(PictureOrigin.Link, item.Origin);
        Assert.Equal("https://example.com/a.png", item.Link);
        Assert.Null(item.File);
        Assert.Equal(0, item.Bytes);
        Assert.Equal(PictureSlots.HashOfLink("https://example.com/a.png"), item.Sha256);
        Assert.Null(PictureSlots.LinkItem("http://example.com/a.png", s_when));
        Assert.Null(PictureSlots.LinkItem("https://127.0.0.1/a.png", s_when));
    }

    [Fact]
    public void Limits_MatchTheOwnerRules()
    {
        Assert.Equal(3, PictureLimits.MaxOwnPerCharacter);
        Assert.Equal(2 * 1024 * 1024, PictureLimits.MaxFileBytes);
        Assert.Equal(60 * 1024 * 1024, PictureLimits.OnlineQuotaBytes);
        Assert.True(PictureLimits.IsSize(512));
        Assert.True(PictureLimits.IsSize(1024));
        Assert.False(PictureLimits.IsSize(256));
    }
}
