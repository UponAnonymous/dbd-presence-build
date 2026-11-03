// The character roster (PLAN_V2_S5B §2.1–2.3, §2.8; S5b-2, S5b-7 service side; owner decision D35):
// FR-ROS-01 (seeded from seed.json, no third-party fetch, weekly re-sign), FR-ROS-02 (checks; a failed check stores
// nothing), FR-ROS-03 (signed roster document, GET /v1/roster contract), FR-ROS-05 (code, id and names; TS seed
// agreement), D35 (owner-only reports, 3 separate matches with the identical folded name, Undo and block), NFR-SVC-03
// (rate limits and the write budget), NFR-SVC-13 (CPU), FR-ADM-02/03/05 (admin auth and audit on every new route).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, inject, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import {
  MATCHES_TO_ADD,
  MAX_AUTO_ADDS_PER_DAY,
  MAX_SIGHTINGS,
  MAX_SIGHTINGS_PER_ACCOUNT,
  MIN_SIGHTING_SPACING_MS,
  RESIGN_AFTER_MS,
  SIGHTING_RETENTION_MS,
  countedSightings,
  readyToAdd,
  runRosterMaintenance,
} from "../src/db/roster.ts";
import { ACCOUNT_LIMITS, D1_DAILY_WRITE_LIMIT, SENSITIVE_WRITE_BUDGET, accountRouteDailyWrites } from "../src/ratelimit.ts";
import { foldName, skeleton } from "../src/roster/fold.ts";
import { MAX_CHARACTERS, isCharacterShaped, rosterPayload, rosterProblem, screenNameFrom, type RosterCharacter } from "../src/roster/model.ts";
import { MAX_ROSTER_REPORTERS, MAX_SIGHTINGS_PER_REPORT } from "../src/routes/roster.ts";
import { verifyDocument } from "../src/signing/document.ts";
import { CPU_BUDGET_MS, bestMedian } from "./cpu.ts";
import { ADMIN, API, Harness, envelopeOf, newDevice, resetDatabase, rowCount, trustedKeys } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

type Json = Record<string, unknown>;
const DAY = 86_400_000;

interface Account {
  userId: string;
  auth: Record<string, string>;
  discordId: string;
  device: Awaited<ReturnType<typeof newDevice>>;
}

let discordSeq = 0;
async function account(): Promise<Account> {
  await h.admin("PUT", "/api/settings", { accessMode: "public" });
  const discordId = `6900000000000${String(++discordSeq).padStart(5, "0")}`;
  const device = await newDevice();
  const { status, body } = await h.signIn(discordId, device);
  expect(status).toBe(200);
  return { userId: (body["user"] as { id: string }).id, auth: { Authorization: `Bearer ${body["accessToken"] as string}` }, discordId, device };
}

/** A new access token after the clock moved past the old one's hour (same account and device). */
async function renew(who: Account): Promise<void> {
  const { status, body } = await h.signIn(who.discordId, who.device);
  expect(status).toBe(200);
  who.auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
}

async function reporter(): Promise<Account> {
  const who = await account();
  expect((await h.admin("PUT", `/api/users/${who.userId}/roster-reporter`, { enabled: true })).status).toBe(200);
  return who;
}

const matchId = (n: number): string => n.toString(16).padStart(32, "0");

function report(who: Account, sightings: unknown, extra: Json = {}): Promise<Response> {
  return h.call("POST", `${API}/v1/roster/sightings`, { headers: who.auth, body: { appVersion: "0.4.1", sightings, ...extra } });
}

async function reportOne(who: Account, name: string, role: string, match: number): Promise<Json> {
  const response = await report(who, [{ name, role, match: matchId(match) }]);
  expect(response.status).toBe(200);
  return (await response.json()) as Json;
}

/** Reports a name on three separate days, the way the owner's app would over a few evenings (D35: 3 matches, an hour apart at least, two days from first to third). */
async function learn(who: Account, name: string, role: string, firstMatch = 1): Promise<Json> {
  let last: Json = {};
  for (let i = 0; i < MATCHES_TO_ADD; i++) {
    if (i > 0) {
      h.now += DAY;
      await renew(who);
    }
    last = await reportOne(who, name, role, firstMatch + i);
  }
  return last;
}

interface PayloadCharacter {
  code: string;
  id: string;
  role: string;
  displayName: string;
  screenName: string;
  aliases: { name: string; seenOnScreen: string }[];
  licensed: { holder: string } | null;
  withdrawn: boolean;
}

async function fetchRoster(deps = h.deps): Promise<{ response: Response; document: string }> {
  const response = await handleRequest(new Request(`${API}/v1/roster?channel=stable`), env, deps);
  const text = await response.clone().text();
  return { response, document: (JSON.parse(text) as { document: string }).document };
}

async function signedCharacters(): Promise<PayloadCharacter[]> {
  h.now += 61_000; // past the 60-second document cache
  const { document } = await fetchRoster();
  const result = await verifyDocument(document, "roster", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
  expect(result.outcome).toBe("Valid");
  return (JSON.parse(result.envelope!.payloadJson) as { characters: PayloadCharacter[] }).characters;
}

async function adminRoster(): Promise<{ roster: { characters: (PayloadCharacter & { source: string })[]; version: number; stale: boolean; nextCodes: Record<string, string> }; pending: Json[]; blocked: Json[]; reporters: Json[] }> {
  const response = await h.admin("GET", "/api/roster");
  expect(response.status).toBe(200);
  return (await response.json()) as never;
}

function seedCharacters(): RosterCharacter[] {
  const seed = JSON.parse(inject("rosterSeed")) as { characters: { code: string; id: string; role: "killer" | "survivor" }[] };
  const vocabulary = JSON.parse(inject("vocabulary")) as { killers: { id: string; name: string; aliases?: { name: string; seenOnScreen: string }[] }[]; survivors: { id: string; name: string; aliases?: { name: string; seenOnScreen: string }[] }[] };
  const byId = new Map([...vocabulary.killers, ...vocabulary.survivors].map((e) => [e.id, e]));
  return seed.characters.map((c) => {
    const entry = byId.get(c.id)!;
    return { ...c, displayName: entry.name, screenName: entry.name, aliases: entry.aliases ?? [], licensedHolder: null, source: "seed", addedAt: 0, withdrawnAt: null };
  });
}

describe("seed and shared rules (FR-ROS-01, FR-ROS-05)", () => {
  it("migration 0005 holds exactly seed.json, with the vocabulary's names", async () => {
    const seed = JSON.parse(inject("rosterSeed")) as { characters: { code: string; id: string; role: string }[] };
    const { results } = await env.DB.prepare("SELECT code, id, role, display_name, screen_name, source FROM roster_characters ORDER BY code").all<Json>();
    expect(results.map((r) => ({ code: r["code"], id: r["id"], role: r["role"] }))).toEqual([...seed.characters].sort((a, b) => (a.code < b.code ? -1 : 1)));
    expect(results).toHaveLength(98);
    const names = new Map(seedCharacters().map((c) => [c.code, c.displayName]));
    for (const row of results) {
      expect(row["display_name"]).toBe(names.get(row["code"] as string));
      expect(row["screen_name"]).toBe(row["display_name"]);
      expect(row["source"]).toBe("seed");
    }
  });

  it("the seed passes every roster rule (codes fit roles, unique ids, names distinct after the OCR fold)", () => {
    expect(rosterProblem(seedCharacters())).toBeNull();
  });

  it("folds names exactly like the app (shared fixture, also checked by the C# Core and Vision suites)", () => {
    const fixture = JSON.parse(inject("rosterFoldCases")) as { cases: { input: string; fold: string | null; skeleton: string | null }[] };
    expect(fixture.cases.length).toBeGreaterThan(20);
    for (const c of fixture.cases) {
      expect(foldName(c.input), c.input).toBe(c.fold);
      expect(skeleton(c.input), c.input).toBe(c.skeleton);
    }
  });

  it("derives the screen name from a display name with a dash (The Good Guy - Chucky → The Good Guy)", () => {
    expect(screenNameFrom("The Good Guy - Chucky")).toBe("The Good Guy");
    expect(screenNameFrom("The Onryō – Sadako")).toBe("The Onryō");
    expect(screenNameFrom("Jill Valentine")).toBe("Jill Valentine");
  });

  it("refuses rosters the app would refuse", () => {
    const seed = seedCharacters();
    const extra = (c: Partial<RosterCharacter>): RosterCharacter[] => [...seed, { code: "K45", id: "the-new-one", role: "killer", displayName: "The New One", screenName: "The New One", aliases: [], licensedHolder: null, source: "owner", addedAt: 1, withdrawnAt: null, ...c }];
    expect(rosterProblem(extra({}))).toBeNull();
    expect(rosterProblem(extra({ code: "S99" }))).toBe("bad_code");
    expect(rosterProblem(extra({ code: "K4" }))).toBe("bad_code");
    expect(rosterProblem(extra({ code: "K01" }))).toBe("bad_code");
    expect(rosterProblem(extra({ id: "the-trapper" }))).toBe("bad_id");
    expect(rosterProblem(extra({ id: "generic" }))).toBe("bad_id");
    expect(rosterProblem(extra({ displayName: "The‮One" }))).toBe("bad_name");
    expect(rosterProblem(extra({ displayName: "The\u0007One" }))).toBe("bad_name");
    expect(rosterProblem(extra({ displayName: "x".repeat(81) }))).toBe("bad_name");
    expect(rosterProblem(extra({ displayName: "The 𝐎ne" }))).toBe("bad_name");
    expect(rosterProblem(extra({ displayName: "The Trappér", screenName: "The Trappér" }))).toBe("name_collision");
    expect(rosterProblem(extra({ displayName: "The Trapper", screenName: "The Trapper" }))).toBe("name_collision");
    expect(rosterProblem(extra({ aliases: [{ name: "The Ne1v One", seenOnScreen: "" }] }))).toBe("bad_alias");
    expect(rosterProblem(Array.from({ length: MAX_CHARACTERS + 1 }, (_, i) => ({ ...seed[0]!, code: `K${i}` })))).toBe("too_many_characters");
  });

  it("checks the largest possible roster well within the CPU budget (NFR-SVC-13)", async () => {
    // Every code there can be (K00–K99, S00–S99), each with two aliases: more than the 400-name cap ever needs.
    const letters = "abcdefghjkmpqtuwxyz";
    const word = (n: number): string => letters[n % letters.length]! + letters[Math.floor(n / letters.length) % letters.length]! + letters[Math.floor(n / 361) % letters.length]!;
    const many: RosterCharacter[] = Array.from({ length: 200 }, (_, i) => ({
      code: `${i < 100 ? "K" : "S"}${String(i % 100).padStart(2, "0")}`,
      id: `c-${i}`,
      role: i < 100 ? "killer" : "survivor",
      displayName: `Name ${word(3 * i)}`,
      screenName: `Name ${word(3 * i)}`,
      aliases: [1, 2].map((k) => ({ name: `Name ${word(3 * i + k)}`, seenOnScreen: "2026-11-02, test" })),
      licensedHolder: null,
      source: "owner",
      addedAt: 0,
      withdrawnAt: null,
    }));
    expect(rosterProblem(many)).toBeNull();
    const timing = await bestMedian(() => {
      rosterProblem(many);
      JSON.stringify(rosterPayload(many));
    });
    expect(timing.medianMs).toBeLessThan(CPU_BUDGET_MS);
  });
});

describe("GET /v1/roster (FR-ROS-03)", () => {
  it("is public and serves the seed, signed with purpose roster, channel stable and a 30-day lifetime", async () => {
    const { response, document } = await fetchRoster();
    expect(response.status).toBe(200);
    expect(response.headers.get("Cache-Control")).toBe("public, max-age=300");
    expect(response.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
    expect(response.headers.has("Set-Cookie")).toBe(false);
    const envelope = envelopeOf(document);
    expect(envelope).toMatchObject({ kid: "test-content-1", purpose: "roster", channel: "stable", version: Math.floor(h.now / 1000), issuedAt: new Date(h.now).toISOString() });
    expect(Date.parse(envelope["expiresAt"] as string) - Date.parse(envelope["issuedAt"] as string)).toBe(30 * DAY);
    const result = await verifyDocument(document, "roster", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
    expect(result.outcome).toBe("Valid");
    expect(JSON.parse(result.envelope!.payloadJson)).toEqual(rosterPayload(seedCharacters()));
  });

  it("matches the committed contract fixture (the C# suites parse the same document)", async () => {
    const fixture = JSON.parse(inject("signedRoster")) as { expectedPayload: { schemaVersion: number; characters: PayloadCharacter[] }; documentText: string; now: string; seedCodes: string[] };
    const served = (await signedCharacters());
    expect(served).toEqual(fixture.expectedPayload.characters.filter((c) => fixture.seedCodes.includes(c.code)));
    const result = await verifyDocument(fixture.documentText, "roster", "stable", { keys: await trustedKeys(), now: new Date(fixture.now) });
    expect(result.outcome).toBe("Valid");
    expect(JSON.parse(result.envelope!.payloadJson)).toEqual(fixture.expectedPayload);
    expect(fixture.expectedPayload.characters.filter((c) => c.withdrawn).map((c) => c.code)).toEqual(["S55"]);
  });

  it("refuses another channel and other methods, and answers 503 without the content key", async () => {
    expect((await h.call("GET", `${API}/v1/roster?channel=beta`)).status).toBe(400);
    expect((await h.call("POST", `${API}/v1/roster`)).status).toBe(405);
    const response = await handleRequest(new Request(`${API}/v1/roster`), { ...env, CONTENT_SIGNING_KEY: undefined }, h.deps);
    expect(response.status).toBe(503);
    // The pack-index-only purpose set does not cover the roster: a key must be trusted for "roster" explicitly.
    const { document } = await fetchRoster();
    const keys = JSON.parse(inject("signingKeys")) as { keys: { kid: string; spki: string; purposes: string[] }[] };
    const { TrustedKeySet } = await import("../src/signing/document.ts");
    const packOnly = await TrustedKeySet.fromJson({ keys: keys.keys.map((k) => ({ ...k, purposes: ["pack-index"] })) });
    expect((await verifyDocument(document, "roster", "stable", { keys: packOnly, now: new Date(h.now) })).outcome).toBe("KeyNotAllowedForPurpose");
  });

  it("is cached for 60 seconds and creates its first version only once", async () => {
    const first = (await fetchRoster()).document;
    h.now += 30_000;
    expect((await fetchRoster()).document).toBe(first);
    h.now += 31_000;
    const later = (await fetchRoster()).document;
    expect(later).not.toBe(first);
    expect(envelopeOf(later)["version"]).toBe(envelopeOf(first)["version"]);
    expect(envelopeOf(later)["issuedAt"]).toBe(envelopeOf(first)["issuedAt"]);
    expect(await rowCount("roster_versions")).toBe(1);
    expect(await rowCount("audit_log", "action = 'roster.initial'")).toBe(1);
  });

  it("makes no outbound request (NFR-SVC-12: no third-party fetch)", async () => {
    await fetchRoster();
    const who = await reporter();
    for (let m = 1; m <= MATCHES_TO_ADD; m++) await reportOne(who, "the lantern", "killer", m);
    await runRosterMaintenance(env.DB, h.now + RESIGN_AFTER_MS);
    expect(h.calls.filter((c) => !c.url.startsWith("https://discord.com/") && !c.url.startsWith("https://dbdp-test.cloudflareaccess.com/"))).toEqual([]);
  });
});

describe("weekly re-sign (FR-ROS-01)", () => {
  it("re-signs the unchanged roster at 7 days and not before, keeping its version", async () => {
    const before = envelopeOf((await fetchRoster()).document);
    const sixDays = await runRosterMaintenance(env.DB, h.now + 6 * DAY);
    expect(sixDays.resigned).toBe(false);
    h.now += RESIGN_AFTER_MS;
    const week = await runRosterMaintenance(env.DB, h.now);
    expect(week).toMatchObject({ resigned: true, version: before["version"] });
    h.now += 61_000;
    const after = envelopeOf((await fetchRoster()).document);
    expect(after["version"]).toBe(before["version"]);
    expect(after["issuedAt"]).toBe(new Date(h.now - 61_000).toISOString());
    expect(await rowCount("audit_log", "action = 'roster.resign'")).toBe(1);
    expect((await runRosterMaintenance(env.DB, h.now)).resigned).toBe(false);
  });

  it("without a re-sign the document expires at 30 days, and the panel flags it as stale from 25 days", async () => {
    await fetchRoster();
    h.now += 26 * DAY;
    expect((await adminRoster()).roster.stale).toBe(true);
    h.now += 4 * DAY;
    const { document } = await fetchRoster();
    expect((await verifyDocument(document, "roster", "stable", { keys: await trustedKeys(), now: new Date(h.now) })).outcome).toBe("Expired");
  });

  it("runs from the daily cron and forgets sightings older than 30 days", async () => {
    const who = await reporter();
    await reportOne(who, "the lantern", "killer", 1);
    expect(await rowCount("roster_sightings")).toBe(1);
    const swept = await runRosterMaintenance(env.DB, h.now + SIGHTING_RETENTION_MS + 1);
    expect(swept.sightingsDeleted).toBe(1);
    expect(await rowCount("roster_sightings")).toBe(0);
  });
});

describe("sighting rules (pure)", () => {
  it("accepts names of a character's shape and refuses a player's (digit, one word, too short, too many words)", () => {
    for (const ok of ["the lantern", "leon s kennedy", "zelda quillfeather", "a bc de"]) expect(isCharacterShaped(ok), ok).toBe(true);
    for (const bad of ["nightshade", "zelda 77", "the lantern 2", "a b c", "ab c", "one two three four five six", "", "the  lantern"]) expect(isCharacterShaped(bad), bad).toBe(false);
  });

  it("counts reports an hour apart and needs two days between the first and the third", () => {
    const t = 1_000_000_000_000;
    expect(countedSightings([t, t + 1, t + MIN_SIGHTING_SPACING_MS - 1, t + MIN_SIGHTING_SPACING_MS])).toEqual([t, t + MIN_SIGHTING_SPACING_MS]);
    expect(readyToAdd([t, t + DAY, t + 2 * DAY])).toBe(true);
    expect(readyToAdd([t, t + DAY, t + 2 * DAY - 1])).toBe(false);
    expect(readyToAdd([t, t + 1, t + 2 * DAY])).toBe(false);
  });
});

describe("learning new characters from the owner's game (D35)", () => {
  it("only an account the owner marked may report: every other account is refused 403 and nothing is stored", async () => {
    const friend = await account();
    const response = await report(friend, [{ name: "the lantern", role: "killer", match: matchId(1) }]);
    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "not_roster_reporter" });
    expect(await rowCount("roster_sightings")).toBe(0);
    expect((await h.call("POST", `${API}/v1/roster/sightings`, { body: { appVersion: "0.4.1", sightings: [] } })).status).toBe(401);
    // GET /v1/me tells each app whether it reports.
    const me = async (who: Account) => ((await (await h.call("GET", `${API}/v1/me`, { headers: who.auth })).json()) as { user: { rosterReporter: boolean } }).user.rosterReporter;
    expect(await me(friend)).toBe(false);
    const owner = await reporter();
    expect(await me(owner)).toBe(true);
  });

  it("adds a name after it was seen in 3 separate matches, signed for everyone and shown as added automatically", async () => {
    const owner = await reporter();
    const before = await signedCharacters();
    expect(await reportOne(owner, "the lantern", "killer", 1)).toMatchObject({ accepted: 1, added: [] });
    h.now += DAY;
    await renew(owner);
    expect(await reportOne(owner, "the lantern", "killer", 2)).toMatchObject({ accepted: 1, added: [] });
    expect((await adminRoster()).pending).toEqual([expect.objectContaining({ name: "the lantern", role: "killer", matches: 2 })]);
    h.now += DAY;
    await renew(owner);
    const third = await reportOne(owner, "the lantern", "killer", 3);
    expect(third["added"]).toEqual([{ code: "K45", id: "the-lantern", role: "killer", displayName: "The Lantern", restored: false }]);
    const after = await signedCharacters();
    expect(after).toHaveLength(before.length + 1);
    expect(after.find((c) => c.code === "K45")).toEqual({ code: "K45", id: "the-lantern", role: "killer", displayName: "The Lantern", screenName: "The Lantern", aliases: [], licensed: null, withdrawn: false });
    const view = await adminRoster();
    expect(view.roster.characters.find((c) => c.code === "K45")).toMatchObject({ source: "auto", withdrawn: false });
    expect(view.pending).toEqual([]);
    expect(await rowCount("roster_sightings")).toBe(0);
    expect(await rowCount("audit_log", "action = 'roster.auto_add' AND actor = ?", `user:${owner.userId}`)).toBe(1);
    // A fourth sighting of the now-known name is ignored.
    expect(await reportOne(owner, "the lantern", "killer", 4)).toMatchObject({ accepted: 0, ignored: { known: 1, blocked: 0, full: 0 } });
  });

  it("counts separate matches only: the same match reported again counts once", async () => {
    const owner = await reporter();
    for (let i = 0; i < 5; i++) await reportOne(owner, "the lantern", "killer", 7);
    h.now += DAY;
    await renew(owner);
    await reportOne(owner, "the lantern", "killer", 8);
    expect((await adminRoster()).pending).toEqual([expect.objectContaining({ matches: 2 })]);
    expect((await signedCharacters()).some((c) => c.id === "the-lantern")).toBe(false);
  });

  it("needs the identical folded name: near spellings and the other role count separately", async () => {
    const owner = await reporter();
    await reportOne(owner, "the lantern", "killer", 1);
    await reportOne(owner, "the lanterm", "killer", 2);
    await reportOne(owner, "the lantern", "survivor", 3);
    h.now += DAY;
    await renew(owner);
    await reportOne(owner, "the lantern", "killer", 4);
    const pending = (await adminRoster()).pending;
    expect(pending).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ name: "the lantern", role: "killer", matches: 2 }),
        expect.objectContaining({ name: "the lanterm", role: "killer", matches: 1 }),
        expect.objectContaining({ name: "the lantern", role: "survivor", matches: 1 }),
      ]),
    );
    expect((await signedCharacters()).some((c) => c.id === "the-lantern")).toBe(false);
  });

  it("ignores names already in the roster, also when OCR confusions make them look different", async () => {
    const owner = await reporter();
    expect(await reportOne(owner, "the trapper", "killer", 1)).toMatchObject({ accepted: 0, ignored: { known: 1 } });
    expect(await reportOne(owner, "the iich", "killer", 2)).toMatchObject({ accepted: 0, ignored: { known: 1 } });
    expect(await reportOne(owner, "leon s kennedy", "survivor", 3)).toMatchObject({ accepted: 0, ignored: { known: 1 } });
    expect(await rowCount("roster_sightings")).toBe(0);
  });

  it("Undo removes the character, keeps it withdrawn in the signed roster and blocks the name until the owner allows it", async () => {
    const owner = await reporter();
    await learn(owner, "the lantern", "killer");
    const undo = await h.admin("POST", "/api/roster/characters/K45/withdraw", { withdrawn: true });
    expect(undo.status).toBe(200);
    expect((await signedCharacters()).find((c) => c.code === "K45")).toMatchObject({ id: "the-lantern", withdrawn: true });
    expect((await adminRoster()).blocked).toEqual([expect.objectContaining({ name: "the lantern" })]);
    for (let m = 10; m <= 13; m++) expect(await reportOne(owner, "the lantern", "killer", m)).toMatchObject({ accepted: 0, ignored: { blocked: 1 } });
    expect(await rowCount("roster_sightings")).toBe(0);

    expect((await h.admin("DELETE", "/api/roster/blocks/the-lantern")).status).toBe(200);
    expect((await h.admin("DELETE", "/api/roster/blocks/the-lantern")).status).toBe(404);
    const back = await learn(owner, "the lantern", "killer", 20);
    // Learned again: the same code and id come back (the app never forgets either).
    expect(back["added"]).toEqual([{ code: "K45", id: "the-lantern", role: "killer", displayName: "The Lantern", restored: true }]);
    expect((await signedCharacters()).find((c) => c.code === "K45")).toMatchObject({ withdrawn: false });
    expect(await rowCount("audit_log", "action IN ('roster.withdraw', 'roster.allow_name')")).toBe(2);
  });

  it("does not count reports less than an hour apart, and needs two days from the first to the third (M1c, L1)", async () => {
    const owner = await reporter();
    // Three reports within one session, however many invented match ids: only one counts.
    for (let m = 1; m <= 3; m++) {
      await reportOne(owner, "the lantern", "killer", m);
      h.now += MIN_SIGHTING_SPACING_MS - 1;
      await renew(owner);
    }
    h.now += DAY;
    await renew(owner);
    await reportOne(owner, "the lantern", "killer", 4);
    expect((await signedCharacters()).some((c) => c.id === "the-lantern")).toBe(false);
    // Hourly sightings reach three counted ones, but not yet two days from the first.
    h.now += MIN_SIGHTING_SPACING_MS;
    await renew(owner);
    expect(await reportOne(owner, "the lantern", "killer", 5)).toMatchObject({ added: [] });
    h.now += DAY;
    await renew(owner);
    expect((await reportOne(owner, "the lantern", "killer", 6))["added"]).toEqual([expect.objectContaining({ id: "the-lantern" })]);
  });

  it("adds at most MAX_AUTO_ADDS_PER_DAY characters a day; the rest stay pending until the next day (M1c)", async () => {
    const owner = await reporter();
    const names = ["the lantern", "the warden", "nova reyes", "ira blake"];
    const roles = ["killer", "killer", "survivor", "survivor"];
    const send = async (round: number) => {
      const response = await report(owner, names.map((name, i) => ({ name, role: roles[i], match: matchId(round * 10 + i) })));
      expect(response.status).toBe(200);
      return ((await response.json()) as { added: unknown[] }).added;
    };
    await send(1);
    h.now += DAY;
    await renew(owner);
    await send(2);
    h.now += DAY;
    await renew(owner);
    expect(await send(3)).toHaveLength(MAX_AUTO_ADDS_PER_DAY);
    expect((await adminRoster()).pending).toHaveLength(1);
    h.now += DAY;
    await renew(owner);
    expect(await send(4)).toHaveLength(1);
    expect((await adminRoster()).pending).toEqual([]);
  });

  it("limits the rows of one account, so it cannot fill the table for the other (I2), and the owner can clear what is pending", async () => {
    const owner = await reporter();
    const other = await reporter();
    await env.DB.batch(
      Array.from({ length: MAX_SIGHTINGS_PER_ACCOUNT }, (_, i) =>
        env.DB.prepare("INSERT INTO roster_sightings (folded, role, match_id, seen_at, user_id) VALUES (?, 'killer', ?, ?, ?)").bind(`filler ${i}`, matchId(i), h.now, owner.userId),
      ),
    );
    expect(await reportOne(owner, "the lantern", "killer", 1)).toMatchObject({ accepted: 0, ignored: { full: 1 } });
    expect(await reportOne(other, "the lantern", "killer", 1)).toMatchObject({ accepted: 1 });
    const cleared = await h.admin("DELETE", "/api/roster/sightings");
    expect(cleared.status).toBe(200);
    expect(await rowCount("roster_sightings")).toBe(0);
    expect(await rowCount("audit_log", "action = 'roster.clear_sightings'")).toBe(1);
    expect((await h.call("DELETE", `${ADMIN}/api/roster/sightings`)).status).toBe(401);
  });

  it("deletes an account's sightings with the account", async () => {
    const owner = await reporter();
    await reportOne(owner, "the lantern", "killer", 1);
    expect(await rowCount("roster_sightings")).toBe(1);
    await env.DB.prepare("DELETE FROM users WHERE id = ?").bind(owner.userId).run();
    expect(await rowCount("roster_sightings")).toBe(0);
  });

  it("a burned code is reused by renaming the removed character, then restoring it (L1)", async () => {
    const owner = await reporter();
    await learn(owner, "the lantern", "killer");
    expect((await h.admin("POST", "/api/roster/characters/K45/withdraw", { withdrawn: true })).status).toBe(200);
    const renamed = await h.admin("PUT", "/api/roster/characters/K45", { displayName: "The Beacon", screenName: "The Beacon" });
    expect(renamed.status).toBe(200);
    expect((await h.admin("POST", "/api/roster/characters/K45/withdraw", { withdrawn: false })).status).toBe(200);
    expect((await signedCharacters()).find((c) => c.code === "K45")).toMatchObject({ screenName: "The Beacon", withdrawn: false });
    expect((await adminRoster()).roster.nextCodes["killer"]).toBe("K46");
  });

  it("validates every sighting: folded charset, length, role, match id, batch size and unknown members", async () => {
    const owner = await reporter();
    const bad: unknown[] = [
      [{ name: "The Lantern", role: "killer", match: matchId(1) }],
      [{ name: "the  lantern", role: "killer", match: matchId(1) }],
      [{ name: " the lantern", role: "killer", match: matchId(1) }],
      [{ name: "x", role: "killer", match: matchId(1) }],
      [{ name: "a".repeat(41), role: "killer", match: matchId(1) }],
      [{ name: "the-lantern", role: "killer", match: matchId(1) }],
      [{ name: "lantérn", role: "killer", match: matchId(1) }],
      // Folded names that are no character's shape (round-2 review M1c): one word, a digit, too short, too many words.
      [{ name: "nightshade", role: "killer", match: matchId(1) }],
      [{ name: "zelda quillfeather 77", role: "survivor", match: matchId(1) }],
      [{ name: "a b c", role: "survivor", match: matchId(1) }],
      [{ name: "one two three four five six", role: "survivor", match: matchId(1) }],
      [{ name: "the lantern", role: "villain", match: matchId(1) }],
      [{ name: "the lantern", role: "killer", match: "1234" }],
      [{ name: "the lantern", role: "killer", match: matchId(1).toUpperCase().replace(/0/g, "A") }],
      [{ name: "the lantern", role: "killer", match: matchId(1), player: "Someone" }],
      [],
      Array.from({ length: MAX_SIGHTINGS_PER_REPORT + 1 }, (_, i) => ({ name: "the lantern", role: "killer", match: matchId(i + 1) })),
      "the lantern",
    ];
    // Spread over two days: refused reports are counted against the account's failure cap.
    for (const [i, sightings] of bad.entries()) {
      if (i > 0 && i % ACCOUNT_LIMITS["roster-report"].failures.cap === 0) {
        h.now += DAY;
        await renew(owner);
      }
      expect((await report(owner, sightings)).status, JSON.stringify(sightings)).toBe(400);
    }
    h.now += DAY;
    await renew(owner);
    expect((await report(owner, [{ name: "the lantern", role: "killer", match: matchId(1) }], { appVersion: "latest" })).status).toBe(400);
    expect((await report(owner, [{ name: "the lantern", role: "killer", match: matchId(1) }], { image: "x" })).status).toBe(400);
    expect(await rowCount("roster_sightings")).toBe(0);
  });

  it("is refused while online features are switched off", async () => {
    const owner = await reporter();
    await h.admin("PUT", "/api/settings", { emergencyStop: true });
    const response = await report(owner, [{ name: "the lantern", role: "killer", match: matchId(1) }]);
    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "online_features_off" });
  });

  it("caps reports per account and day (429 with Retry-After), and counts refused reports separately", async () => {
    const owner = await reporter();
    const cap = ACCOUNT_LIMITS["roster-report"].account.cap;
    for (let i = 0; i < cap; i++) expect((await report(owner, [{ name: "the lantern", role: "killer", match: matchId(100 + (i % 2)) }])).status).toBe(200);
    const limited = await report(owner, [{ name: "the lantern", role: "killer", match: matchId(100) }]);
    expect(limited.status).toBe(429);
    expect(Number(limited.headers.get("Retry-After"))).toBeGreaterThan(0);
    h.now += DAY;
    await renew(owner);
    expect((await report(owner, [{ name: "the lantern", role: "killer", match: matchId(100) }])).status).toBe(200);

    const friend = await account();
    for (let i = 0; i < ACCOUNT_LIMITS["roster-report"].failures.cap; i++) expect((await report(friend, [{ name: "the lantern", role: "killer", match: matchId(1) }])).status).toBe(403);
    expect((await report(friend, [{ name: "the lantern", role: "killer", match: matchId(1) }])).status).toBe(429);
  });

  it("stores at most MAX_SIGHTINGS rows", async () => {
    const owner = await reporter();
    await env.DB.batch(Array.from({ length: MAX_SIGHTINGS }, (_, i) => env.DB.prepare("INSERT INTO roster_sightings (folded, role, match_id, seen_at) VALUES (?, 'killer', ?, ?)").bind(`filler ${i}`, matchId(i), h.now)));
    expect(await reportOne(owner, "the lantern", "killer", 1)).toMatchObject({ accepted: 0, ignored: { full: 1 } });
    expect(await rowCount("roster_sightings")).toBe(MAX_SIGHTINGS);
  });

  it("bounds the rows a report writes, an adding report included, and the routes' daily writes", async () => {
    const owner = await reporter();
    const counter = { rows: 0 };
    const counted = countingDb(env.DB, counter);
    const send = (sightings: unknown) =>
      handleRequest(new Request(`${API}/v1/roster/sightings`, { method: "POST", headers: { ...owner.auth, "Content-Type": "application/json", "CF-Connecting-IP": h.ip }, body: JSON.stringify({ appVersion: "0.4.1", sightings }) }), { ...env, DB: counted }, h.deps);
    const names = ["the lantern", "the warden", "nova reyes"];
    const roles = ["killer", "killer", "survivor"];
    expect(names.length).toBe(MAX_AUTO_ADDS_PER_DAY);
    for (let m = 1; m <= 2; m++) {
      expect((await send(names.map((name, i) => ({ name, role: roles[i], match: matchId(m * 10 + i) })))).status).toBe(200);
      h.now += DAY;
      await renew(owner);
    }
    counter.rows = 0;
    const heavy = await send(names.map((name, i) => ({ name, role: roles[i], match: matchId(30 + i) })));
    expect(heavy.status).toBe(200);
    expect(((await heavy.json()) as { added: unknown[] }).added).toHaveLength(MAX_AUTO_ADDS_PER_DAY);
    expect(counter.rows).toBeLessThanOrEqual(ACCOUNT_LIMITS["roster-report"].rowsPerRequest);
    console.log(JSON.stringify({ measuredRowsWritten: { rosterReportAddingThree: counter.rows } }));
    // The account routes (uploads sized for a whole pack, FR-ONL-14) stay inside the half the sensitive routes leave.
    expect(accountRouteDailyWrites(50)).toBeLessThan(D1_DAILY_WRITE_LIMIT - SENSITIVE_WRITE_BUDGET);
  });
});

describe("Owner Panel routes (FR-ADM-12, FR-ROS-02)", () => {
  it("need Cloudflare Access and the owner list, like every admin route", async () => {
    for (const [method, path, body] of [
      ["GET", "/api/roster", undefined],
      ["POST", "/api/roster/characters", { code: "K45", displayName: "The Lantern" }],
      ["PUT", "/api/roster/characters/K45", { displayName: "The Lantern" }],
      ["POST", "/api/roster/characters/K45/withdraw", { withdrawn: true }],
      ["DELETE", "/api/roster/blocks/the-lantern", undefined],
    ] as const) {
      expect((await h.call(method, `${ADMIN}${path}`, { body })).status, path).toBe(401);
      expect((await h.admin(method, path, body, { "Cf-Access-Jwt-Assertion": await h.accessJwt({ email: "someone@example.com" }) })).status, path).toBe(403);
      if (method !== "GET") expect((await h.admin(method, path, body, { Origin: "https://evil.example" })).status, path).toBe(403);
    }
  });

  it("lists characters with their source, the next free codes, version and signing time", async () => {
    const view = await adminRoster();
    expect(view.roster.characters).toHaveLength(98);
    expect(view.roster.characters[0]).toMatchObject({ code: "K01", id: "the-trapper", source: "seed", withdrawn: false });
    expect(view.roster.nextCodes).toEqual({ killer: "K45", survivor: "S55" });
    expect(view.roster.stale).toBe(false);
    expect(view.reporters).toEqual([]);
  });

  it("adds a character by hand with all checks; a failed check stores nothing", async () => {
    const add = (body: Json) => h.admin("POST", "/api/roster/characters", body);
    const good = await add({ code: "K45", displayName: "The Lantern - Ada Voss", aliases: [{ name: "Lantern", seenOnScreen: "2026-11-02, owner screenshot" }], licensedHolder: "Example Studio" });
    expect(good.status).toBe(200);
    const character = (await signedCharacters()).find((c) => c.code === "K45");
    expect(character).toEqual({ code: "K45", id: "the-lantern", role: "killer", displayName: "The Lantern - Ada Voss", screenName: "The Lantern", aliases: [{ name: "Lantern", seenOnScreen: "2026-11-02, owner screenshot" }], licensed: { holder: "Example Studio" }, withdrawn: false });
    const versions = await rowCount("roster_versions");
    const refusals: [Json, number, string][] = [
      [{ code: "K45", displayName: "Another" }, 409, "code_exists"],
      [{ code: "S55", displayName: "The Trappér" }, 409, "name_collision"],
      [{ code: "S55", displayName: "Dwight Fairfie1d" }, 409, "name_collision"],
      [{ code: "S55", displayName: "Nova", id: "the-lantern" }, 409, "id_exists"],
      [{ code: "S55", displayName: "Nova", id: "generic" }, 400, "invalid_request"],
      [{ code: "S5", displayName: "Nova" }, 400, "invalid_request"],
      [{ code: "X55", displayName: "Nova" }, 400, "invalid_request"],
      [{ code: "S55", displayName: "Nova‮" }, 400, "invalid_name"],
      [{ code: "S55", displayName: "No<b>va" }, 400, "invalid_name"],
      [{ code: "S55", displayName: "" }, 400, "invalid_name"],
      [{ code: "S55", displayName: "Nova", aliases: [{ name: "Nova R" }] }, 400, "invalid_alias"],
      [{ code: "S55", displayName: "Nova", aliases: [{ name: "Nova R", seenOnScreen: "" }] }, 400, "invalid_alias"],
      [{ code: "S55", displayName: "Nova", role: "survivor" }, 400, "invalid_request"],
    ];
    for (const [body, status, error] of refusals) {
      const response = await add(body);
      expect(response.status, JSON.stringify(body)).toBe(status);
      expect(((await response.json()) as Json)["error"]).toBe(error);
    }
    expect(await rowCount("roster_versions")).toBe(versions);
    expect(await rowCount("roster_characters")).toBe(99);
    expect(await rowCount("audit_log", "action = 'roster.add'")).toBe(1);
  });

  it("renames a learned character; seed characters cannot be renamed or removed", async () => {
    const owner = await reporter();
    await learn(owner, "the lantern", "killer");
    const renamed = await h.admin("PUT", "/api/roster/characters/K45", { displayName: "The Lantern – Ada Voss", screenName: "The Lantern" });
    expect(renamed.status).toBe(200);
    expect((await signedCharacters()).find((c) => c.code === "K45")).toMatchObject({ id: "the-lantern", displayName: "The Lantern – Ada Voss", screenName: "The Lantern" });
    expect((await h.admin("PUT", "/api/roster/characters/K45", { displayName: "The Trapper" })).status).toBe(409);
    for (const [method, path, body] of [
      ["PUT", "/api/roster/characters/K01", { displayName: "Trapper" }],
      ["POST", "/api/roster/characters/K01/withdraw", { withdrawn: true }],
    ] as const) {
      const response = await h.admin(method, path, body);
      expect(response.status).toBe(409);
      expect(((await response.json()) as Json)["error"]).toBe("seed_character");
    }
    expect((await h.admin("PUT", "/api/roster/characters/K77", { displayName: "Nobody" })).status).toBe(404);
  });

  it("marks at most two accounts as the owner's game accounts, only active ones", async () => {
    const accounts = [await account(), await account(), await account()];
    for (const who of accounts.slice(0, MAX_ROSTER_REPORTERS)) expect((await h.admin("PUT", `/api/users/${who.userId}/roster-reporter`, { enabled: true })).status).toBe(200);
    const third = await h.admin("PUT", `/api/users/${accounts[2]!.userId}/roster-reporter`, { enabled: true });
    expect(third.status).toBe(409);
    expect(((await third.json()) as Json)["error"]).toBe("reporter_limit");
    expect((await adminRoster()).reporters).toHaveLength(2);
    expect((await h.admin("PUT", `/api/users/${accounts[0]!.userId}/roster-reporter`, { enabled: false })).status).toBe(200);
    expect((await h.admin("POST", `/api/users/${accounts[2]!.userId}/revoke`, {})).status).toBe(200);
    expect((await h.admin("PUT", `/api/users/${accounts[2]!.userId}/roster-reporter`, { enabled: true })).status).toBe(409);
    const users = ((await (await h.admin("GET", "/api/users")).json()) as { users: { id: string; rosterReporter: boolean }[] }).users;
    expect(users.filter((u) => u.rosterReporter).map((u) => u.id)).toEqual([accounts[1]!.userId]);
    expect(await rowCount("audit_log", "action = 'user.roster_reporter'")).toBe(3);
  });
});

describe("the default pack (S5b-3, FR-PACK-11)", () => {
  async function pack(id: string): Promise<void> {
    expect((await h.admin("POST", "/api/packs", { id, name: `Pack ${id}`, author: "The owner", licenceNote: "Made for DBD Presence." })).status).toBe(200);
  }

  it("allows one default pack: a second is refused, and clearing the mark frees it", async () => {
    await pack("p1");
    await pack("p2");
    expect((await h.admin("PUT", "/api/packs/p1/default", { default: true })).status).toBe(200);
    const second = await h.admin("PUT", "/api/packs/p2/default", { default: true });
    expect(second.status).toBe(409);
    expect(((await second.json()) as Json)["error"]).toBe("default_exists");
    expect((await h.admin("PUT", "/api/packs/p1/default", { default: false })).status).toBe(200);
    expect((await h.admin("PUT", "/api/packs/p2/default", { default: true })).status).toBe(200);
    const listed = ((await (await h.admin("GET", "/api/packs")).json()) as { packs: { id: string; default: boolean }[] }).packs;
    expect(listed.map((p) => [p.id, p.default])).toEqual([["p1", false], ["p2", true]]);
    expect(await rowCount("audit_log", "action = 'pack.default'")).toBe(3);
  });
});

// Wraps D1 so every row written through it is counted (meta.rows_written of each run and batch result).
const UNWRAP = Symbol("unwrap");
function countingDb(db: D1Database, counter: { rows: number }): D1Database {
  const wrapStatement = (statement: D1PreparedStatement): D1PreparedStatement =>
    new Proxy(statement, {
      get(target, property) {
        if (property === UNWRAP) return target;
        if (property === "bind") return (...values: unknown[]) => wrapStatement(target.bind(...values));
        if (property === "run" || property === "all" || property === "first") {
          return async (...args: unknown[]) => {
            if (property === "first") return (target.first as (...a: unknown[]) => Promise<unknown>).apply(target, args);
            const result = await (target[property] as () => Promise<D1Result>).call(target);
            counter.rows += result.meta.rows_written ?? 0;
            return result;
          };
        }
        const value = Reflect.get(target, property, target) as unknown;
        return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
      },
    });
  return new Proxy(db, {
    get(target, property) {
      if (property === "prepare") return (query: string) => wrapStatement(target.prepare(query));
      if (property === "batch") {
        return async (statements: D1PreparedStatement[]) => {
          const results = await target.batch(statements.map((s) => (s as unknown as Record<symbol, D1PreparedStatement>)[UNWRAP] ?? s));
          for (const r of results) counter.rows += r.meta.rows_written ?? 0;
          return results;
        };
      }
      const value = Reflect.get(target, property, target) as unknown;
      return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
    },
  });
}
