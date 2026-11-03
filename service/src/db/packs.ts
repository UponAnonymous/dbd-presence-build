// Packs in D1 (migrations/0002_packs_and_config.sql) and their files in R2 (bucket PACKS). See src/packs/model.ts for
// the index payload. Every index change is one D1 batch: the change itself, the new pack_index row and the audit
// row. pack_index.prev_version is UNIQUE, so two changes made against the same index version cannot both commit; the
// loser re-reads the published state and tries again.

import { writeAudited, type AuditEntry } from "./audit.ts";
import { HttpError } from "../http.ts";
import { indexPayload, indexProblem, nextVersion, objectName, type EntryKind, type IndexPayload, type PublishedEntry, type PublishedPack } from "../packs/model.ts";
import { deleteReleased } from "../storage.ts";

export interface PackRow {
  id: string;
  name: string;
  author: string;
  licence_note: string;
  credit: string | null;
  fallback: number;
  behaviour_art: number;
  is_default: number;
  third_party: number;
  permission_creator: string | null;
  permission_date: string | null;
  permission_proof: string | null;
  status: "draft" | "published" | "unpublished";
  version: number;
  changed: number;
  created_at: number;
  updated_at: number;
  published_at: number | null;
}

/** A draft entry joined with its file's details. */
export interface EntryRow {
  id: string;
  pack_id: string;
  kind: EntryKind;
  key: string;
  sha256: string;
  licensed_holder: string | null;
  withdrawn: number;
  source_sha256: string | null;
  created_at: number;
  updated_at: number;
  format: "png" | "jpeg";
  size: number;
  width: number;
  height: number;
}

interface PublishedPackRow {
  pack_id: string;
  name: string;
  author: string;
  licence_note: string;
  credit: string | null;
  fallback: number;
  behaviour_art: number;
  is_default: number;
  version: number;
}

interface PublishedEntryRow {
  id: string;
  pack_id: string;
  kind: EntryKind;
  key: string;
  sha256: string;
  format: "png" | "jpeg";
  size: number;
  width: number;
  height: number;
  licensed_holder: string | null;
  withdrawn: number;
  source_sha256: string | null;
}

export interface IndexRow {
  version: number;
  payload: string;
  published_at: number;
}

export const ENTRY_SELECT =
  "SELECT e.id, e.pack_id, e.kind, e.key, e.sha256, e.licensed_holder, e.withdrawn, e.source_sha256, e.created_at, e.updated_at, o.format, o.size, o.width, o.height " +
  "FROM pack_entries e JOIN pack_objects o ON o.sha256 = e.sha256";

/** How many index versions are kept (the newest is served; older ones are history for the owner). */
const INDEX_HISTORY = 20;
const MAX_ATTEMPTS = 5;

export function publishedPack(row: PublishedPackRow): PublishedPack {
  return {
    id: row.pack_id,
    name: row.name,
    author: row.author,
    licenceNote: row.licence_note,
    credit: row.credit,
    fallback: row.fallback === 1,
    behaviourArt: row.behaviour_art === 1,
    default: row.is_default === 1,
    version: row.version,
  };
}

export function publishedEntry(row: PublishedEntryRow): PublishedEntry {
  return {
    id: row.id,
    pack: row.pack_id,
    kind: row.kind,
    key: row.key,
    sha256: row.sha256,
    format: row.format,
    size: row.size,
    width: row.width,
    height: row.height,
    licensedHolder: row.licensed_holder,
    withdrawn: row.withdrawn === 1,
    sourceSha256: row.source_sha256,
  };
}

export async function latestIndex(db: D1Database): Promise<IndexRow | null> {
  return db.prepare("SELECT version, payload, published_at FROM pack_index ORDER BY version DESC LIMIT 1").first<IndexRow>();
}

async function readPublished(db: D1Database): Promise<{ packs: PublishedPack[]; entries: PublishedEntry[]; version: number }> {
  const [packs, entries, head] = await db.batch([
    db.prepare("SELECT * FROM published_packs"),
    db.prepare("SELECT * FROM published_entries"),
    db.prepare("SELECT version FROM pack_index ORDER BY version DESC LIMIT 1"),
  ]);
  return {
    packs: (packs!.results as PublishedPackRow[]).map(publishedPack),
    entries: (entries!.results as PublishedEntryRow[]).map(publishedEntry),
    version: (head!.results[0] as { version: number } | undefined)?.version ?? 0,
  };
}

function isIndexConflict(error: unknown): boolean {
  return error instanceof Error && /UNIQUE constraint failed: pack_index\.(prev_version|version)/.test(error.message);
}

export interface IndexChange {
  /** The published set after the change, from the published set before it. */
  apply: (packs: PublishedPack[], entries: PublishedEntry[]) => { packs: PublishedPack[]; entries: PublishedEntry[] };
  /** The statements that make the change, run in the same transaction as the new index row. */
  statements: D1PreparedStatement[];
  audit: AuditEntry;
}

/**
 * Commits a change to the published set together with the next signed-index version. Refuses (409 `index_limits`)
 * a set that the app's parser would refuse. Returns the new version and payload.
 */
export async function commitIndexChange(db: D1Database, now: number, change: IndexChange): Promise<{ version: number; payload: IndexPayload }> {
  for (let attempt = 1; ; attempt++) {
    const before = await readPublished(db);
    const after = change.apply(before.packs, before.entries);
    const payload = indexPayload(after.packs, after.entries);
    if (indexProblem(payload) !== null) throw new HttpError(409, "index_limits");
    const version = nextVersion(before.version, now);
    const statements = [
      ...change.statements,
      db.prepare("INSERT INTO pack_index (version, prev_version, payload, published_at) VALUES (?, ?, ?, ?)").bind(version, before.version, JSON.stringify(payload), now),
      db.prepare("DELETE FROM pack_index WHERE version NOT IN (SELECT version FROM pack_index ORDER BY version DESC LIMIT ?)").bind(INDEX_HISTORY),
    ];
    const audit = { ...change.audit, after: { ...(change.audit.after as Record<string, unknown> | undefined), indexVersion: version } };
    try {
      await writeAudited(db, now, audit, statements);
      return { version, payload };
    } catch (error) {
      if (attempt < MAX_ATTEMPTS && isIndexConflict(error)) continue;
      throw error;
    }
  }
}

/**
 * Deletes the files among `shas` that nothing refers to any more: no draft entry and no published entry. The D1 row
 * goes first (conditionally, in one statement; its ledger row is marked released), then the R2 object with a retry, then
 * the ledger row (src/storage.ts deleteReleased). A failed R2 delete stays a counted tombstone for the owner's purge and the
 * daily sweep. Returns how many rows were deleted.
 */
export async function releaseObjects(db: D1Database, bucket: R2Bucket, shas: Iterable<string>): Promise<number> {
  let deleted = 0;
  for (const sha of new Set(shas)) {
    const row = await db
      .prepare(
        "DELETE FROM pack_objects WHERE sha256 = ? AND NOT EXISTS (SELECT 1 FROM pack_entries WHERE sha256 = ?) " +
          "AND NOT EXISTS (SELECT 1 FROM published_entries WHERE sha256 = ?) RETURNING format",
      )
      .bind(sha, sha, sha)
      .first<{ format: "png" | "jpeg" }>();
    if (!row) continue;
    await deleteReleased(db, "packs", bucket, [objectName(sha, row.format)]);
    deleted++;
  }
  return deleted;
}

/** Files uploaded at least this long ago and never used are removed by the owner's purge. */
export const PURGE_GRACE_MS = 60 * 60 * 1000;

/**
 * The owner-triggered purge: deletes unreferenced files older than PURGE_GRACE_MS, then any R2 object that has no
 * pack_objects row at all (left by an interrupted upload or delete; storage reservations of interrupted uploads are
 * counted with them). Returns the counts.
 */
export async function purgeObjects(db: D1Database, bucket: R2Bucket, now: number): Promise<{ unreferenced: number; orphaned: number }> {
  const { results } = await db
    .prepare(
      "DELETE FROM pack_objects WHERE created_at <= ? AND NOT EXISTS (SELECT 1 FROM pack_entries e WHERE e.sha256 = pack_objects.sha256) " +
        "AND NOT EXISTS (SELECT 1 FROM published_entries p WHERE p.sha256 = pack_objects.sha256) RETURNING sha256, format",
    )
    .bind(now - PURGE_GRACE_MS)
    .all<{ sha256: string; format: "png" | "jpeg" }>();
  await deleteReleased(db, "packs", bucket, results.map((row) => objectName(row.sha256, row.format)));
  // Tombstones of earlier deletes whose R2 delete failed.
  await deleteReleased(db, "packs", bucket);

  // Storage reservations (src/storage.ts) whose upload never got its row: the bytes are given back and the object, if
  // it was written, is deleted.
  const { results: reservations } = await db
    .prepare(
      "DELETE FROM storage_objects WHERE area = 'packs' AND released = 0 AND created_at <= ? " +
        "AND NOT EXISTS (SELECT 1 FROM pack_objects o WHERE o.sha256 || CASE o.format WHEN 'png' THEN '.png' ELSE '.jpg' END = storage_objects.key) RETURNING key",
    )
    .bind(now - PURGE_GRACE_MS)
    .all<{ key: string }>();
  if (reservations.length > 0) await bucket.delete(reservations.map((r) => r.key));

  const known = new Set((await db.prepare("SELECT sha256, format FROM pack_objects").all<{ sha256: string; format: "png" | "jpeg" }>()).results.map((r) => objectName(r.sha256, r.format)));
  let orphaned = 0;
  let cursor: string | undefined;
  do {
    const page = await bucket.list({ limit: 1000, ...(cursor ? { cursor } : {}) });
    const stale = page.objects.filter((o) => !known.has(o.key) && now - o.uploaded.getTime() >= PURGE_GRACE_MS).map((o) => o.key);
    if (stale.length > 0) {
      await bucket.delete(stale);
      orphaned += stale.length;
    }
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return { unreferenced: results.length, orphaned: orphaned + reservations.length };
}
