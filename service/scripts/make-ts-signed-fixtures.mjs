// Writes tests/fixtures/signing/ts-signed/*.json: documents produced by the TypeScript signer (src/signing/document.ts)
// with the public TEST key, verified by both the C# suite (tests/DbdPresence.Packs.Tests/TsSignedDocumentTests.cs) and
// the TypeScript suite (test/signing.test.ts). This is the cross-stack contract in the signer-to-verifier direction.
//
//   node scripts/make-ts-signed-fixtures.mjs        (from service/, Node 22.18 or later: strips the TypeScript types)
//
// ECDSA signatures are randomised, so `sig` changes on every run; the envelopes are deterministic.

import { readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { importSigningKey, signDocument, MAX_LIFETIME_MS } from "../src/signing/document.ts";
import { configPayload } from "../src/routes/config-payload.ts";

const root = join(import.meta.dirname, "..", "..", "tests", "fixtures", "signing");
const pem = readFileSync(join(root, "keys", "test-content-1.private.pem"), "utf8");
const pkcs8 = Buffer.from(pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, ""), "base64");
const key = await importSigningKey(new Uint8Array(pkcs8));
const kid = "test-content-1";
const issuedAt = new Date("2026-01-01T00:00:00.000Z");
const now = "2026-01-02T00:00:00.000Z";

const cases = [
  {
    name: "ts-config",
    description: "A config document as GET /v1/config signs it (default settings), full 14-day lifetime.",
    purpose: "config",
    channel: "stable",
    version: 1767225600,
    payload: configPayload({
      accessMode: "invite-only",
      deviceLimit: 3,
      disableCapture: false,
      disableDetection: false,
      emergencyStop: false,
      minimumVersion: "0.1.0",
      banner: null,
    }),
  },
  {
    name: "ts-licence",
    description: "An active licence as sign-in and refresh issue it, full 7-day lifetime.",
    purpose: "licence",
    channel: "stable",
    version: 1767225600,
    payload: {
      schemaVersion: 1,
      sub: "6f1c1e1a-3b0e-4a51-9d7a-2f6f0f5f1a01",
      cnf: "Zx9b3hKq0c8mD8w1Qb7yGm3oFv2nLr6tUa5sEe4WjPk",
      status: "active",
      features: ["online"],
      iat: "2026-01-01T00:00:00.000Z",
      nbf: "2026-01-01T00:00:00.000Z",
      exp: "2026-01-08T00:00:00.000Z",
      serverTime: "2026-01-01T00:00:00.000Z",
    },
  },
  {
    name: "ts-pack-index",
    description: "A pack index with non-ASCII text and escapes, to prove the signed bytes survive exactly.",
    purpose: "pack-index",
    channel: "stable",
    version: 7,
    payload: {
      packs: [{ id: "original", name: "Original — “quoted” \\ é 🎃", version: 1, author: "Test author", licenceNote: "Test vector content", credit: null }],
      entries: [],
    },
  },
  {
    name: "ts-roster",
    description: "A character roster as GET /v1/roster signs it (PLAN_V2_S5B section 2.3): non-ASCII names, an alias, a licensed and a withdrawn character.",
    purpose: "roster",
    channel: "stable",
    version: 1767225600,
    payload: {
      schemaVersion: 1,
      characters: [
        { code: "K27", id: "the-onryo", role: "killer", displayName: "The Onryō", screenName: "The Onryō", aliases: [], licensed: { holder: "Example Rights Holder" }, withdrawn: false },
        { code: "K45", id: "the-test-killer", role: "killer", displayName: "The Test Killer", screenName: "The Test Killer", aliases: [], licensed: null, withdrawn: true },
        { code: "S27", id: "leon-scott-kennedy", role: "survivor", displayName: "Leon Scott Kennedy", screenName: "Leon Scott Kennedy", aliases: [{ name: "Leon S. Kennedy", seenOnScreen: "2026-10-24, owner screenshot" }], licensed: null, withdrawn: false },
      ],
    },
  },
  {
    name: "ts-releases",
    description: "A release feed on the beta channel (V2-S6), full 30-day lifetime: two packages with notes; both stacks parse the payload too.",
    purpose: "releases",
    channel: "beta",
    version: 3,
    payload: {
      schemaVersion: 1,
      packageId: "DbdPresence",
      releases: [
        { version: "1.3.0-beta.2", fileName: "DbdPresence-1.3.0-beta.2-beta-full.nupkg", size: 71234567, sha256: "a".repeat(64), notes: "Beta — “quoted” notes é" },
        { version: "1.2.0", fileName: "DbdPresence-1.2.0-beta-full.nupkg", size: 70000000, sha256: "b".repeat(64) },
      ],
    },
  },
  {
    name: "ts-key-replacement",
    description: "A key replacement (V2-S6, PLAN_V2 section 6.2): revokes one key and adds its successor; 3650-day lifetime.",
    purpose: "key-replacement",
    channel: "all",
    version: 2,
    payload: {
      schemaVersion: 1,
      revoke: ["release-prod-1"],
      add: [{ kid: "release-prod-2", spki: "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERCPQPvUJpBBcAQq9_BPoXB_TdGL-7PsZ3y4OOjAqHqqOryKO43rYL9fgF-ZMF5cjiObeYXLnahTljaOxOAhQPQ", purposes: ["releases"] }],
    },
  },
];

mkdirSync(join(root, "ts-signed"), { recursive: true });
for (const c of cases) {
  const expiresAt = new Date(issuedAt.getTime() + MAX_LIFETIME_MS[c.purpose]);
  const documentText = await signDocument(c.purpose, c.channel, c.version, c.payload, key, kid, { issuedAt, expiresAt });
  const fixture = {
    name: c.name,
    description: c.description,
    expectedPurpose: c.purpose,
    expectedChannel: c.channel,
    now,
    storedVersions: [],
    expected: "Valid",
    expectedVersion: c.version,
    expectedIssuedAt: issuedAt.toISOString(),
    expectedExpiresAt: expiresAt.toISOString(),
    expectedPayload: c.payload,
    documentText,
  };
  writeFileSync(join(root, "ts-signed", `${c.name}.json`), JSON.stringify(fixture, null, 2) + "\n");
  console.log(`wrote ${c.name}.json`);
}
