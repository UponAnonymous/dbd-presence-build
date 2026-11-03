# Signing test vectors

Shared by the C# suite (`tests/DbdPresence.Packs.Tests`) and the TypeScript service suite (PLAN_V2 §6.1, ADR-0012, NFR-SVC-04).

**The key in `keys/` is a TEST key. It is public in this repository, so it must never be trusted by a release build.**
Production builds embed their own keys and release CI asserts that this `kid` (`test-content-1`) is absent.

## Layout

- `keys/test-content-1.private.pem` / `.public.pem`: ECDSA P-256 test key pair (PKCS#8 and SubjectPublicKeyInfo PEM).
- `keys/test-config-only-1.*`: a second TEST key that is trusted only for the `config` purpose (vector `key-wrong-purpose`).
- `keys/keys.json`: the trusted set for the vectors, `{ "keys": [{ "kid", "spki", "purposes" }] }` with `spki` as base64url DER and `purposes` the purposes that key may sign.
- `vectors/*.json`: one case each.
- `ts-signed/*.json`: documents produced by the TypeScript service signer (`service/src/signing/document.ts`), one per purpose,
  each expected `Valid` with the given envelope and payload. Verified by `TsSignedDocumentTests` (C#) and `service/test/signing.test.ts`.
  Regenerate with `node scripts/make-ts-signed-fixtures.mjs` from `service/` (signatures change on each run).

## Vector format

```json
{
  "name": "valid",
  "description": "...",
  "expectedPurpose": "pack-index",
  "now": "2026-01-15T12:00:00.000Z",
  "storedVersions": [{ "purpose": "pack-index", "channel": "stable", "version": 5 }],
  "expected": "Valid",
  "documentText": "{\"kid\":\"...\",\"signed\":\"...\",\"sig\":\"...\"}"
}
```

`documentText` is the exact text of the signed document. `signed` is base64url (no padding) of the envelope bytes and
`sig` is base64url of a 64-byte IEEE P1363 `r || s` ECDSA P-256 SHA-256 signature over exactly those bytes.
`expected` is one of `Valid`, `BadSignature`, `UnknownKey`, `KeyNotAllowedForPurpose`, `WrongPurpose`, `WrongChannel`, `LifetimeTooLong`, `Expired`, `RolledBack`, `VersionTooFarAhead`, `VersionNotDerivedFromIssueTime`, `Malformed`.
Maximum `expiresAt - issuedAt` per purpose: config 14 days, pack-index 30, releases 30, licence 7, roster 30, key-replacement 3650. `issuedAt` may be at most 5 minutes after `now` (else `Malformed`).
Verifying never records a version; a runner that wants to model a commit does so explicitly.
Version rules (TM-01): any purpose except `licence` is `VersionTooFarAhead` when `version - floor(issuedAt seconds) > 1000000`; a `licence` must have `version == floor(issuedAt seconds)` (`VersionNotDerivedFromIssueTime`).
A runner sets its clock to `now`, preloads the highest-version store from `storedVersions`, verifies against
`expectedPurpose` and compares the outcome.

## Regenerating

```
dotnet run --project tools/DbdPresence.Packs.VectorGen -c Release -- tests/fixtures/signing
```

The envelopes and payloads are deterministic. ECDSA signatures are randomised, so `sig` values change on each run.
The existing private key is reused if present.
