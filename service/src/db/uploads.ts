// User uploads in D1 (migrations/0003_uploads_and_storage.sql) and their files in R2 bucket UPLOADS (FR-ONL-09,
// FR-ONL-11, FR-ADM-09; ADR-0015). A file is stored under a 128-bit random name with no user id and served unlisted at
// https://<cdn host>/u/<name> (src/routes/cdn.ts). Each file belongs to exactly one upload row: the same bytes from the
// same user are one upload, and files are never shared between users, so removing the row (the user's delete, the
// owner's takedown, account deletion) makes the address stop working at once: /u/ serves a file only while its row
// exists (src/routes/cdn.ts). The row goes first and its ledger row is marked released in the same transaction (trigger
// `uploads_removed`, migration 0004); the R2 object is deleted right after, with a retry, and only then the ledger row
// (src/storage.ts deleteReleased). A failed delete leaves a counted tombstone; `sweepUploads` (daily) retries it and
// deletes any object or reservation an interrupted request left behind.

import type { Env } from "../env.ts";
import { deleteReleased } from "../storage.ts";

export interface UploadRow {
  id: string;
  user_id: string;
  object_key: string;
  sha256: string;
  format: "png" | "jpeg";
  size: number;
  width: number;
  height: number;
  created_at: number;
}

/** `<32 lowercase hex>.<png|jpg>`: the R2 key and the name under /u/ on the content host. */
export const OBJECT_KEY_PATTERN = /^[0-9a-f]{32}\.(png|jpg)$/;

export function uploadObjectKey(randomHex: string, format: "png" | "jpeg"): string {
  return `${randomHex}.${format === "png" ? "png" : "jpg"}`;
}

export function uploadUrl(env: Env, objectKey: string): string {
  return `https://${env.CDN_HOST}/u/${objectKey}`;
}

/** What the uploader (and the owner) sees of one upload. Never the user's Discord id. */
export function uploadView(env: Env, row: UploadRow): Record<string, unknown> {
  return {
    id: row.id,
    url: uploadUrl(env, row.object_key),
    sha256: row.sha256,
    format: row.format,
    size: row.size,
    width: row.width,
    height: row.height,
    createdAt: new Date(row.created_at).toISOString(),
  };
}

/**
 * Deletes the R2 objects of upload rows that are already gone (their ledger rows released), with a retry each. Without
 * `keys`, every released upload object is tried (account deletion: this also catches an upload that committed while the
 * account was being deleted). True when none is left pending; a pending one stays a tombstone for the daily sweep, and its
 * address already answers 404.
 */
export async function deleteUploadObjects(db: D1Database, bucket: R2Bucket, keys?: readonly string[]): Promise<boolean> {
  return (await deleteReleased(db, "uploads", bucket, keys)).pending === 0;
}

/** Reservations and objects younger than this are left alone by the sweep (an upload may still be running). */
export const SWEEP_GRACE_MS = 60 * 60 * 1000;

export interface SweepReport {
  reservations: number;
  orphanedObjects: number;
  /** Tombstones whose R2 delete succeeded now / still failed. */
  released: number;
  stillPending: number;
}

/**
 * The daily sweep (run by the cron after retention): gives back storage reservations whose upload never got its row,
 * deleting the object if it was written (older than SWEEP_GRACE_MS); retries the R2 delete of every tombstone; then
 * deletes every object in UPLOADS that no upload row names (older than SWEEP_GRACE_MS).
 */
export async function sweepUploads(db: D1Database, bucket: R2Bucket, now: number): Promise<SweepReport> {
  const { results: reservations } = await db
    .prepare(
      "DELETE FROM storage_objects WHERE area = 'uploads' AND released = 0 AND created_at <= ? AND NOT EXISTS (SELECT 1 FROM uploads u WHERE u.object_key = storage_objects.key) RETURNING key",
    )
    .bind(now - SWEEP_GRACE_MS)
    .all<{ key: string }>();
  if (reservations.length > 0) await bucket.delete(reservations.map((r) => r.key));
  const tombstones = await deleteReleased(db, "uploads", bucket);

  const known = new Set((await db.prepare("SELECT object_key FROM uploads").all<{ object_key: string }>()).results.map((r) => r.object_key));
  let orphanedObjects = 0;
  let cursor: string | undefined;
  do {
    const page = await bucket.list({ limit: 1000, ...(cursor ? { cursor } : {}) });
    const stale = page.objects.filter((o) => !known.has(o.key) && now - o.uploaded.getTime() >= SWEEP_GRACE_MS).map((o) => o.key);
    if (stale.length > 0) {
      await bucket.delete(stale);
      orphanedObjects += stale.length;
    }
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return { reservations: reservations.length, orphanedObjects, released: tombstones.deleted, stillPending: tombstones.pending };
}
