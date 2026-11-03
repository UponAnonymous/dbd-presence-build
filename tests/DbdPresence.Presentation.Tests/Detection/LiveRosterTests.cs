// Covers: D35 and ADR-0017 (unknown character names go from the live loop to the sink, once per match), FR-ROS-04 (a merged roster reaches the running detection
// loop and its names with no restart), FR-DET-18 (the vocabulary is swapped between frames, never during one), DETECTION_VALIDATION section 7 (detectors stay Disabled).
// MECHANICS ONLY: screens are drawn text on flat colour read by a fake recogniser and the character is invented. How Windows OCR reads the real game's rows, and
// how the live roster service behaves, is UNKNOWN (PENDING-ENV).
using System.Text.Json.Nodes;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Core.Roster;
using DbdPresence.Packs.Roster;
using DbdPresence.Presentation.Detection;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Vision.Tests.Support;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Tests.Detection;

public sealed class LiveRosterTests
{
    private const string NewSurvivor = "ZELDA QUILLFEATHER";

    private sealed class RecordingSink : IUnknownCharacterSink
    {
        private readonly Lock _gate = new();
        private readonly List<UnknownCharacterSighting> _seen = [];

        public IReadOnlyList<UnknownCharacterSighting> Seen
        {
            get
            {
                lock (_gate)
                {
                    return [.. _seen];
                }
            }
        }

        public void Report(UnknownCharacterSighting sighting)
        {
            lock (_gate)
            {
                _seen.Add(sighting);
            }
        }
    }

    private sealed class RecordingConsumer(Exception? failure = null) : IRosterConsumer
    {
        public int Applied { get; private set; }

        public void Apply(RosterDocument roster)
        {
            if (failure is not null)
            {
                throw failure;
            }

            Applied++;
        }
    }

    private static List<PlayerLine> Rows() => HudScenes.SurvivorGame(ownerCharacter: NewSurvivor);

    private static RosterDocument Document(string code = "S90", string id = "zelda-quillfeather", string name = "Zelda Quillfeather") =>
        new([new RosterEntry(code, id, Role.Survivor, name, name, [], null, false)]);

    private static Vocabulary Enlarged(string id = "zelda-quillfeather") =>
        HudHarness.Vocabulary.WithCharacters([], [new VocabularyEntry(id, "Zelda Quillfeather", null, null, "test", "S90")]);

    private static Observation CharacterObservation(LoopHarness h, string id) =>
        new(DetectorId.Character, 1, h.Time.GetUtcNow(), ObservationKind.Character, id, 0.9, "test", new ContextKey("p", "1280x720", "en-US", "1"));

    /// <summary>Unknown names are reported only once the owner's name is known (round-2 review M1), which the menus teach.</summary>
    private static async Task LearnOwnerNameAsync(LoopHarness h)
    {
        h.Show(HudScenes.MainMenu(1280));
        await h.CyclesAsync(UnknownCharacterWatchMenuFrames);
    }

    // ---- The loop hands unknown names to the sink --------------------------------------------------------------------------

    [Fact]
    public async Task TheLoop_ReportsAnUnknownCharacterOnTheScoreboardToTheSink_OncePerMatch()
    {
        var sink = new RecordingSink();
        using var h = new LoopHarness(unknownSink: sink);
        h.StartGame();
        await LearnOwnerNameAsync(h);
        h.Show(HudScenes.Scoreboard(1280, Rows(), 1));

        await h.CyclesAsync(8);

        var sighting = Assert.Single(sink.Seen);
        Assert.Equal(NewSurvivor, sighting.Name);
        Assert.Equal(Role.Survivor, sighting.Role);
    }

    [Fact]
    public async Task WithoutASink_TheLoopRunsAsBefore()
    {
        using var h = new LoopHarness();
        h.StartGame();
        h.Show(HudScenes.Scoreboard(1280, Rows(), 1));

        await h.CyclesAsync(4);

        Assert.Equal(DetectionRunState.Reading, h.Loop.Status.Run);
        Assert.Equal(0, h.Loop.FailureCount);
    }

    [Fact]
    public async Task TheLoopsDetectors_StayDisabledForDiscord_WhateverTheSinkReceives()
    {
        using var h = new LoopHarness(unknownSink: new RecordingSink());
        h.StartGame();
        h.Show(HudScenes.Scoreboard(1280, Rows(), 1));

        await h.CyclesAsync(4);

        Assert.All(h.Loop.DetectorSettings.Values, s => Assert.Equal(DetectionTier.Disabled, s.Tier));
    }

    // ---- A larger vocabulary without a restart -------------------------------------------------------------------------------

    [Fact]
    public async Task AfterUpdateVocabulary_TheEngineAcceptsTheNewCharacterAtOnce_AndTheOldOnesStillStand()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CycleAsync();
        Assert.Equal(SubmitResult.RejectedInvalid, h.Loop.Engine.Submit(CharacterObservation(h, "zelda-quillfeather")));

        h.Loop.UpdateVocabulary(Enlarged());
        h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.NotEqual(SubmitResult.RejectedInvalid, h.Loop.Engine.Submit(CharacterObservation(h, "zelda-quillfeather"))); // the scene gate may still hold it, but the id is known
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(SubmitResult.RejectedInvalid, h.Loop.Engine.Submit(CharacterObservation(h, "dwight-fairfield")));
    }

    [Fact]
    public async Task AfterUpdateVocabulary_TheNextMatchIsReadWithTheNewCharacter_AndItIsNoLongerReportedAsUnknown()
    {
        var sink = new RecordingSink();
        using var h = new LoopHarness(unknownSink: sink);
        h.StartGame();
        await LearnOwnerNameAsync(h);
        h.Show(HudScenes.Scoreboard(1280, Rows(), 1));
        await h.CyclesAsync(4);
        _ = Assert.Single(sink.Seen);

        h.Loop.UpdateVocabulary(Enlarged());
        h.Show(HudScenes.MainMenu(1280));
        await h.CyclesAsync(UnknownCharacterWatchMenuFrames);
        h.Show(HudScenes.Scoreboard(1280, Rows(), 1));
        await h.CyclesAsync(6);

        _ = Assert.Single(sink.Seen);
        Assert.Equal(0, h.Loop.FailureCount);
    }

    [Fact]
    public async Task TheSwap_HappensBetweenFrames_ACycleInFlightFinishesWithTheListsItStartedWith()
    {
        var sink = new RecordingSink();
        var gate = new GateRecognizer(new BitmapFontOcr());
        using var h = new LoopHarness(recognizer: gate, unknownSink: sink);
        h.StartGame();
        h.Show(HudScenes.Scoreboard(1280, Rows(), 1));

        gate.Hold();
        var inFlight = h.Loop.RunCycleAsync(TestContext.Current.CancellationToken);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        h.Loop.UpdateVocabulary(Enlarged());
        gate.Release();
        _ = await inFlight;
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.CyclesAsync(4);

        // The frame in flight was read with the old lists (the name was unknown and counted once, which is not enough to report it); the following frames use the new ones (known).
        Assert.Empty(sink.Seen);
        Assert.Equal(0, h.Loop.FailureCount);
    }

    [Fact]
    public async Task UpdatingTheVocabularyWhileCyclesRun_NeverFailsACycle()
    {
        using var h = new LoopHarness(unknownSink: new RecordingSink());
        h.StartGame();
        h.Show(HudScenes.Scoreboard(1280, Rows(), 1));
        var cycles = Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                _ = await h.Loop.RunCycleAsync(TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 40; i++)
        {
            h.Loop.UpdateVocabulary(HudHarness.Vocabulary.WithCharacters([], [new VocabularyEntry($"zelda-{i}", $"Zelda {s_trees[i % s_trees.Length]}", null, null, "test", $"S{90 + (i % 10)}")]));
        }

        await cycles.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(0, h.Loop.FailureCount);
    }

    [Fact]
    public void AnUpdateThatDropsAKillerOfThePromptMap_IsRefused_AndChangesNothing()
    {
        using var h = new LoopHarness();

        _ = Assert.Throws<ArgumentException>(() => h.Loop.UpdateVocabulary(WithoutTheLich()));
    }

    // ---- The roster consumer ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRosterConsumer_GivesTheMergedRosterToThePageFirst_ThenToTheLoopAndTheNames()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CycleAsync();
        var inner = new RecordingConsumer();
        var names = new DetectionNames(HudHarness.Vocabulary);
        var consumer = new DetectionRosterConsumer(inner, HudHarness.Vocabulary, h.Loop, names);
        Assert.Equal("zelda-quillfeather", names.Character("zelda-quillfeather"));

        consumer.Apply(Document());

        Assert.Equal(1, inner.Applied);
        Assert.Equal("Zelda Quillfeather", names.Character("zelda-quillfeather"));
        Assert.Equal("Dwight Fairfield", names.Character("dwight-fairfield"));
        Assert.NotEqual(SubmitResult.RejectedInvalid, h.Loop.Engine.Submit(CharacterObservation(h, "zelda-quillfeather"))); // the scene gate may still hold it, but the id is known
    }

    [Fact]
    public async Task WhenThePageRefusesTheRoster_TheLoopAndTheNamesAreNotChanged()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CycleAsync();
        var names = new DetectionNames(HudHarness.Vocabulary);
        var consumer = new DetectionRosterConsumer(new RecordingConsumer(new FormatException("clash")), HudHarness.Vocabulary, h.Loop, names);

        _ = Assert.Throws<FormatException>(() => consumer.Apply(Document()));

        Assert.Equal("zelda-quillfeather", names.Character("zelda-quillfeather"));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Loop.Engine.Submit(CharacterObservation(h, "zelda-quillfeather")));
    }

    [Fact]
    public async Task ARosterThatClashesWithAKnownName_ChangesNothingInTheLoop()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CycleAsync();
        var names = new DetectionNames(HudHarness.Vocabulary);
        var consumer = new DetectionRosterConsumer(new RecordingConsumer(), HudHarness.Vocabulary, h.Loop, names);

        _ = Assert.Throws<FormatException>(() => consumer.Apply(Document(code: "S91", id: "dwight-two", name: "Dwight Fairfield")));

        Assert.Equal(SubmitResult.RejectedInvalid, h.Loop.Engine.Submit(CharacterObservation(h, "dwight-two")));
    }

    [Fact]
    public async Task TheNames_CanBeUpdatedFromAnyThread_WithoutTearing()
    {
        var names = new DetectionNames(HudHarness.Vocabulary);
        var stop = false;
        var reader = Task.Run(
            () =>
            {
                while (!Volatile.Read(ref stop))
                {
                    Assert.Equal("Dwight Fairfield", names.Character("dwight-fairfield"));
                }
            },
            TestContext.Current.CancellationToken);

        for (var i = 0; i < 200; i++)
        {
            names.Update(Enlarged());
        }

        Volatile.Write(ref stop, true);
        await reader;
        Assert.Equal("Zelda Quillfeather", names.Character("zelda-quillfeather"));
    }

    private const int UnknownCharacterWatchMenuFrames = 4;

    private static readonly string[] s_trees = ["Alder", "Birch", "Cedar", "Dogwood", "Elder", "Hazel", "Juniper", "Kauri", "Larch", "Maple", "Poplar", "Rowan"];

    private static Vocabulary WithoutTheLich()
    {
        var assembly = typeof(Vocabulary).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("vocabulary.en.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var root = JsonNode.Parse(stream)!.AsObject();
        var killers = root["killers"]!.AsArray();
        killers.Remove(killers.Single(k => k!["id"]!.GetValue<string>() == "the-lich"));
        root["counts"]!["killers"] = killers.Count;
        return Vocabulary.Parse(root.ToJsonString());
    }

    /// <summary>A recogniser that can be held in the middle of a cycle, to show what a swap does to a frame already being read.</summary>
    private sealed class GateRecognizer(DbdPresence.Core.Capture.ITextRecognizer inner) : DbdPresence.Core.Capture.ITextRecognizer
    {
        private TaskCompletionSource? _release;

        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release() => _release?.TrySetResult();

        public async Task<DbdPresence.Core.Capture.TextRecognitionResult> RecognizeAsync(DbdPresence.Core.Capture.PixelBuffer image, CancellationToken cancellationToken)
        {
            if (_release is { } gate)
            {
                _ = Entered.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
        }
    }
}
