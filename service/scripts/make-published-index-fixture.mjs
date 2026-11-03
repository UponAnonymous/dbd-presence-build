// Writes tests/fixtures/packs/published-index.json: a pack index as the service publishes and signs it, verified by
// the C# suite (tests/DbdPresence.Packs.Tests/PublishedIndexFixtureTests.cs: PackIndexParser must accept the payload)
// and by the TypeScript suite (test/packs.test.ts: publishing the same sample through the admin API produces this
// payload, and the document verifies). Signed with the public TEST key (never trusted by a release build).
//
//   node scripts/make-published-index-fixture.mjs        (from service/, Node 22.18 or later)
//
// The payload and envelope are deterministic; the ECDSA signature changes on every run.

import { readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { indexPayload } from "../src/packs/model.ts";
import { importSigningKey, signDocument, MAX_LIFETIME_MS } from "../src/signing/document.ts";
import { SAMPLE_PACKS, asPublished, sampleEntries } from "../test/published-index-sample.ts";

const root = join(import.meta.dirname, "..", "..", "tests", "fixtures");
const pem = readFileSync(join(root, "signing", "keys", "test-content-1.private.pem"), "utf8");
const pkcs8 = Buffer.from(pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, ""), "base64");
const key = await importSigningKey(new Uint8Array(pkcs8));
const issuedAt = new Date("2026-11-02T10:00:00.000Z");
const expiresAt = new Date(issuedAt.getTime() + MAX_LIFETIME_MS["pack-index"]);
const version = 1793613600;

const entries = await sampleEntries();
const payload = indexPayload(SAMPLE_PACKS, asPublished(entries));
const documentText = await signDocument("pack-index", "stable", version, payload, key, "test-content-1", { issuedAt, expiresAt });
const fixture = {
  name: "published-index",
  description:
    "A pack index as the service publishes it (two packs, one Behaviour-art pack that is also the default pack, and one fallback pack; " +
    "PNG and JPEG; one entry with its source hash; one licensed entry; one withdrawn licensed entry, which the payload leaves out). " +
    "Response body of GET /v1/packs is { schemaVersion: 1, document }.",
  expectedPurpose: "pack-index",
  expectedChannel: "stable",
  now: "2026-11-03T00:00:00.000Z",
  expectedVersion: version,
  expectedIssuedAt: issuedAt.toISOString(),
  expectedExpiresAt: expiresAt.toISOString(),
  withdrawnEntryIds: entries.filter((e) => e.withdrawn).map((e) => e.id),
  expectedPayload: payload,
  documentText,
};
mkdirSync(join(root, "packs"), { recursive: true });
writeFileSync(join(root, "packs", "published-index.json"), JSON.stringify(fixture, null, 2) + "\n");
console.log("wrote tests/fixtures/packs/published-index.json");
