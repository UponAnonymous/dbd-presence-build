namespace DbdPresence.Packs.Signing;

/// <summary>Result of verifying a signed document (ADR-0012). Bad input never throws; it maps to one of these.</summary>
public enum VerificationOutcome
{
    /// <summary>Signature, key, purpose, expiry and version are all acceptable.</summary>
    Valid,

    /// <summary>The signature does not match the received bytes.</summary>
    BadSignature,

    /// <summary>The <c>kid</c> is not in the trusted key set.</summary>
    UnknownKey,

    /// <summary>The envelope's purpose is not the one the caller expected.</summary>
    WrongPurpose,

    /// <summary>The <c>kid</c> is trusted, but not for the purpose the caller expects (a content key must not sign a release feed).</summary>
    KeyNotAllowedForPurpose,

    /// <summary>The envelope's channel is not the one the caller asked for, so it cannot stand in for that channel's document.</summary>
    WrongChannel,

    /// <summary><c>expiresAt</c> is not in the future.</summary>
    Expired,

    /// <summary><c>expiresAt - issuedAt</c> exceeds the per-purpose maximum lifetime (a leaked key must not mint a document that stays valid for years).</summary>
    LifetimeTooLong,

    /// <summary>The version is lower than the highest version already seen for this purpose and channel.</summary>
    RolledBack,

    /// <summary>
    /// The version is more than <see cref="SignedDocumentVerifier.MaxVersionAheadOfIssue"/> above the signing time in seconds, so it could only
    /// be a floor-poisoning attempt (threat-model review 2026-10-25, TM-01).
    /// </summary>
    VersionTooFarAhead,

    /// <summary>A licence whose version is not the signing time in whole seconds (TM-01): the licence version is derived, never chosen.</summary>
    VersionNotDerivedFromIssueTime,

    /// <summary>The input is oversized, not valid JSON, too deeply nested, or not shaped like a signed document.</summary>
    Malformed,
}

/// <summary>Well-known envelope purposes (ADR-0012).</summary>
public static class SignedPurposes
{
    public const string PackIndex = "pack-index";
    public const string Config = "config";
    public const string Releases = "releases";
    public const string Licence = "licence";

    /// <summary>The character roster (PLAN_V2_S5B §2.3): signed by the content key, re-signed unchanged each week.</summary>
    public const string Roster = "roster";

    /// <summary>
    /// Key-replacement documents (PLAN_V2 §6.2): signed offline by the recovery key, they revoke a compromised key and trust its successor
    /// (<c>DbdPresence.Packs.Releases.KeyReplacement</c>). Only a recovery key may be trusted for this purpose.
    /// </summary>
    public const string KeyReplacement = "key-replacement";

    /// <summary>Every purpose the client knows.</summary>
    public static readonly IReadOnlyList<string> All = [PackIndex, Config, Releases, Licence, Roster, KeyReplacement];

    /// <summary>The longest <c>expiresAt - issuedAt</c> accepted for a purpose (PLAN_V2 §6.1 and §3.3), or null for an unknown purpose.</summary>
    public static TimeSpan? MaxLifetime(string purpose) => purpose switch
    {
        Config => TimeSpan.FromDays(14),
        PackIndex => TimeSpan.FromDays(30),
        Releases => TimeSpan.FromDays(30),
        Licence => TimeSpan.FromDays(7),
        Roster => TimeSpan.FromDays(30),

        // A revocation must still reach an install that was offline for a long time (D8). The recovery key never leaves the owner's offline
        // copies, so the cap protects less here than for the online keys (ADR-0012 addendum, 2026-10-25).
        KeyReplacement => TimeSpan.FromDays(3650),
        _ => null,
    };
}

/// <summary>The verified envelope. Only produced after the signature checked out.</summary>
/// <param name="Kid">Key identifier.</param>
/// <param name="Purpose">What the document is for.</param>
/// <param name="Channel">Release channel, for example <c>stable</c>.</param>
/// <param name="Version">Monotonic version used for rollback protection.</param>
/// <param name="IssuedAt">Signing time.</param>
/// <param name="ExpiresAt">Freeze-protection deadline.</param>
/// <param name="PayloadJson">The raw JSON text of the payload member.</param>
public sealed record SignedEnvelope(
    string Kid,
    string Purpose,
    string Channel,
    long Version,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string PayloadJson);

/// <summary>Outcome plus, when <see cref="VerificationOutcome.Valid"/>, the envelope.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Envelope">Set only when valid; for other outcomes it is null.</param>
public sealed record VerificationResult(VerificationOutcome Outcome, SignedEnvelope? Envelope)
{
    /// <summary>True when the document may be used.</summary>
    public bool IsValid => Outcome == VerificationOutcome.Valid;
}

/// <summary>Highest accepted version per (purpose, channel), for rollback protection.</summary>
public interface IVersionStore
{
    /// <summary>The highest version recorded, or null if none.</summary>
    long? GetHighest(string purpose, string channel);

    /// <summary>
    /// Records <paramref name="version"/> if it is higher than the one stored for the same signing key.
    /// <paramref name="kid"/> is the key that signed it (null only for a floor of unknown origin, which is never dropped).
    /// </summary>
    void RecordHighest(string purpose, string channel, long version, string? kid);

    /// <summary>Drops every floor that was committed under one of <paramref name="revokedKids"/> (TM-01); the highest remaining floor applies.</summary>
    void Forget(IReadOnlyCollection<string> revokedKids);
}

/// <summary>Process-local <see cref="IVersionStore"/> for tests; production uses <see cref="FileVersionStore"/> so rollback protection survives a restart.</summary>
public sealed class InMemoryVersionStore : IVersionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Purpose, string Channel, string Kid), long> _highest = [];

    /// <inheritdoc />
    public long? GetHighest(string purpose, string channel)
    {
        lock (_gate)
        {
            long? best = null;
            foreach (var (key, value) in _highest)
            {
                if (key.Purpose == purpose && key.Channel == channel && (best is null || value > best))
                {
                    best = value;
                }
            }

            return best;
        }
    }

    /// <inheritdoc />
    public void RecordHighest(string purpose, string channel, long version, string? kid)
    {
        var key = (purpose, channel, kid ?? string.Empty);
        lock (_gate)
        {
            if (!_highest.TryGetValue(key, out var current) || version > current)
            {
                _highest[key] = version;
            }
        }
    }

    /// <inheritdoc />
    public void Forget(IReadOnlyCollection<string> revokedKids)
    {
        ArgumentNullException.ThrowIfNull(revokedKids);
        lock (_gate)
        {
            foreach (var key in _highest.Keys.Where(k => k.Kid.Length > 0 && revokedKids.Contains(k.Kid)).ToList())
            {
                _highest.Remove(key);
            }
        }
    }
}
