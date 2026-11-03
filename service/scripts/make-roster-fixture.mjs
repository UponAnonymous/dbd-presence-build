// Writes tests/fixtures/roster/signed-roster.json: a character roster as GET /v1/roster serves it (PLAN_V2_S5B §2.3,
// S5b-2; D35), for the cross-stack contract. It holds the 98 seed characters (tests/fixtures/roster/seed.json with the
// vocabulary's names), one character the service learned from the owner's game (K45, source "auto") and one the owner
// added and then removed (S55, withdrawn). The C# suites parse and merge it (RosterParserTests, RosterSyncTests); the
// TypeScript suite checks that the service serves exactly the seed part of it (test/roster.test.ts). Signed with the
// public TEST key, never trusted by a release build.
//
//   node scripts/make-roster-fixture.mjs        (from service/, Node 22.18 or later)
//
// The payload and envelope are deterministic; the ECDSA signature changes on every run.

import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { rosterPayload, rosterProblem } from "../src/roster/model.ts";
import { importSigningKey, signDocument, MAX_LIFETIME_MS } from "../src/signing/document.ts";

const root = join(import.meta.dirname, "..", "..");
const fixtures = join(root, "tests", "fixtures");
const seed = JSON.parse(readFileSync(join(fixtures, "roster", "seed.json"), "utf8"));
const vocabulary = JSON.parse(readFileSync(join(root, "src", "DbdPresence.Vision", "Vocabulary", "vocabulary.en.json"), "utf8"));
const byId = new Map([...vocabulary.killers, ...vocabulary.survivors].map((e) => [e.id, e]));

const characters = seed.characters.map((c) => {
  const entry = byId.get(c.id);
  return {
    code: c.code,
    id: c.id,
    role: c.role,
    displayName: entry.name,
    screenName: entry.name,
    aliases: (entry.aliases ?? []).map((a) => ({ name: a.name, seenOnScreen: a.seenOnScreen })),
    licensedHolder: null,
    source: "seed",
    addedAt: 0,
    withdrawnAt: null,
  };
});
characters.push(
  { code: "K45", id: "the-lantern", role: "killer", displayName: "The Lantern", screenName: "The Lantern", aliases: [], licensedHolder: null, source: "auto", addedAt: 1, withdrawnAt: null },
  { code: "S55", id: "mara-quell", role: "survivor", displayName: "Mara Quell", screenName: "Mara Quell", aliases: [], licensedHolder: null, source: "owner", addedAt: 1, withdrawnAt: 2 },
);
const problem = rosterProblem(characters);
if (problem !== null) throw new Error(`the sample roster breaks a rule: ${problem}`);

const pem = readFileSync(join(fixtures, "signing", "keys", "test-content-1.private.pem"), "utf8");
const key = await importSigningKey(new Uint8Array(Buffer.from(pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, ""), "base64")));
const issuedAt = new Date("2026-11-02T10:00:00.000Z");
const expiresAt = new Date(issuedAt.getTime() + MAX_LIFETIME_MS.roster);
const version = 1793613600;
const payload = rosterPayload(characters);
const documentText = await signDocument("roster", "stable", version, payload, key, "test-content-1", { issuedAt, expiresAt });
const fixture = {
  name: "signed-roster",
  description:
    "The character roster as the service signs it: the 98 seed characters, one learned from the owner's game (K45) and one the owner " +
    "removed (S55, withdrawn). Response body of GET /v1/roster?channel=stable is { schemaVersion: 1, document }.",
  expectedPurpose: "roster",
  expectedChannel: "stable",
  now: "2026-11-03T00:00:00.000Z",
  expectedVersion: version,
  expectedIssuedAt: issuedAt.toISOString(),
  expectedExpiresAt: expiresAt.toISOString(),
  seedCodes: seed.characters.map((c) => c.code),
  expectedPayload: payload,
  documentText,
};
writeFileSync(join(fixtures, "roster", "signed-roster.json"), JSON.stringify(fixture, null, 2) + "\n");
console.log("wrote tests/fixtures/roster/signed-roster.json");
