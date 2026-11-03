// Covers: docs/design/whole-app.md §4 (Discord status: styles Detailed, Simple and Private mapped to the presets and the streamer switch, the "Your own text"
// chip, Undo for 10 s, one switch per part of the game, global picture choices, live mini previews from the same inputs as the real status), FR-TPL-05,
// FR-VIS-01..04, FR-ART-01 (no big picture caption), D29 (live values only), D30 (everything automatic).
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class DiscordStatusViewModelTests
{
    private static readonly AppInfo s_app = new("1.0.0", "abc1234", IsSigned: false, "settings", "data");

    private static string Words(TemplateSet set) => string.Join("|", set.ByState.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value.Details}/{kv.Value.State}/{kv.Value.LargeText}/{kv.Value.SmallText}/{kv.Value.Timer}"));

    [Fact]
    public void ByDefault_TheDetailedStyleIsInUse_WithNoOwnText()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        Assert.Equal(StatusStyle.Detailed, kit.Status.SelectedStyle);
        Assert.False(kit.Status.HasOwnText);
        Assert.False(kit.Status.IsPrivate);
        Assert.Equal(["Detailed", "Simple", "Private"], kit.Status.Cards.Select(c => c.Name));
        Assert.Equal([true, false, false], kit.Status.Cards.Select(c => c.IsSelected));
        Assert.Equal("Detailed style. Where you are, your role, character and map, and a timer.", kit.Status.Cards[0].AutomationName);
    }

    [Fact]
    public void Simple_UsesTheMinimalPreset_WithStreamerModeOff()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Simple);

        var profile = h.Settings.Current.ActiveProfile;
        Assert.Equal(Words(TemplatePresets.Minimal), Words(profile.Templates));
        Assert.False(h.Settings.Current.Privacy.StreamerMode);
        Assert.Equal(StatusStyle.Simple, kit.Status.SelectedStyle);
        Assert.Null(kit.Status.StyleNote);
    }

    [Fact]
    public void Private_IsStreamerMode_AndLeavesTheTextAlone()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        var before = Words(h.Settings.Current.ActiveProfile.Templates);

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Private);

        Assert.True(h.Settings.Current.Privacy.StreamerMode);
        Assert.Equal(before, Words(h.Settings.Current.ActiveProfile.Templates));
        Assert.Equal(StatusStyle.Private, kit.Status.SelectedStyle);
        Assert.True(kit.Status.IsPrivate);
        Assert.False(kit.Status.HasOwnText);
        Assert.Equal([false, false, true], kit.Status.Cards.Select(c => c.IsSelected));

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Detailed);

        Assert.False(h.Settings.Current.Privacy.StreamerMode);
        Assert.Equal(StatusStyle.Detailed, kit.Status.SelectedStyle);
    }

    [Fact]
    public void ChangingTheStreamerModeSetting_FromAnywhere_MovesTheSelection()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        h.Settings.Update(s => s with { Privacy = s.Privacy with { StreamerMode = true } });

        Assert.Equal(StatusStyle.Private, kit.Status.SelectedStyle);
    }

    [Fact]
    public void WritingYourOwnText_ShowsTheOwnTextChip_AndSelectsNoStyle()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Studio.Details.Text = "My own running text";

        Assert.True(kit.Status.HasOwnText);
        Assert.Null(kit.Status.SelectedStyle);
        Assert.All(kit.Status.Cards, c => Assert.False(c.IsSelected));
    }

    [Fact]
    public void OwnText_OfThePrivateSet_IsDetectedToo()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        kit.Status.SelectStyleCommand.Execute(StatusStyle.Private);
        kit.Studio.Target = TemplateTarget.Streamer;

        kit.Studio.Details.Text = "Live now";

        Assert.Equal(StatusStyle.Private, kit.Status.SelectedStyle);
        Assert.True(kit.Status.HasOwnText);
    }

    [Fact]
    public void ChoosingAStyle_OverOwnText_SaysSo_CanBeUndone_AndTheNoteGoesAfterTenSeconds()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        kit.Studio.Details.Text = "My own running text";
        var custom = Words(h.Settings.Current.ActiveProfile.Templates);

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Simple);

        Assert.Equal("Switched to Simple. Your own text was replaced.", kit.Status.StyleNote);
        Assert.True(kit.Status.UndoStyleCommand.CanExecute(null));
        Assert.Equal(Words(TemplatePresets.Minimal), Words(h.Settings.Current.ActiveProfile.Templates));

        kit.Status.UndoStyleCommand.Execute(null);

        Assert.Equal(custom, Words(h.Settings.Current.ActiveProfile.Templates));
        Assert.Null(kit.Status.StyleNote);
        Assert.False(kit.Status.UndoStyleCommand.CanExecute(null));

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Detailed);
        Assert.NotNull(kit.Status.StyleNote);
        h.Time.Advance(DiscordStatusViewModel.UndoWindow - TimeSpan.FromSeconds(1));
        Assert.NotNull(kit.Status.StyleNote);
        h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(kit.Status.StyleNote);
        Assert.False(kit.Status.UndoStyleCommand.CanExecute(null));
    }

    [Fact]
    public void Undo_AlsoRestoresTheStreamerSwitch()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        kit.Status.SelectStyleCommand.Execute(StatusStyle.Private);
        kit.Studio.Details.Text = "x";
        kit.Studio.Target = TemplateTarget.Normal;
        kit.Studio.Details.Text = "My own text";

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Simple);
        Assert.False(h.Settings.Current.Privacy.StreamerMode);

        kit.Status.UndoStyleCommand.Execute(null);

        Assert.True(h.Settings.Current.Privacy.StreamerMode);
    }

    [Fact]
    public void SwitchingBetweenTheStandardStyles_ReplacesNothingOfTheUsers_SoNoUndoIsOffered()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Simple);
        kit.Status.SelectStyleCommand.Execute(StatusStyle.Detailed);

        Assert.Null(kit.Status.StyleNote);
        Assert.Equal(Words(TemplatePresets.Detailed), Words(h.Settings.Current.ActiveProfile.Templates));
    }

    [Fact]
    public void ThePictureChoices_DoNotCountAsOwnText()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Status.SelectBigPictureCommand.Execute(BigPicture.MyRole);
        kit.Status.SelectSmallBadgeCommand.Execute(SmallBadge.AppLogo);

        Assert.Equal(StatusStyle.Detailed, kit.Status.SelectedStyle);
        Assert.False(kit.Status.HasOwnText);
    }

    [Fact]
    public void BigPicture_IsAppliedToEveryMoment_InBothTextSets()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Status.SelectBigPictureCommand.Execute(BigPicture.AppLogo);

        var profile = h.Settings.Current.ActiveProfile;
        Assert.All(profile.Templates.ByState.Values, t => Assert.Equal(ImageSlot.AppLogo, t.LargeImage));
        Assert.All(profile.StreamerTemplates.ByState.Values, t => Assert.Equal(ImageSlot.AppLogo, t.LargeImage));
        Assert.Equal(BigPicture.AppLogo, kit.Status.SelectedBig);

        kit.Status.SelectBigPictureCommand.Execute(BigPicture.MyCharacter);

        Assert.All(h.Settings.Current.ActiveProfile.Templates.ByState.Values, t => Assert.Equal(ImageSlot.Character, t.LargeImage));
        Assert.Equal(BigPicture.MyCharacter, kit.Status.SelectedBig);
    }

    [Fact]
    public void BigPictureNone_ShowsTheCaptionAboutTheSmallBadge()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        Assert.False(kit.Status.ShowNoBigPictureNote);

        kit.Status.SelectBigPictureCommand.Execute(BigPicture.None);

        Assert.True(kit.Status.ShowNoBigPictureNote);
        Assert.Equal(
            "Discord shows a small badge only on top of a big picture. With no big picture, Discord shows its own app icon instead.",
            kit.Status.NoBigPictureNote);
    }

    [Fact]
    public void WhenMomentsUseDifferentPictures_NoChipIsSelected()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        Assert.Null(kit.Status.SelectedBig); // the standard Detailed text mixes role, map and logo pictures by moment
    }

    [Fact]
    public void SmallBadge_NoneTurnsTheBadgeOff_AnyOtherChoiceTurnsItOn()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Status.SelectSmallBadgeCommand.Execute(SmallBadge.None);

        Assert.False(h.Settings.Current.ActiveProfile.Visibility.ShowSmallImage);
        Assert.Equal(SmallBadge.None, kit.Status.SelectedSmall);

        kit.Status.SelectSmallBadgeCommand.Execute(SmallBadge.MyRole);

        Assert.True(h.Settings.Current.ActiveProfile.Visibility.ShowSmallImage);
        Assert.All(h.Settings.Current.ActiveProfile.Templates.ByState.Values, t => Assert.Equal(ImageSlot.Role, t.SmallImage));
        Assert.Equal(SmallBadge.MyRole, kit.Status.SelectedSmall);
    }

    [Fact]
    public void ThePictureChoice_SurvivesAStyleChange()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        kit.Status.SelectBigPictureCommand.Execute(BigPicture.AppLogo);

        kit.Status.SelectStyleCommand.Execute(StatusStyle.Simple);

        Assert.All(h.Settings.Current.ActiveProfile.Templates.ByState.Values, t => Assert.Equal(ImageSlot.AppLogo, t.LargeImage));
    }

    [Fact]
    public void EveryPartOfTheGameHasOneSwitch_WithTheDesignedDefaults()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        var privacy = kit.Privacy;

        Assert.True(privacy.ShowScene);
        Assert.True(privacy.ShowRole);
        Assert.True(privacy.ShowCharacter);
        Assert.True(privacy.ShowMode);
        Assert.True(privacy.ShowMap);
        Assert.False(privacy.ShowLiveMatchStats); // off by default (SAFETY R-02)
        Assert.True(privacy.ShowResult);
        Assert.True(privacy.ShowTimers);
    }

    [Fact]
    public void TheNewSwitches_AreSavedOnTheActiveProfile()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Privacy.ShowMode = false;
        kit.Privacy.ShowScene = false;
        kit.Privacy.ShowResult = false;
        kit.Privacy.ShowMap = false;
        kit.Privacy.ShowLiveMatchStats = true;
        kit.Privacy.PostMatchDetailsOnly = true;

        var v = h.Settings.Current.ActiveProfile.Visibility;
        Assert.False(v.ShowMode);
        Assert.False(v.ShowScene);
        Assert.False(v.ShowResult);
        Assert.False(v.ShowMap);
        Assert.True(v.ShowLiveMatchStats);
        Assert.True(v.PostMatchDetailsOnly);
    }

    [Fact]
    public void TheKillerYouFace_IsNeverOfferedASwitch_AndTheLineSaysWhy()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        Assert.Contains("killer you're facing is never shown on Discord", kit.Status.KillerLine, StringComparison.Ordinal);
        Assert.Null(typeof(PrivacyViewModel).GetProperty("ShowKiller"));
    }

    [Fact]
    public void MiniPreviews_AreComposedFromTheSameLiveInputs_AndLeaveOutWhatIsUnknown()
    {
        using var h = new Harness(detectedRole: Role.Killer);
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow().AddMinutes(-4));

        var cards = kit.Status.Cards;

        Assert.Equal(("In a lobby", "Killer"), (cards[0].Line1, cards[0].Line2)); // Detailed, in the lobby as the Killer the app is sure of
        Assert.Equal(("Playing Dead by Daylight", string.Empty), (cards[1].Line1, cards[1].Line2)); // Simple
        Assert.Equal(("Playing Dead by Daylight", "As Killer"), (cards[2].Line1, cards[2].Line2)); // Private
        Assert.Equal(h.Sink.Last?.Details, cards[0].Line1); // the selected style equals what Discord got
        Assert.Equal(h.Sink.Last?.State, cards[0].Line2);
    }

    [Fact]
    public void MiniPreviews_WithTheGameClosed_ShowWhatEachStyleSaysWhilePlaying_WithoutInventingAnyDetail()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        Assert.Equal(("Playing Dead by Daylight", string.Empty), (kit.Status.Cards[0].Line1, kit.Status.Cards[0].Line2));
        Assert.Equal(("Playing Dead by Daylight", string.Empty), (kit.Status.Cards[2].Line1, kit.Status.Cards[2].Line2));
    }

    [Fact]
    public void HidingWhereYouAre_ChangesTheDetailedMiniPreview()
    {
        using var h = new Harness(detectedRole: Role.Survivor);
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());
        Assert.Equal("In a lobby", kit.Status.Cards[0].Line1);

        kit.Privacy.ShowScene = false;

        Assert.Equal("Playing Dead by Daylight", kit.Status.Cards[0].Line1);
        Assert.Contains("Where you are: hidden by you.", kit.Status.Live.Reasons.All);
    }

    [Fact]
    public void TheLivePreview_IsEmpty_WhileTheGameIsClosed_AndSaysWhy()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();

        Assert.True(kit.Status.Live.IsEmpty);
        Assert.Equal("Nothing on Discord right now. Your status appears when you start the game.", kit.Status.Live.EmptyText);
        Assert.Empty(kit.Status.ShownReasons);
    }

    [Fact]
    public void ThePreview_MatchesThePublishedActivity_AndTheReasonsAreListedThreeAtATime()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());
        kit.Privacy.ShowRole = false;
        kit.Privacy.ShowMap = false;
        kit.Status.SelectStyleCommand.Execute(StatusStyle.Private);

        Assert.Equal(h.Sink.Last?.Details, kit.Status.Preview.Details);
        Assert.True(kit.Status.Live.Reasons.All.Count >= 1);
        Assert.True(kit.Status.ShownReasons.Count <= PlainReasonsLimit);
    }

    private const int PlainReasonsLimit = 3;

    [Fact]
    public void WhilePaused_TheCardShowsWhatAppearsOnResume_WithAResumeButton()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());

        kit.Controls.IsPaused = true;

        Assert.Equal("Paused: Discord shows nothing right now. This is what it shows when you resume.", kit.Status.PausedLine);
        Assert.False(kit.Status.Preview.IsEmpty);
        kit.Status.Hints.Single(x => x.Id == "paused").Actions.Single().Command.Execute(null);
        Assert.False(h.Coordinator.Paused);
    }

    [Fact]
    public void ScreenReadingOff_ShowsTheStripThatPointsToSettings()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        Assert.True(kit.Status.ShowScreenReadingOffStrip);
        Assert.Contains("Screen reading is off", kit.Status.ScreenReadingOffStrip, StringComparison.Ordinal);

        h.Settings.Update(s => s with { Detection = s.Detection with { Enabled = true, RiskAcknowledged = true } });

        Assert.False(kit.Status.ShowScreenReadingOffStrip);
    }

    [Fact]
    public void TheStyleCopy_UsesPlainWords()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        var all = string.Join(" ", kit.Status.Cards.Select(c => c.Name + " " + c.Caption));

        foreach (var banned in new[] { "template", "token", "preset", "streamer", "situation" })
        {
            Assert.DoesNotContain(banned, all, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PrivateStyle_NeverGetsTheMapOrTheCharacterPicture_WhateverTheChoice()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Status.SelectBigPictureCommand.Execute(BigPicture.TheMap);
        Assert.All(h.Settings.Current.ActiveProfile.StreamerTemplates.ByState.Values, t => Assert.Equal(ImageSource.AppLogo, t.LargeImage.Source));
        kit.Status.SelectBigPictureCommand.Execute(BigPicture.MyCharacter);
        Assert.All(h.Settings.Current.ActiveProfile.StreamerTemplates.ByState.Values, t => Assert.Equal(ImageSource.AppLogo, t.LargeImage.Source));
        Assert.All(h.Settings.Current.ActiveProfile.Templates.ByState.Values, t => Assert.Equal(ImageSource.CharacterArtwork, t.LargeImage.Source));

        kit.Status.SelectBigPictureCommand.Execute(BigPicture.MyRole);
        Assert.All(h.Settings.Current.ActiveProfile.StreamerTemplates.ByState.Values, t => Assert.Equal(ImageSource.RoleArtwork, t.LargeImage.Source));
    }
}
