using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Roster;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Prompts;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Screens;

/// <summary>What one frame produced.</summary>
/// <param name="Observations">Every detector's observations for the frame, ready for <c>MatchStateEngine.Submit</c>. Observations only: nothing here confirms or publishes.</param>
/// <param name="Screen">The screen the anchors showed, or <see cref="ScreenKind.Unknown"/>.</param>
/// <param name="OcrCalls">How many text recognitions ran for this frame (the change gate skips unchanged regions).</param>
/// <param name="OwnerNameKnown">Whether the owner's name is known, which the owner's row needs.</param>
public sealed record HudFrame(IReadOnlyList<Observation> Observations, ScreenKind Screen, int OcrCalls, bool OwnerNameKnown);

/// <summary>The settings a HUD pipeline may run with, after its profile has vetoed what it must (<see cref="HudDetectionPipeline.GateSettings"/>).</summary>
/// <param name="Effective">The settings to give the engine: the requested ones, with every refused HUD detector put back to <see cref="DetectorSettings.Default"/> (Disabled).</param>
/// <param name="Refused">One line per refused detector, with the reason (for the UI and logs; contains no screen text).</param>
public sealed record HudSettingsGate(IReadOnlyDictionary<DetectorId, DetectorSettings> Effective, IReadOnlyList<string> Refused);

/// <summary>
/// Runs the text-anchored HUD detectors on the region crops of one frame (FR-DET-18 to FR-DET-23). Per frame it (1) reads only the cheap anchor
/// regions through the <see cref="ChangeGatedTextReader"/>, (2) finds out which screen is up, (3) reads the detail regions that screen needs (the
/// rows of TAB and the scoreboard, the lobby name pairs, the killer list) and (4) runs the six detectors on the result. The detectors are pure; the
/// pipeline holds the state that is not a detection: the OCR cache and the owner's name. It returns observations; the caller submits them to the
/// engine, which alone confirms. Every detector stays Disabled for Discord until a validation report exists.
/// </summary>
public sealed class HudDetectionPipeline
{
    private readonly HudProfile _profile;
    private readonly ChangeGatedTextReader _reader;
    private readonly OwnStatusTemplates? _statusTemplates;
    private readonly UnknownCharacterWatch? _unknownCharacters;
    private readonly OwnerNameTracker _owner = new();
    private readonly string? _ownerOverride;
    private readonly Func<string?>? _excludedNames;
    private readonly TimeProvider _time;
    private readonly IPromptObservationSink _promptSink;
    private readonly PromptCandidates _promptCandidates = new();
    private PromptEvidence _promptEvidence = PromptEvidence.Empty;
    private bool _promptsReported;
    private HudAnalyzer _analyzer;
    private HudVocabulary _vocabulary;
    private ScreenKind? _lastScreen;

    /// <summary>Initializes a new instance of the <see cref="HudDetectionPipeline"/> class.</summary>
    /// <param name="profile">The HUD profile.</param>
    /// <param name="vocabulary">The closed lists.</param>
    /// <param name="ocr">The text recogniser.</param>
    /// <param name="time">The clock.</param>
    /// <param name="gate">Change-gate options; null uses the initial guesses.</param>
    /// <param name="ownerNameOverride">The owner's name from a setting; when given, it replaces what the profile-name region shows.</param>
    /// <param name="promptSink">Receives the power prompts of a match once the scoreboard names the killer (the owner's app keeps them on this PC); null drops them.</param>
    /// <param name="statusTemplates">The own-row state icons the owner's captures taught this PC; null means none, and the own-status detector then reads nothing.</param>
    /// <param name="unknownCharacters">
    /// Receives each character name read in a character-name position that the closed lists do not contain (D35), once per match (<see cref="UnknownCharacterWatch"/>);
    /// null reads none. Player names are never passed on: see <see cref="HudAnalyzer.ReadUnknownCharacters"/>.
    /// </param>
    /// <param name="excludedName">
    /// Gives another name that is a person's, not a character's (the signed-in Discord display name), read for each frame; the unknown-character path
    /// never reports a line that contains it. Null or a null result means none.
    /// </param>
    public HudDetectionPipeline(
        HudProfile profile, HudVocabulary vocabulary, ITextRecognizer ocr, TimeProvider time, ChangeGateOptions? gate = null, string? ownerNameOverride = null,
        IPromptObservationSink? promptSink = null, OwnStatusTemplates? statusTemplates = null, IUnknownCharacterSink? unknownCharacters = null, Func<string?>? excludedName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(vocabulary);
        _profile = profile;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _vocabulary = vocabulary;
        _promptSink = promptSink ?? NullPromptObservationSink.Instance;
        _reader = new ChangeGatedTextReader(ocr, time, gate);
        _analyzer = new HudAnalyzer(profile, vocabulary);
        _ownerOverride = string.IsNullOrWhiteSpace(ownerNameOverride) ? null : ownerNameOverride.Trim();
        _statusTemplates = statusTemplates;
        _excludedNames = excludedName;
        _unknownCharacters = unknownCharacters is null ? null : new UnknownCharacterWatch(unknownCharacters, _time.GetUtcNow().ToUnixTimeSeconds());
        Detectors = BuildDetectors(profile, vocabulary, statusTemplates);
        Regions = profile.Requests();
    }

    /// <summary>Gets the detectors, all Disabled by default.</summary>
    public IReadOnlyList<IDetector> Detectors { get; private set; }

    /// <summary>
    /// Replaces the closed lists (the signed roster added characters). The detectors and the analyzer are rebuilt on the new lists; what is not a
    /// detection (the text cache, the owner's name, the match's prompt state, the unknown-character state) is kept. The pipeline is not thread safe:
    /// call this between frames only (the live loop does, before a cycle starts).
    /// </summary>
    /// <param name="vocabulary">The new lists; they must hold every id the old ones did (the roster is append-only).</param>
    public void SetVocabulary(HudVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = vocabulary;
        _analyzer = new HudAnalyzer(_profile, vocabulary);
        Detectors = BuildDetectors(_profile, vocabulary, _statusTemplates);
    }

    private static IDetector[] BuildDetectors(HudProfile profile, HudVocabulary vocabulary, OwnStatusTemplates? statusTemplates) =>
    [
        new ScreenStateDetector(profile, vocabulary),
        new QueueModeDetector(profile, vocabulary),
        new MapTextDetector(profile, vocabulary),
        new RoleCueDetector(profile, vocabulary),
        new CharacterDetector(profile, vocabulary),
        new KillerNameDetector(profile, vocabulary),
        new KillerPromptDetector(profile, vocabulary),
        new OwnStatusDetector(profile, vocabulary, statusTemplates),
        new SurvivorResultDetector(profile, vocabulary),
        new KillerResultDetector(profile, vocabulary),
        new HookCountDetector(profile, vocabulary),
    ];

    /// <summary>Gets every region the capture side is asked for (FR-DET-13: only these are captured).</summary>
    public IReadOnlyList<RegionRequest> Regions { get; }

    /// <summary>Gets how many text recognitions ran so far.</summary>
    public long OcrCalls => _reader.OcrCalls;

    /// <summary>Gets how many reads reused a cached text.</summary>
    public long CacheHits => _reader.CacheHits;

    /// <summary>Gets whether the owner's name is known (set, or read from the menus).</summary>
    public bool OwnerNameKnown => (_ownerOverride ?? _owner.Current) is not null;

    /// <summary>
    /// Ties publication to the profile (review U5 F4): a HUD detector may be above Disabled only when this pipeline's profile is Measured and is the
    /// profile the tier was validated on (<see cref="HudProfile.PublicationProblem"/>). Any other request is refused and the detector stays Disabled
    /// (fail closed). The caller builds the engine from <see cref="HudSettingsGate.Effective"/>. Detectors this pipeline does not own pass through.
    /// </summary>
    /// <param name="requested">The configured settings per detector.</param>
    public HudSettingsGate GateSettings(IReadOnlyDictionary<DetectorId, DetectorSettings> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var effective = new Dictionary<DetectorId, DetectorSettings>(requested);
        var refused = new List<string>();
        foreach (var detector in Detectors)
        {
            if (requested.TryGetValue(detector.Id, out var settings) && _profile.PublicationProblem(settings) is { } problem)
            {
                effective[detector.Id] = DetectorSettings.Default;
                refused.Add($"{detector.Id}: {problem}");
            }
        }

        return new HudSettingsGate(effective, refused);
    }

    /// <summary>Forgets everything read so far: cached text and the owner's name. Call when the game exits.</summary>
    public void Reset()
    {
        _reader.Reset();
        ClearRowCaches();
        _owner.Reset();
        _lastScreen = null;
        ResetPrompts();
        _unknownCharacters?.Reset();
    }

    /// <summary>Gets how many parsed player rows are held in memory.</summary>
    internal int CachedRowCount => _analyzer.CachedRowCount + Detectors.OfType<HudDetector>().Sum(d => d.Analyzer.CachedRowCount);

    /// <summary>Gets the ids of the regions whose recognised text is held in memory.</summary>
    internal IReadOnlyCollection<string> CachedRegionIds => _reader.CachedRegionIds;

    /// <summary>Processes one frame.</summary>
    /// <param name="regions">The crops by region id (any region missing is simply not read).</param>
    /// <param name="frameWidth">Width of the window the crops were taken from.</param>
    /// <param name="frameHeight">Height of the window the crops were taken from.</param>
    /// <param name="context">The detection context (profile id, resolution class, language, game version).</param>
    /// <param name="priorScene">The scene the engine had confirmed before this frame.</param>
    /// <param name="cancellationToken">Cancels text recognition.</param>
    /// <param name="priorRole">The role the engine had confirmed before this frame; the own-row status detector reads for a survivor and the hook counter for a killer.</param>
    public async Task<HudFrame> ProcessAsync(
        IReadOnlyDictionary<string, PixelBuffer> regions,
        int frameWidth,
        int frameHeight,
        ContextKey context,
        Scene priorScene,
        CancellationToken cancellationToken,
        GameRole priorRole = GameRole.None)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(context);
        var now = _time.GetUtcNow();
        var before = _reader.OcrCalls;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = new Dictionary<string, ImageBuffer>(StringComparer.Ordinal);
        foreach (var region in _profile.Regions.Where(r => r.ProvidesImage))
        {
            if (regions.TryGetValue(region.Id, out var crop))
            {
                images[region.Id] = crop.ToImageBuffer();
            }
        }

        foreach (var region in _profile.Regions.Where(r => r.ReadsText && !r.Detail))
        {
            await ReadInto(texts, region, regions, cancellationToken).ConfigureAwait(false);
        }

        var anchors = new FrameCrops(frameWidth, frameHeight, images).WithText(texts);
        var screen = _analyzer.Classify(anchors, new DetectionContext(context, now, priorScene, CurrentOwner()));
        if (_lastScreen is { } last && last != screen.Kind)
        {
            // The screen changed: text read for the old screen (other players' names in rows, the trial column and the spectator bar) is dropped now,
            // so it is neither used for the new screen nor kept in memory (review U5 F8). It is never persisted or logged in any case.
            ClearRowCaches();
            _reader.Forget(HoldsPlayerNames);
        }

        _lastScreen = screen.Kind;
        foreach (var region in _profile.Regions.Where(r => r.ReadsText && r.Detail && DetailNeeded(screen.Kind, r.Id, priorRole)))
        {
            await ReadInto(texts, region, regions, cancellationToken).ConfigureAwait(false);
        }

        var crops = new FrameCrops(frameWidth, frameHeight, images).WithText(texts);
        if (screen.Kind is ScreenKind.MainMenu or ScreenKind.ModeList or ScreenKind.Lobby or ScreenKind.Searching or ScreenKind.PreMatchLobby)
        {
            _owner.Observe(_analyzer.ReadProfileName(crops));
        }

        var detection = new DetectionContext(context, now, priorScene, CurrentOwner(), _promptEvidence, priorRole);
        var observations = new List<Observation>();
        foreach (var detector in Detectors)
        {
            observations.AddRange(detector.Analyze(crops, detection).Observations);
        }

        TrackPrompts(screen.Kind, crops, detection, observations, now);
        _unknownCharacters?.Observe(screen.Kind, _analyzer.ReadUnknownCharacters(crops, detection, screen.Kind, _excludedNames?.Invoke()), now);

        return new HudFrame(observations, screen.Kind, (int)(_reader.OcrCalls - before), OwnerNameKnown);
    }

    private string? CurrentOwner() => _ownerOverride ?? _owner.Current;

    private void ResetPrompts()
    {
        _promptEvidence = PromptEvidence.Empty;
        _promptCandidates.Clear();
        _promptsReported = false;
    }

    /// <summary>
    /// Keeps the match's power prompt state between frames (it is not a detection, so it lives here and not in a detector): the evidence the next
    /// frame's detector receives, the recurring prompt texts for the learning path, and the hand-over to the sink once the scoreboard names the killer.
    /// Everything is cleared when the next match begins (a menu, the queue or the pre-match lobby is up).
    /// </summary>
    private void TrackPrompts(ScreenKind screen, FrameCrops crops, DetectionContext detection, List<Observation> observations, DateTimeOffset now)
    {
        if (screen is ScreenKind.MainMenu or ScreenKind.ModeList or ScreenKind.Lobby or ScreenKind.Searching or ScreenKind.PreMatchLobby)
        {
            ResetPrompts();
            return;
        }

        foreach (var observation in observations.Where(o => o.Kind == ObservationKind.Character && o.Value is not null && _vocabulary.IsKillerId(o.Value)))
        {
            _promptEvidence = _promptEvidence.WithCorroboration(observation.Value!);
        }

        if (screen == ScreenKind.TrialHud)
        {
            var reading = _analyzer.ReadPrompt(crops, detection);
            _promptEvidence = _promptEvidence.With(reading.Match);
            _promptCandidates.Observe(reading.Candidates, NamesOnScreen(crops, detection), _vocabulary);
            return;
        }

        if (screen == ScreenKind.Scoreboard && !_promptsReported)
        {
            ReportPrompts(observations, crops, detection, now);
        }
    }

    /// <summary>
    /// The learning hand-over. It needs the owner to have played a killer (the owner's row read as the red killer row), and then either the owner's
    /// character and the killer row naming the same killer (prompts are tied to it) or neither being in the closed list (the prompts are reported
    /// without a killer). Anything less, such as a survivor match, reports nothing. "Neither is in the closed list" means both rows were read and name
    /// nothing of the list; a row that was not read (an OCR miss) is not that, so the match is not reported as one with an unknown killer. The recurring
    /// texts are filtered once more against every player name the scoreboard lists, so a name that appeared only in the prompt region during the match
    /// is dropped here (review F5).
    /// </summary>
    private void ReportPrompts(List<Observation> observations, FrameCrops crops, DetectionContext detection, DateTimeOffset now)
    {
        string? Value(ObservationKind kind) => observations.FirstOrDefault(o => o.Kind == kind)?.Value;
        if (Value(ObservationKind.Role) != nameof(GameRole.Killer))
        {
            return;
        }

        var prompts = _promptCandidates.Recurring(ScoreboardNames(crops), _vocabulary);
        var character = Value(ObservationKind.Character);
        var killer = Value(ObservationKind.Killer);
        if (character is not null && _vocabulary.IsKillerId(character) && string.Equals(character, killer, StringComparison.Ordinal))
        {
            _promptsReported = true;
            if (prompts.Count > 0)
            {
                _promptSink.PromptsAssociated(new PromptAssociation(character, prompts, now));
            }
        }
        else if (character is null && killer is null && _analyzer.ReadIdentity(crops, detection) is { OwnerRowReadNoMatch: true, KillerRowReadNoMatch: true })
        {
            _promptsReported = true;
            if (prompts.Count > 0)
            {
                _promptSink.KillerNotRecognised(new UnmatchedKillerPrompts(prompts, now));
            }
        }
    }

    /// <summary>The player names the scoreboard lists: every line of its rows that is not a character name.</summary>
    private IEnumerable<string> ScoreboardNames(FrameCrops crops)
    {
        for (var i = 0; i < HudProfile.PlayerRows; i++)
        {
            foreach (var line in PromptText.Lines(crops.GetText(HudRegionIds.ScoreRow(i))))
            {
                if (!_vocabulary.Survivors.Match(line).IsAccepted && !_vocabulary.Killers.Match(line).IsAccepted)
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>The words read on this frame that name people: the player column and the owner's name. A prompt-shaped text containing one of them is not kept.</summary>
    private static IEnumerable<string> NamesOnScreen(FrameCrops crops, DetectionContext detection)
    {
        for (var i = 0; i < HudProfile.HudRows; i++)
        {
            if (crops.GetText(HudRegionIds.HudRow(i)) is { } text)
            {
                yield return text;
            }
        }

        if (detection.OwnerName is { } owner)
        {
            yield return owner;
        }
    }

    private async Task ReadInto(Dictionary<string, string> texts, HudRegion region, IReadOnlyDictionary<string, PixelBuffer> regions, CancellationToken cancellationToken)
    {
        if (!regions.TryGetValue(region.Id, out var crop))
        {
            return;
        }

        var read = await _reader.ReadAsync(region.Id, crop, cancellationToken).ConfigureAwait(false);
        if (read.Text is not null)
        {
            texts[region.Id] = read.Text;
        }
    }

    private static bool DetailNeeded(ScreenKind screen, string regionId, GameRole priorRole) => screen switch
    {
        ScreenKind.MatchDetails => regionId == HudRegionIds.TabRealmMap || IsRowOf(regionId, "tab."),
        ScreenKind.Scoreboard => IsRowOf(regionId, "score."),
        ScreenKind.PreMatchLobby => regionId == HudRegionIds.KillerList || regionId.StartsWith("lobby.pair.", StringComparison.Ordinal),
        ScreenKind.TrialHud => regionId == HudRegionIds.PowerPrompt || (regionId == HudRegionIds.HookCounter && priorRole == GameRole.Killer),
        _ => false,
    };

    /// <summary>The pipeline's analyzer and every detector's analyzer keep their own parsed-row cache; all of them are cleared together.</summary>
    private void ClearRowCaches()
    {
        _analyzer.ClearCaches();
        foreach (var detector in Detectors.OfType<HudDetector>())
        {
            detector.Analyzer.ClearCaches();
        }
    }

    /// <summary>Regions whose text can be other players' names: the detail regions (rows, lobby pairs, killer list), the trial column and the spectator bar.</summary>
    private bool HoldsPlayerNames(string regionId) =>
        _profile.Region(regionId) is { Detail: true } || regionId.StartsWith("hud.row.", StringComparison.Ordinal) || regionId == HudRegionIds.SpectateBar;

    private static bool IsRowOf(string regionId, string prefix) => regionId.StartsWith(prefix + "row.", StringComparison.Ordinal);
}
