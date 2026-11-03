// Covers: FR-ART-06 (the preview never fetches anything; pictures come from local, hash-verified files), D29 (the preview shows exactly
// what Discord shows: real pack pictures, labelled tiles for asset keys and links, the live situation by default, paused),
// FR-TPL-06 (no invented preview values), FR-PACK-06 (pack resolution), D30 (the role comes only from detection; no manual role pointer).
using DbdPresence.Core.Game;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Settings;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class RealPreviewTests : IDisposable
{
    private const string Cdn = "https://cdn.example.org/packs/";
    private const string Link = "https://example.com/my-picture.png";
    private readonly Harness _host = new();
    private readonly PackHarness _packs;
    private readonly FakeSink _sink = new();
    private readonly FakeLifecycleSource _game = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
    private readonly PresenceCoordinator _coordinator;
    private readonly StubFeed _feed = new();

    public RealPreviewTests()
    {
        _packs = new PackHarness(_host);
        _coordinator = new PresenceCoordinator(_host.Settings, _host.Catalog, _game, _sink, _time, _packs.Provider, detection: _feed);
        _coordinator.Start();
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _host.Dispose();
    }

    private static PackEntry Entry(string pack, string key, string marker)
    {
        var content = System.Text.Encoding.UTF8.GetBytes("image-" + marker);
        return new PackEntry(
            pack + "-" + key.Replace(':', '-'), pack, Enum.Parse<PackEntryKind>(key.Split(':')[0], ignoreCase: true), key,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), content.Length, 1024, 1024, PackImageFormat.Png, null);
    }

    private static string LocalPath(PackEntry entry) => "/cache/" + entry.CacheFileName;

    private void AddPack(params string[] keys)
    {
        var info = new PackInfo("official", "official", 1, "DBD Presence", "Fan content shown as a courtesy.", null, true, false);
        var entries = keys.Select(k => Entry("official", k, k)).ToArray();
        _packs.Library.SetSynced(new SyncedPacks(new PackIndex([info], entries), new AllCached(), e => Cdn + e.CacheFileName, null, LocalPath));
    }

    private void Configure(Role role, ImageSlot? large = null)
    {
        // D30: a role exists only when (scripted) detection confirmed it; None means detection adds nothing.
        if (role == Role.None)
        {
            _feed.Clear();
        }
        else
        {
            _feed.SetRole(_time.GetUtcNow(), role);
        }

        _host.Settings.Update(s =>
        {
            var profile = s.ActiveProfile;
            if (large is not null)
            {
                var map = profile.Templates.ByState.ToDictionary(kv => kv.Key, kv => kv.Value with { LargeImage = large });
                profile = profile with { Templates = profile.Templates with { ByState = map } };
            }

            return s with { Profiles = [profile] };
        });
    }

    private void Run() => _game.SetRunning(_time.GetUtcNow().AddMinutes(-5));

    private PresenceStudioViewModel Studio() => new(_host.Settings, _host.Catalog, _host.Dispatcher, _time, _packs.Provider, _coordinator);

    private HomeViewModel Home() => new(new PresenceControlsViewModel(_coordinator, _host.Settings, _host.Dispatcher, _time), _coordinator, _host.Settings, _host.Dispatcher, _time);

    private ActivityPreview Expected(ValidatedActivity? activity) =>
        ActivityPreview.From(activity, _coordinator.Application.Name, _coordinator.Snapshot.Artwork ?? new ArtworkSettings(), _time.GetUtcNow(), r => _packs.Provider.Match(r));

    private static bool Same(ActivityPreview a, ActivityPreview b) => a with { Buttons = b.Buttons } == b && a.Buttons.SequenceEqual(b.Buttons);

    [Fact]
    public void APackPicture_IsShownFromItsLocalPath_InStudioAndHome()
    {
        AddPack("app:mark", "role:killer");
        Run();
        Configure(Role.Killer);
        var killer = Entry("official", "role:killer", "role:killer");

        foreach (var preview in new[] { Studio().Preview, Home().Preview })
        {
            var large = preview.LargeImage!;
            Assert.Equal(ImageTileKind.Picture, large.Kind);
            Assert.Equal(LocalPath(killer), large.Location!.Path);
            Assert.StartsWith(Cdn, large.Reference, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ADiscordAssetKey_IsALabelledTile_AndAHintExplainsIt()
    {
        Run();
        Configure(Role.Killer);

        var studio = Studio();
        var large = studio.Preview.LargeImage!;

        Assert.Equal(ImageTileKind.AssetKey, large.Kind);
        Assert.Null(large.Location);
        Assert.Equal(ImagePreview.AssetKeyCaption, large.Caption);
        Assert.Contains(studio.Hints, h => h.Id == "asset-role_killer");
        Assert.Contains(Home().Hints, h => h.Id == "asset-role_killer");
    }

    [Fact]
    public void AUserLink_IsALabelledTile_AndIsNeverFetched()
    {
        Run();
        Configure(Role.Killer, new ImageSlot(ImageSource.Fixed, Link));

        var large = Studio().Preview.LargeImage!;

        Assert.Equal(ImageTileKind.UserLink, large.Kind);
        Assert.Equal("Shown by Discord from your link", large.Caption);
        Assert.Null(large.Location);
    }

    [Fact]
    public void NoImage_ShowsDiscordsOwnApplicationIcon()
    {
        Run();
        Configure(Role.None, ImageSlot.None);

        var large = Studio().Preview.LargeImage!;

        Assert.Equal(ImageTileKind.ApplicationIcon, large.Kind);
        Assert.True(large.IsApplicationIcon);
    }

    [Fact]
    public void ThePicture_OfAPackFileThatIsNotOnThisPc_IsALabelledTile()
    {
        AddPack("role:killer");
        _packs.Library.SetSynced(_packs.Library.Synced! with { LocalPathFor = null });
        Run();
        Configure(Role.Killer);

        var large = Studio().Preview.LargeImage!;

        Assert.Equal(ImageTileKind.PackPictureMissing, large.Kind);
        Assert.Null(large.Location);
    }

    [Fact]
    public void TheStudio_OpensOnTheLiveSituation_AndSaysWhatDiscordShows()
    {
        var closed = Studio();
        Assert.Equal(PresenceState.NotRunning, closed.SelectedState.State);
        Assert.True(closed.IsEditingLive);

        Run();
        var running = Studio();
        Assert.Equal(PresenceState.Running, running.SelectedState.State);
        Assert.Equal("Editing what Discord shows now.", running.LiveStateNote);

        running.SelectedState = running.States.Single(s => s.State == PresenceState.Results);
        Assert.False(running.IsEditingLive);
        Assert.StartsWith("Editing: After the match (my usual style) · Discord shows: Playing (no screen reading) (my usual style)", running.LiveStateNote, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStudio_OpensInTheStreamerTemplates_WhileStreamerModeIsOn()
    {
        _host.Settings.Update(s => s with { Privacy = s.Privacy with { StreamerMode = true } });
        Run();

        var studio = Studio();

        Assert.Equal(TemplateTarget.Streamer, studio.Target);
        Assert.True(studio.IsEditingLive);
    }

    [Fact]
    public void WithNoRole_NoRoleIsInvented_AndNoHintOffersToSetOneByHand()
    {
        Run();
        Configure(Role.None);

        var studio = Studio();

        Assert.DoesNotContain(studio.Hints, h => h.Id == "no-role");
        Assert.DoesNotContain("Survivor", studio.Preview.State ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(studio.Preview.SmallImage);
    }

    [Fact]
    public void TheDetectedRole_ReachesThePayloadAndBothPreviews()
    {
        Run();
        var home = Home();
        var studio = Studio();
        Assert.True(home.Preview.LargeImage!.IsApplicationIcon || home.Preview.LargeImage.Reference != "role_survivor");

        _feed.SetRole(_time.GetUtcNow(), Role.Survivor);

        Assert.Equal("role_survivor", _sink.Last!.LargeImage);
        Assert.Equal("role_survivor", home.Preview.LargeImage!.Reference);
        Assert.Equal("role_survivor", studio.Preview.LargeImage!.Reference);
    }

    [Fact]
    public void WhenPaused_ThePreviewSaysSo_AndShowsWhatAppearsOnResume()
    {
        Run();
        Configure(Role.Killer);
        _coordinator.SetPaused(true);

        var studio = Studio();
        var home = Home();

        Assert.NotNull(_coordinator.Snapshot.ActivityIfNotPaused);
        Assert.Contains("paused", studio.LiveStateNote, StringComparison.Ordinal);
        var paused = studio.Hints.Single(h => h.Id == "paused");
        Assert.Equal("Paused: Discord shows nothing right now. This is what it shows when you resume.", paused.Message);
        Assert.Equal("Resume", paused.Actions.Single().Label);

        // Home says the same in a line above the card, but offers no second Resume: the headline already has it (design §3.4, one action per area).
        Assert.Equal(paused.Message, home.PausedLine);
        Assert.DoesNotContain(home.Hints, h => h.Id == "paused");
        Assert.Equal("Paused", home.Hero.Title);
        Assert.Equal("Resume", home.Hero.Action!.Label);

        Assert.False(studio.Preview.IsEmpty);
        Assert.False(home.Preview.IsEmpty);

        studio.Hints.Single(h => h.Id == "paused").Actions.Single().Command.Execute(null);
        Assert.False(_coordinator.Paused);
    }

    [Fact]
    public void ThePreview_NeverProducesARemoteImageSource()
    {
        AddPack("app:mark", "role:killer");
        foreach (var running in new[] { false, true })
        {
            if (running)
            {
                Run();
            }

            foreach (var large in new ImageSlot?[] { null, new ImageSlot(ImageSource.Fixed, Link), ImageSlot.AppLogo, ImageSlot.None })
            {
                Configure(Role.Killer, large);
                foreach (var preview in new[] { Studio().Preview, Home().Preview })
                {
                    foreach (var image in new[] { preview.LargeImage, preview.SmallImage })
                    {
                        if (image?.Location is { } location)
                        {
                            Assert.Equal(ImageTileKind.Picture, image.Kind);
                            Assert.False(
                                Uri.TryCreate(location.Path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
                                "A preview image source must be a local file path.");
                        }
                        else if (image is not null)
                        {
                            Assert.NotEqual(ImageTileKind.Picture, image.Kind);
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(Role.None)]
    [InlineData(Role.Killer)]
    [InlineData(Role.Survivor)]
    public void ThePreview_IsTheSamePayloadDiscordGets_ForEveryRolePauseGameAndImageSource(Role role)
    {
        AddPack("app:mark", "role:killer");
        foreach (var running in new[] { false, true })
        {
            foreach (var paused in new[] { false, true })
            {
                foreach (var large in new ImageSlot?[] { null, ImageSlot.AppLogo, new ImageSlot(ImageSource.Fixed, Link), ImageSlot.None })
                {
                    _coordinator.SetPaused(false);
                    _game.Set(GameLifecycle.Initial);
                    if (running)
                    {
                        Run();
                    }

                    Configure(role, large);
                    _coordinator.SetPaused(paused);
                    var shown = paused ? _coordinator.Snapshot.ActivityIfNotPaused : _sink.Last;
                    var expected = Expected(shown);

                    var studio = Studio();
                    var home = Home();

                    var label = $"{role} running={running} paused={paused} large={large}";
                    Assert.True(studio.IsEditingLive, label);
                    Assert.True(Same(expected, studio.Preview), $"Studio differs from the payload: {label}");
                    Assert.True(Same(expected, home.Preview), $"Home differs from the payload: {label}");
                    if (!paused)
                    {
                        Assert.Equal(_coordinator.Snapshot.Published, _sink.Last);
                    }
                }
            }
        }
    }

    private sealed class AllCached : IPackContentCache
    {
        public bool Contains(PackEntry entry) => true;
    }
}
