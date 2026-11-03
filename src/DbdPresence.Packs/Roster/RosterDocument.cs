using System.Text;
using System.Text.Json;
using DbdPresence.Core.Game;
using DbdPresence.Core.Roster;
using DbdPresence.Packs.Index;

namespace DbdPresence.Packs.Roster;

/// <summary>Another spelling of a character's name, with where it was seen on screen.</summary>
/// <param name="Name">The spelling.</param>
/// <param name="SeenOnScreen">Provenance (date and source).</param>
public sealed record RosterAlias(string Name, string SeenOnScreen);

/// <summary>One character of the signed roster (PLAN_V2_S5B §2.3).</summary>
/// <param name="Code">The game's character code (<c>K45</c>). Never changes.</param>
/// <param name="Id">The pack slug (<c>the-lantern</c>). Never changes.</param>
/// <param name="Role">Killer for a <c>K</c> code, survivor for an <c>S</c> code.</param>
/// <param name="DisplayName">The name the Characters page shows.</param>
/// <param name="ScreenName">The name as the game prints it; detection matches this one.</param>
/// <param name="Aliases">Other on-screen spellings.</param>
/// <param name="LicensedHolder">The rights holder of a licensed character, or null.</param>
/// <param name="Withdrawn">True when the owner removed the character: it stays known (its code and id never return to anyone else) but is not shown or detected.</param>
public sealed record RosterEntry(string Code, string Id, Role Role, string DisplayName, string ScreenName, IReadOnlyList<RosterAlias> Aliases, string? LicensedHolder, bool Withdrawn);

/// <summary>The verified and parsed roster payload.</summary>
/// <param name="Characters">Every character the service knows, by code.</param>
public sealed record RosterDocument(IReadOnlyList<RosterEntry> Characters)
{
    /// <summary>The characters that are not in <paramref name="embedded"/> and not withdrawn: what the vocabulary gains from this roster.</summary>
    /// <param name="embedded">The roster built from the app's embedded vocabulary.</param>
    /// <returns>The new characters, by code.</returns>
    public IReadOnlyList<RosterEntry> NewCharacters(CharacterRoster embedded)
    {
        ArgumentNullException.ThrowIfNull(embedded);
        return [.. Characters.Where(c => !c.Withdrawn && embedded.FindByCode(c.Code) is null)];
    }

    /// <summary>
    /// The import roster with this document's new characters added after the embedded ones (FR-ROS-04): their display name, and their
    /// screen name and aliases as other names. Embedded characters keep their embedded names.
    /// </summary>
    /// <param name="embedded">The roster built from the app's embedded vocabulary.</param>
    /// <returns>The merged roster.</returns>
    /// <exception cref="FormatException">A new character clashes with an embedded one (a code or an id used twice).</exception>
    public CharacterRoster MergeInto(CharacterRoster embedded)
    {
        ArgumentNullException.ThrowIfNull(embedded);
        var added = NewCharacters(embedded).Select(c => new RosterCharacter(
            c.Code,
            c.Id,
            c.Role,
            c.DisplayName,
            [.. new[] { c.ScreenName }.Concat(c.Aliases.Select(a => a.Name)).Where(n => !string.Equals(n, c.DisplayName, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)]));
        return CharacterRoster.Create(embedded.Characters.Concat(added));
    }
}

/// <summary>Outcome of parsing a roster payload: the document or the reasons it was refused.</summary>
public sealed class RosterParseResult
{
    private RosterParseResult(RosterDocument? roster, IReadOnlyList<string> errors)
    {
        Roster = roster;
        Errors = errors;
    }

    /// <summary>Gets the document, or null when <see cref="Errors"/> is not empty.</summary>
    public RosterDocument? Roster { get; }

    /// <summary>Gets the problems found (capped).</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Gets a value indicating whether the document is usable.</summary>
    public bool IsValid => Roster is not null;

    internal static RosterParseResult Ok(RosterDocument roster) => new(roster, []);

    internal static RosterParseResult Fail(IReadOnlyList<string> errors) => new(null, errors);
}

/// <summary>
/// Strict parser for the <c>roster</c> payload (PLAN_V2_S5B §2.3, FR-ROS-04), the same rules as the service
/// (<c>service/src/roster/model.ts</c>): <c>{ "schemaVersion": 1, "characters": [{ code, id, role, displayName, screenName, aliases:
/// [{ name, seenOnScreen }], licensed, withdrawn }] }</c>. Unknown members are ignored. A failed rule refuses the whole document.
/// The append-only rules against what the app already knows are in <see cref="CheckAppendOnly"/>.
/// </summary>
public static class RosterParser
{
    /// <summary>Most characters in one roster.</summary>
    public const int MaxCharacters = 400;

    /// <summary>Most aliases per character.</summary>
    public const int MaxAliases = 8;

    /// <summary>Longest name.</summary>
    public const int MaxNameLength = 80;

    /// <summary>Longest alias provenance.</summary>
    public const int MaxSeenOnScreenLength = 200;

    /// <summary>Longest rights holder.</summary>
    public const int MaxHolderLength = 120;

    /// <summary>Largest payload, in UTF-8 bytes.</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    private const int MaxErrors = 20;

    private static readonly JsonDocumentOptions s_options = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    /// <summary>Parses and validates the payload JSON text of a verified <c>roster</c> envelope.</summary>
    /// <param name="payloadJson">The payload.</param>
    /// <returns>The document or the errors.</returns>
    public static RosterParseResult Parse(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        var errors = new List<string>();
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
        {
            return RosterParseResult.Fail(["The roster is larger than 256 KiB."]);
        }

        try
        {
            using var json = JsonDocument.Parse(payloadJson, s_options);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
            {
                return RosterParseResult.Fail(["The roster must be an object with schemaVersion 1."]);
            }

            if (!root.TryGetProperty("characters", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return RosterParseResult.Fail(["'characters' must be an array."]);
            }

            if (list.GetArrayLength() > MaxCharacters)
            {
                return RosterParseResult.Fail([$"More than {MaxCharacters} characters."]);
            }

            var characters = new List<RosterEntry>();
            foreach (var item in list.EnumerateArray())
            {
                if (Read(item, errors) is { } entry)
                {
                    characters.Add(entry);
                }
            }

            Validate(characters, errors);
            return errors.Count == 0 ? RosterParseResult.Ok(new RosterDocument(characters)) : RosterParseResult.Fail(errors);
        }
        catch (JsonException ex)
        {
            return RosterParseResult.Fail(["Not valid JSON: " + ex.Message]);
        }
    }

    /// <summary>
    /// The append-only rules (PLAN_V2_S5B §2.3): every code the app knows, embedded or from the previous roster, is present with the same
    /// id and role, and only characters the app does not embed may be withdrawn. A key compromise can therefore never remove or rename
    /// the base vocabulary, and no id ever moves to another character.
    /// </summary>
    /// <param name="next">The new document.</param>
    /// <param name="embedded">The roster built from the embedded vocabulary.</param>
    /// <param name="previous">The last good document, or null.</param>
    /// <returns>The broken rules; empty when the document may be used.</returns>
    public static IReadOnlyList<string> CheckAppendOnly(RosterDocument next, CharacterRoster embedded, RosterDocument? previous)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(embedded);
        var errors = new List<string>();
        var byCode = next.Characters.ToDictionary(c => c.Code, StringComparer.Ordinal);
        foreach (var known in embedded.Characters)
        {
            if (!byCode.TryGetValue(known.Code, out var entry) || entry.Id != known.Id || entry.Role != known.Role)
            {
                Add(errors, $"The roster drops or changes the embedded character {known.Code}.");
            }
            else if (entry.Withdrawn)
            {
                Add(errors, $"The roster withdraws the embedded character {known.Code}, which only a release can change.");
            }
        }

        foreach (var known in previous?.Characters ?? [])
        {
            if (!byCode.TryGetValue(known.Code, out var entry) || entry.Id != known.Id || entry.Role != known.Role)
            {
                Add(errors, $"The roster drops or changes the character {known.Code} it listed before.");
            }
        }

        return errors;
    }

    private static RosterEntry? Read(JsonElement item, List<string> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            Add(errors, "A character must be an object.");
            return null;
        }

        var code = Text(item, "code");
        var id = Text(item, "id");
        var role = Text(item, "role");
        var display = Text(item, "displayName");
        var screen = Text(item, "screenName");
        if (code is null || id is null || role is null || display is null || screen is null)
        {
            Add(errors, "A character is missing code, id, role, displayName or screenName.");
            return null;
        }

        if (!item.TryGetProperty("withdrawn", out var withdrawn) || withdrawn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            Add(errors, $"Character {Short(code)}: 'withdrawn' must be true or false.");
            return null;
        }

        string? holder = null;
        if (!item.TryGetProperty("licensed", out var licensed) || licensed.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
        {
            Add(errors, $"Character {Short(code)}: 'licensed' must be null or an object with 'holder'.");
            return null;
        }

        if (licensed.ValueKind == JsonValueKind.Object)
        {
            holder = Text(licensed, "holder");
            if (holder is null || !IsNote(holder, MaxHolderLength))
            {
                Add(errors, $"Character {Short(code)}: the licensed holder must be 1 to {MaxHolderLength} characters of text.");
                return null;
            }
        }

        if (!item.TryGetProperty("aliases", out var aliasArray) || aliasArray.ValueKind != JsonValueKind.Array || aliasArray.GetArrayLength() > MaxAliases)
        {
            Add(errors, $"Character {Short(code)}: 'aliases' must be an array of at most {MaxAliases}.");
            return null;
        }

        var aliases = new List<RosterAlias>();
        foreach (var alias in aliasArray.EnumerateArray())
        {
            var name = alias.ValueKind == JsonValueKind.Object ? Text(alias, "name") : null;
            var seen = alias.ValueKind == JsonValueKind.Object ? Text(alias, "seenOnScreen") : null;
            if (name is null || seen is null || !IsValidName(name) || !IsNote(seen, MaxSeenOnScreenLength))
            {
                Add(errors, $"Character {Short(code)}: every alias needs a valid name and its 'seenOnScreen' provenance.");
                return null;
            }

            aliases.Add(new RosterAlias(name, seen));
        }

        Role parsedRole = role switch { "killer" => Role.Killer, "survivor" => Role.Survivor, _ => Role.None };
        return new RosterEntry(code, id, parsedRole, display, screen, aliases, holder, withdrawn.GetBoolean());
    }

    private static void Validate(List<RosterEntry> characters, List<string> errors)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in characters)
        {
            if (!CharacterRoster.IsCode(c.Code) || c.Role != (c.Code[0] == 'K' ? Role.Killer : Role.Survivor) || !codes.Add(c.Code))
            {
                Add(errors, $"Character {Short(c.Code)}: the code is malformed, used twice or does not fit the role.");
            }

            if (!PackKey.IsSlug(c.Id) || c.Id == PackKey.GenericSlug || !ids.Add(c.Id))
            {
                Add(errors, $"Character {Short(c.Code)}: the id must be a unique pack slug other than '{PackKey.GenericSlug}'.");
            }

            if (!IsValidName(c.DisplayName) || !IsValidName(c.ScreenName))
            {
                Add(errors, $"Character {Short(c.Code)}: a name is empty, too long, not NFC, or has characters other than letters, digits, spaces and . ' \" & : – -.");
            }
        }
    }

    /// <summary>
    /// A display or screen name the app accepts (the service's rule): NFC, 1 to 80 characters, trimmed, single spaces, only Latin letters,
    /// digits, spaces and <c>. ' " &amp; : – -</c>, and a non-empty fold.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > MaxNameLength || !name.IsNormalized(NormalizationForm.FormC) || name != name.Trim() || name.Contains("  ", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            var allowed = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ' ' or '.' or '\'' or '"' or '&' or ':' or '–' or '-'
                or (>= 'À' and <= 'Ö') or (>= 'Ø' and <= 'ö') or (>= 'ø' and <= 'ɏ') or (>= 'Ḁ' and <= 'ỿ');
            if (!allowed)
            {
                return false;
            }
        }

        return RosterNameFold.Fold(name) is { Length: > 0 };
    }

    private static bool IsNote(string text, int max) =>
        text.Length >= 1 && text.Length <= max && text == text.Trim() && !text.Any(c => char.IsControl(c) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Short(string value) => value.Length <= 12 ? value : value[..12] + "...";

    private static void Add(List<string> errors, string message)
    {
        if (errors.Count < MaxErrors)
        {
            errors.Add(message);
        }
    }
}

/// <summary>
/// The composition root's side of the roster merge (FR-ROS-04, PLAN_V2_S5B §2.4): it builds the detection vocabulary with
/// <c>Vocabulary.WithCharacters</c> (Vision) and the import roster with <see cref="RosterDocument.MergeInto"/> from the embedded ones and
/// the received roster. It either applies the whole roster or throws <see cref="FormatException"/> (a name collision) and changes
/// nothing; the sync then keeps the last good roster.
/// </summary>
public interface IRosterConsumer
{
    /// <summary>Applies a verified roster that passed the append-only rules.</summary>
    /// <param name="roster">The roster.</param>
    /// <exception cref="FormatException">The roster cannot be merged; nothing was changed.</exception>
    void Apply(RosterDocument roster);
}

/// <summary>How the latest roster check ended (the Characters page shows "could not refresh" from it).</summary>
public enum RosterSyncOutcome
{
    /// <summary>A new roster was verified, merged and cached.</summary>
    Updated,

    /// <summary>The service's roster is the one in use.</summary>
    UpToDate,

    /// <summary>Online features are not available; nothing was contacted.</summary>
    NotAllowed,

    /// <summary>The service could not be reached or answered with an error.</summary>
    Offline,

    /// <summary>The service asked the app to wait.</summary>
    RateLimited,

    /// <summary>The document failed verification, the parser, the append-only rules or the merge; the last good roster stays.</summary>
    Refused,

    /// <summary>The roster could not be saved on this PC; the last good roster stays.</summary>
    StorageFailed,
}

/// <summary>What the Characters page reads about the roster.</summary>
/// <param name="LastSuccessAt">When the roster was last checked successfully, or null.</param>
/// <param name="LastOutcome">The latest attempt, or null before the first.</param>
/// <param name="Expired">True when the roster in use is past its 30-day lifetime; it keeps being used, with the note.</param>
/// <param name="Note">A plain-language sentence about the latest attempt, or null.</param>
public sealed record RosterSyncStatus(DateTimeOffset? LastSuccessAt, RosterSyncOutcome? LastOutcome, bool Expired, string? Note)
{
    /// <summary>Gets the status before anything happened.</summary>
    public static RosterSyncStatus None { get; } = new(null, null, false, null);

    /// <summary>Gets a value indicating whether the Characters page should say the list could not be refreshed.</summary>
    public bool CouldNotRefresh => Expired || LastOutcome is RosterSyncOutcome.Offline or RosterSyncOutcome.Refused or RosterSyncOutcome.StorageFailed or RosterSyncOutcome.RateLimited;
}

/// <summary>The roster as the composition root uses it (implemented by <c>DbdPresence.Online</c>; Presentation never references that assembly).</summary>
public interface IRosterSource
{
    /// <summary>Raised (on any thread) after <see cref="Current"/> or <see cref="Status"/> changed.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the last good roster, or null when none was ever received.</summary>
    RosterDocument? Current { get; }

    /// <summary>Gets the status.</summary>
    RosterSyncStatus Status { get; }

    /// <summary>
    /// Sets the consumer that merges every roster (the composition root, once its vocabulary exists). The current roster, if any, is applied
    /// at once; one the consumer refuses is logged and the embedded vocabulary stays.
    /// </summary>
    /// <param name="consumer">The consumer.</param>
    void Attach(IRosterConsumer consumer);
}
