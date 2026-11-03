using System.Globalization;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Settings;
using DbdPresence.Core.Templates;

namespace DbdPresence.Core.Composition;

/// <summary>The composer's decision, including human-readable notes for "Why is this hidden?" (UI_SPEC §5).</summary>
/// <param name="Activity">The activity to publish, or null to clear the activity.</param>
/// <param name="TemplateState">The state whose template was used, if any.</param>
/// <param name="Notes">Reasons for fallbacks and hidden fields.</param>
/// <param name="Issues">Validator truncations and omissions.</param>
/// <param name="Reasons">The same findings as <paramref name="Notes"/> as codes, which the Presentation layer turns into plain sentences (whole-app design §3.4).</param>
public sealed record CompositionResult(
    ValidatedActivity? Activity,
    PresenceState? TemplateState,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<HiddenReason> Reasons);

/// <summary>
/// Turns confirmed inputs + profile + privacy into a validated activity (ARCHITECTURE §5). Pure and
/// deterministic: no clock, I/O or randomness.
/// </summary>
public static class PresenceComposer
{
    /// <summary>The labelling added to image hover text while Behaviour's artwork is shown (FR-RPC-12).</summary>
    public const string BehaviourLabel = "Unofficial · © Behaviour Interactive";

    private const string LabelSeparator = " · ";

    /// <summary>Composes the activity for the given inputs.</summary>
    /// <param name="inputs">Confirmed inputs.</param>
    /// <param name="profile">The active profile.</param>
    /// <param name="privacy">Live privacy state.</param>
    /// <param name="displayName">The user's "Playing ___" text (FR-RPC-13); the profile's own name override wins when both are set.</param>
    public static CompositionResult Compose(PresenceInputs inputs, PresenceProfile profile, PrivacyState privacy, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(privacy);
        var notes = new List<string>();
        var reasons = new List<HiddenReason>();

        if (privacy.Suspended)
        {
            return Clear("Presence is off: access for this account was withdrawn.", HiddenReasonCode.AccessWithdrawn);
        }

        if (privacy.HideFullActivity)
        {
            return Clear("Activity hidden by Privacy settings.", HiddenReasonCode.HiddenCompletely);
        }

        var state = inputs.State;
        if (privacy.Paused)
        {
            if (profile.WhenPaused == PauseBehavior.Clear || state == PresenceState.NotRunning)
            {
                return Clear("Paused.", HiddenReasonCode.Paused);
            }

            notes.Add("Paused: showing the generic template without match details.");
            reasons.Add(new(HiddenReasonCode.PausedGeneric));
            state = PresenceState.Running;
            inputs = inputs with
            {
                MapName = null,
                MapId = null,
                RealmName = null,
                GeneratorsRemaining = null,
                HookCount = null,
                OwnStatus = OwnStatus.None,
                OutcomeId = null,
                Result = null,
                StatusDead = null,
                StatusHooked = null,
                MatchStartedUtc = null,
            };
        }

        if (state == PresenceState.NotRunning && profile.WhenNotRunning == NotRunningBehavior.Clear)
        {
            return Clear("Game not running: activity cleared (Discord settings).", HiddenReasonCode.GameNotRunning);
        }

        var templates = privacy.StreamerMode ? profile.StreamerTemplates : profile.Templates;
        if (privacy.StreamerMode)
        {
            notes.Add("Streamer mode: using the streamer template set.");
            reasons.Add(new(HiddenReasonCode.PrivateStyle));

            // Defence in depth: whatever the Private text or pictures say, the map, the character and live match details never reach Discord in
            // this style. Map and character pictures are refused and their tokens are left out (the Private style reason already explains it).
            profile = profile with { Visibility = profile.Visibility with { ShowMap = false, ShowCharacter = false, ShowLiveMatchStats = false } };
        }

        if (!profile.Visibility.ShowScene && state is not (PresenceState.NotRunning or PresenceState.Launching or PresenceState.Running))
        {
            // Where the user is stays private: every moment that needs screen reading shows the plain "playing" text instead.
            notes.Add("Where you are is hidden by you: showing the plain playing text.");
            reasons.Add(new(HiddenReasonCode.SceneHiddenByUser));
            state = PresenceState.Running;
        }

        // D30: the role comes only from confirmed detection; nothing is declared by hand.
        var role = inputs.DetectedRole;
        foreach (var candidate in PresenceStateChain.For(state))
        {
            var template = templates.Get(candidate);
            if (template is null)
            {
                notes.Add($"{candidate} template is empty — falling back.");
                reasons.Add(new(HiddenReasonCode.TextFallback));
                continue;
            }

            var timerStart = TimerStart(template.Timer, inputs, profile.Visibility, notes, reasons);
            var values = TokenValues(inputs, profile, candidate, role, timerStart, notes, reasons);
            var details = Render(template.Details, values);
            if (details is null)
            {
                notes.Add($"{candidate} details need a value that is not confirmed — falling back.");
                AddMissingDetails(template.Details, values, profile.Visibility, reasons);
                reasons.Add(new(HiddenReasonCode.TextFallback));
                continue;
            }

            // The automatic picture shows moments of the match, so Private style (streamer mode) gets the role icon only.
            var automatic = !privacy.StreamerMode;
            var largeImage = ResolveImage(template.LargeImage, profile, inputs, candidate, role, isLarge: true, automatic, notes, reasons);
            var smallImage = profile.Visibility.ShowSmallImage
                ? ResolveImage(template.SmallImage, profile, inputs, candidate, role, isLarge: false, automatic, notes, reasons)
                : null;
            var largeText = RenderOptionalField(template.LargeText, values, profile.Visibility, "Large image text", notes, reasons);
            var smallText = profile.Visibility.ShowSmallImage ? RenderOptionalField(template.SmallText, values, profile.Visibility, "Small image text", notes, reasons) : null;
            if (profile.Artwork.IsBehaviourArtwork)
            {
                // The hover text is the one place Discord shows beside the image that this is not an official app.
                if (largeImage is not null)
                {
                    largeText = WithBehaviourLabel(largeText);
                }
                else if (smallImage is not null)
                {
                    smallText = WithBehaviourLabel(smallText);
                }
            }

            var draft = new ActivityDraft
            {
                Kind = profile.ActivityKind,
                Details = details,
                State = RenderOptionalField(template.State, values, profile.Visibility, "State line", notes, reasons),
                StartTimestamp = timerStart,
                LargeImage = largeImage,
                LargeText = largeText,
                SmallImage = smallImage,
                SmallText = smallText,
                Buttons = profile.Visibility.ShowButtons ? [.. template.Buttons.Select(b => new ButtonDraft(b.Label, b.Url))] : [],
                StatusDisplay = profile.StatusDisplay,
                NameOverride = !string.IsNullOrWhiteSpace(profile.NameOverride) ? profile.NameOverride
                    : string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            };

            var validation = ActivityValidator.Validate(draft);
            if (validation.Activity.Details is null)
            {
                notes.Add($"{candidate} details are too short for Discord — falling back.");
                reasons.Add(new(HiddenReasonCode.TextFallback));
                continue;
            }

            return new CompositionResult(validation.Activity, candidate, notes, validation.Issues, Distinct(FilterPrivate(reasons, privacy.StreamerMode)));
        }

        notes.Add("No usable template for this state: activity cleared.");
        reasons.Add(new(HiddenReasonCode.NothingToShow));
        return new CompositionResult(null, null, notes, [], Distinct(FilterPrivate(reasons, privacy.StreamerMode)));

        CompositionResult Clear(string reason, HiddenReasonCode code) => new(null, null, [.. notes, reason], [], [.. reasons, new HiddenReason(code)]);
    }

    /// <summary>Formats an elapsed duration for the {duration} token at minute resolution.</summary>
    public static string FormatDuration(TimeSpan elapsed)
    {
        var minutes = Math.Max(0, (int)elapsed.TotalMinutes);
        return minutes < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min");
    }

    /// <summary>Appends the label to <paramref name="text"/>, shortening the user's text (never the label) to stay within Discord's limits.</summary>
    private static string WithBehaviourLabel(string? text)
    {
        var cleaned = ActivityText.Clean(text);
        if (cleaned.Length == 0)
        {
            return BehaviourLabel;
        }

        if (cleaned.Contains(BehaviourLabel, StringComparison.Ordinal))
        {
            return cleaned;
        }

        var reserveChars = BehaviourLabel.Length + LabelSeparator.Length;
        var reserveBytes = TextFit.Utf8Length(BehaviourLabel) + TextFit.Utf8Length(LabelSeparator);
        var room = TextFit.Fit(cleaned, DiscordLimits.TextMax - reserveChars, DiscordLimits.TextMax - reserveBytes);
        return room + LabelSeparator + BehaviourLabel;
    }

    /// <summary>In the Private style the map, character and live details are held back by the style itself, so "hidden by you" would be untrue.</summary>
    private static List<HiddenReason> FilterPrivate(List<HiddenReason> reasons, bool streamer) => streamer
        ? [.. reasons.Where(r => r.Code is not (HiddenReasonCode.MapHiddenByUser or HiddenReasonCode.CharacterHiddenByUser or HiddenReasonCode.LiveStatsOff))]
        : reasons;

    private static IReadOnlyList<HiddenReason> Distinct(List<HiddenReason> reasons) => [.. reasons.Distinct()];

    private static bool IsInTrial(PresenceState state) =>
        state is PresenceState.InTrial or PresenceState.InTrialSurvivor or PresenceState.InTrialKiller;

    private static DateTimeOffset? TimerStart(TimerSource source, PresenceInputs inputs, VisibilitySettings visibility, List<string> notes, List<HiddenReason> reasons)
    {
        if (source == TimerSource.None)
        {
            return null;
        }

        if (!visibility.ShowTimers)
        {
            notes.Add("Timer hidden by Privacy settings.");
            reasons.Add(new(HiddenReasonCode.TimerHiddenByUser));
            return null;
        }

        if (source == TimerSource.GameSession)
        {
            return inputs.GameStartedUtc;
        }

        if (inputs.MatchStartedUtc is null)
        {
            return null;
        }

        if (inputs.MatchStartApproximate && !visibility.AllowApproximateDurations)
        {
            notes.Add("Match timer hidden: the trial start is approximate (joined late).");
            reasons.Add(new(HiddenReasonCode.TimerStartNotSeen));
            return null;
        }

        return inputs.MatchStartedUtc;
    }

    private static Dictionary<string, string> TokenValues(
        PresenceInputs inputs, PresenceProfile profile, PresenceState state, Role role, DateTimeOffset? timerStart, List<string> notes, List<HiddenReason> reasons)
    {
        var visibility = profile.Visibility;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var withholdTrialDetails = visibility.PostMatchDetailsOnly && IsInTrial(state);

        if (visibility.ShowRole)
        {
            if (role != Role.None)
            {
                values[TemplateTokens.Role] = role.ToString();
            }
        }
        else if (role != Role.None)
        {
            reasons.Add(new(HiddenReasonCode.RoleHiddenByUser));
        }

        if (visibility.ShowCharacter)
        {
            AddIfPresent(values, TemplateTokens.Character, inputs.CharacterName?.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(inputs.CharacterName))
        {
            reasons.Add(new(HiddenReasonCode.CharacterHiddenByUser));
        }

        if (visibility.ShowMode)
        {
            AddIfPresent(values, TemplateTokens.Mode, inputs.ModeName?.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(inputs.ModeName))
        {
            reasons.Add(new(HiddenReasonCode.ModeHiddenByUser));
        }

        if (visibility.ShowMap && !withholdTrialDetails)
        {
            AddIfPresent(values, TemplateTokens.Map, inputs.MapName);
            AddIfPresent(values, TemplateTokens.Realm, inputs.RealmName);
        }
        else if (inputs.MapName is not null)
        {
            notes.Add(withholdTrialDetails ? "Map withheld until the trial ends (post-match details only)." : "Map hidden by Privacy settings.");
            reasons.Add(new(withholdTrialDetails ? HiddenReasonCode.MapAfterMatch : HiddenReasonCode.MapHiddenByUser));
        }

        if (visibility.ShowLiveMatchStats && !withholdTrialDetails)
        {
            AddIfPresent(values, TemplateTokens.Generators, inputs.GeneratorsRemaining?.ToString(CultureInfo.InvariantCulture));
            AddIfPresent(values, TemplateTokens.StatusDead, inputs.StatusDead?.ToString(CultureInfo.InvariantCulture));
            AddIfPresent(values, TemplateTokens.StatusHooked, inputs.StatusHooked?.ToString(CultureInfo.InvariantCulture));
            AddIfPresent(values, TemplateTokens.HookCount, role == Role.Killer ? inputs.HookCount?.ToString(CultureInfo.InvariantCulture) : null);
        }
        else if (inputs.GeneratorsRemaining is not null || inputs.StatusDead is not null || inputs.StatusHooked is not null || inputs.HookCount is not null)
        {
            notes.Add("Live match stats hidden (Privacy settings — off by default).");
            reasons.Add(new(HiddenReasonCode.LiveStatsOff));
        }

        if (visibility.ShowResult)
        {
            AddIfPresent(values, TemplateTokens.Result, inputs.Result);
        }
        else if (inputs.Result is not null)
        {
            reasons.Add(new(HiddenReasonCode.ResultHiddenByUser));
        }

        if (timerStart is { } start)
        {
            values[TemplateTokens.Duration] = FormatDuration(inputs.Now - start);
        }

        return values;
    }

    private static void AddIfPresent(Dictionary<string, string> values, string token, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[token] = value;
        }
    }

    private static string? Render(string template, IReadOnlyDictionary<string, string> values) =>
        TemplateRenderer.Render(TemplateParser.Parse(template), values);

    private static string? RenderOptionalField(
        string template, IReadOnlyDictionary<string, string> values, VisibilitySettings visibility, string label, List<string> notes, List<HiddenReason> reasons)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var rendered = Render(template, values);
        if (string.IsNullOrEmpty(rendered))
        {
            notes.Add($"{label} omitted: it needs a value that is not confirmed.");
            AddMissingDetails(template, values, visibility, reasons);
            return null;
        }

        // An optional part that was left out because its detail is not known says which detail it was.
        AddMissingDetails(template, values, visibility, reasons);
        return rendered;
    }

    private static void AddMissingDetails(string template, IReadOnlyDictionary<string, string> values, VisibilitySettings visibility, List<HiddenReason> reasons)
    {
        foreach (var token in TemplateParser.Parse(template).Tokens.Where(t => !values.ContainsKey(t) && !HiddenByUser(t, visibility)))
        {
            var reason = new HiddenReason(HiddenReasonCode.DetailNotConfirmed, DetailName(token));
            if (!reasons.Contains(reason))
            {
                reasons.Add(reason);
            }
        }
    }

    /// <summary>True when the user's own switch hides the detail, which has its own reason; "not confirmed" would be untrue then.</summary>
    private static bool HiddenByUser(string token, VisibilitySettings visibility) => token switch
    {
        TemplateTokens.Role => !visibility.ShowRole,
        TemplateTokens.Character => !visibility.ShowCharacter,
        TemplateTokens.Mode => !visibility.ShowMode,
        TemplateTokens.Map or TemplateTokens.Realm => !visibility.ShowMap,
        TemplateTokens.Generators or TemplateTokens.StatusDead or TemplateTokens.StatusHooked or TemplateTokens.HookCount => !visibility.ShowLiveMatchStats,
        TemplateTokens.Result => !visibility.ShowResult,
        TemplateTokens.Duration => !visibility.ShowTimers,
        _ => false,
    };

    /// <summary>The words for a token in user-facing reasons (whole-app design §8: "a detail", never "token").</summary>
    private static string DetailName(string token) => token switch
    {
        TemplateTokens.Role => "Role",
        TemplateTokens.Character => "Character",
        TemplateTokens.Map => "Map",
        TemplateTokens.Realm => "Realm",
        TemplateTokens.Generators => "Generators left",
        TemplateTokens.Mode => "Game mode",
        TemplateTokens.Result => "Match result",
        TemplateTokens.Duration => "Time played",
        TemplateTokens.StatusDead => "Survivors out",
        TemplateTokens.StatusHooked => "Survivors on hooks",
        TemplateTokens.HookCount => "Hook count",
        _ => token,
    };

    /// <summary>
    /// The automatic small picture (FR-AUTO-01), evaluated from the top: a result after the match, then the hook or dying icon in a trial, else the
    /// role icon. It returns the reference of a status or result picture, or null for "use the role icon". Every row needs the role to be shown and
    /// the picture to be in the pack; the hook and dying rows also follow "Show live match stats" and "Post-match details only" (FR-AUTO-06).
    /// </summary>
    private static string? AutomaticReference(PresenceInputs inputs, PresenceProfile profile, PresenceState state, Role role, List<string> notes, List<HiddenReason> reasons)
    {
        var visibility = profile.Visibility;
        if (!visibility.ShowRole)
        {
            return null;
        }

        string? slug = null;
        var moment = string.Empty;
        if (state == PresenceState.Results && visibility.ShowResult && inputs.OutcomeId is { } outcome && FitsRole(outcome, role))
        {
            slug = AutomaticIcons.SlugOfOutcome(outcome);
            moment = "Match result";
        }
        else if (state == PresenceState.InTrialSurvivor && role == Role.Survivor && inputs.OwnStatus != OwnStatus.None)
        {
            if (!visibility.ShowLiveMatchStats)
            {
                notes.Add("Hook and down pictures are off with live match details (Privacy settings; off by default): showing the role icon.");
                reasons.Add(new(HiddenReasonCode.LiveStatsOff));
                return null;
            }

            if (visibility.PostMatchDetailsOnly)
            {
                return null; // trial details wait until the trial ends
            }

            slug = AutomaticIcons.SlugOf(inputs.OwnStatus);
            moment = inputs.OwnStatus == OwnStatus.Hooked ? "On the hook" : "Down";
        }

        if (slug is null)
        {
            return null;
        }

        if (profile.Artwork.StatusImages.TryGetValue(slug, out var reference) && !string.IsNullOrWhiteSpace(reference))
        {
            return reference;
        }

        notes.Add($"Automatic picture for '{moment}' omitted: the pack has no picture for it, so the role icon is shown.");
        reasons.Add(new(HiddenReasonCode.StatusPictureMissing, moment));
        return null;
    }

    /// <summary>A survivor outcome fits the Survivor role and a killer outcome the Killer role; anything else is a contradiction and is dropped.</summary>
    private static bool FitsRole(string outcomeId, Role role) => role switch
    {
        Role.Survivor => ResultOutcomes.IsSurvivorOutcome(outcomeId),
        Role.Killer => ResultOutcomes.IsKillerOutcome(outcomeId),
        _ => false,
    };

    private static string? ResolveImage(
        ImageSlot slot, PresenceProfile profile, PresenceInputs inputs, PresenceState state, Role role, bool isLarge, bool automaticAllowed, List<string> notes, List<HiddenReason> reasons)
    {
        var artwork = profile.Artwork;
        var source = slot.Source;
        string? automatic = null;
        if (source == ImageSource.Automatic)
        {
            // Automatic is for the small image only; on the large image (rejected by the validator) and in Private style it is the role icon.
            automatic = !isLarge && automaticAllowed ? AutomaticReference(inputs, profile, state, role, notes, reasons) : null;
            source = automatic is null ? ImageSource.RoleArtwork : ImageSource.Fixed;
        }

        var resolved = source switch
        {
            ImageSource.Fixed => automatic ?? slot.Value,
            ImageSource.AppLogo => artwork.AppLogo,
            ImageSource.RoleArtwork when profile.Visibility.ShowRole => role switch
            {
                Role.Survivor => artwork.RoleSurvivor,
                Role.Killer => artwork.RoleKiller,
                _ => null,
            },
            ImageSource.MapArtwork when profile.Visibility.ShowMap && inputs.MapId is { } mapId =>
                artwork.Maps.TryGetValue(mapId, out var mapArt) ? mapArt : null,
            ImageSource.CharacterArtwork when profile.Visibility.ShowCharacter && !string.IsNullOrWhiteSpace(inputs.CharacterName) =>
                artwork.CharacterImage ?? RoleArtworkFor(profile, role),
            ImageSource.CharacterArtwork when profile.Visibility.ShowCharacter => RoleArtworkFor(profile, role),
            _ => null,
        };

        if (resolved is null && source == ImageSource.RoleArtwork && profile.Visibility.ShowRole && role is not (Role.Survivor or Role.Killer))
        {
            notes.Add("Role artwork omitted: your role is not known; it appears when detection confirms it.");
            reasons.Add(new(HiddenReasonCode.RolePictureNeedsRole));
        }

        if (resolved is null && isLarge && source != ImageSource.None)
        {
            resolved = artwork.Fallback;
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            return null;
        }

        if (IsAssetKey(resolved) && !artwork.ConfirmedUploadedKeys.Contains(resolved))
        {
            if (artwork.ReplaceUnconfirmedKeys)
            {
                var fallback = isLarge && artwork.ConfirmedUploadedKeys.Contains(artwork.Fallback) ? artwork.Fallback : null;
                notes.Add($"Image '{resolved}' is not confirmed as uploaded to your Discord application — {(fallback is null ? "omitted" : "using the fallback")}.");
                reasons.Add(new(HiddenReasonCode.PictureMayNotExist, resolved));
                return fallback;
            }

            notes.Add($"Image '{resolved}' is not confirmed as uploaded: Discord shows it only if your Discord application has an art asset with exactly this name.");
            reasons.Add(new(HiddenReasonCode.PictureMayNotExist, resolved));
        }

        return resolved;
    }

    private static string? RoleArtworkFor(PresenceProfile profile, Role role) => !profile.Visibility.ShowRole
        ? null
        : role switch
        {
            Role.Survivor => profile.Artwork.RoleSurvivor,
            Role.Killer => profile.Artwork.RoleKiller,
            _ => null,
        };

    private static bool IsAssetKey(string reference) => !reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
