using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Packs.Releases;

/// <summary>The result of applying a key replacement.</summary>
/// <param name="Effective">The key set to use.</param>
/// <param name="Skipped">The entries that were not applied, one plain line each (for the log).</param>
public sealed record KeyApplication(IReadOnlyDictionary<string, TrustedKey> Effective, IReadOnlyList<string> Skipped);

/// <summary>A key a replacement document adds to the trusted set.</summary>
/// <param name="Kid">Its key id; never one the app already trusts.</param>
/// <param name="Spki">The P-256 SubjectPublicKeyInfo bytes.</param>
/// <param name="Purposes">What it may sign; one of the allowed purpose sets, never <see cref="SignedPurposes.KeyReplacement"/>.</param>
public sealed record AddedKey(string Kid, byte[] Spki, IReadOnlyList<string> Purposes);

/// <summary>
/// The payload of a key-replacement document (purpose <c>key-replacement</c>, channel <see cref="Channel"/>; PLAN_V2 §6.2, ADR-0012): signed
/// offline with the recovery key, it is the only way to revoke a compromised service, content, config or release key and to trust its successor.
/// It is cumulative: the newest document lists every key revoked and every key added so far, so the app needs only the latest one.
/// A replacement can never add or revoke a recovery key; the recovery key changes only with a new app release.
/// </summary>
/// <param name="Revoke">The key ids no longer trusted.</param>
/// <param name="Add">The keys trusted in addition.</param>
public sealed record KeyReplacement(IReadOnlyList<string> Revoke, IReadOnlyList<AddedKey> Add)
{
    /// <summary>The only schema version this build reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The channel every key-replacement document is signed for.</summary>
    public const string Channel = "all";

    /// <summary>The most entries in each list.</summary>
    public const int MaxEntries = 16;

    /// <summary>The purpose sets one key may have (the same sets <c>build/check-online-keys.py</c> enforces for the compiled-in keys).</summary>
    public static readonly IReadOnlyList<IReadOnlySet<string>> AllowedPurposeSets =
    [
        new HashSet<string>(StringComparer.Ordinal) { SignedPurposes.Licence },
        new HashSet<string>(StringComparer.Ordinal) { SignedPurposes.Config },
        new HashSet<string>(StringComparer.Ordinal) { SignedPurposes.PackIndex, SignedPurposes.Roster },
        new HashSet<string>(StringComparer.Ordinal) { SignedPurposes.Releases },
    ];

    private static readonly JsonDocumentOptions s_options = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    /// <summary>Reads a payload; anything unexpected is refused with a short reason, never an exception.</summary>
    /// <param name="payloadJson">The payload member of a verified envelope.</param>
    /// <param name="replacement">The replacement.</param>
    /// <param name="error">Why it was refused, or null.</param>
    /// <returns>True when the payload is valid on its own (see <see cref="Apply"/> for the rules against the trusted set).</returns>
    public static bool TryParse(string payloadJson, out KeyReplacement? replacement, out string? error)
    {
        replacement = null;
        error = null;
        try
        {
            using var json = JsonDocument.Parse(payloadJson, s_options);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("schemaVersion" or "revoke" or "add")))
            {
                return Fail("unexpected_members", out error);
            }

            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var value) || value != SchemaVersion)
            {
                return Fail("unsupported_schema", out error);
            }

            if (!root.TryGetProperty("revoke", out var revokeList) || revokeList.ValueKind != JsonValueKind.Array || revokeList.GetArrayLength() > MaxEntries
                || !root.TryGetProperty("add", out var addList) || addList.ValueKind != JsonValueKind.Array || addList.GetArrayLength() > MaxEntries)
            {
                return Fail("bad_lists", out error);
            }

            var revoke = new List<string>();
            foreach (var item in revokeList.EnumerateArray())
            {
                var kid = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!IsKid(kid) || revoke.Contains(kid!, StringComparer.Ordinal))
                {
                    return Fail("bad_revoke", out error);
                }

                revoke.Add(kid!);
            }

            var add = new List<AddedKey>();
            foreach (var item in addList.EnumerateArray())
            {
                if (!TryAddedKey(item, out var key) || add.Any(k => k.Kid == key!.Kid) || revoke.Contains(key!.Kid, StringComparer.Ordinal))
                {
                    return Fail("bad_add", out error);
                }

                add.Add(key);
            }

            if (revoke.Count == 0 && add.Count == 0)
            {
                return Fail("empty", out error);
            }

            replacement = new KeyReplacement(revoke, add);
            return true;
        }
        catch (JsonException)
        {
            // Not JSON, too deep or with duplicate members: refused like any other malformed payload.
            return Fail("malformed", out error);
        }
        catch (InvalidOperationException)
        {
            // A string member holds invalid UTF-8: refused like any other malformed payload.
            return Fail("malformed", out error);
        }
    }

    /// <summary>
    /// Applies the replacement to the compiled-in key set (security review 2026-10-25, M2). Each entry stands on its own, so one entry that
    /// cannot apply never drops the others, above all never the revocations: every revoked key is removed, except a recovery key (a
    /// replacement can never revoke one); every added key is trusted, except one whose key id the build already trusts with a different key
    /// or purposes, or that is not a P-256 key. Re-adding a key the build already trusts with the same key and purposes changes nothing.
    /// </summary>
    /// <param name="builtIn">The compiled-in keys of this build.</param>
    /// <returns>The key set to use and the entries that were not applied.</returns>
    public KeyApplication Apply(IReadOnlyDictionary<string, TrustedKey> builtIn)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        var skipped = new List<string>();
        var next = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var (kid, key) in builtIn)
        {
            var isRecovery = key.Purposes.Contains(SignedPurposes.KeyReplacement);
            if (Revoke.Contains(kid, StringComparer.Ordinal) && isRecovery)
            {
                skipped.Add($"revoke {kid}: a recovery key cannot be revoked");
            }

            if (!Revoke.Contains(kid, StringComparer.Ordinal) || isRecovery)
            {
                next[kid] = key;
            }
        }

        foreach (var key in Add)
        {
            if (builtIn.TryGetValue(key.Kid, out var existing))
            {
                if (!(existing.Spki.AsSpan().SequenceEqual(key.Spki) && existing.Purposes.ToHashSet(StringComparer.Ordinal).SetEquals(key.Purposes)))
                {
                    skipped.Add($"add {key.Kid}: the build already trusts this key id with another key");
                }

                continue;
            }

            try
            {
                // Validates the added key on its own as a P-256 public key with purposes.
                _ = new TrustedKeySet(new Dictionary<string, TrustedKey> { [key.Kid] = new(key.Spki, key.Purposes) });
                next[key.Kid] = new TrustedKey(key.Spki, key.Purposes);
            }
            catch (ArgumentException)
            {
                skipped.Add($"add {key.Kid}: not a P-256 public key");
            }
        }

        return new KeyApplication(next, skipped);
    }

    /// <summary>
    /// Whether this replacement keeps every revocation of <paramref name="earlier"/> (security review 2026-10-25, M1): replacements are
    /// cumulative, so a newer one that leaves out an earlier revocation would trust a revoked key again, and is refused.
    /// </summary>
    /// <param name="earlier">The replacement applied before.</param>
    /// <returns>True when <see cref="Revoke"/> contains every key id <paramref name="earlier"/> revoked.</returns>
    public bool KeepsRevocationsOf(KeyReplacement earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        return earlier.Revoke.All(kid => Revoke.Contains(kid, StringComparer.Ordinal));
    }

    /// <summary>Writes the payload JSON for signing, in a fixed member order. Used by the offline signing tool.</summary>
    /// <returns>Compact JSON.</returns>
    /// <exception cref="ArgumentException">The replacement breaks a rule that <see cref="TryParse"/> would refuse.</exception>
    public string ToPayloadJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("revoke");
            foreach (var kid in Revoke)
            {
                writer.WriteStringValue(kid);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("add");
            foreach (var key in Add)
            {
                writer.WriteStartObject();
                writer.WriteString("kid", key.Kid);
                writer.WriteString("spki", Base64Url.EncodeToString(key.Spki));
                writer.WriteStartArray("purposes");
                foreach (var purpose in key.Purposes)
                {
                    writer.WriteStringValue(purpose);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        if (!TryParse(text, out _, out var error))
        {
            throw new ArgumentException($"The key replacement is not valid: {error}.");
        }

        return text;
    }

    private static bool TryAddedKey(JsonElement item, out AddedKey? key)
    {
        key = null;
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("kid" or "spki" or "purposes")))
        {
            return false;
        }

        var kid = item.TryGetProperty("kid", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        var spkiText = item.TryGetProperty("spki", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (!IsKid(kid) || spkiText is null || !item.TryGetProperty("purposes", out var p) || p.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var purposes = new List<string>();
        foreach (var purpose in p.EnumerateArray())
        {
            if (purpose.ValueKind != JsonValueKind.String || purposes.Contains(purpose.GetString()!, StringComparer.Ordinal))
            {
                return false;
            }

            purposes.Add(purpose.GetString()!);
        }

        if (!AllowedPurposeSets.Any(set => set.SetEquals(purposes)) || !Base64Url.IsValid(spkiText, out var length) || length is 0 or > 512)
        {
            return false;
        }

        var spki = new byte[length];
        if (!Base64Url.TryDecodeFromChars(spkiText, spki, out var written) || written != length || Base64Url.EncodeToString(spki) != spkiText)
        {
            return false;
        }

        key = new AddedKey(kid!, spki, purposes);
        return true;
    }

    private static bool IsKid(string? kid) => kid is { Length: > 0 and <= 128 } && kid.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static bool Fail(string reason, out string? error)
    {
        error = reason;
        return false;
    }
}
