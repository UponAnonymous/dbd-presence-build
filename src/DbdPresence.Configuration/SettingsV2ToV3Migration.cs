using System.Text.Json.Nodes;
using DbdPresence.Core.Pictures;

namespace DbdPresence.Configuration;

/// <summary>
/// Schema v2 to v3 (PLAN_V2_S5B §2.6, FR-PACK-16): every per-image override of a character becomes one own picture that is in use, and the new
/// fields get their defaults. A pure transform of the document, so it can be repeated without harm: a character that already has pictures is left as it is.
/// <list type="bullet">
/// <item>Overrides of the generic keys (role icons, generic tiles, map) are not migrated, because only owner packs fill them now (O-3); they are
/// named in <c>pictureNotice</c> so the app tells the user once. Their files stay on disk.</item>
/// <item><c>packs.overrides</c> itself is kept as it is: this build still reads it, and nothing is lost. The imported packs are migrated by the
/// pack library, which can copy their files.</item>
/// <item>The old file's size is not known here, so <c>bytes</c> is 0 ("not read yet"). The date is the Unix epoch, which stands for "unknown".</item>
/// <item>Nothing is queued for saving online: the picture rules start as <c>Unset</c> and the user accepts them first.</item>
/// </list>
/// Whole-app design §11 and D30 add to the same step: three new visibility switches (<c>showMode</c>, <c>showScene</c>, <c>showResult</c>, all on) are written
/// explicitly because the JSON reader does not apply a property's initial value when a document lacks it (it would read false), and the declared role,
/// character and mode (<c>manual</c>) are removed: nothing reads them any more.
/// </summary>
public sealed class SettingsV2ToV3Migration : ISettingsMigration
{
    /// <inheritdoc />
    public int FromVersion => 2;

    /// <summary>Brings one profile object (from a settings file or an exported profile) up to the shape of schema v3.</summary>
    /// <param name="profile">The profile JSON; changed in place.</param>
    public static void MigrateProfile(JsonObject profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Remove("manual");
        if (profile["visibility"] is JsonObject visibility)
        {
            SetWhenMissing(visibility, "showMode", true);
            SetWhenMissing(visibility, "showScene", true);
            SetWhenMissing(visibility, "showResult", true);
        }
    }

    /// <summary>
    /// The part of the step that fills what an older build did not write: the visibility switches of every profile and <c>screenReadingTipDismissed</c>.
    /// It only adds what is missing and never changes a value, so it is safe to run on any document of schema v3 as well: a v3 file written before the
    /// redesign was merged has no such fields, the step itself will not run for it again, and a missing switch would otherwise read as off (review F14).
    /// </summary>
    /// <param name="document">The settings document; changed in place.</param>
    public static void RepairDocument(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document["profiles"] is JsonArray profileList)
        {
            foreach (var profile in profileList.OfType<JsonObject>())
            {
                MigrateProfile(profile);
            }
        }

        if (document["lifecycle"] is JsonObject lifecycle)
        {
            SetWhenMissing(lifecycle, "screenReadingTipDismissed", false);
        }
    }

    /// <inheritdoc />
    public void Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RepairDocument(document);

        var pictures = document["pictures"] as JsonObject ?? [];
        var generic = new JsonArray();
        if (document["packs"] is JsonObject { } packs && packs["overrides"] is JsonObject overrides)
        {
            foreach (var (key, node) in overrides)
            {
                if (node is not JsonValue value || !value.TryGetValue(out string? file) || file is null || file.Length != 68
                    || !file.EndsWith(".png", StringComparison.Ordinal) || !PictureKeys.IsSha256(file[..64]))
                {
                    continue;
                }

                if (!PictureKeys.IsCharacterKey(key))
                {
                    if (key.Contains(':', StringComparison.Ordinal))
                    {
                        generic.Add(key);
                    }

                    continue;
                }

                if (pictures.ContainsKey(key))
                {
                    continue;
                }

                var sha = file[..64];
                pictures[key] = new JsonObject
                {
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["sha256"] = sha,
                        ["sourceSha256"] = null,
                        ["bytes"] = 0,
                        ["file"] = file,
                        ["link"] = null,
                        ["origin"] = nameof(PictureOrigin.Pc),
                        ["addedAt"] = DateTimeOffset.UnixEpoch.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    }),
                    ["use"] = new JsonObject
                    {
                        ["kind"] = nameof(PictureUseKind.Personal),
                        ["sha256"] = sha,
                        ["pack"] = null,
                        ["entry"] = null,
                    },
                };
            }
        }

        document["pictures"] = pictures;
        document["pictureSize"] = document["pictureSize"] is JsonValue { } size && size.TryGetValue(out int edge) && PictureLimits.IsSize(edge)
            ? edge
            : PictureLimits.DefaultSize;
        document["pictureRules"] ??= nameof(PictureRules.Unset);
        if (generic.Count > 0 && document["pictureNotice"] is null)
        {
            document["pictureNotice"] = new JsonObject { ["genericOverrides"] = generic, ["overLimit"] = 0 };
        }
    }

    private static void SetWhenMissing(JsonObject target, string name, bool value)
    {
        if (!target.ContainsKey(name))
        {
            target[name] = value;
        }
    }
}
