using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Core.Detection;

/// <summary>Display names for a canonical map id.</summary>
/// <param name="MapName">English map display name.</param>
/// <param name="RealmName">English realm display name.</param>
public sealed record MapNames(string MapName, string RealmName);

/// <summary>Resolves canonical map ids to display names. Implemented by the closed vocabulary in <c>DbdPresence.Vision</c>.</summary>
public interface IMapNameResolver
{
    /// <summary>Returns the names for a map id, or null when the id is not in the vocabulary.</summary>
    MapNames? Resolve(string mapId);
}

/// <summary>Per-session publication switches.</summary>
/// <param name="ExperimentalSessionOptIn">The user enabled "Show experimental detections on Discord – this session only" (FR-DET-17).</param>
public sealed record PublicationOptions(bool ExperimentalSessionOptIn = false);

/// <summary>A detected value that was kept out of presence, and why. Shown in the UI so users see why a field is hidden.</summary>
/// <param name="Field">The field.</param>
/// <param name="Reason">Why it was not published.</param>
public sealed record SuppressedField(FieldKind Field, string Reason);

/// <summary>The result of applying the publication policy to a snapshot.</summary>
/// <param name="Inputs">The composer inputs; every non-null field is publishable.</param>
/// <param name="BetaFields">Fields published at the Experimental tier. They must carry the non-removable beta marker (FR-DET-17).</param>
/// <param name="Suppressed">Detected values that were held back.</param>
public sealed record PublicationResult(PresenceInputs Inputs, IReadOnlySet<FieldKind> BetaFields, IReadOnlyList<SuppressedField> Suppressed);

/// <summary>
/// Turns an engine snapshot into composer inputs and decides, per detector, what may reach Discord (DETECTION_VALIDATION §7).
/// A field is published only when all of these hold: the engine confirmed it and it is fresh, its detector's tier is not
/// Disabled, the thresholds are measured rather than initial guesses, the detector is not degraded, and, at the
/// Experimental tier, the per-session opt-in is on. Results, eliminations and status never publish at Experimental: for the own status and the two
/// outcome detectors this is enforced here (FR-AUTO-05), and each class (Hooked, Dying, every outcome) must also be on the detector's publishable list.
/// The map is published only when its field is Confirmed and resolves in the vocabulary (D16, FR-PACK-06).
/// </summary>
public static class PublicationPolicy
{
    /// <summary>Applies the policy.</summary>
    /// <param name="snapshot">The engine snapshot.</param>
    /// <param name="settings">Per-detector settings; a missing entry is Disabled.</param>
    /// <param name="options">Per-session switches.</param>
    /// <param name="maps">Resolves map ids to names; null means maps are never published.</param>
    /// <param name="now">The current time.</param>
    /// <param name="gameStartedUtc">When the game process started, if known.</param>
    /// <returns>The publishable inputs and what was held back.</returns>
    public static PublicationResult Compose(
        MatchSnapshot snapshot,
        IReadOnlyDictionary<DetectorId, DetectorSettings> settings,
        PublicationOptions options,
        IMapNameResolver? maps,
        DateTimeOffset now,
        DateTimeOffset? gameStartedUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        var suppressed = new List<SuppressedField>();
        var beta = new HashSet<FieldKind>();

        bool Allowed(DetectorId detector, FieldKind kind, ConfirmedField field, bool validatedOnly = false)
        {
            if (field.LastValue is null || field.Source == FieldSource.UserDeclared)
            {
                return false;
            }

            if (field.State != FieldState.Confirmed)
            {
                suppressed.Add(new SuppressedField(kind, field.Reason ?? $"field is {field.State}"));
                return false;
            }

            var s = settings.GetValueOrDefault(detector, DetectorSettings.Default);
            if (s.Tier == DetectionTier.Disabled)
            {
                suppressed.Add(new SuppressedField(kind, "detector tier is Disabled (in-app only)"));
                return false;
            }

            if (s.Provenance != ThresholdProvenance.Measured || s.Problem() is not null)
            {
                suppressed.Add(new SuppressedField(kind, "thresholds are initial guesses"));
                return false;
            }

            if (validatedOnly && s.Tier != DetectionTier.Validated)
            {
                suppressed.Add(new SuppressedField(kind, "results and status need the Validated tier"));
                return false;
            }

            if (s.Tier == DetectionTier.Experimental)
            {
                if (!options.ExperimentalSessionOptIn)
                {
                    suppressed.Add(new SuppressedField(kind, "experimental detections are off for this session"));
                    return false;
                }

                beta.Add(kind);
            }

            return true;
        }

        var sceneField = snapshot.Scene;
        var scene = snapshot.ConfirmedScene;
        var sceneOk = Allowed(DetectorId.Scene, FieldKind.Scene, sceneField);
        var roleOk = Allowed(DetectorId.Role, FieldKind.Role, snapshot.Role);
        var role = roleOk ? snapshot.DetectedRole : GameRole.None;

        var state = snapshot.Game switch
        {
            GameState.NotRunning => PresenceState.NotRunning,
            GameState.Launching => PresenceState.Launching,
            _ => sceneOk ? StateFor(scene, role) : PresenceState.Running,
        };

        var inTrial = scene is Scene.InTrial or Scene.Spectating or Scene.InMatchMenu;
        var postMatch = scene is Scene.Results or Scene.Lobby or Scene.MainMenu;

        string? mapId = null;
        if (snapshot.Game == GameState.Running)
        {
            var mapField = snapshot.Map;
            if (!inTrial && postMatch && snapshot.LastSession?.MapId is { } lastMap)
            {
                mapField = new ConfirmedField(FieldState.Confirmed, lastMap, FieldSource.Detected, snapshot.LastSession.EndedUtc, null, null);
            }
            else if (!inTrial)
            {
                mapField = ConfirmedField.Empty();
            }

            if (Allowed(DetectorId.Map, FieldKind.Map, mapField))
            {
                mapId = mapField.ConfirmedValue;
            }
        }

        MapNames? names = mapId is null ? null : maps?.Resolve(mapId);
        if (mapId is not null && names is null)
        {
            suppressed.Add(new SuppressedField(FieldKind.Map, "map id is not in the vocabulary"));
            beta.Remove(FieldKind.Map);
            mapId = null;
        }

        int? generators = null;
        if (snapshot.Game == GameState.Running && scene == Scene.InTrial && Allowed(DetectorId.Generators, FieldKind.Generators, snapshot.Generators))
        {
            generators = snapshot.ConfirmedGenerators;
        }

        int? hookCount = null;
        if (snapshot.Game == GameState.Running && sceneOk && scene == Scene.InTrial && role == GameRole.Killer
            && Allowed(DetectorId.HookCount, FieldKind.HookCount, snapshot.HookCount))
        {
            hookCount = snapshot.ConfirmedHookCount;
        }

        var ownStatus = OwnStatus.None;
        if (snapshot.Game == GameState.Running && sceneOk && scene == Scene.InTrial && role == GameRole.Survivor
            && Allowed(DetectorId.Status, FieldKind.OwnStatus, snapshot.OwnStatus, validatedOnly: true))
        {
            var value = snapshot.OwnStatus.ConfirmedValue;
            if (value is not null && OwnStatuses.IsPublishable(value))
            {
                if (ClassListed(DetectorId.Status, value))
                {
                    ownStatus = value == OwnStatuses.Hooked ? OwnStatus.Hooked : OwnStatus.Dying;
                }
                else
                {
                    suppressed.Add(new SuppressedField(FieldKind.OwnStatus, "this status has not passed the per-class check"));
                }
            }
        }

        string? outcomeId = null;
        if (snapshot.Game == GameState.Running && sceneOk && scene == Scene.Results && role is GameRole.Survivor or GameRole.Killer)
        {
            var survivor = role == GameRole.Survivor;
            var (detector, kind, field, other) = survivor
                ? (DetectorId.SurvivorResult, FieldKind.SurvivorResult, snapshot.SurvivorResult, snapshot.KillerResult)
                : (DetectorId.KillerResult, FieldKind.KillerResult, snapshot.KillerResult, snapshot.SurvivorResult);
            if (other.ConfirmedValue is not null)
            {
                suppressed.Add(new SuppressedField(kind, "the outcome does not fit the role"));
            }

            if (Allowed(detector, kind, field, validatedOnly: true) && field.ConfirmedValue is { } outcome)
            {
                if (!(survivor ? ResultOutcomes.IsSurvivorOutcome(outcome) : ResultOutcomes.IsKillerOutcome(outcome)))
                {
                    suppressed.Add(new SuppressedField(kind, "the outcome does not fit the role"));
                }
                else if (ClassListed(detector, outcome))
                {
                    outcomeId = outcome;
                }
                else
                {
                    suppressed.Add(new SuppressedField(kind, "this outcome has not passed the per-class check"));
                }
            }
        }

        var session = sceneOk && inTrial ? snapshot.Session : null;
        var inputs = new PresenceInputs
        {
            State = state,
            Now = now,
            GameStartedUtc = gameStartedUtc,
            MatchStartedUtc = session?.StartedUtc,
            MatchStartApproximate = session?.Quality == StartQuality.JoinedLate,
            DetectedRole = role,
            MapName = names?.MapName,
            MapId = mapId,
            RealmName = names?.RealmName,
            GeneratorsRemaining = generators,
            HookCount = hookCount,
            OwnStatus = ownStatus,
            OutcomeId = outcomeId,
        };

        return new PublicationResult(inputs, beta, suppressed);

        bool ClassListed(DetectorId detector, string value) =>
            settings.GetValueOrDefault(detector, DetectorSettings.Default).PublishableClasses?.Contains(value, StringComparer.Ordinal) == true;
    }

    private static PresenceState StateFor(Scene scene, GameRole role) => scene switch
    {
        Scene.MainMenu => PresenceState.MainMenu,
        Scene.Matchmaking => PresenceState.Matchmaking,
        Scene.Lobby => role switch
        {
            GameRole.Survivor => PresenceState.LobbySurvivor,
            GameRole.Killer => PresenceState.LobbyKiller,
            _ => PresenceState.Lobby,
        },
        Scene.Loading => PresenceState.Loading,
        Scene.InTrial or Scene.Spectating or Scene.InMatchMenu => role switch
        {
            GameRole.Survivor => PresenceState.InTrialSurvivor,
            GameRole.Killer => PresenceState.InTrialKiller,
            _ => PresenceState.InTrial,
        },
        Scene.Results => PresenceState.Results,
        _ => PresenceState.Running,
    };
}
