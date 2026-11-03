// The character roster in D1 (migrations/0005_roster_and_default_pack.sql; PLAN_V2_S5B §2.2–2.3, owner decision D35).
// Every content change is one D1 batch: the change itself, the next roster_versions row and the audit row.
// roster_versions.prev_version is UNIQUE, so two changes made against the same version cannot both commit; the loser
// re-reads the roster and plans its change again. Signing happens when the document is served (src/routes/roster.ts),
// always with the stored signed_at as issuedAt; only a content change or the weekly re-sign moves signed_at.

import { writeAudited, type AuditEntry } from "./audit.ts";
import { HttpError } from "../http.ts";
import { nextVersion } from "../packs/model.ts";
import { skeleton, skeletonOfFolded } from "../roster/fold.ts";
import { keepsEveryCharacter, rosterPayload, rosterProblem, spellings, type RosterAlias, type RosterCharacter, type RosterRole, type RosterSource } from "../roster/model.ts";

const DAY_MS = 24 * 60 * 60 * 1000;
/** The cron re-signs the unchanged roster once its signature is this old (the weekly re-sign). */
export const RESIGN_AFTER_MS = 7 * DAY_MS;
/** The Owner Panel warns when the roster has not been signed for this long (the cron is failing; it expires at 30 days). */
export const STALE_AFTER_MS = 25 * DAY_MS;
/** A name must be seen in this many separate matches before it is added (D35). */
export const MATCHES_TO_ADD = 3;
/** Sightings are kept this long; a name seen less often than that is forgotten. */
export const SIGHTING_RETENTION_MS = 30 * DAY_MS;
/** At most this many sighting rows are stored (D18: rows stay few); further new sightings are dropped until old ones expire. */
export const MAX_SIGHTINGS = 500;
/** At most this many of those rows come from one account, so one account cannot use up the room of the other (round-2 review I2). */
export const MAX_SIGHTINGS_PER_ACCOUNT = 250;
/** Sightings count only when their reports were at least this far apart (round-2 review L1: one burst of invented ids is one sighting). */
export const MIN_SIGHTING_SPACING_MS = 60 * 60 * 1000;
/** The third counted sighting of a name must be at least this long after the first (round-2 review M1c: a name is learned over days, not within one session). */
export const MIN_SIGHTING_SPAN_MS = 2 * DAY_MS;
/** At most this many characters are added automatically in a day (round-2 review M1c). */
export const MAX_AUTO_ADDS_PER_DAY = 3;
const VERSION_HISTORY = 3;
const MAX_ATTEMPTS = 5;

export interface RosterRow {
  code: string;
  id: string;
  role: RosterRole;
  display_name: string;
  screen_name: string;
  aliases: string;
  licensed_holder: string | null;
  source: RosterSource;
  added_at: number;
  withdrawn_at: number | null;
}

export interface RosterHead {
  version: number;
  payload: string;
  created_at: number;
  signed_at: number;
}

function aliasesOf(text: string): RosterAlias[] {
  const parsed = JSON.parse(text) as { name: string; seenOnScreen: string }[];
  return parsed.map((a) => ({ name: a.name, seenOnScreen: a.seenOnScreen }));
}

export function characterFromRow(row: RosterRow): RosterCharacter {
  return {
    code: row.code,
    id: row.id,
    role: row.role,
    displayName: row.display_name,
    screenName: row.screen_name,
    aliases: aliasesOf(row.aliases),
    licensedHolder: row.licensed_holder,
    source: row.source,
    addedAt: row.added_at,
    withdrawnAt: row.withdrawn_at,
  };
}

const HEAD_SELECT = "SELECT version, payload, created_at, signed_at FROM roster_versions ORDER BY version DESC LIMIT 1";

/** Every character, by code, and the newest signed version (null before the first). */
export async function readRoster(db: D1Database): Promise<{ characters: RosterCharacter[]; head: RosterHead | null }> {
  const [rows, head] = await db.batch([db.prepare("SELECT * FROM roster_characters ORDER BY code"), db.prepare(HEAD_SELECT)]);
  return { characters: (rows!.results as RosterRow[]).map(characterFromRow), head: (head!.results[0] as RosterHead | undefined) ?? null };
}

export async function latestRoster(db: D1Database): Promise<RosterHead | null> {
  return db.prepare(HEAD_SELECT).first<RosterHead>();
}

export interface PlannedRosterChange {
  /** The whole roster after the change. */
  after: RosterCharacter[];
  /** The statements that make the change, run in the same transaction as the new version row. */
  statements: D1PreparedStatement[];
  audit: AuditEntry;
}

/** Plans a change from the roster as it is now; null when there is nothing to change. Called again after a conflict. */
export type RosterPlan = (characters: RosterCharacter[], head: RosterHead | null) => PlannedRosterChange | null;

function isRosterConflict(error: unknown): boolean {
  return error instanceof Error && /UNIQUE constraint failed: roster_(versions|characters)\./.test(error.message);
}

/**
 * Commits a planned change together with the next roster version (signed from now). Refuses (409 `roster_invalid`) a
 * roster the app would refuse, and one that drops or changes the id or role of a character it had. Returns the new
 * version, or null when the plan found nothing to change.
 */
export async function commitRosterChange(db: D1Database, now: number, plan: RosterPlan): Promise<{ version: number; characters: RosterCharacter[] } | null> {
  for (let attempt = 1; ; attempt++) {
    const { characters, head } = await readRoster(db);
    const planned = plan(characters, head);
    if (planned === null) return null;
    if (!keepsEveryCharacter(characters, planned.after) || rosterProblem(planned.after) !== null) throw new HttpError(409, "roster_invalid");
    const previous = head?.version ?? 0;
    const version = nextVersion(previous, now);
    const statements = [
      ...planned.statements,
      db
        .prepare("INSERT INTO roster_versions (version, prev_version, payload, created_at, signed_at) VALUES (?, ?, ?, ?, ?)")
        .bind(version, previous, JSON.stringify(rosterPayload(planned.after)), now, now),
      db.prepare("DELETE FROM roster_versions WHERE version NOT IN (SELECT version FROM roster_versions ORDER BY version DESC LIMIT ?)").bind(VERSION_HISTORY),
    ];
    const audit = { ...planned.audit, after: { ...(planned.audit.after as Record<string, unknown> | undefined), rosterVersion: version } };
    try {
      await writeAudited(db, now, audit, statements);
      return { version, characters: planned.after };
    } catch (error) {
      if (attempt < MAX_ATTEMPTS && isRosterConflict(error)) continue;
      throw error;
    }
  }
}

/** The newest signed version; the first one is made from the seeded characters when there is none yet. */
export async function ensureRoster(db: D1Database, now: number): Promise<RosterHead> {
  const existing = await latestRoster(db);
  if (existing) return existing;
  await commitRosterChange(db, now, (characters, head) =>
    head !== null ? null : { after: characters, statements: [], audit: { actor: "system", action: "roster.initial", target: null, after: { characters: characters.length } } },
  );
  const head = await latestRoster(db);
  if (!head) throw new Error("the roster has no version after it was created");
  return head;
}

/**
 * The weekly re-sign (PLAN_V2_S5B §2.3), run by the daily cron: the unchanged roster gets a new signing time once its
 * signature is RESIGN_AFTER_MS old, so the document never expires while the service runs. Also forgets old sightings.
 */
export async function runRosterMaintenance(db: D1Database, now: number): Promise<{ resigned: boolean; version: number; sightingsDeleted: number }> {
  const head = await ensureRoster(db, now);
  const swept = await db.prepare("DELETE FROM roster_sightings WHERE seen_at < ?").bind(now - SIGHTING_RETENTION_MS).run();
  let resigned = false;
  if (now - head.signed_at >= RESIGN_AFTER_MS) {
    const [result] = await writeAudited(
      db,
      now,
      { actor: "system", action: "roster.resign", target: null, before: { version: head.version, signedAt: new Date(head.signed_at).toISOString() }, after: { version: head.version, signedAt: new Date(now).toISOString() } },
      [db.prepare("UPDATE roster_versions SET signed_at = ? WHERE version = ? AND signed_at = ?").bind(now, head.version, head.signed_at)],
      { sql: "EXISTS (SELECT 1 FROM roster_versions WHERE version = ? AND signed_at = ?)", binds: [head.version, now] },
    );
    resigned = result!.meta.changes === 1;
  }
  return { resigned, version: head.version, sightingsDeleted: swept.meta.changes };
}

/**
 * The sightings of one name that count: only those from reports at least MIN_SIGHTING_SPACING_MS apart, counted from the
 * oldest. A burst of invented match ids in one report, or several reports within the hour, count once.
 */
export function countedSightings(seenAt: readonly number[]): number[] {
  const counted: number[] = [];
  for (const at of [...seenAt].sort((a, b) => a - b)) {
    if (counted.length === 0 || at - counted[counted.length - 1]! >= MIN_SIGHTING_SPACING_MS) counted.push(at);
  }
  return counted;
}

/** True when the counted sightings are enough to add the name: at least MATCHES_TO_ADD of them, the newest at least MIN_SIGHTING_SPAN_MS after the first. */
export function readyToAdd(seenAt: readonly number[]): boolean {
  const counted = countedSightings(seenAt);
  return counted.length >= MATCHES_TO_ADD && counted[counted.length - 1]! - counted[0]! >= MIN_SIGHTING_SPAN_MS;
}

/** The skeletons of every spelling of every character that is not withdrawn: a sighting with one of these is a known name. */
export function knownSkeletons(characters: readonly RosterCharacter[]): Set<string> {
  const known = new Set<string>();
  for (const c of characters) if (c.withdrawnAt === null) for (const s of spellings(c)) known.add(skeleton(s)!);
  return known;
}

/** True when the folded name is a spelling of a character that is not withdrawn (compared as the app's vocabulary does). */
export function isKnownFolded(characters: readonly RosterCharacter[], folded: string): boolean {
  return knownSkeletons(characters).has(skeletonOfFolded(folded));
}

export function insertCharacterStatement(db: D1Database, c: RosterCharacter): D1PreparedStatement {
  return db
    .prepare(
      "INSERT INTO roster_characters (code, id, role, display_name, screen_name, aliases, licensed_holder, source, added_at, withdrawn_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
    )
    .bind(c.code, c.id, c.role, c.displayName, c.screenName, JSON.stringify(c.aliases), c.licensedHolder, c.source, c.addedAt, c.withdrawnAt);
}
