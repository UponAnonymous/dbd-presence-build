// Regenerates the shared signing test vectors (PLAN_V2 §6.1, ADR-0012, NFR-SVC-04).
//
//   dotnet run --project tools/DbdPresence.Packs.VectorGen -- tests/fixtures/signing
//
// Everything is deterministic except the ECDSA signatures themselves (ECDSA uses fresh randomness). The test key is
// created once and then reused from the PEM files, so key.json and the public PEM only change if the key is deleted.
// The tool is written independently of the product verifier so the vectors do not merely agree with it by construction.
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: VectorGen <fixtures/signing directory>");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var keyDir = Path.Combine(root, "keys");
var vectorDir = Path.Combine(root, "vectors");
Directory.CreateDirectory(keyDir);
Directory.CreateDirectory(vectorDir);

const string Kid = "test-content-1";
const string ConfigOnlyKid = "test-config-only-1";
string[] allPurposes = ["pack-index", "config", "releases", "licence", "roster", "key-replacement"];
string[] configOnly = ["config"];
using var key = LoadOrCreateKey(Kid);
using var configOnlyKey = LoadOrCreateKey(ConfigOnlyKid);

// test-content-1 may sign every purpose (so the wrong-purpose vector still reaches the envelope check);
// test-config-only-1 may sign only "config" (the key-wrong-purpose vector).
File.WriteAllText(
    Path.Combine(keyDir, "keys.json"),
    JsonSerializer.Serialize(
        new
        {
            keys = new[]
            {
                new { kid = Kid, spki = Base64Url.EncodeToString(key.ExportSubjectPublicKeyInfo()), purposes = allPurposes },
                new { kid = ConfigOnlyKid, spki = Base64Url.EncodeToString(configOnlyKey.ExportSubjectPublicKeyInfo()), purposes = configOnly },
            },
        },
        JsonOptions.Indented) + "\n");

ECDsa LoadOrCreateKey(string kid)
{
    var privatePath = Path.Combine(keyDir, kid + ".private.pem");
    var created = ECDsa.Create();
    if (File.Exists(privatePath))
    {
        created.ImportFromPem(File.ReadAllText(privatePath));
    }
    else
    {
        created.GenerateKey(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, created.ExportPkcs8PrivateKeyPem());
    }

    File.WriteAllText(Path.Combine(keyDir, kid + ".public.pem"), created.ExportSubjectPublicKeyInfoPem());
    return created;
}

const string Now = "2026-01-15T12:00:00.000Z";
const string Issued = "2026-01-01T00:00:00.000Z";
const string Expires = "2026-01-31T00:00:00.000Z";
const string Purpose = "pack-index";

string Envelope(string kid, string purpose, long version, string issuedAt, string expiresAt) =>
    JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            ["kid"] = kid,
            ["purpose"] = purpose,
            ["channel"] = "stable",
            ["version"] = version,
            ["issuedAt"] = issuedAt,
            ["expiresAt"] = expiresAt,
            ["payload"] = Payload(),
        },
        JsonOptions.Compact);

string Sign(string outerKid, byte[] envelopeBytes, byte[]? signatureOverride = null, ECDsa? signer = null) =>
    JsonSerializer.Serialize(
        new Dictionary<string, string>
        {
            ["kid"] = outerKid,
            ["signed"] = Base64Url.EncodeToString(envelopeBytes),
            ["sig"] = Base64Url.EncodeToString(signatureOverride ?? (signer ?? key).SignData(envelopeBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        },
        JsonOptions.Compact);

void Write(string name, string description, string expected, string documentText, long? storedVersion = null, string expectedChannel = "stable", string expectedPurpose = Purpose)
{
    var vector = new Dictionary<string, object?>
    {
        ["name"] = name,
        ["description"] = description,
        ["expectedPurpose"] = expectedPurpose,
        ["expectedChannel"] = expectedChannel,
        ["now"] = Now,
        ["storedVersions"] = storedVersion is { } v ? new[] { new { purpose = Purpose, channel = "stable", version = v } } : [],
        ["expected"] = expected,
        ["documentText"] = documentText,
    };
    File.WriteAllText(Path.Combine(vectorDir, name + ".json"), JsonSerializer.Serialize(vector, JsonOptions.Indented) + "\n");
}

var valid = Encoding.UTF8.GetBytes(Envelope(Kid, Purpose, 5, Issued, Expires));
var validDocument = Sign(Kid, valid);

Write("valid", "Correctly signed, unexpired, no stored version.", "Valid", validDocument);
Write("valid-equal-version", "Same version as already stored: accepted as idempotent.", "Valid", validDocument, storedVersion: 5);
Write("valid-newer-version", "Higher than the stored version: accepted.", "Valid", validDocument, storedVersion: 4);

var tamperedBytes = Encoding.UTF8.GetBytes(Envelope(Kid, Purpose, 5, Issued, Expires).Replace("the-trapper", "the-trapped", StringComparison.Ordinal));
// Re-use the original signature over the unmodified bytes so only the payload differs.
var originalSignature = key.SignData(valid, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
Write("tampered-payload", "Payload edited after signing; the original signature no longer matches.", "BadSignature", Sign(Kid, tamperedBytes, originalSignature));

var badSignature = key.SignData(valid, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
badSignature[0] ^= 0x01;
Write("tampered-signature", "One bit of the signature flipped.", "BadSignature", Sign(Kid, valid, badSignature));

Write(
    "expired",
    "Signed correctly but expiresAt is before the verification time.",
    "Expired",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope(Kid, Purpose, 5, "2025-11-01T00:00:00.000Z", "2025-12-01T00:00:00.000Z"))));

Write(
    "rolled-back",
    "Version 3 offered after version 5 was already accepted.",
    "RolledBack",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope(Kid, Purpose, 3, Issued, Expires))),
    storedVersion: 5);

// Issued is 2026-01-01T00:00:00Z = 1767225600 seconds. TM-01: a version may be at most 1,000,000 above the signing time in seconds.
Write(
    "version-too-far-ahead",
    "Version is 1,000,001 above the signing time in seconds, so it could only park a rollback floor out of reach.",
    "VersionTooFarAhead",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope(Kid, Purpose, 1767225600L + 1_000_001L, Issued, Expires))));

Write(
    "licence-version-mismatch",
    "A licence whose version is not the signing time in whole seconds (TM-01).",
    "VersionNotDerivedFromIssueTime",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope(Kid, "licence", 1767225601L, Issued, "2026-01-08T00:00:00.000Z"))),
    expectedPurpose: "licence");

Write(
    "wrong-purpose",
    "Validly signed config document offered where a pack index is expected.",
    "WrongPurpose",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope(Kid, "config", 5, Issued, Expires))));

Write(
    "key-wrong-purpose",
    "A key trusted only for config signs a pack index: the signature is good but the key may not sign this purpose.",
    "KeyNotAllowedForPurpose",
    Sign(ConfigOnlyKid, Encoding.UTF8.GetBytes(Envelope(ConfigOnlyKid, Purpose, 5, Issued, Expires)), signer: configOnlyKey));

Write(
    "lifetime-too-long",
    "Pack index valid for 59 days; the cap for pack-index is 30.",
    "LifetimeTooLong",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope(Kid, Purpose, 5, Issued, "2026-03-01T00:00:00.000Z"))));

Write(
    "issued-in-future",
    "issuedAt is ten minutes after the verification time; more than the five minutes of clock skew allowed.",
    "Malformed",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope(Kid, Purpose, 5, "2026-01-15T12:10:00.000Z", "2026-01-30T00:00:00.000Z"))));

Write(
    "wrong-channel",
    "Validly signed stable pack index offered where the beta channel's index is expected.",
    "WrongChannel",
    validDocument,
    expectedChannel: "beta");

Write(
    "unknown-kid",
    "Signed by the test key but claiming a kid that is not trusted.",
    "UnknownKey",
    Sign("unknown-key", Encoding.UTF8.GetBytes(Envelope("unknown-key", Purpose, 5, Issued, Expires))));

Write(
    "kid-mismatch",
    "The outer kid is trusted but the signed envelope names a different kid.",
    "Malformed",
    Sign(Kid, Encoding.UTF8.GetBytes(Envelope("other-key", Purpose, 5, Issued, Expires))));

Write(
    "malformed-envelope",
    "Validly signed bytes that are not an envelope.",
    "Malformed",
    Sign(Kid, Encoding.UTF8.GetBytes("this is not json")));

Write(
    "malformed-document",
    "The outer document is not JSON.",
    "Malformed",
    "{\"kid\":");

Console.WriteLine($"Wrote vectors to {vectorDir}");
return 0;

static object Payload()
{
    static object Entry(string id, string key, string kind, string? holder) => new Dictionary<string, object?>
    {
        ["id"] = id,
        ["pack"] = "original",
        ["kind"] = kind,
        ["key"] = key,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("vector-" + id))),
        ["size"] = 40000,
        ["width"] = 1024,
        ["height"] = 1024,
        ["format"] = "png",
        ["licensed"] = holder is null ? null : new { holder },
    };

    return new Dictionary<string, object>
    {
        ["packs"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["id"] = "original",
                ["name"] = "Original",
                ["version"] = 1,
                ["author"] = "Test author",
                ["licenceNote"] = "Test vector content",
                ["credit"] = null,
            },
        },
        ["entries"] = new[]
        {
            Entry("original-role-killer", "role:killer", "role", null),
            Entry("original-the-trapper", "character:the-trapper", "character", null),
            Entry("original-ghostface", "character:ghostface", "character", "Test rights holder"),
        },
    };
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
