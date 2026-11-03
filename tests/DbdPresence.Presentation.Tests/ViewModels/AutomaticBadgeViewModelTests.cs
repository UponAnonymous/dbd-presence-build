// Covers: FR-AUTO-01 (Automatic is offered for the small badge only, never for the big picture), docs/design/whole-app.md §4.1 and §8 (plain words next to the
// option, no pretend values), D29 (the page shows live values only: there is no "preview values" picker), FR-AUTO-06 (hook and down pictures need live match
// details), FR-VIS-02 (Private style never shows status or result pictures), FR-AUTO-07 (the hook count is a detail the user can add).
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Core.Templates;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class AutomaticBadgeViewModelTests
{
    private static readonly AppInfo s_app = new("1.0.0", "abc1234", IsSigned: false, "settings", "data");

    [Fact]
    public void TheSmallBadge_OffersAutomaticFirst_AndTheBigPictureNever()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        Assert.Equal(["Automatic", "My role", "App logo", "None"], kit.Status.SmallBadges.Select(c => c.Label));
        Assert.DoesNotContain(kit.Status.BigPictures, c => c.Label == "Automatic");
        Assert.DoesNotContain(kit.Studio.ImageSources, c => c.Value == ImageSource.Automatic);
        Assert.Equal(ImageSource.Automatic, kit.Studio.SmallImageSources[0].Value);
    }

    [Fact]
    public void ChoosingAutomatic_AppliesToEveryMoment_ButNotToThePrivateTextSet_AndTurnsTheBadgeOn()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        kit.Status.SelectSmallBadgeCommand.Execute(SmallBadge.None);

        kit.Status.SelectSmallBadgeCommand.Execute(SmallBadge.Automatic);

        var profile = h.Settings.Current.ActiveProfile;
        Assert.True(profile.Visibility.ShowSmallImage);
        Assert.All(profile.Templates.ByState.Values, t => Assert.Equal(ImageSlot.Automatic, t.SmallImage));
        Assert.DoesNotContain(profile.StreamerTemplates.ByState.Values, t => t.SmallImage.Source == ImageSource.Automatic);
        Assert.Equal(SmallBadge.Automatic, kit.Status.SelectedSmall);
    }

    [Fact]
    public void Automatic_DoesNotCountAsOwnText_AndSurvivesAStyleChange()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Status.SelectSmallBadgeCommand.Execute(SmallBadge.Automatic);
        kit.Status.SelectStyleCommand.Execute(StatusStyle.Detailed);

        Assert.False(kit.Status.HasOwnText);
        Assert.Equal(StatusStyle.Detailed, kit.Status.SelectedStyle);
        Assert.Equal(SmallBadge.Automatic, kit.Status.SelectedSmall);
    }

    [Fact]
    public void TheDetailedStyle_StartsWithAutomaticOnTheMomentsThatCanChange()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        var templates = h.Settings.Current.ActiveProfile.Templates.ByState;

        Assert.Equal(ImageSlot.Automatic, templates[PresenceState.InTrialSurvivor].SmallImage);
        Assert.Equal(ImageSlot.Automatic, templates[PresenceState.Results].SmallImage);
    }

    [Fact]
    public void TheWordsNextToAutomatic_ArePlain_FollowTheCopyRules_AndPromiseNothingTheAppCannotKeep()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        string[] banned = ["!", "simply", "just", "easily", "please", "template", "token", "situation", "asset", "detector", "tier", "validated", "experimental"];

        foreach (var text in new[] { kit.Status.AutomaticCaption, kit.Studio.AutomaticSmallBadgeNote })
        {
            Assert.All(banned, word => Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase));
            Assert.Contains("role icon", text, StringComparison.Ordinal);
            Assert.Contains("when the app is sure", text, StringComparison.Ordinal);
            Assert.Contains("your pack has the picture", text, StringComparison.Ordinal);
        }

        Assert.Contains("Show live match details", kit.Status.AutomaticCaption, StringComparison.Ordinal);
        Assert.Contains("Private style never shows them", kit.Status.AutomaticCaption, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereIsNoPreviewValuesPicker_ThePageOnlyShowsLiveValues()
    {
        var members = typeof(DiscordStatusViewModel).GetMembers().Concat(typeof(PresenceStudioViewModel).GetMembers()).Select(m => m.Name);

        Assert.DoesNotContain(members, name => name.Contains("PreviewValue", StringComparison.OrdinalIgnoreCase) || name.Contains("PreviewCase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheStudio_CanSetAutomaticOnTheSmallBadgeOfOneMoment()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        kit.Studio.SelectedState = kit.Studio.States.Single(s => s.State == PresenceState.Results);

        kit.Studio.SmallImageSource = ImageSource.None;
        kit.Studio.SmallImageSource = ImageSource.Automatic;

        Assert.Equal(ImageSlot.Automatic, h.Settings.Current.ActiveProfile.Templates.ByState[PresenceState.Results].SmallImage);
    }

    [Fact]
    public void TheHookCount_IsADetailTheUserCanAdd_WithAPlainName()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        var chip = kit.Studio.Tokens.Single(t => t.Label == "Hook count");

        Assert.Contains("killer", chip.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TemplateTokens.HookCount, TemplateTokens.Known.Keys);
    }

    [Fact]
    public void AnOlderProfile_WithRoleBadges_KeepsThemUntilTheUserChoosesAutomatic()
    {
        var old = BuiltInProfiles.Default with
        {
            Templates = new TemplateSet
            {
                ByState = TemplatePresets.Detailed.ByState.ToDictionary(kv => kv.Key, kv => kv.Value with { SmallImage = ImageSlot.Role }),
            },
        };
        using var h = new Harness(new AppSettings { Profiles = [old] });
        using var kit = new AppKit(h, s_app);

        Assert.Equal(SmallBadge.MyRole, kit.Status.SelectedSmall);
        Assert.All(h.Settings.Current.ActiveProfile.Templates.ByState.Values, t => Assert.Equal(ImageSlot.Role, t.SmallImage));
    }
}
