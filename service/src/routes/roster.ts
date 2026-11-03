// The character roster (PLAN_V2_S5B §2.2–2.3 and §2.8, S5b-2 and S5b-7; owner decision D35, which replaces the S5b plan's
// source of new characters: no website is read).
//
// GET /v1/roster?channel=stable (public): the signed roster, `{ "schemaVersion": 1, "document": "…" }`, signed with the
// content key (purpose `roster`, 30 days) from the stored signing time, cached for 60 seconds like the pack index.
//
// POST /v1/roster/sightings (session): the owner's app reports character names it read on screen in a character-name
// position that are not in its vocabulary: `{ "appVersion", "sightings": [{ "name", "role", "match" }] }`, at most 4 per
// request. `name` is the folded name (src/roster/fold.ts), `role` killer or survivor, `match` a random 32-hex id per
// match. Nothing else is sent: no player names, no images. Only an account the owner marked in the panel may report
// (403 `not_roster_reporter` for every other account, D35: friends' apps never report). A name that is known, or that the
// owner removed (blocked), is ignored. Once the identical folded name with the same role has been seen in 3 separate
// matches, it is added to the roster and so to the signed roster for everyone ("Added automatically" in the panel).
//
// Admin (owner; src/routes/admin.ts protections, every change audited): GET /api/roster; POST /api/roster/characters
// (add by hand); PUT /api/roster/characters/{code} (rename); POST /api/roster/characters/{code}/withdraw (remove or
// restore; removing an automatically added character is the D35 Undo and blocks its name); DELETE
// /api/roster/blocks/{name} (allow a removed name to be learned again); PUT /api/users/{id}/roster-reporter.
// Seed characters cannot be renamed or removed: the app embeds them, and the signed roster can never take them away.

import { requireSession } from "../auth/session.ts";
import { rosterCacheKey } from "../config-cache.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import {
  MATCHES_TO_ADD,
  MAX_AUTO_ADDS_PER_DAY,
  MAX_SIGHTINGS,
  MAX_SIGHTINGS_PER_ACCOUNT,
  STALE_AFTER_MS,
  commitRosterChange,
  ensureRoster,
  insertCharacterStatement,
  countedSightings,
  knownSkeletons,
  readRoster,
  readyToAdd,
  type RosterHead,
} from "../db/roster.ts";
import { VERSION_PATTERN, getSettings } from "../db/settings.ts";
import { HttpError, SCHEMA_VERSION, json, readJsonBody } from "../http.ts";
import { boolean, object, oneOf, string, uuid } from "../lib/validate.ts";
import { FOLDED_NAME_PATTERN, foldName, skeleton, skeletonOfFolded } from "../roster/fold.ts";
import {
  CODE_PATTERN,
  ID_PATTERN,
  MAX_ALIASES,
  MAX_CHARACTERS,
  MAX_HOLDER_LENGTH,
  MAX_SEEN_ON_SCREEN_LENGTH,
  RESERVED_IDS,
  ROSTER_ROLES,
  freeId,
  isCharacterShaped,
  isValidName,
  isValidNote,
  nextCode,
  roleOfCode,
  screenNameFrom,
  slugFromFolded,
  spellings,
  titleCase,
  type RosterAlias,
  type RosterCharacter,
  type RosterRole,
} from "../roster/model.ts";
import { MAX_LIFETIME_MS, signDocument } from "../signing/document.ts";
import { loadSigningKey } from "../signing/keys.ts";
import { requireAdmin } from "./admin.ts";
import { countingFailures, limiterFor, revokedResponse } from "./common.ts";

export const ROSTER_CHANNEL = "stable";
/** Clients re-check every 6 hours; intermediaries may keep the document for 5 minutes. */
export const ROSTER_CACHE_CONTROL = "public, max-age=300";
/** Most sightings in one report; the app batches what it saw. */
export const MAX_SIGHTINGS_PER_REPORT = 4;
/** Most accounts the owner may mark as their own game accounts. */
export const MAX_ROSTER_REPORTERS = 2;
const MATCH_ID = /^[0-9a-f]{32}$/;
const DAY_MS = 24 * 60 * 60 * 1000;
const BLOCK_PARAM = /^(?=.{2,40}$)[a-z0-9]+(-[a-z0-9]+)*$/;

const iso = (ms: number | null): string | null => (ms === null ? null : new Date(ms).toISOString());

async function clearRosterCache(ctx: Context): Promise<void> {
  await ctx.deps.configCache.clear(rosterCacheKey(ctx.env.API_HOST, ROSTER_CHANNEL));
}

// ---------------------------------------------------------------------------------------------------------------
// GET /v1/roster?channel=stable

export async function getRoster(ctx: Context): Promise<Response> {
  const channel = ctx.url.searchParams.get("channel") ?? ROSTER_CHANNEL;
  if (channel !== ROSTER_CHANNEL) throw new HttpError(400, "unknown_channel");
  // A missing or broken key is reported even while a document signed earlier is still cached.
  const key = await loadSigningKey(ctx.env.CONTENT_SIGNING_KEY);
  const cacheKey = rosterCacheKey(ctx.env.API_HOST, channel);
  const respond = (body: string) => new Response(body, { status: 200, headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": ROSTER_CACHE_CONTROL } });
  const cached = await ctx.deps.configCache.get(cacheKey, ctx.now);
  if (cached !== null) return respond(cached);

  const head = await ensureRoster(ctx.env.DB, ctx.now);
  const document = await signDocument("roster", channel, head.version, JSON.parse(head.payload) as unknown, key.key, key.kid, {
    issuedAt: new Date(head.signed_at),
    expiresAt: new Date(head.signed_at + MAX_LIFETIME_MS.roster),
  });
  const body = JSON.stringify({ schemaVersion: SCHEMA_VERSION, document });
  await ctx.deps.configCache.put(cacheKey, body, ctx.now);
  return respond(body);
}

// ---------------------------------------------------------------------------------------------------------------
// POST /v1/roster/sightings

interface Sighting {
  name: string;
  role: RosterRole;
  match: string;
}

function readReport(value: unknown): { appVersion: string; sightings: Sighting[] } {
  const body = object(value, ["appVersion", "sightings"]);
  const appVersion = string(body["appVersion"], { max: 17, pattern: VERSION_PATTERN });
  const list = body["sightings"];
  if (!Array.isArray(list) || list.length < 1 || list.length > MAX_SIGHTINGS_PER_REPORT) throw new HttpError(400, "invalid_request");
  const unique = new Map<string, Sighting>();
  for (const item of list) {
    const s = object(item, ["name", "role", "match"]);
    const sighting: Sighting = {
      name: string(s["name"], { min: 2, max: 40, pattern: FOLDED_NAME_PATTERN }),
      role: oneOf(s["role"], ROSTER_ROLES),
      match: string(s["match"], { min: 32, max: 32, pattern: MATCH_ID }),
    };
    // The app only reports names of a character's shape; a name with a digit or a single word is a player's, not a character's.
    if (!isCharacterShaped(sighting.name)) throw new HttpError(400, "invalid_request");
    unique.set(`${sighting.name}|${sighting.role}|${sighting.match}`, sighting);
  }
  return { appVersion, sightings: [...unique.values()] };
}

interface AddedCharacter {
  code: string;
  id: string;
  role: RosterRole;
  displayName: string;
  restored: boolean;
}

/** Adds (or restores) the characters whose folded name has now been seen in MATCHES_TO_ADD separate matches. */
async function promote(ctx: Context, userId: string, appVersion: string, candidates: { folded: string; role: RosterRole }[]): Promise<AddedCharacter[]> {
  const db = ctx.env.DB;
  let added: AddedCharacter[] = [];
  const committed = await commitRosterChange(db, ctx.now, (characters) => {
    added = [];
    const after = [...characters];
    const statements: D1PreparedStatement[] = [];
    const known = knownSkeletons(characters);
    const everySkeleton = new Set(after.flatMap((c) => spellings(c).map((s) => skeleton(s)!)));
    // At most MAX_AUTO_ADDS_PER_DAY characters a day, counting those added in the last 24 hours; the rest stay pending.
    let budget = MAX_AUTO_ADDS_PER_DAY - characters.filter((c) => c.source === "auto" && c.addedAt > ctx.now - DAY_MS).length;
    for (const { folded, role } of candidates) {
      if (budget <= 0) break;
      const key = skeletonOfFolded(folded);
      if (known.has(key)) continue;
      const removed = after.findIndex((c) => c.withdrawnAt !== null && c.role === role && foldName(c.screenName) === folded);
      if (removed >= 0) {
        // The same name was learned before and removed by hand (not blocked any more): it comes back with its code and id.
        const restored: RosterCharacter = { ...after[removed]!, withdrawnAt: null };
        after[removed] = restored;
        statements.push(db.prepare("UPDATE roster_characters SET withdrawn_at = NULL WHERE code = ?").bind(restored.code));
        added.push({ code: restored.code, id: restored.id, role, displayName: restored.displayName, restored: true });
      } else {
        if (everySkeleton.has(key) || after.length >= MAX_CHARACTERS) continue;
        const code = nextCode(after, role);
        const id = freeId(slugFromFolded(folded), new Set(after.map((c) => c.id)));
        const name = titleCase(folded);
        if (code === null || id === null || !isValidName(name)) continue;
        const character: RosterCharacter = { code, id, role, displayName: name, screenName: name, aliases: [], licensedHolder: null, source: "auto", addedAt: ctx.now, withdrawnAt: null };
        after.push(character);
        statements.push(insertCharacterStatement(db, character));
        added.push({ code, id, role, displayName: name, restored: false });
      }
      budget--;
      known.add(key);
      everySkeleton.add(key);
      statements.push(db.prepare("DELETE FROM roster_sightings WHERE folded = ?").bind(folded));
    }
    if (added.length === 0) return null;
    return {
      after,
      statements,
      audit: { actor: `user:${userId}`, action: "roster.auto_add", target: null, after: { characters: added.map((a) => ({ code: a.code, id: a.id, role: a.role, restored: a.restored })), appVersion } },
    };
  });
  return committed === null ? [] : added;
}

async function recordSightings(ctx: Context, userId: string, appVersion: string, sightings: Sighting[]): Promise<Record<string, unknown>> {
  const db = ctx.env.DB;
  const [{ characters }, blocks, stored, mine] = await Promise.all([
    readRoster(db),
    db.prepare("SELECT folded FROM roster_blocks").all<{ folded: string }>(),
    db.prepare("SELECT COUNT(*) AS n FROM roster_sightings").first<{ n: number }>(),
    db.prepare("SELECT COUNT(*) AS n FROM roster_sightings WHERE user_id = ?").bind(userId).first<{ n: number }>(),
  ]);
  const known = knownSkeletons(characters);
  const blocked = new Set(blocks.results.map((b) => b.folded));
  let room = Math.min(MAX_SIGHTINGS - (stored?.n ?? 0), MAX_SIGHTINGS_PER_ACCOUNT - (mine?.n ?? 0));
  const ignored = { known: 0, blocked: 0, full: 0 };
  const accepted: Sighting[] = [];
  for (const s of sightings) {
    if (known.has(skeletonOfFolded(s.name))) ignored.known++;
    else if (blocked.has(s.name)) ignored.blocked++;
    else if (room <= 0) ignored.full++;
    else {
      accepted.push(s);
      room--;
    }
  }
  if (accepted.length === 0) return { accepted: 0, ignored, added: [] };

  await db.batch(
    accepted.map((s) => db.prepare("INSERT OR IGNORE INTO roster_sightings (folded, role, match_id, seen_at, user_id) VALUES (?, ?, ?, ?, ?)").bind(s.name, s.role, s.match, ctx.now, userId)),
  );
  const names = [...new Set(accepted.map((s) => s.name))];
  const { results: seen } = await db
    .prepare(`SELECT folded, role, seen_at FROM roster_sightings WHERE folded IN (${names.map(() => "?").join(", ")})`)
    .bind(...names)
    .all<{ folded: string; role: RosterRole; seen_at: number }>();
  const candidates: { folded: string; role: RosterRole }[] = [];
  for (const folded of names) {
    // Only sightings from reports at least an hour apart count, and the third must come two days after the first (D35, round-2 review M1c).
    const ready = ROSTER_ROLES.map((role) => ({ role, times: seen.filter((r) => r.folded === folded && r.role === role).map((r) => r.seen_at) }))
      .filter((r) => readyToAdd(r.times))
      .map((r) => ({ role: r.role, matches: countedSightings(r.times).length }))
      .sort((a, b) => b.matches - a.matches);
    // The same name seen as a killer and as a survivor equally often is left until the counts tell them apart.
    if (ready.length === 1 || (ready.length > 1 && ready[0]!.matches > ready[1]!.matches)) candidates.push({ folded, role: ready[0]!.role });
  }
  const added = candidates.length > 0 ? await promote(ctx, userId, appVersion, candidates) : [];
  if (added.length > 0) await clearRosterCache(ctx);
  return { accepted: accepted.length, ignored, added };
}

export async function reportSightings(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  const limiter = limiterFor(ctx);
  // A verified principal: refused now (read only) at its daily cap of reports or of refused ones.
  await limiter.checkAccount("roster-report", session.userId);
  return countingFailures(limiter, "roster-report", session.userId, async () => {
    if (session.status === "revoked") return revokedResponse(ctx, session.userId, session.thumbprint);
    if (!session.hasAccess) throw new HttpError(403, "invite_required");
    const db = ctx.env.DB;
    const [settings, user] = await Promise.all([getSettings(db), db.prepare("SELECT roster_reporter FROM users WHERE id = ?").bind(session.userId).first<{ roster_reporter: number }>()]);
    if (!user) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
    if (settings.emergencyStop) throw new HttpError(403, "online_features_off");
    if (user.roster_reporter !== 1) throw new HttpError(403, "not_roster_reporter");
    const report = readReport(await readJsonBody(ctx.request, 2048));
    await limiter.verified("roster-report", session.userId);
    return json(await recordSightings(ctx, session.userId, report.appVersion, report.sightings));
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Admin views

function characterView(c: RosterCharacter): Record<string, unknown> {
  return {
    code: c.code,
    id: c.id,
    role: c.role,
    displayName: c.displayName,
    screenName: c.screenName,
    aliases: c.aliases,
    licensedHolder: c.licensedHolder,
    source: c.source,
    addedAt: c.source === "seed" ? null : iso(c.addedAt),
    withdrawn: c.withdrawnAt !== null,
    withdrawnAt: iso(c.withdrawnAt),
  };
}

async function rosterBody(ctx: Context, extra: Record<string, unknown> = {}): Promise<Response> {
  const db = ctx.env.DB;
  const head: RosterHead = await ensureRoster(db, ctx.now);
  const [{ characters }, pending, blocks, reporters] = await Promise.all([
    readRoster(db),
    db
      .prepare("SELECT folded, role, COUNT(*) AS matches, MIN(seen_at) AS first_seen, MAX(seen_at) AS last_seen FROM roster_sightings GROUP BY folded, role ORDER BY matches DESC, last_seen DESC LIMIT 100")
      .all<{ folded: string; role: RosterRole; matches: number; first_seen: number; last_seen: number }>(),
    db.prepare("SELECT folded, blocked_at FROM roster_blocks ORDER BY blocked_at DESC LIMIT 200").all<{ folded: string; blocked_at: number }>(),
    db.prepare("SELECT id, display_name FROM users WHERE roster_reporter = 1 ORDER BY created_at").all<{ id: string; display_name: string }>(),
  ]);
  return json({
    roster: {
      version: head.version,
      signedAt: iso(head.signed_at),
      expiresAt: iso(head.signed_at + MAX_LIFETIME_MS.roster),
      stale: ctx.now - head.signed_at > STALE_AFTER_MS,
      characters: characters.map(characterView),
      nextCodes: { killer: nextCode(characters, "killer"), survivor: nextCode(characters, "survivor") },
    },
    pending: pending.results.map((p) => ({ name: p.folded, role: p.role, matches: p.matches, firstSeenAt: iso(p.first_seen), lastSeenAt: iso(p.last_seen) })),
    blocked: blocks.results.map((b) => ({ name: b.folded, blockedAt: iso(b.blocked_at) })),
    reporters: reporters.results.map((r) => ({ id: r.id, displayName: r.display_name })),
    limits: { maxCharacters: MAX_CHARACTERS, matchesToAdd: MATCHES_TO_ADD, maxReporters: MAX_ROSTER_REPORTERS, maxAliases: MAX_ALIASES },
    ...extra,
  });
}

export async function getRosterAdmin(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  return rosterBody(ctx);
}

// ---------------------------------------------------------------------------------------------------------------
// Admin changes

function name(value: unknown): string {
  if (typeof value !== "string" || !isValidName(value)) throw new HttpError(400, "invalid_name");
  return value;
}

function aliases(value: unknown): RosterAlias[] {
  if (!Array.isArray(value) || value.length > MAX_ALIASES) throw new HttpError(400, "invalid_alias");
  return value.map((item) => {
    if (typeof item !== "object" || item === null || Array.isArray(item)) throw new HttpError(400, "invalid_alias");
    const a = item as Record<string, unknown>;
    if (Object.keys(a).some((k) => k !== "name" && k !== "seenOnScreen")) throw new HttpError(400, "invalid_alias");
    const seen = a["seenOnScreen"];
    if (typeof a["name"] !== "string" || !isValidName(a["name"]) || typeof seen !== "string" || !isValidNote(seen, MAX_SEEN_ON_SCREEN_LENGTH)) throw new HttpError(400, "invalid_alias");
    return { name: a["name"], seenOnScreen: seen };
  });
}

function holder(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== "string" || !isValidNote(value, MAX_HOLDER_LENGTH)) throw new HttpError(400, "invalid_request");
  return value;
}

function codeParam(ctx: Context): string {
  const code = ctx.params["id"];
  if (code === undefined || !CODE_PATTERN.test(code)) throw new HttpError(400, "invalid_request");
  return code;
}

/** 409 `name_collision` when one of `c`'s spellings folds, as the app's vocabulary compares names, onto another character's. */
function assertDistinct(c: RosterCharacter, others: readonly RosterCharacter[]): void {
  const taken = new Set(others.filter((o) => o.code !== c.code).flatMap((o) => spellings(o).map((s) => skeleton(s)!)));
  if (spellings(c).some((s) => taken.has(skeleton(s)!))) throw new HttpError(409, "name_collision");
}

function blockStatements(db: D1Database, folded: string, now: number): D1PreparedStatement[] {
  return [
    db.prepare("INSERT INTO roster_blocks (folded, blocked_at) VALUES (?, ?) ON CONFLICT (folded) DO NOTHING").bind(folded, now),
    db.prepare("DELETE FROM roster_sightings WHERE folded = ?").bind(folded),
  ];
}

export async function addCharacter(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const body = object(await readJsonBody(ctx.request, 4096), ["code", "displayName", "screenName", "id", "aliases", "licensedHolder"], ["code", "displayName"]);
  const code = string(body["code"], { min: 3, max: 3, pattern: CODE_PATTERN });
  const displayName = name(body["displayName"]);
  const screenName = body["screenName"] === undefined || body["screenName"] === null ? screenNameFrom(displayName) : name(body["screenName"]);
  const aliasList = body["aliases"] === undefined ? [] : aliases(body["aliases"]);
  const licensedHolder = body["licensedHolder"] === undefined ? null : holder(body["licensedHolder"]);
  const wantedId = body["id"] === undefined || body["id"] === null ? null : string(body["id"], { max: 64, pattern: ID_PATTERN });
  if (wantedId !== null && RESERVED_IDS.has(wantedId)) throw new HttpError(400, "invalid_request");
  const folded = foldName(screenName)!;
  const db = ctx.env.DB;
  await ensureRoster(db, ctx.now);
  await commitRosterChange(db, ctx.now, (characters) => {
    if (characters.some((c) => c.code === code)) throw new HttpError(409, "code_exists");
    if (characters.length >= MAX_CHARACTERS) throw new HttpError(409, "roster_full");
    const ids = new Set(characters.map((c) => c.id));
    if (wantedId !== null && ids.has(wantedId)) throw new HttpError(409, "id_exists");
    const id = wantedId ?? freeId(slugFromFolded(folded), ids);
    if (id === null) throw new HttpError(409, "id_exists");
    const character: RosterCharacter = { code, id, role: roleOfCode(code), displayName, screenName, aliases: aliasList, licensedHolder, source: "owner", addedAt: ctx.now, withdrawnAt: null };
    assertDistinct(character, characters);
    return {
      after: [...characters, character],
      // Adding a name by hand also clears an earlier Undo of it and its pending sightings.
      statements: [insertCharacterStatement(db, character), db.prepare("DELETE FROM roster_blocks WHERE folded = ?").bind(folded), db.prepare("DELETE FROM roster_sightings WHERE folded = ?").bind(folded)],
      audit: { actor, action: "roster.add", target: `roster:${code}`, after: { code, id, role: character.role, aliases: aliasList.length, licensed: licensedHolder !== null } },
    };
  });
  await clearRosterCache(ctx);
  return rosterBody(ctx);
}

export async function updateCharacter(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const code = codeParam(ctx);
  const body = object(await readJsonBody(ctx.request, 4096), ["displayName", "screenName", "aliases", "licensedHolder"], []);
  const db = ctx.env.DB;
  await ensureRoster(db, ctx.now);
  await commitRosterChange(db, ctx.now, (characters) => {
    const before = characters.find((c) => c.code === code);
    if (!before) throw new HttpError(404, "not_found");
    if (before.source === "seed") throw new HttpError(409, "seed_character");
    const after: RosterCharacter = {
      ...before,
      displayName: body["displayName"] === undefined ? before.displayName : name(body["displayName"]),
      screenName: body["screenName"] === undefined ? before.screenName : name(body["screenName"]),
      aliases: body["aliases"] === undefined ? before.aliases : aliases(body["aliases"]),
      licensedHolder: body["licensedHolder"] === undefined ? before.licensedHolder : holder(body["licensedHolder"]),
    };
    const fields = (["displayName", "screenName", "aliases", "licensedHolder"] as const).filter((k) => JSON.stringify(before[k]) !== JSON.stringify(after[k]));
    if (fields.length === 0) return null;
    assertDistinct(after, characters);
    return {
      after: characters.map((c) => (c.code === code ? after : c)),
      statements: [
        db
          .prepare("UPDATE roster_characters SET display_name = ?, screen_name = ?, aliases = ?, licensed_holder = ? WHERE code = ?")
          .bind(after.displayName, after.screenName, JSON.stringify(after.aliases), after.licensedHolder, code),
      ],
      audit: { actor, action: "roster.update", target: `roster:${code}`, after: { code, fields } },
    };
  });
  await clearRosterCache(ctx);
  return rosterBody(ctx);
}

/**
 * Removes (withdraws) or restores a character the owner added or the service learned. Removing also blocks its folded
 * screen name, so the owner's app cannot add it back on its own (the D35 Undo); restoring lifts that block.
 */
export async function withdrawCharacter(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const code = codeParam(ctx);
  const withdrawn = boolean(object(await readJsonBody(ctx.request, 256), ["withdrawn"])["withdrawn"]);
  const db = ctx.env.DB;
  await ensureRoster(db, ctx.now);
  await commitRosterChange(db, ctx.now, (characters) => {
    const before = characters.find((c) => c.code === code);
    if (!before) throw new HttpError(404, "not_found");
    if (before.source === "seed") throw new HttpError(409, "seed_character");
    if ((before.withdrawnAt !== null) === withdrawn) return null;
    const folded = foldName(before.screenName)!;
    const after: RosterCharacter = { ...before, withdrawnAt: withdrawn ? ctx.now : null };
    return {
      after: characters.map((c) => (c.code === code ? after : c)),
      statements: [
        db.prepare("UPDATE roster_characters SET withdrawn_at = ? WHERE code = ?").bind(after.withdrawnAt, code),
        ...(withdrawn ? blockStatements(db, folded, ctx.now) : [db.prepare("DELETE FROM roster_blocks WHERE folded = ?").bind(folded)]),
      ],
      audit: { actor, action: withdrawn ? "roster.withdraw" : "roster.restore", target: `roster:${code}`, after: { code, id: before.id, source: before.source, blocked: withdrawn } },
    };
  });
  await clearRosterCache(ctx);
  return rosterBody(ctx);
}

/** Allows a removed name to be learned from the game again (the block written by Undo goes). */
export async function allowName(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const param = ctx.params["id"];
  if (param === undefined || !BLOCK_PARAM.test(param)) throw new HttpError(400, "invalid_request");
  const folded = param.replaceAll("-", " ");
  const db = ctx.env.DB;
  if (!(await db.prepare("SELECT 1 FROM roster_blocks WHERE folded = ?").bind(folded).first())) throw new HttpError(404, "not_found");
  await writeAudited(db, ctx.now, { actor, action: "roster.allow_name", target: null, after: { name: folded } }, [db.prepare("DELETE FROM roster_blocks WHERE folded = ?").bind(folded)]);
  return rosterBody(ctx);
}

/** Forgets every pending sighting (the owner's "clear pending" in the panel); characters already added and blocks stay. */
export async function clearSightings(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const db = ctx.env.DB;
  const before = await db.prepare("SELECT COUNT(*) AS n FROM roster_sightings").first<{ n: number }>();
  await writeAudited(db, ctx.now, { actor, action: "roster.clear_sightings", target: null, after: { rows: before?.n ?? 0 } }, [db.prepare("DELETE FROM roster_sightings")]);
  return rosterBody(ctx);
}

/** Marks (or unmarks) an app account as the owner's own: only such accounts may report new characters (D35). */
export async function setRosterReporter(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const userId = uuid(ctx.params["id"]);
  const enabled = boolean(object(await readJsonBody(ctx.request, 256), ["enabled"])["enabled"]);
  const db = ctx.env.DB;
  const user = await db.prepare("SELECT status, roster_reporter FROM users WHERE id = ?").bind(userId).first<{ status: string; roster_reporter: number }>();
  if (!user) throw new HttpError(404, "not_found");
  if ((user.roster_reporter === 1) !== enabled) {
    if (enabled && user.status !== "active") throw new HttpError(409, "user_not_active");
    // Set only while the limit still holds, so two marks at once cannot pass it together.
    const [result] = await writeAudited(
      db,
      ctx.now,
      { actor, action: "user.roster_reporter", target: `user:${userId}`, before: { rosterReporter: !enabled }, after: { rosterReporter: enabled } },
      [
        enabled
          ? db.prepare("UPDATE users SET roster_reporter = 1 WHERE id = ? AND status = 'active' AND (SELECT COUNT(*) FROM users WHERE roster_reporter = 1) < ?").bind(userId, MAX_ROSTER_REPORTERS)
          : db.prepare("UPDATE users SET roster_reporter = 0 WHERE id = ?").bind(userId),
      ],
      { sql: "EXISTS (SELECT 1 FROM users WHERE id = ? AND roster_reporter = ?)", binds: [userId, enabled ? 1 : 0] },
    );
    if (result!.meta.changes !== 1) throw new HttpError(409, "reporter_limit");
  }
  return json({ user: { id: userId, rosterReporter: enabled } });
}
