using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace DbdPresence.Packs.Signing;

/// <summary>
/// Verifies the signed-document envelope of ADR-0012 / NFR-SVC-04. The document is
/// <c>{ "kid", "signed": base64url(envelope bytes), "sig": base64url(P1363 r||s ECDSA P-256 over those bytes) }</c>.
/// Order: size cap, outer shape, key lookup, key-purpose scope, signature over the exact bytes, and only then the envelope is parsed;
/// then kid, purpose, expiry and rollback. No polymorphic deserialisation: documents are read with <see cref="JsonDocument"/>.
/// </summary>
public sealed class SignedDocumentVerifier
{
    /// <summary>Largest accepted input document in bytes.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>
    /// Highest accepted envelope version: 2^53 - 1, the largest integer the TypeScript service holds exactly (security review 2026-10-25, L1).
    /// A higher version is <see cref="VerificationOutcome.Malformed"/>, so a signed document cannot push a version floor out of reach.
    /// </summary>
    public const long MaxVersion = 9_007_199_254_740_991;

    /// <summary>
    /// How far above the signing time (in seconds since the epoch) an envelope version may be (TM-01). The service makes a version
    /// <c>max(previous + 1, seconds now)</c> (<c>nextVersion</c>), so a genuine version sits at or just above the signing time in seconds;
    /// it exceeds that only by one for each change made within the same second. A million is far above any real burst of changes and far below
    /// 2^53 - 1, so a stolen key cannot park a floor out of reach. The bound is tied to the signing time and not to the stored floor, because
    /// the gap between two genuine versions is the time between the changes and can be years of seconds. Releases and key replacements use
    /// small counters, which are always below it.
    /// </summary>
    public const long MaxVersionAheadOfIssue = 1_000_000;

    private const int MaxOuterDepth = 4;
    private const int MaxEnvelopeDepth = 24;
    private const int MaxIdentifierLength = 128;
    private const int SignatureLength = 64;
    private static readonly TimeSpan s_maxClockSkew = TimeSpan.FromMinutes(5);

    private static readonly JsonDocumentOptions s_outerOptions = new() { MaxDepth = MaxOuterDepth, AllowDuplicateProperties = false };
    private static readonly JsonDocumentOptions s_envelopeOptions = new() { MaxDepth = MaxEnvelopeDepth, AllowDuplicateProperties = false };

    private readonly IVersionStore _versions;
    private readonly Lock _revokedGate = new();
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private volatile TrustedKeySet _keys;
    private readonly TimeProvider _time;

    /// <summary>Creates a verifier.</summary>
    public SignedDocumentVerifier(TrustedKeySet keys, IVersionStore versions, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(time);
        _keys = keys;
        _versions = versions;
        _time = time;
    }

    /// <summary>Gets the trusted key set in use.</summary>
    public TrustedKeySet Keys => _keys;

    /// <summary>Gets every key id that a key replacement has removed from the trusted set since start (TM-01).</summary>
    public IReadOnlyCollection<string> RevokedKeyIds
    {
        get
        {
            lock (_revokedGate)
            {
                return [.. _revoked];
            }
        }
    }

    /// <summary>
    /// Replaces the trusted key set, for a key replacement signed by the recovery key (PLAN_V2 §6.2). Documents verified afterwards use the new
    /// set. Version floors committed under a key that the new set no longer trusts are dropped (TM-01), so a floor set by a stolen key does not
    /// outlive the key; floors of the keys that remain, and floors of unknown origin, are kept.
    /// </summary>
    /// <param name="keys">The new key set.</param>
    /// <exception cref="IOException">The dropped floors could not be saved. The new key set is in use and the floors are dropped in memory; the next start drops them again.</exception>
    public void UseKeys(TrustedKeySet keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var removed = _keys.Kids.Where(k => !keys.Contains(k)).ToList();
        _keys = keys;
        if (removed.Count == 0)
        {
            return;
        }

        lock (_revokedGate)
        {
            _revoked.UnionWith(removed);
        }

        _versions.Forget(removed);
    }

    /// <summary>
    /// Verifies <paramref name="document"/>. This does <b>not</b> record the version: the caller calls <see cref="CommitVersion(SignedEnvelope)"/>
    /// only after the payload parsed and the download committed, so a hostile or broken document cannot ratchet the stored version.
    /// </summary>
    /// <param name="document">The received bytes, exactly as served or stored.</param>
    /// <param name="expectedPurpose">The purpose the caller needs, for example <see cref="SignedPurposes.PackIndex"/>. The signing key must be trusted for it.</param>
    /// <param name="expectedChannel">The channel the caller asked for, for example <c>stable</c>. Binding it stops a validly signed document of
    /// another channel (with its own, lower version history) from being accepted in place of this channel's document.</param>
    public VerificationResult Verify(ReadOnlySpan<byte> document, string expectedPurpose, string expectedChannel) =>
        Evaluate(document, expectedPurpose, expectedChannel, enforceFreshness: true);

    /// <summary>
    /// Verifies a document that was accepted earlier and kept on disk (for example the last good configuration): signature, key
    /// purpose, purpose and channel are checked, but not expiry, lifetime or the version floor, so the caller can still read an
    /// expired copy and apply its own fallback rules (ADR-0012). Records nothing.
    /// </summary>
    /// <param name="document">The stored bytes.</param>
    /// <param name="expectedPurpose">The purpose the caller needs.</param>
    /// <param name="expectedChannel">The channel the caller needs.</param>
    public VerificationResult VerifyStored(ReadOnlySpan<byte> document, string expectedPurpose, string expectedChannel) =>
        Evaluate(document, expectedPurpose, expectedChannel, enforceFreshness: false);

    /// <summary>Records <paramref name="version"/> as the highest accepted for the purpose and channel (rollback protection), under the signing key <paramref name="kid"/>.</summary>
    public void CommitVersion(string purpose, string channel, long version, string? kid)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentException.ThrowIfNullOrEmpty(channel);
        _versions.RecordHighest(purpose, channel, version, kid);
    }

    /// <summary>Records the version of a verified <paramref name="envelope"/> under its signing key.</summary>
    public void CommitVersion(SignedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        CommitVersion(envelope.Purpose, envelope.Channel, envelope.Version, envelope.Kid);
    }

    /// <summary>
    /// Startup seeding: raises the stored version floor from a signed document that was already cached. The signature, key purpose,
    /// purpose and channel are checked; expiry, lifetime and the current floor are not, because an old cached index still proves that
    /// its version was once accepted. Nothing is recorded unless the outcome is <see cref="VerificationOutcome.Valid"/>.
    /// </summary>
    public VerificationOutcome SeedFromCachedDocument(ReadOnlySpan<byte> document, string expectedPurpose, string expectedChannel)
    {
        var result = Evaluate(document, expectedPurpose, expectedChannel, enforceFreshness: false);
        if (result.Envelope is { } envelope)
        {
            CommitVersion(envelope);
        }

        return result.Outcome;
    }

    private VerificationResult Evaluate(ReadOnlySpan<byte> document, string expectedPurpose, string expectedChannel, bool enforceFreshness)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedPurpose);
        ArgumentException.ThrowIfNullOrEmpty(expectedChannel);
        if (SignedPurposes.MaxLifetime(expectedPurpose) is not { } maxLifetime)
        {
            throw new ArgumentException($"Unknown signed-document purpose '{expectedPurpose}'.", nameof(expectedPurpose));
        }

        if (document.Length is 0 or > MaxDocumentBytes)
        {
            return Fail(VerificationOutcome.Malformed);
        }

        if (!TryReadOuter(document, out var kid, out var envelopeBytes, out var signature))
        {
            return Fail(VerificationOutcome.Malformed);
        }

        var keys = _keys;
        if (!keys.Contains(kid))
        {
            return Fail(VerificationOutcome.UnknownKey);
        }

        if (!keys.IsAllowedFor(kid, expectedPurpose))
        {
            return Fail(VerificationOutcome.KeyNotAllowedForPurpose);
        }

        if (signature.Length != SignatureLength || !keys.VerifySignature(kid, envelopeBytes, signature))
        {
            return Fail(VerificationOutcome.BadSignature);
        }

        // The signature is good: only now are the envelope bytes parsed.
        if (!TryReadEnvelope(envelopeBytes, out var envelope) || envelope.Kid != kid)
        {
            return Fail(VerificationOutcome.Malformed);
        }

        if (!string.Equals(envelope.Purpose, expectedPurpose, StringComparison.Ordinal))
        {
            return Fail(VerificationOutcome.WrongPurpose);
        }

        if (!string.Equals(envelope.Channel, expectedChannel, StringComparison.Ordinal))
        {
            return Fail(VerificationOutcome.WrongChannel);
        }

        if (CheckVersionRelation(envelope) is { } problem)
        {
            return Fail(problem);
        }

        if (enforceFreshness)
        {
            var now = _time.GetUtcNow();
            if (envelope.ExpiresAt - envelope.IssuedAt > maxLifetime)
            {
                return Fail(VerificationOutcome.LifetimeTooLong);
            }

            if (envelope.IssuedAt - now > s_maxClockSkew)
            {
                return Fail(VerificationOutcome.Malformed);
            }

            if (now >= envelope.ExpiresAt)
            {
                return Fail(VerificationOutcome.Expired);
            }

            var highest = _versions.GetHighest(expectedPurpose, expectedChannel);
            if (highest is { } known && envelope.Version < known)
            {
                return Fail(VerificationOutcome.RolledBack);
            }
        }

        return new VerificationResult(VerificationOutcome.Valid, envelope);
    }

    private static VerificationOutcome? CheckVersionRelation(SignedEnvelope envelope)
    {
        var issuedSeconds = envelope.IssuedAt.ToUnixTimeSeconds();
        if (envelope.Purpose == SignedPurposes.Licence)
        {
            return envelope.Version == issuedSeconds ? null : VerificationOutcome.VersionNotDerivedFromIssueTime;
        }

        return envelope.Version - issuedSeconds > MaxVersionAheadOfIssue ? VerificationOutcome.VersionTooFarAhead : null;
    }

    private static VerificationResult Fail(VerificationOutcome outcome) => new(outcome, null);

    private static bool TryReadOuter(ReadOnlySpan<byte> document, out string kid, out byte[] envelopeBytes, out byte[] signature)
    {
        kid = string.Empty;
        envelopeBytes = [];
        signature = [];
        try
        {
            using var json = JsonDocument.Parse(document.ToArray(), s_outerOptions);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "kid", out var kidValue)
                || kidValue.Length is 0 or > MaxIdentifierLength
                || !TryString(root, "signed", out var signed)
                || !TryString(root, "sig", out var sig)
                || !TryDecode(signed, out envelopeBytes)
                || !TryDecode(sig, out signature))
            {
                return false;
            }

            kid = kidValue;
            return true;
        }
        catch (JsonException)
        {
            // Not JSON, too deep, or duplicate members: reported as Malformed by the caller.
            return false;
        }
        catch (InvalidOperationException)
        {
            // A string member holds invalid UTF-8 and cannot be transcoded: reported as Malformed by the caller.
            return false;
        }
        catch (FormatException)
        {
            // A member that looked like base64url did not decode: reported as Malformed by the caller.
            return false;
        }
    }

    private static bool TryReadEnvelope(byte[] envelopeBytes, out SignedEnvelope envelope)
    {
        envelope = null!;
        try
        {
            using var json = JsonDocument.Parse(envelopeBytes, s_envelopeOptions);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "kid", out var kid) || kid.Length is 0 or > MaxIdentifierLength
                || !TryString(root, "purpose", out var purpose) || purpose.Length is 0 or > MaxIdentifierLength
                || !TryString(root, "channel", out var channel) || channel.Length is 0 or > MaxIdentifierLength
                || !TryVersion(root, out var version)
                || !TryTime(root, "issuedAt", out var issuedAt)
                || !TryTime(root, "expiresAt", out var expiresAt)
                || expiresAt <= issuedAt
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind is JsonValueKind.Undefined)
            {
                return false;
            }

            envelope = new SignedEnvelope(kid, purpose, channel, version, issuedAt, expiresAt, payload.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            // Signed but not an envelope (or too deep): reported as Malformed by the caller.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Signed, but a string member holds invalid UTF-8 and cannot be transcoded: reported as Malformed by the caller.
            return false;
        }
        catch (FormatException)
        {
            // Signed, but a member is not in the expected encoding: reported as Malformed by the caller.
            return false;
        }
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryVersion(JsonElement parent, out long version)
    {
        version = 0;
        return parent.TryGetProperty("version", out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out version)
            && version is >= 0 and <= MaxVersion;
    }

    private static bool TryTime(JsonElement parent, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(parent, name, out var text)
            && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }

    private static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || !Base64Url.IsValid(text, out var length))
        {
            return false;
        }

        bytes = new byte[length];
        return Base64Url.TryDecodeFromChars(text, bytes, out var written) && written == length;
    }
}
