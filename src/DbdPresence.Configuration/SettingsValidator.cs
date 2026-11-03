using System.Text.RegularExpressions;
using DbdPresence.Core.Game;
using DbdPresence.Core.Pictures;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration;

/// <summary>
/// Validates settings field by field (ADR-0007). An invalid value is reset to its default with an issue
/// describing what happened — never a crash, never a silent change. Template <em>text</em> is kept even
/// when it has syntax errors (the editor shows them and the composer falls back), but its length is bounded.
/// </summary>
public static partial class SettingsValidator
{
    /// <summary>Maximum stored length of one template field (source text, before rendering).</summary>
    public const int TemplateSourceMax = 512;

    /// <summary>Maximum number of profiles.</summary>
    public const int MaxProfiles = 50;

    /// <summary>Maximum number of extra process names.</summary>
    public const int MaxExtraProcesses = 10;

    /// <summary>Maximum number of map artwork entries.</summary>
    public const int MaxMapArtwork = 200;

    /// <summary>Maximum number of per-image pack overrides.</summary>
    public const int MaxPackOverrides = 500;

    /// <summary>Maximum number of characters with own pictures (the roster limit).</summary>
    public const int MaxPictureKeys = 400;

    /// <summary>Maximum number of confirmed asset keys.</summary>
    public const int MaxConfirmedKeys = 300;

    /// <summary>Validates and sanitises a settings document.</summary>
    public static (AppSettings Settings, IReadOnlyList<SettingsIssue> Issues) Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<SettingsIssue>();
        var defaults = new AppSettings();

        var discord = settings.Discord ?? defaults.Discord;
        if (!IsValidApplicationId(discord.OwnApplicationId))
        {
            issues.Add(new("discord.ownApplicationId", "Not a Discord Application ID (17–20 digits); cleared."));
            discord = discord with { OwnApplicationId = string.Empty };
        }

        discord = discord with { OwnApplicationId = discord.OwnApplicationId ?? string.Empty };
        if (string.IsNullOrWhiteSpace(discord.SelectedApplication) || !ApplicationKey().IsMatch(discord.SelectedApplication))
        {
            issues.Add(new("discord.selectedApplication", "Not a valid application key; using the default."));
            discord = discord with { SelectedApplication = DiscordSettings.DefaultApplicationKey };
        }

        discord = discord with { DisplayName = ValidateDisplayName(discord.DisplayName, issues) };

        if (string.IsNullOrWhiteSpace(discord.ApplicationName) || discord.ApplicationName.Length > 32)
        {
            issues.Add(new("discord.applicationName", "Must be 1–32 characters; reset to default."));
            discord = discord with { ApplicationName = defaults.Discord.ApplicationName };
        }

        var profiles = ValidateProfiles(settings.Profiles, issues);
        var activeId = profiles.Any(p => p.Id == settings.ActiveProfileId) ? settings.ActiveProfileId : profiles[0].Id;
        if (activeId != settings.ActiveProfileId)
        {
            issues.Add(new("activeProfileId", $"Unknown profile '{settings.ActiveProfileId}'; using '{activeId}'."));
        }

        return (settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Discord = discord,
            Profiles = profiles,
            ActiveProfileId = activeId,
            Privacy = settings.Privacy ?? defaults.Privacy,
            Appearance = ValidateAppearance(settings.Appearance ?? defaults.Appearance, issues),
            Lifecycle = ValidateLifecycle(settings.Lifecycle ?? defaults.Lifecycle, issues),
            Detection = ValidateDetection(settings.Detection ?? defaults.Detection, issues),
            Packs = ValidatePacks(settings.Packs ?? defaults.Packs, issues),
            Study = settings.Study ?? defaults.Study,
            Pictures = ValidatePictures(settings.Pictures, issues),
            PictureSize = PictureLimits.IsSize(settings.PictureSize) ? settings.PictureSize : ResetPictureSize(settings.PictureSize, issues),
            PictureRules = Defined(settings.PictureRules, PictureRules.Unset, "pictureRules", issues),
            PictureNotice = settings.PictureNotice is { HasContent: true } notice ? ValidateNotice(notice) : null,
        }, issues);
    }

    /// <summary>Validates one profile (also used for imports).</summary>
    public static PresenceProfile ValidateProfile(PresenceProfile profile, string path, List<SettingsIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(issues);
        var defaults = new PresenceProfile();

        var name = profile.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 40)
        {
            issues.Add(new($"{path}.name", "Profile names must be 1–40 characters; renamed."));
            name = name.Length == 0 ? "Profile" : name[..40];
        }

        var nameOverride = profile.NameOverride ?? string.Empty;
        if (nameOverride.Length > DiscordLimits.TextMax)
        {
            issues.Add(new($"{path}.nameOverride", $"At most {DiscordLimits.TextMax} characters; cleared."));
            nameOverride = string.Empty;
        }

        return profile with
        {
            Id = ValidId(profile.Id) ? profile.Id : Slug(name),
            Name = name,
            ActivityKind = Defined(profile.ActivityKind, ActivityKind.Playing, $"{path}.activityKind", issues),
            StatusDisplay = profile.StatusDisplay is { } display && !Enum.IsDefined(display) ? null : profile.StatusDisplay,
            NameOverride = nameOverride,
            WhenNotRunning = Defined(profile.WhenNotRunning, NotRunningBehavior.Clear, $"{path}.whenNotRunning", issues),
            WhenPaused = Defined(profile.WhenPaused, PauseBehavior.Clear, $"{path}.whenPaused", issues),
            Templates = ValidateTemplates(profile.Templates ?? defaults.Templates, $"{path}.templates", issues),
            StreamerTemplates = ValidateTemplates(profile.StreamerTemplates ?? defaults.StreamerTemplates, $"{path}.streamerTemplates", issues),
            Visibility = profile.Visibility ?? defaults.Visibility,
            Artwork = ValidateArtwork(profile.Artwork ?? defaults.Artwork, $"{path}.artwork", issues),
#pragma warning disable CS0618 // D30: older files may still carry declared details; they are read, ignored and cleared here, without a finding.
            Manual = new ManualDetails(),
#pragma warning restore CS0618
        };
    }

    /// <summary>
    /// Cleans the "Playing ___" text the same way outgoing Discord text is cleaned (<see cref="ActivityText.Clean"/>): trimmed,
    /// 1–128 characters. Blank becomes null (the application's own name); too long is cleared with an issue.
    /// </summary>
    /// <param name="value">The typed text.</param>
    /// <param name="issues">Receives a finding when the value was rejected.</param>
    public static string? ValidateDisplayName(string? value, List<SettingsIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var cleaned = ActivityText.Clean(value);
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (cleaned.Length > DiscordLimits.TextMax)
        {
            issues.Add(new("discord.displayName", $"At most {DiscordLimits.TextMax} characters; reset to the application's name."));
            return null;
        }

        return cleaned;
    }

    /// <summary>True for an empty value (not configured) or a 17–20 digit snowflake.</summary>
    public static bool IsValidApplicationId(string? value) => string.IsNullOrEmpty(value) || Snowflake().IsMatch(value);

    /// <summary>True for a configured (non-empty) valid Application ID.</summary>
    public static bool IsConfiguredApplicationId(string? value) => !string.IsNullOrEmpty(value) && Snowflake().IsMatch(value);

    /// <summary>True for an acceptable extra executable name.</summary>
    public static bool IsValidProcessName(string? value) => value is not null && ProcessName().IsMatch(value);

    private static List<PresenceProfile> ValidateProfiles(IReadOnlyList<PresenceProfile>? profiles, List<SettingsIssue> issues)
    {
        var result = new List<PresenceProfile>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (profile, index) in (profiles ?? []).Where(p => p is not null).Take(MaxProfiles).Select((p, i) => (p, i)))
        {
            var valid = ValidateProfile(profile, $"profiles[{index}]", issues);
            var id = valid.Id;
            for (var n = 2; !ids.Add(id); n++)
            {
                id = $"{valid.Id}-{n}";
            }

            if (id != valid.Id)
            {
                issues.Add(new($"profiles[{index}].id", $"Duplicate profile id '{valid.Id}'; renamed to '{id}'."));
            }

            result.Add(valid with { Id = id });
        }

        if ((profiles?.Count ?? 0) > MaxProfiles)
        {
            issues.Add(new("profiles", $"At most {MaxProfiles} profiles are kept."));
        }

        if (result.Count == 0)
        {
            issues.Add(new("profiles", "No valid profiles; the default profile was restored."));
            result.Add(BuiltInProfiles.Default);
        }

        return result;
    }

    private static TemplateSet ValidateTemplates(TemplateSet set, string path, List<SettingsIssue> issues)
    {
        var result = new Dictionary<PresenceState, PresenceTemplate>();
        foreach (var (state, template) in set.ByState ?? new Dictionary<PresenceState, PresenceTemplate>())
        {
            if (!Enum.IsDefined(state) || template is null)
            {
                issues.Add(new($"{path}", $"Ignored a template for unknown state '{state}'."));
                continue;
            }

            var p = $"{path}.{state}";
            result[state] = template with
            {
                Details = Bounded(template.Details, $"{p}.details", issues),
                State = Bounded(template.State, $"{p}.state", issues),
                LargeText = Bounded(template.LargeText, $"{p}.largeText", issues),
                SmallText = Bounded(template.SmallText, $"{p}.smallText", issues),
                LargeImage = LargeSlot(template.LargeImage, $"{p}.largeImage", issues),
                SmallImage = ValidSlot(template.SmallImage, ImageSlot.None, $"{p}.smallImage", issues),
                Buttons = ValidButtons(template.Buttons, $"{p}.buttons", issues),
                Timer = Defined(template.Timer, TimerSource.GameSession, $"{p}.timer", issues),
            };
        }

        return new TemplateSet { ByState = result };
    }

    private static List<ButtonTemplate> ValidButtons(IReadOnlyList<ButtonTemplate>? buttons, string path, List<SettingsIssue> issues)
    {
        var kept = new List<ButtonTemplate>();
        foreach (var button in buttons ?? [])
        {
            if (button is null)
            {
                continue;
            }

            var label = button.Label?.Trim() ?? string.Empty;
            var url = button.Url?.Trim() ?? string.Empty;
            if (label.Length == 0 || !TextFit.Fits(label, DiscordLimits.ButtonLabelMaxChars, DiscordLimits.ButtonLabelMaxBytes)
                || !ActivityValidator.IsHttpsUrl(url, DiscordLimits.ButtonUrlMax))
            {
                issues.Add(new(path, $"Removed an invalid button (label 1–{DiscordLimits.ButtonLabelMaxChars} characters, https URL)."));
                continue;
            }

            kept.Add(new ButtonTemplate(label, url));
        }

        if (kept.Count > DiscordLimits.ButtonCount)
        {
            issues.Add(new(path, $"Discord shows at most {DiscordLimits.ButtonCount} buttons; extra buttons removed."));
            kept = kept[..DiscordLimits.ButtonCount];
        }

        return kept;
    }

    /// <summary>The large image cannot be Automatic (FR-AUTO-01: the badge only); it is repaired to the role artwork with a note.</summary>
    private static ImageSlot LargeSlot(ImageSlot? slot, string path, List<SettingsIssue> issues)
    {
        if (slot?.Source == ImageSource.Automatic)
        {
            issues.Add(new(path, "Automatic is only for the small picture; the big picture was set to the role icon."));
            return ImageSlot.Role;
        }

        return ValidSlot(slot, ImageSlot.AppLogo, path, issues);
    }

    private static ImageSlot ValidSlot(ImageSlot? slot, ImageSlot fallback, string path, List<SettingsIssue> issues)
    {
        if (slot is null || !Enum.IsDefined(slot.Source))
        {
            return fallback;
        }

        if (slot.Source == ImageSource.Fixed && (slot.Value is null || !ActivityValidator.IsValidImageReference(slot.Value.Trim())))
        {
            issues.Add(new(path, "Image must be an uploaded asset key (a–z, 0–9, _ - .) or an https URL; reset."));
            return fallback;
        }

        return slot.Source == ImageSource.Fixed ? slot with { Value = slot.Value!.Trim() } : slot with { Value = null };
    }

    private static ArtworkSettings ValidateArtwork(ArtworkSettings artwork, string path, List<SettingsIssue> issues)
    {
        var defaults = new ArtworkSettings();
        string Reference(string? value, string fallback, string field)
        {
            if (value is not null && ActivityValidator.IsValidImageReference(value.Trim()))
            {
                return value.Trim();
            }

            issues.Add(new($"{path}.{field}", "Not a valid asset key or https URL; reset to default."));
            return fallback;
        }

        var maps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (mapId, reference) in (artwork.Maps ?? new Dictionary<string, string>()).Take(MaxMapArtwork))
        {
            if (!string.IsNullOrWhiteSpace(mapId) && mapId.Length <= 64 && reference is not null && ActivityValidator.IsValidImageReference(reference.Trim()))
            {
                maps[mapId] = reference.Trim();
            }
            else
            {
                issues.Add(new($"{path}.maps", $"Removed invalid map artwork entry '{mapId}'."));
            }
        }

        var confirmed = (artwork.ConfirmedUploadedKeys ?? [])
            .Where(k => k is not null && ActivityValidator.IsValidImageReference(k) && !k.StartsWith("https://", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxConfirmedKeys)
            .ToArray();

        return artwork with
        {
            AppLogo = Reference(artwork.AppLogo, defaults.AppLogo, "appLogo"),
            RoleSurvivor = Reference(artwork.RoleSurvivor, defaults.RoleSurvivor, "roleSurvivor"),
            RoleKiller = Reference(artwork.RoleKiller, defaults.RoleKiller, "roleKiller"),
            Fallback = Reference(artwork.Fallback, defaults.Fallback, "fallback"),
            Maps = maps,
            ConfirmedUploadedKeys = confirmed,
        };
    }

    private static AppearanceSettings ValidateAppearance(AppearanceSettings appearance, List<SettingsIssue> issues)
    {
        var defaults = new AppearanceSettings();
        var accent = appearance.Accent;
        if (accent is null || !HexColour().IsMatch(accent))
        {
            issues.Add(new("appearance.accent", "Accent must be a #RRGGBB colour; reset."));
            accent = defaults.Accent;
        }

        var tint = appearance.SurfaceTint ?? string.Empty;
        if (tint.Length > 0 && !HexColour().IsMatch(tint))
        {
            issues.Add(new("appearance.surfaceTint", "Surface tint must be a #RRGGBB colour; cleared."));
            tint = string.Empty;
        }

        var scale = appearance.FontScalePercent;
        if (scale is < 90 or > 150 || scale % 10 != 0)
        {
            issues.Add(new("appearance.fontScalePercent", "Font scale must be 90–150% in steps of 10; reset to 100%."));
            scale = 100;
        }

        return appearance with
        {
            Theme = Defined(appearance.Theme, ThemePreference.System, "appearance.theme", issues),
            Density = Defined(appearance.Density, Density.Comfortable, "appearance.density", issues),
            Accent = accent.ToUpperInvariant(),
            SurfaceTint = tint.ToUpperInvariant(),
            FontScalePercent = scale,
        };
    }

    private static int ResetPictureSize(int size, List<SettingsIssue> issues)
    {
        issues.Add(new("pictureSize", $"Picture size {size} is not 512 or 1024; using 512."));
        return PictureLimits.DefaultSize;
    }

    private static PictureMigrationNotice ValidateNotice(PictureMigrationNotice notice) => notice with
    {
        GenericOverrides = [.. (notice.GenericOverrides ?? []).Where(k => k is { Length: <= 80 } && OverrideKey().IsMatch(k)).Take(20)],
        OverLimit = Math.Clamp(notice.OverLimit, 0, 100_000),
    };

    private static Dictionary<string, CharacterPictures> ValidatePictures(IReadOnlyDictionary<string, CharacterPictures>? pictures, List<SettingsIssue> issues)
    {
        var result = new Dictionary<string, CharacterPictures>(StringComparer.Ordinal);
        var all = pictures ?? new Dictionary<string, CharacterPictures>();
        if (all.Count > MaxPictureKeys)
        {
            issues.Add(new("pictures", $"Kept the pictures of the first {MaxPictureKeys} characters; the rest were removed."));
        }

        foreach (var (key, value) in all.Take(MaxPictureKeys))
        {
            if (!PictureKeys.IsCharacterKey(key) || value?.Items is null)
            {
                issues.Add(new("pictures", $"Removed pictures stored under an invalid key ('{(key is { Length: <= 40 } ? key : "?")}')."));
                continue;
            }

            var items = new List<PictureItem>();
            foreach (var item in value.Items)
            {
                if (item is not null && PictureSlots.IsValid(item) && items.TrueForAll(i => i.Sha256 != item.Sha256))
                {
                    items.Add(item);
                }
                else
                {
                    issues.Add(new($"pictures.{key}", "Removed a picture that breaks the picture rules."));
                }
            }

            if (items.Count > PictureLimits.MaxOwnPerCharacter)
            {
                issues.Add(new($"pictures.{key}", $"Kept the first {PictureLimits.MaxOwnPerCharacter} pictures."));
                items.RemoveRange(PictureLimits.MaxOwnPerCharacter, items.Count - PictureLimits.MaxOwnPerCharacter);
            }

            var use = value.Use;
            var usable = use is null
                || (use.Kind == PictureUseKind.Personal && items.Exists(i => i.Sha256 == use.Sha256))
                || (use.Kind == PictureUseKind.Owner && PackSlug().IsMatch(use.Pack ?? string.Empty) && PackSlug().IsMatch(use.Entry ?? string.Empty));
            if (!usable)
            {
                issues.Add(new($"pictures.{key}", "The picture in use is not in the list; the default portrait is used."));
                use = null;
            }

            if (items.Count > 0 || use is not null)
            {
                result[key] = new CharacterPictures(items, use);
            }
        }

        return result;
    }

    private static PackSettings ValidatePacks(PackSettings packs, List<SettingsIssue> issues)
    {
        string PackId(string? value, string field)
        {
            if (value is not null && (value.Length == 0 || PackSlug().IsMatch(value)))
            {
                return value;
            }

            issues.Add(new($"packs.{field}", "Not a valid pack id; no pack is chosen."));
            return PackSettings.NoPack;
        }

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, file) in (packs.Overrides ?? new Dictionary<string, string>()).Take(MaxPackOverrides))
        {
            if (key is not null && OverrideKey().IsMatch(key) && file is not null && OverrideFileName().IsMatch(file))
            {
                overrides[key] = file;
            }
            else
            {
                issues.Add(new("packs.overrides", $"Removed an invalid image override ('{(key is { Length: <= 40 } ? key : "?")}')."));
            }
        }

        return packs with
        {
            KillerPack = PackId(packs.KillerPack, "killerPack"),
            SurvivorPack = PackId(packs.SurvivorPack, "survivorPack"),
            CharactersPack = PackId(packs.CharactersPack, "charactersPack"),
            MapsPack = PackId(packs.MapsPack, "mapsPack"),
            Overrides = overrides,
        };
    }

    private static LifecycleSettings ValidateLifecycle(LifecycleSettings lifecycle, List<SettingsIssue> issues)
    {
        var names = (lifecycle.ExtraProcessNames ?? []).Where(n => n is not null).Select(n => n.Trim()).ToList();
        var valid = names.Where(IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxExtraProcesses).ToArray();
        if (valid.Length != names.Count)
        {
            issues.Add(new("lifecycle.extraProcessNames", $"Kept {valid.Length} valid executable names (*.exe, at most {MaxExtraProcesses})."));
        }

        var dismissed = lifecycle.DismissedAnnouncement ?? string.Empty;
        if (dismissed.Length != 0 && (dismissed.Length != 16 || !dismissed.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            issues.Add(new("lifecycle.dismissedAnnouncement", "Not a valid announcement id; reset."));
            dismissed = string.Empty;
        }

        return lifecycle with
        {
            DismissedAnnouncement = dismissed,
            ExtraProcessNames = valid,
            Notifications = Defined(lifecycle.Notifications, NotificationLevel.CriticalOnly, "lifecycle.notifications", issues),
            Window = ValidWindow(lifecycle.Window, issues),
        };
    }

    private static WindowPlacement? ValidWindow(WindowPlacement? window, List<SettingsIssue> issues)
    {
        const double MaxCoordinate = 100_000;
        if (window is null)
        {
            return null;
        }

        double[] values = [window.Left, window.Top, window.Width, window.Height];
        if (values.Any(v => !double.IsFinite(v) || Math.Abs(v) > MaxCoordinate) || window.Width <= 0 || window.Height <= 0)
        {
            issues.Add(new("lifecycle.window", "Window position was invalid; the window opens at its default place."));
            return null;
        }

        return window with
        {
            Width = Math.Max(window.Width, WindowPlacement.MinWidth),
            Height = Math.Max(window.Height, WindowPlacement.MinHeight),
        };
    }

    private static DetectionSettings ValidateDetection(DetectionSettings detection, List<SettingsIssue> issues)
    {
        var defaults = new DetectionSettings();
        int Clamp(int value, int min, int max, string field)
        {
            if (value >= min && value <= max)
            {
                return value;
            }

            issues.Add(new($"detection.{field}", $"Must be {min}–{max} ms; clamped."));
            return Math.Clamp(value, min, max);
        }

        var locale = detection.Locale;
        if (string.IsNullOrWhiteSpace(locale) || !LocaleTag().IsMatch(locale))
        {
            issues.Add(new("detection.locale", "Not a valid language tag; reset to en-US."));
            locale = defaults.Locale;
        }

        return detection with
        {
            // Detection can only be enabled after the risk notice was acknowledged.
            Enabled = detection.Enabled && detection.RiskAcknowledged,
            MenuScanIntervalMs = Clamp(detection.MenuScanIntervalMs, 1000, 2000, "menuScanIntervalMs"),
            TrialScanIntervalMs = Clamp(detection.TrialScanIntervalMs, 500, 1000, "trialScanIntervalMs"),
            Locale = locale,
        };
    }

    private static string Bounded(string? text, string path, List<SettingsIssue> issues)
    {
        text ??= string.Empty;
        if (text.Length <= TemplateSourceMax)
        {
            return text;
        }

        issues.Add(new(path, $"Template longer than {TemplateSourceMax} characters; truncated."));
        return text[..TemplateSourceMax];
    }

    private static TEnum Defined<TEnum>(TEnum value, TEnum fallback, string path, List<SettingsIssue> issues)
        where TEnum : struct, Enum
    {
        if (Enum.IsDefined(value))
        {
            return value;
        }

        issues.Add(new(path, $"Unknown value '{value}'; reset to '{fallback}'."));
        return fallback;
    }

    private static bool ValidId(string? id) => id is not null && ProfileId().IsMatch(id);

    private static string Slug(string name)
    {
        var slug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "profile" : slug[..Math.Min(slug.Length, 40)];
    }

    [GeneratedRegex("^[0-9]{17,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex Snowflake();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationKey();

    [GeneratedRegex(@"^[A-Za-z0-9 ._()\-]{1,60}\.exe$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessName();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColour();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileId();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlug();

    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackSlug();

    [GeneratedRegex("^(role|character|map|app):[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex OverrideKey();

    [GeneratedRegex(@"^[0-9a-f]{64}\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex OverrideFileName();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocaleTag();
}
