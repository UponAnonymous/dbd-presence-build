using System.Text.Json;

namespace DbdPresence.Packs.Index;

/// <summary>Outcome of parsing a pack index payload: either an index or the validation errors.</summary>
public sealed class PackIndexParseResult
{
    private PackIndexParseResult(PackIndex? index, IReadOnlyList<string> errors)
    {
        Index = index;
        Errors = errors;
    }

    /// <summary>The validated index, or null when <see cref="Errors"/> is not empty.</summary>
    public PackIndex? Index { get; }

    /// <summary>Human-readable validation errors (capped).</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>True when <see cref="Index"/> is set.</summary>
    public bool IsValid => Index is not null;

    internal static PackIndexParseResult Ok(PackIndex index) => new(index, []);

    internal static PackIndexParseResult Fail(IReadOnlyList<string> errors) => new(null, errors);
}

/// <summary>
/// Strict parser and validator for the <c>pack-index</c> payload (FR-PACK-01):
/// <c>{ "packs": [{ id, name, version, author, licenceNote, credit?, fallback?, behaviourArt?, default? }],
/// "entries": [{ id, pack, kind, key, sha256, sourceSha256?, size, width, height, format, licensed }] }</c>.
/// <c>licensed</c> must be present: <c>null</c> or <c>{ "holder": "..." }</c>. Unknown members are ignored
/// (NFR-SVC-06). Entry ids are unique across the index; keys are unique within a pack. Only the first pack flagged
/// <c>default</c> keeps the flag (S5b-3: one default per channel).
/// </summary>
public static class PackIndexParser
{
    /// <summary>Largest accepted per-file size in bytes.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>Largest accepted width or height in pixels.</summary>
    public const int MaxDimension = 1024;

    /// <summary>Largest accepted sum of entry sizes within one pack, in bytes (ADR-0013; 32 MB since PLAN_V2_S5B §2.5).</summary>
    public const long MaxPackBytes = 32L * 1024 * 1024;

    /// <summary>Most packs in one index.</summary>
    public const int MaxPacks = 100;

    /// <summary>Most entries in one index.</summary>
    public const int MaxEntries = 4000;

    private const int MaxTextLength = 200;
    private const int MaxErrors = 20;
    private const int MaxDepth = 16;

    private static readonly JsonDocumentOptions s_options = new() { MaxDepth = MaxDepth, AllowDuplicateProperties = false };

    /// <summary>Parses and validates the payload JSON text taken from a verified envelope.</summary>
    public static PackIndexParseResult Parse(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        var errors = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(payloadJson, s_options);
            var index = ParseRoot(json.RootElement, errors);
            return errors.Count == 0 && index is not null ? PackIndexParseResult.Ok(index) : PackIndexParseResult.Fail(errors);
        }
        catch (JsonException ex)
        {
            errors.Add("Not valid JSON: " + ex.Message);
            return PackIndexParseResult.Fail(errors);
        }
    }

    /// <summary>Validates an index built in code (for example an index assembled from imported packs) with the same rules.</summary>
    public static IReadOnlyList<string> Validate(PackIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var errors = new List<string>();
        var packIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in index.Packs)
        {
            ValidatePack(pack, packIds, errors);
        }

        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<(string Pack, string Key)>();
        foreach (var entry in index.Entries)
        {
            ValidateEntry(entry, packIds, entryIds, keys, errors);
        }

        foreach (var group in index.Entries.GroupBy(e => e.PackId, StringComparer.Ordinal))
        {
            if (group.Sum(e => e.Size) > MaxPackBytes)
            {
                Add(errors, $"Pack '{group.Key}' is larger than {MaxPackBytes / (1024 * 1024)} MB in total.");
            }
        }

        if (index.Packs.Count > MaxPacks)
        {
            Add(errors, $"More than {MaxPacks} packs.");
        }

        if (index.Entries.Count > MaxEntries)
        {
            Add(errors, $"More than {MaxEntries} entries.");
        }

        return errors;
    }

    private static PackIndex? ParseRoot(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Add(errors, "The index must be a JSON object.");
            return null;
        }

        var packs = new List<PackInfo>();
        var entries = new List<PackEntry>();
        if (ReadArray(root, "packs", errors) is { } packArray)
        {
            foreach (var element in packArray.EnumerateArray())
            {
                if (ReadPack(element, errors) is { } pack)
                {
                    packs.Add(pack);
                }

                if (packs.Count > MaxPacks)
                {
                    break;
                }
            }
        }

        if (ReadArray(root, "entries", errors) is { } entryArray)
        {
            foreach (var element in entryArray.EnumerateArray())
            {
                if (ReadEntry(element, errors) is { } entry)
                {
                    entries.Add(entry);
                }

                if (entries.Count > MaxEntries)
                {
                    break;
                }
            }
        }

        if (errors.Count > 0)
        {
            return null;
        }

        // One default pack per channel: the first one flagged keeps the flag, any other is read as an ordinary pack.
        var firstDefault = packs.FindIndex(p => p.Default);
        for (var i = firstDefault + 1; firstDefault >= 0 && i < packs.Count; i++)
        {
            if (packs[i].Default)
            {
                packs[i] = packs[i] with { Default = false };
            }
        }

        var index = new PackIndex(packs, entries);
        errors.AddRange(Validate(index));
        return errors.Count == 0 ? index : null;
    }

    private static JsonElement? ReadArray(JsonElement parent, string name, List<string> errors)
    {
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array)
        {
            return element;
        }

        Add(errors, $"'{name}' must be an array.");
        return null;
    }

    private static PackInfo? ReadPack(JsonElement element, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            Add(errors, "A pack must be an object.");
            return null;
        }

        var ok = true;
        ok &= Text(element, "id", required: true, out var id, errors, "pack");
        ok &= Text(element, "name", required: true, out var name, errors, "pack");
        ok &= Text(element, "author", required: true, out var author, errors, "pack");
        ok &= Text(element, "licenceNote", required: true, out var licence, errors, "pack");
        ok &= OptionalText(element, "credit", out var credit, errors, "pack");
        ok &= Integer(element, "version", out var version, errors, "pack");
        ok &= OptionalBool(element, "fallback", out var fallback, errors, "pack");
        ok &= OptionalBool(element, "behaviourArt", out var behaviourArt, errors, "pack");
        ok &= OptionalBool(element, "default", out var isDefault, errors, "pack");
        if (!ok)
        {
            return null;
        }

        return new PackInfo(id, name, ClampToInt(version), author, licence, credit, fallback, behaviourArt, isDefault);
    }

    private static PackEntry? ReadEntry(JsonElement element, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            Add(errors, "An entry must be an object.");
            return null;
        }

        var ok = true;
        ok &= Text(element, "id", required: true, out var id, errors, "entry");
        ok &= Text(element, "pack", required: true, out var packId, errors, "entry");
        ok &= Text(element, "kind", required: true, out var kindText, errors, "entry");
        ok &= Text(element, "key", required: true, out var key, errors, "entry");
        ok &= Text(element, "sha256", required: true, out var sha, errors, "entry");
        ok &= Text(element, "format", required: true, out var formatText, errors, "entry");
        ok &= Integer(element, "size", out var size, errors, "entry");
        ok &= Integer(element, "width", out var width, errors, "entry");
        ok &= Integer(element, "height", out var height, errors, "entry");
        ok &= ReadLicensed(element, out var licensed, errors, id);
        ok &= OptionalText(element, "sourceSha256", out var sourceSha, errors, "entry");
        if (!ok)
        {
            return null;
        }

        if (!PackKey.TryParseKind(kindText, out var kind))
        {
            Add(errors, $"Entry '{id}': unknown kind '{kindText}'.");
            return null;
        }

        PackImageFormat format;
        switch (formatText)
        {
            case "png":
                format = PackImageFormat.Png;
                break;
            case "jpeg":
                format = PackImageFormat.Jpeg;
                break;
            default:
                Add(errors, $"Entry '{id}': format must be 'png' or 'jpeg'.");
                return null;
        }

        return new PackEntry(id, packId, kind, key, sha, size, ClampToInt(width), ClampToInt(height), format, licensed, sourceSha);
    }

    /// <summary>Saturates so out-of-range values are reported by validation instead of wrapping.</summary>
    private static int ClampToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    private static bool ReadLicensed(JsonElement entry, out LicensedInfo? licensed, List<string> errors, string id)
    {
        licensed = null;
        if (!entry.TryGetProperty("licensed", out var element))
        {
            Add(errors, $"Entry '{id}': 'licensed' must be present (null or an object with 'holder').");
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("holder", out var holder)
            || holder.ValueKind != JsonValueKind.String)
        {
            Add(errors, $"Entry '{id}': 'licensed' must be null or an object with a text 'holder'.");
            return false;
        }

        licensed = new LicensedInfo(holder.GetString() ?? string.Empty);
        return true;
    }

    private static bool Text(JsonElement parent, string name, bool required, out string value, List<string> errors, string what)
    {
        value = string.Empty;
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        if (required)
        {
            Add(errors, $"A {what} is missing the text member '{name}'.");
        }

        return !required;
    }

    private static bool OptionalText(JsonElement parent, string name, out string? value, List<string> errors, string what)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            Add(errors, $"A {what} member '{name}' must be text or null.");
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool OptionalBool(JsonElement parent, string name, out bool value, List<string> errors, string what)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        Add(errors, $"A {what} member '{name}' must be true, false or null.");
        return false;
    }

    private static bool Integer(JsonElement parent, string name, out long value, List<string> errors, string what)
    {
        value = 0;
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
        {
            return true;
        }

        Add(errors, $"A {what} is missing the integer member '{name}'.");
        return false;
    }

    private static void ValidatePack(PackInfo pack, HashSet<string> packIds, List<string> errors)
    {
        if (!PackKey.IsSlug(pack.Id))
        {
            Add(errors, $"Pack id '{Shorten(pack.Id)}' must be 1 to {PackKey.MaxSlugLength} characters of [a-z0-9-].");
        }
        else if (!packIds.Add(pack.Id))
        {
            Add(errors, $"Duplicate pack id '{pack.Id}'.");
        }

        RequireText(pack.Name, $"Pack '{pack.Id}': name", errors);
        RequireText(pack.Author, $"Pack '{pack.Id}': author", errors);
        RequireText(pack.LicenceNote, $"Pack '{pack.Id}': licenceNote", errors);
        if (pack.Credit is not null)
        {
            RequireText(pack.Credit, $"Pack '{pack.Id}': credit", errors);
        }

        if (pack.Version < 1)
        {
            Add(errors, $"Pack '{pack.Id}': version must be at least 1.");
        }
    }

    private static void ValidateEntry(PackEntry entry, HashSet<string> packIds, HashSet<string> entryIds, HashSet<(string Pack, string Key)> keys, List<string> errors)
    {
        var label = $"Entry '{Shorten(entry.Id)}'";
        if (!PackKey.IsSlug(entry.Id))
        {
            Add(errors, $"{label}: id must be 1 to {PackKey.MaxSlugLength} characters of [a-z0-9-].");
        }
        else if (!entryIds.Add(entry.Id))
        {
            Add(errors, $"{label}: duplicate id.");
        }

        if (!packIds.Contains(entry.PackId))
        {
            Add(errors, $"{label}: unknown pack '{Shorten(entry.PackId)}'.");
        }

        if (!PackKey.TryParse(entry.Key, out var keyKind, out _))
        {
            Add(errors, $"{label}: key '{Shorten(entry.Key)}' must be kind:slug with slug characters [a-z0-9-].");
        }
        else
        {
            if (keyKind != entry.Kind)
            {
                Add(errors, $"{label}: key '{entry.Key}' does not match kind '{PackKey.KindTag(entry.Kind)}'.");
            }

            if (!keys.Add((entry.PackId, entry.Key)))
            {
                Add(errors, $"{label}: duplicate key '{entry.Key}' in pack '{entry.PackId}'.");
            }
        }

        if (!IsLowerHexSha256(entry.Sha256))
        {
            Add(errors, $"{label}: sha256 must be 64 lowercase hex characters.");
        }

        if (entry.SourceSha256 is { } source && !IsLowerHexSha256(source))
        {
            Add(errors, $"{label}: sourceSha256 must be null or 64 lowercase hex characters.");
        }

        if (entry.Size is < 1 or > MaxFileBytes)
        {
            Add(errors, $"{label}: size must be between 1 and {MaxFileBytes} bytes.");
        }

        if (entry.Width is < 1 or > MaxDimension || entry.Height is < 1 or > MaxDimension)
        {
            Add(errors, $"{label}: width and height must be between 1 and {MaxDimension}.");
        }

        if (entry.Licensed is { } licensed)
        {
            RequireText(licensed.Holder, $"{label}: licensed holder", errors);
        }
    }

    private static void RequireText(string value, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTextLength)
        {
            Add(errors, $"{label} must be 1 to {MaxTextLength} characters.");
        }
    }

    private static bool IsLowerHexSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Shorten(string value) => value.Length <= 40 ? value : value[..40] + "...";

    private static void Add(List<string> errors, string message)
    {
        if (errors.Count < MaxErrors)
        {
            errors.Add(message);
        }
    }
}
