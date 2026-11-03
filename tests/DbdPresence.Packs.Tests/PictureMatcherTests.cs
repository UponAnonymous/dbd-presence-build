// Covers: FR-PACK-07 (import assigns by code, then by name; T_UI_ prefix and _Portrait suffix; code and name disagreeing is a conflict and nothing is
// guessed; the owner-pack names in every form), FR-PACK-12 (no free slot at 3 pictures; one file per character per import), FR-PACK-17 (size
// estimate: In use first, stops at the first picture that does not fit, owner copies and links use nothing). PLAN_V2_S5B section 2.7, S5b-5.
using System.Text.Json;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Packs.Tests;

public sealed class PictureMatcherTests
{
    private static readonly DateTimeOffset s_when = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string> OwnerPackNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRoster.FixturePath("roster", "import-names.json")));
        var data = new TheoryData<string, string>();
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            data.Add(file.GetProperty("stem").GetString()!, file.GetProperty("id").GetString()!);
        }

        return data;
    }

    private static ImportItem Item(string name, string? marker = null)
    {
        byte[] png = [.. FakePng.Make(64, 64, 200), .. System.Text.Encoding.UTF8.GetBytes(marker ?? name)];
        return new ImportItem(name, png, 64, 64, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)), null);
    }

    private static IReadOnlyList<PictureProposal> Match(string[] names, IReadOnlyDictionary<string, CharacterPictures>? existing = null) =>
        PictureMatcher.Match([.. names.Select(n => Item(n))], TestRoster.Embedded, existing);

    [Theory]
    [MemberData(nameof(OwnerPackNames))]
    public void OwnerPackNames_MatchByCode_InEveryForm(string stem, string id)
    {
        foreach (var file in new[] { stem + ".png", stem + "_Portrait.png", "T_UI_" + stem + "_Portrait.png", "t_ui_" + stem + "_portrait.PNG" })
        {
            var proposal = Assert.Single(Match([file]));

            Assert.Equal(MatchOutcome.ByCode, proposal.Outcome);
            Assert.Equal(id, proposal.Character!.Id);
        }
    }

    [Fact]
    public void CodeWithTheUiPrefixAndPortraitSuffix_IsStripped()
    {
        var proposal = Assert.Single(Match(["T_UI_K27_TheOnryo_Portrait.png"]));

        Assert.Equal(MatchOutcome.ByCode, proposal.Outcome);
        Assert.Equal("the-onryo", proposal.Character!.Id);
    }

    [Fact]
    public void ACodeAloneIsEnough_EvenWithAnOddName()
    {
        var proposal = Assert.Single(Match(["T_UI_K27_Whatever_Portrait.png"]));

        Assert.Equal(MatchOutcome.ByCode, proposal.Outcome);
        Assert.Equal("the-onryo", proposal.Character!.Id);
    }

    [Fact]
    public void NameWithoutACode_IsAByNameSuggestion()
    {
        var proposal = Assert.Single(Match(["TheOnryo.png"]));

        Assert.Equal(MatchOutcome.ByName, proposal.Outcome);
        Assert.Equal("the-onryo", proposal.Character!.Id);
    }

    [Fact]
    public void CodeAndNameDisagreeing_IsAConflict_NothingIsChosen()
    {
        var proposal = Assert.Single(Match(["K27_TheTrapper_Portrait.png"]));

        Assert.Equal(MatchOutcome.Conflict, proposal.Outcome);
        Assert.Equal("the-onryo", proposal.Character!.Id);
        Assert.Equal("the-trapper", proposal.Other!.Id);
        Assert.Contains("The code says", proposal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownNames_AreUnmatched_MapsAndRolesAreNotImported()
    {
        var proposals = Match(["holiday-card.png", "hawkins.png", "killer.png"]);

        Assert.Equal(MatchOutcome.Unmatched, proposals[0].Outcome);
        Assert.Equal(MatchOutcome.NotImported, proposals[1].Outcome);
        Assert.Equal("Map pictures aren't used yet.", proposals[1].Reason);
        Assert.Equal(MatchOutcome.NotImported, proposals[2].Outcome);
        Assert.Contains("Role icons", proposals[2].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeNotInTheRoster_FallsBackToTheName()
    {
        var proposal = Assert.Single(Match(["K99_TheOnryo.png"]));

        Assert.Equal(MatchOutcome.ByName, proposal.Outcome);
        Assert.Equal("the-onryo", proposal.Character!.Id);
    }

    [Fact]
    public void SecondFileForTheSameCharacter_NeedsAChoice()
    {
        var proposals = Match(["K27_TheOnryo.png", "T_UI_K27_TheOnryo_Portrait.png"]);

        Assert.Equal(MatchOutcome.ByCode, proposals[0].Outcome);
        Assert.Equal(MatchOutcome.SameCharacter, proposals[1].Outcome);
        Assert.Null(proposals[1].Character);
    }

    [Fact]
    public void SameBytesTwice_SecondIsNotImported()
    {
        var a = Item("K27_TheOnryo.png", "same");
        var b = Item("K01_TheTrapper.png", "same");

        var proposals = PictureMatcher.Match([a, b], TestRoster.Embedded);

        Assert.Equal(MatchOutcome.ByCode, proposals[0].Outcome);
        Assert.Equal(MatchOutcome.NotImported, proposals[1].Outcome);
        Assert.Equal("Same picture as K27_TheOnryo.png.", proposals[1].Reason);
    }

    [Fact]
    public void CharacterWithThreePictures_HasNoFreeSlot()
    {
        var items = "abc".Select(c => new PictureItem(new string(c, 64), null, 100, new string(c, 64) + ".png", null, PictureOrigin.Pc, s_when)).ToArray();
        var existing = new Dictionary<string, CharacterPictures> { ["character:the-onryo"] = new(items, null) };

        var proposals = Match(["K27_TheOnryo.png", "K01_TheTrapper.png"], existing);

        Assert.Equal(MatchOutcome.NoFreeSlot, proposals[0].Outcome);
        Assert.Equal("the-onryo", proposals[0].Character!.Id);
        Assert.NotEqual(MatchOutcome.NoFreeSlot, proposals[1].Outcome);
    }

    [Fact]
    public void PictureTheCharacterAlreadyHas_IsNotImportedAgain()
    {
        var item = Item("K27_TheOnryo.png");
        var existing = new Dictionary<string, CharacterPictures>
        {
            ["character:the-onryo"] = new([new PictureItem(item.Sha256, null, 100, item.Sha256 + ".png", null, PictureOrigin.Import, s_when)], null),
        };

        var proposal = Assert.Single(PictureMatcher.Match([item], TestRoster.Embedded, existing));

        Assert.Equal(MatchOutcome.NotImported, proposal.Outcome);
        Assert.Contains("already has", proposal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_AssignsNothing_ItOnlyProposes()
    {
        var existing = new Dictionary<string, CharacterPictures>();

        Match(["K27_TheOnryo.png"], existing);

        Assert.Empty(existing);
    }

    [Fact]
    public void EmptyImport_GivesNoProposals()
    {
        Assert.Empty(PictureMatcher.Match([], TestRoster.Embedded));
    }

    [Fact]
    public void Estimate_FitsEverything_WhenThereIsRoom()
    {
        var estimate = StorageEstimator.Estimate([new(10_000_000, false), new(5_000_000, true)], usedBytes: 12_000_000, limitBytes: PictureLimits.OnlineQuotaBytes);

        Assert.True(estimate.Fits);
        Assert.Equal(15_000_000, estimate.SaveBytes);
        Assert.Equal(2, estimate.FitCount);
        Assert.Equal(0, estimate.PreviewOnlyCount);
        Assert.Equal(PictureLimits.OnlineQuotaBytes - 12_000_000, estimate.FreeBytes);
    }

    [Fact]
    public void Estimate_StopsAtTheFirstPictureThatDoesNotFit_InUseFirst()
    {
        // 10 MB free: the in-use 6 MB picture is saved first, then the 5 MB one does not fit, so the 1 MB one after it stays preview-only too.
        var items = new EstimateItem[] { new(5_000_000, false), new(1_000_000, false), new(6_000_000, true) };

        var estimate = StorageEstimator.Estimate(items, usedBytes: 0, limitBytes: 10_000_000);

        Assert.False(estimate.Fits);
        Assert.Equal(6_000_000, estimate.SaveBytes);
        Assert.Equal(1, estimate.FitCount);
        Assert.Equal(2, estimate.PreviewOnlyCount);
    }

    [Fact]
    public void Estimate_OwnerCopiesAndLinks_UseNoSpace()
    {
        var estimate = StorageEstimator.Estimate([new(50_000_000, true, UsesQuota: false), new(1_000_000, false)], usedBytes: 59_000_000, limitBytes: PictureLimits.OnlineQuotaBytes);

        Assert.True(estimate.Fits);
        Assert.Equal(1_000_000, estimate.SaveBytes);
        Assert.Equal(2, estimate.FitCount);
    }

    [Fact]
    public void Estimate_OverTheLimitAlready_FitsNothing_AndEmptyIsFine()
    {
        var full = StorageEstimator.Estimate([new(1, true)], usedBytes: 70_000_000, limitBytes: PictureLimits.OnlineQuotaBytes);
        var none = StorageEstimator.Estimate([], 0, PictureLimits.OnlineQuotaBytes);

        Assert.Equal(0, full.FreeBytes);
        Assert.Equal(1, full.PreviewOnlyCount);
        Assert.True(none.Fits);
        Assert.Equal(0, none.SaveBytes);
    }
}
