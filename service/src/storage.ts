// The storage cap (owner decision D18; ADR-0015 addendum). R2's free tier holds 10 GB for the whole Cloudflare account,
// and staging and production share that one account, so the caps are per environment (`storageLimits`): production never
// lets its own objects pass a hard cap of 8 GB and the Owner Panel warns (and new uploads stop) from 6 GB; staging stops
// at 750 MB with the warning and upload stop at 500 MB. With the daily backups (scripts/backup-plan.mjs: 800 MB for
// production, 200 MB for staging) the worst case for the account is 8 + 0.75 + 0.8 + 0.2 = 9.75 GB, below the 10 GB.
// An environment name this module does not know gets the stricter staging limits (fail safe). This module is the one
// place any R2 write of the Worker goes through: pack files (src/routes/packs.ts), user uploads (src/routes/uploads.ts),
// daily audit exports (src/audit-export.ts) and, from V2-S6, release packages.
//
// The running total is the ledger in D1 (migrations/0003_uploads_and_storage.sql, 0004_storage_tombstones.sql): one
// storage_objects row per object, with triggers keeping storage_usage equal to its sum per area. Deleting the row that
// refers to an object only marks its ledger row released; the ledger row goes after the R2 delete succeeded
// (`deleteReleased`), so a failed delete leaves a counted tombstone that the daily sweep retries. A write first *reserves* its bytes by inserting the
// ledger row, in a single statement that succeeds only while the total plus the new object stays within the cap; then
// the object is written; a failed write gives the reservation back. So the cap holds under concurrent writers, and an
// interrupted request can only leave the total too high (a reservation without an object), never too low; the daily
// sweep and the owner's purge remove such leftovers. Units are decimal: 8 GB is 8 000 000 000 bytes, below both 10 GB
// and 10 GiB, whichever Cloudflare counts.

import { HttpError } from "./http.ts";

export type StorageArea = "packs" | "uploads" | "releases" | "audit";
export const STORAGE_AREAS: readonly StorageArea[] = ["packs", "uploads", "releases", "audit"];

/** The caps of one environment (D18). */
export interface StorageLimits {
  /** No write may take the stored total above this. */
  capBytes: number;
  /** The Owner Panel warns from here. */
  warnBytes: number;
  /**
   * New user uploads are refused once the total would pass this (security review 2026-10-24, L3): uploads can never take
   * the space between it and the cap, which stays for audit exports, pack files and releases.
   */
  uploadCapBytes: number;
}

/** Production: 8 GB cap, warning and upload stop at 6 GB (the owner's numbers, D18). */
export const PRODUCTION_STORAGE_LIMITS: Readonly<StorageLimits> = Object.freeze({ capBytes: 8_000_000_000, warnBytes: 6_000_000_000, uploadCapBytes: 6_000_000_000 });
/** Staging: 750 MB cap, warning and upload stop at 500 MB, so both environments together stay within the free tier. */
export const STAGING_STORAGE_LIMITS: Readonly<StorageLimits> = Object.freeze({ capBytes: 750_000_000, warnBytes: 500_000_000, uploadCapBytes: 500_000_000 });

/** The caps for `environment` (`env.ENVIRONMENT`): production's for "production", staging's for anything else (fail safe). */
export function storageLimits(environment: string | undefined): Readonly<StorageLimits> {
  return environment === "production" ? PRODUCTION_STORAGE_LIMITS : STAGING_STORAGE_LIMITS;
}

/** The total a write to `area` may reach. */
export function capFor(area: StorageArea, limits: Readonly<StorageLimits>): number {
  return area === "uploads" ? limits.uploadCapBytes : limits.capBytes;
}

export interface StorageUsage {
  usedBytes: number;
  capBytes: number;
  warnBytes: number;
  /** True from `warnBytes` on. */
  warning: boolean;
  /** True when not even a 1-byte object would fit. */
  full: boolean;
  /** New uploads are refused from here. */
  uploadCapBytes: number;
  /** True when not even a 1-byte upload would fit. */
  uploadsFull: boolean;
  /** Bytes still counted for objects whose R2 delete has not succeeded yet (tombstones; the daily sweep retries them). */
  pendingDeleteBytes: number;
  areas: Record<StorageArea, { bytes: number; objects: number }>;
}

/** The ledger's totals with the caps of `environment` (`env.ENVIRONMENT`, see `storageLimits`). */
export async function storageUsage(db: D1Database, environment: string | undefined): Promise<StorageUsage> {
  const limits = storageLimits(environment);
  const { results } = await db.prepare("SELECT area, bytes, objects FROM storage_usage").all<{ area: StorageArea; bytes: number; objects: number }>();
  const areas = Object.fromEntries(STORAGE_AREAS.map((a) => [a, { bytes: 0, objects: 0 }])) as Record<StorageArea, { bytes: number; objects: number }>;
  for (const row of results) if (STORAGE_AREAS.includes(row.area)) areas[row.area] = { bytes: row.bytes, objects: row.objects };
  const usedBytes = STORAGE_AREAS.reduce((sum, a) => sum + areas[a].bytes, 0);
  const pending = await db.prepare("SELECT COALESCE(SUM(bytes), 0) AS n FROM storage_objects WHERE released = 1").first<{ n: number }>();
  return {
    usedBytes,
    capBytes: limits.capBytes,
    warnBytes: limits.warnBytes,
    warning: usedBytes >= limits.warnBytes,
    full: usedBytes >= limits.capBytes,
    uploadCapBytes: limits.uploadCapBytes,
    uploadsFull: usedBytes >= limits.uploadCapBytes,
    pendingDeleteBytes: pending?.n ?? 0,
    areas,
  };
}

/** The error a write over the cap gets: 507 Insufficient Storage, `storage_full` (`upload_storage_full` for an upload past the upload stop). */
export function storageFull(area: StorageArea = "packs"): HttpError {
  return new HttpError(507, area === "uploads" ? "upload_storage_full" : "storage_full");
}

/**
 * Reserves `bytes` for the object `key` of `area`. "reserved": the ledger row was inserted and the bytes are counted.
 * "exists": the object is already in the ledger (the same content-addressed pack file again, or one whose R2 delete is
 * still pending, which is taken back into use); nothing more is counted. Throws 507 when the object would take the total
 * over the area's cap in `environment` (`capFor`).
 */
export async function reserveStorage(
  db: D1Database,
  area: StorageArea,
  key: string,
  bytes: number,
  now: number,
  environment: string | undefined,
): Promise<"reserved" | "exists"> {
  if (!Number.isSafeInteger(bytes) || bytes < 0) throw new RangeError("bytes must be a non-negative integer");
  const inserted = await db
    .prepare(
      "INSERT INTO storage_objects (area, key, bytes, created_at) SELECT ?, ?, ?, ? " +
        "WHERE (SELECT COALESCE(SUM(bytes), 0) FROM storage_usage) + ? <= ? ON CONFLICT (area, key) DO NOTHING",
    )
    .bind(area, key, bytes, now, bytes, capFor(area, storageLimits(environment)))
    .run();
  if (inserted.meta.changes >= 1) return "reserved";
  const existing = await db.prepare("UPDATE storage_objects SET released = 0 WHERE area = ? AND key = ? RETURNING key").bind(area, key).first();
  if (existing) return "exists";
  throw storageFull(area);
}

/** One person's share of the uploads area (D28): what they store now and their caps. */
export interface UploadQuota {
  bytes: number;
  files: number;
}

/** What one person's uploads use in the ledger, in-flight reservations included (they hold their place until written or released). */
export async function uploadUsage(db: D1Database, owner: string): Promise<UploadQuota> {
  const row = await db
    .prepare("SELECT COALESCE(SUM(bytes), 0) AS bytes, COUNT(*) AS files FROM storage_objects WHERE owner = ? AND area = 'uploads' AND released = 0")
    .bind(owner)
    .first<{ bytes: number; files: number }>();
  return { bytes: row?.bytes ?? 0, files: row?.files ?? 0 };
}

/** The refusal for a person over their cap: 409 `upload_limit`, naming the limit hit (`bytes` or `files`). */
export function uploadLimitError(limit: "bytes" | "files"): HttpError {
  return new HttpError(409, "upload_limit", {}, { limit });
}

/**
 * Reserves `bytes` for a new upload object of `owner` (D18, D28, FR-ONL-15). One statement inserts the ledger row only
 * while all of these hold, so parallel uploads can never pass any of them together:
 * - the stored total plus the new object stays within the environment's upload stop (`uploadCapBytes`: 6 GB in
 *   production, 500 MB in staging);
 * - the person's stored and reserved bytes plus the new object stay within `quota.bytes`;
 * - the person has fewer than `quota.files` objects stored or reserved;
 * - the person is active, has access and is not blocked from uploading.
 * When the insert is refused, the reason is read afterwards (only to name it): 507 `upload_storage_full`, 409
 * `upload_limit` with `limit: "bytes" | "files"`, or 403 `uploads_blocked`.
 */
export async function reserveUpload(
  db: D1Database,
  key: string,
  bytes: number,
  now: number,
  owner: string,
  quota: UploadQuota,
  environment: string | undefined,
): Promise<void> {
  if (!Number.isSafeInteger(bytes) || bytes < 0) throw new RangeError("bytes must be a non-negative integer");
  const uploadCap = storageLimits(environment).uploadCapBytes;
  const inserted = await db
    .prepare(
      "INSERT INTO storage_objects (area, key, bytes, created_at, owner) SELECT 'uploads', ?, ?, ?, ? " +
        "WHERE (SELECT COALESCE(SUM(bytes), 0) FROM storage_usage) + ? <= ? " +
        "AND (SELECT COALESCE(SUM(bytes), 0) FROM storage_objects WHERE owner = ? AND area = 'uploads' AND released = 0) + ? <= ? " +
        "AND (SELECT COUNT(*) FROM storage_objects WHERE owner = ? AND area = 'uploads' AND released = 0) < ? " +
        "AND EXISTS (SELECT 1 FROM users WHERE id = ? AND status = 'active' AND access_granted_at IS NOT NULL AND uploads_blocked = 0) " +
        "ON CONFLICT (area, key) DO NOTHING",
    )
    .bind(key, bytes, now, owner, bytes, uploadCap, owner, bytes, quota.bytes, owner, quota.files, owner)
    .run();
  if (inserted.meta.changes >= 1) return;
  const total = await db.prepare("SELECT COALESCE(SUM(bytes), 0) AS n FROM storage_usage").first<{ n: number }>();
  if ((total?.n ?? 0) + bytes > uploadCap) throw storageFull("uploads");
  const used = await uploadUsage(db, owner);
  if (used.files >= quota.files) throw uploadLimitError("files");
  if (used.bytes + bytes > quota.bytes) throw uploadLimitError("bytes");
  throw new HttpError(403, "uploads_blocked");
}

/**
 * Deletes R2 objects whose ledger rows are released (nothing refers to them any more), with one retry each, and removes
 * each ledger row only after its object is gone. `keys` limits it to those objects; without it, up to `limit` released
 * rows of the area are tried (the daily sweep). A failure is logged by error type and the row stays as a tombstone.
 * Returns how many were deleted and how many are still pending.
 */
export async function deleteReleased(
  db: D1Database,
  area: StorageArea,
  bucket: R2Bucket,
  keys?: readonly string[],
  limit = 1000,
): Promise<{ deleted: number; pending: number }> {
  const rows =
    keys === undefined
      ? (await db.prepare("SELECT key FROM storage_objects WHERE area = ? AND released = 1 LIMIT ?").bind(area, limit).all<{ key: string }>()).results.map((r) => r.key)
      : [...new Set(keys)];
  let deleted = 0;
  let pending = 0;
  for (const key of rows) {
    if (await deleteWithRetry(bucket, key, area)) {
      await db.prepare("DELETE FROM storage_objects WHERE area = ? AND key = ? AND released = 1").bind(area, key).run();
      deleted++;
    } else {
      pending++;
    }
  }
  return { deleted, pending };
}

async function deleteWithRetry(bucket: R2Bucket, key: string, area: StorageArea): Promise<boolean> {
  for (let attempt = 1; attempt <= 2; attempt++) {
    try {
      await bucket.delete(key);
      return true;
    } catch (error) {
      console.error(JSON.stringify({ event: "storage_delete_failed", area, attempt, type: error instanceof Error ? error.name : typeof error }));
    }
  }
  return false;
}

/** The statement that removes an object's ledger row (and so its bytes from the total). */
export function releaseStorageStatement(db: D1Database, area: StorageArea, key: string): D1PreparedStatement {
  return db.prepare("DELETE FROM storage_objects WHERE area = ? AND key = ?").bind(area, key);
}

export async function releaseStorage(db: D1Database, area: StorageArea, key: string): Promise<void> {
  await releaseStorageStatement(db, area, key).run();
}

/**
 * Writes one object under the cap: reserve, then put; if the put fails the reservation is given back and the error is
 * passed on. Returns whether the bytes were newly counted ("exists" for an object the ledger already holds, which is
 * written again unchanged).
 */
export async function putWithinCap(
  db: D1Database,
  bucket: R2Bucket,
  area: StorageArea,
  key: string,
  body: Uint8Array | string,
  options: R2PutOptions,
  now: number,
  environment: string | undefined,
): Promise<"reserved" | "exists"> {
  const bytes = typeof body === "string" ? new TextEncoder().encode(body).length : body.length;
  const outcome = await reserveStorage(db, area, key, bytes, now, environment);
  try {
    await bucket.put(key, body, options);
  } catch (error) {
    if (outcome === "reserved") await releaseStorage(db, area, key);
    throw error;
  }
  return outcome;
}

/**
 * Like `putWithinCap`, for a body that is streamed rather than held in memory (release packages, up to 95 MB, V2-S6): the
 * declared `bytes` are reserved first, then the stream is written. The caller passes `options.sha256` so R2 itself refuses
 * bytes that do not match; a failed put gives the reservation back and is passed on.
 */
export async function putStreamWithinCap(
  db: D1Database,
  bucket: R2Bucket,
  area: StorageArea,
  key: string,
  body: ReadableStream,
  bytes: number,
  options: R2PutOptions,
  now: number,
  environment: string | undefined,
): Promise<"reserved" | "exists"> {
  const outcome = await reserveStorage(db, area, key, bytes, now, environment);
  try {
    await bucket.put(key, body, options);
  } catch (error) {
    if (outcome === "reserved") await releaseStorage(db, area, key);
    throw error;
  }
  return outcome;
}
