using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Packs.Signing;

namespace DbdPresence.ReleaseSigner;

/// <summary>
/// Writes signed documents in the ADR-0012 format, byte for byte what the app's <see cref="SignedDocumentVerifier"/> and the service's
/// <c>verifyDocument</c> read: <c>{"kid","signed": base64url(envelope),"sig": base64url(P1363 r||s)}</c>. Every document is verified with the
/// app's own verifier before it is returned, so the tool can never hand out a document the app would refuse.
/// </summary>
internal static class DocumentSigner
{
    /// <summary>Signs one document.</summary>
    /// <param name="key">The private key.</param>
    /// <param name="kid">Its key id.</param>
    /// <param name="purpose">The purpose (<c>releases</c> or <c>key-replacement</c>).</param>
    /// <param name="channel">The channel.</param>
    /// <param name="version">The document version (higher than every earlier one of this purpose and channel).</param>
    /// <param name="issuedAt">The signing time.</param>
    /// <param name="expiresAt">The expiry; at most the purpose's maximum lifetime after <paramref name="issuedAt"/>.</param>
    /// <param name="payloadJson">The payload (compact JSON).</param>
    /// <returns>The document text.</returns>
    /// <exception cref="ArgumentException">An argument breaks a rule the app enforces.</exception>
    public static string Sign(ECDsa key, string kid, string purpose, string channel, long version, DateTimeOffset issuedAt, DateTimeOffset expiresAt, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(key);
        ReleaseKeys.RequireKid(kid);
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);
        var cap = SignedPurposes.MaxLifetime(purpose) ?? throw new ArgumentException($"Unknown purpose '{purpose}'.", nameof(purpose));
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, SignedDocumentVerifier.MaxVersion);
        if (expiresAt <= issuedAt || expiresAt - issuedAt > cap)
        {
            throw new ArgumentException($"The lifetime must be more than zero and at most {cap.TotalDays:0} days.", nameof(expiresAt));
        }

        if (!channel.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("A channel is lower-case letters, digits and hyphens.", nameof(channel));
        }

        // The payload is re-serialised from a parsed copy so the signed bytes are compact, valid JSON.
        string payload;
        using (var parsed = JsonDocument.Parse(payloadJson))
        {
            payload = JsonSerializer.Serialize(parsed.RootElement);
        }

        var envelope =
            $"{{\"kid\":{JsonSerializer.Serialize(kid)},\"purpose\":{JsonSerializer.Serialize(purpose)},\"channel\":{JsonSerializer.Serialize(channel)}," +
            $"\"version\":{version.ToString(CultureInfo.InvariantCulture)},\"issuedAt\":\"{Timestamp(issuedAt)}\",\"expiresAt\":\"{Timestamp(expiresAt)}\",\"payload\":{payload}}}";
        var envelopeBytes = Encoding.UTF8.GetBytes(envelope);
        var signature = key.SignData(envelopeBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var document = $"{{\"kid\":{JsonSerializer.Serialize(kid)},\"signed\":\"{Base64Url.EncodeToString(envelopeBytes)}\",\"sig\":\"{Base64Url.EncodeToString(signature)}\"}}";

        var check = Verifier(key, kid, purpose, issuedAt).Verify(Encoding.UTF8.GetBytes(document), purpose, channel);
        if (!check.IsValid)
        {
            throw new InvalidOperationException($"The signed document did not verify ({check.Outcome}); nothing was written.");
        }

        return document;
    }

    /// <summary>The signing time format the verifier reads: UTC with milliseconds, <c>2026-10-25T12:00:00.000Z</c>.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The text.</returns>
    public static string Timestamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Reads the envelope of a document without verifying it (to show what it says, or to re-sign its payload).</summary>
    /// <param name="document">The document text.</param>
    /// <returns>The envelope members.</returns>
    /// <exception cref="FormatException">Not a signed document.</exception>
    public static SignedEnvelope ReadEnvelopeUnverified(string document)
    {
        try
        {
            using var outer = JsonDocument.Parse(document);
            var signed = Base64Url.DecodeFromChars(outer.RootElement.GetProperty("signed").GetString()!);
            using var envelope = JsonDocument.Parse(signed);
            var root = envelope.RootElement;
            return new SignedEnvelope(
                root.GetProperty("kid").GetString()!,
                root.GetProperty("purpose").GetString()!,
                root.GetProperty("channel").GetString()!,
                root.GetProperty("version").GetInt64(),
                DateTimeOffset.Parse(root.GetProperty("issuedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                DateTimeOffset.Parse(root.GetProperty("expiresAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                root.GetProperty("payload").GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new FormatException("The file is not a signed document.", ex);
        }
    }

    private static SignedDocumentVerifier Verifier(ECDsa key, string kid, string purpose, DateTimeOffset issuedAt) =>
        new(
            new TrustedKeySet(new Dictionary<string, TrustedKey> { [kid] = new TrustedKey(key.ExportSubjectPublicKeyInfo(), [purpose]) }),
            new InMemoryVersionStore(),
            new FixedClock(issuedAt));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
