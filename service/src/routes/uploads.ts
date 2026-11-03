// User uploads (FR-ONL-09, FR-ONL-11, FR-ADM-09; PLAN_V2 §3.5, §5; ADR-0015, D4, D12, D18).
//
// POST /v1/uploads (licence): multipart/form-data with exactly one part, `file`, of type image/png or image/jpeg. The
// request needs a live session on a live device of an active account that has access (the server-side form of a
// current licence, PLAN_V2 §3.4), uploads not blocked for the user or globally, and the emergency switch off. The file
// must pass the strict parser as it is (src/images/strict.ts: PNG chunk and JPEG marker allow-lists, every CRC, nothing
// after the end, at most 1024 x 1024 and 2 MB); nothing is cleaned up or re-encoded here, because the app re-encodes
// before uploading, and anything that fails is refused (422) and never stored. Accepted files are stored under a
// 128-bit random name with no user id, served unlisted at https://<cdn host>/u/<name>. The same bytes uploaded again
// by the same user return the existing upload. Each person may store at most `uploadLimitBytes` (60 MB) in at most
// `uploadLimitFiles` (300) pictures (D28, FR-ONL-15): 409 `upload_limit` names the limit hit (`limit: "bytes"` or
// `"files"`). Every new file is reserved in the storage ledger before it is written, in one statement that also checks
// the person's caps and the environment's upload stop (6 GB in production, 500 MB in staging; 507 `upload_storage_full`;
// src/storage.ts reserveUpload), so parallel uploads can never pass a cap together.
//
// DELETE /v1/uploads/{id} (session): deletes one of the caller's own uploads. Deleting needs no current licence:
// removing one's own data is never blocked. Another user's id answers 404, like an unknown one.
//
// Admin (owner, src/routes/admin.ts protections): GET /api/uploads lists every upload with its uploader, the upload
// switches and storage use; POST /api/uploads/{id}/takedown removes one; POST /api/users/{id}/uploads sets the
// per-user block. Audit rows hold ids, sizes and formats only.

import { requireSession } from "../auth/session.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import { getSettings, type Settings } from "../db/settings.ts";
import { deleteUploadObjects, uploadObjectKey, uploadView, type UploadRow } from "../db/uploads.ts";
import { HttpError, json, readBodyBytes, readJsonBody } from "../http.ts";
import { inspectImage, MAX_IMAGE_BYTES } from "../images/strict.ts";
import { newId, randomBytes, sha256Hex, toHex } from "../lib/crypto.ts";
import { multipartBoundary, readSingleFilePart } from "../lib/multipart.ts";
import { boolean, object, uuid } from "../lib/validate.ts";
import { releaseStorage, reserveUpload, storageUsage, uploadLimitError, uploadUsage } from "../storage.ts";
import { requireAdmin } from "./admin.ts";
import { limiterFor, revokedResponse } from "./common.ts";

/** Room for the multipart boundary and part headers around the 2 MB file. */
export const MULTIPART_OVERHEAD_BYTES = 16 * 1024;
const LIST_LIMIT = 500;

const TYPES: Readonly<Record<string, "png" | "jpeg">> = { "image/png": "png", "image/jpeg": "jpeg" };

/** Why uploads are not possible for this account now, or null. Shown to the app as an error code. */
function blockedReason(settings: Settings, userBlocked: boolean): "online_features_off" | "uploads_blocked" | null {
  if (settings.emergencyStop) return "online_features_off";
  if (settings.uploadsBlocked || userBlocked) return "uploads_blocked";
  return null;
}

/**
 * The upload part of GET /v1/me: the caller's uploads, their use of the quota and both caps (D28, FR-ONL-15), and
 * whether new uploads are refused now. `blocked` is also `upload_storage_full` while the service-wide upload stop
 * is reached, so the app knows when saving may resume. `limit` is the file cap, kept for apps older than the byte cap.
 */
export async function uploadsForUser(ctx: Context, userId: string, userBlocked: boolean, settings: Settings): Promise<Record<string, unknown>> {
  const db = ctx.env.DB;
  const [{ results }, storage] = await Promise.all([
    db.prepare("SELECT * FROM uploads WHERE user_id = ? ORDER BY created_at, id").bind(userId).all<UploadRow>(),
    storageUsage(db, ctx.env.ENVIRONMENT),
  ]);
  return {
    items: results.map((r) => uploadView(ctx.env, r)),
    limit: settings.uploadLimitFiles,
    limitBytes: settings.uploadLimitBytes,
    limitFiles: settings.uploadLimitFiles,
    usedBytes: results.reduce((sum, r) => sum + r.size, 0),
    usedFiles: results.length,
    blocked: blockedReason(settings, userBlocked) ?? (storage.uploadsFull ? "upload_storage_full" : null),
  };
}

/**
 * Reads the one `file` part (src/lib/multipart.ts). The body is capped before parsing; a body that is not that one
 * well-formed part is refused, and so is a part of another type.
 */
async function readFilePart(request: Request): Promise<{ bytes: Uint8Array; format: "png" | "jpeg" }> {
  const type = request.headers.get("Content-Type") ?? "";
  if (multipartBoundary(type) === null) throw new HttpError(415, "unsupported_media_type");
  const part = readSingleFilePart(await readBodyBytes(request, MAX_IMAGE_BYTES + MULTIPART_OVERHEAD_BYTES), type);
  if (!part.ok) throw new HttpError(400, "invalid_request");
  const format = TYPES[part.type];
  if (format === undefined) throw new HttpError(415, "unsupported_media_type");
  return { bytes: part.bytes, format };
}

export async function createUpload(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  const limiter = limiterFor(ctx);
  // A verified principal: refused now (read only) at its daily cap of uploads or of refused files.
  await limiter.checkAccount("upload", session.userId);
  if (session.status === "revoked") return revokedResponse(ctx, session.userId, session.thumbprint);
  if (!session.hasAccess) throw new HttpError(403, "invite_required");

  const db = ctx.env.DB;
  const [settings, user] = await Promise.all([
    getSettings(db),
    db.prepare("SELECT uploads_blocked FROM users WHERE id = ?").bind(session.userId).first<{ uploads_blocked: number }>(),
  ]);
  if (!user) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
  const blocked = blockedReason(settings, user.uploads_blocked === 1);
  if (blocked !== null) throw new HttpError(403, blocked);

  // Only a principal allowed to upload gets this far; its attempt is counted before the body is read, so no outcome (same
  // bytes again, the limit, a refused body) is free, and refused principals never use up the shared counters.
  await limiter.verified("upload-attempt", session.userId);

  const { bytes, format: declared } = await readFilePart(ctx.request);
  const check = inspectImage(bytes);
  if (!check.ok || check.info.format !== declared) {
    await limiter.failure("upload", session.userId);
    return json({ error: "image_rejected", reason: check.ok ? "type_mismatch" : check.reason }, 422);
  }
  const { format, width, height } = check.info;
  const sha = await sha256Hex(bytes);

  const existing = await db.prepare("SELECT * FROM uploads WHERE user_id = ? AND sha256 = ?").bind(session.userId, sha).first<UploadRow>();
  if (existing) return json({ upload: uploadView(ctx.env, existing), existing: true });
  // A quick refusal before the upload is counted; the reservation below checks the caps again, atomically.
  const quota = { bytes: settings.uploadLimitBytes, files: settings.uploadLimitFiles };
  const used = await uploadUsage(db, session.userId);
  if (used.files >= quota.files) throw uploadLimitError("files");
  if (used.bytes + bytes.length > quota.bytes) throw uploadLimitError("bytes");

  await limiter.verified("upload", session.userId);
  const id = newId();
  const objectKey = uploadObjectKey(toHex(randomBytes(16)), format);
  await reserveUpload(db, objectKey, bytes.length, ctx.now, session.userId, quota, ctx.env.ENVIRONMENT);
  try {
    await ctx.env.UPLOADS.put(objectKey, bytes, { httpMetadata: { contentType: format === "png" ? "image/png" : "image/jpeg" }, sha256: sha });
  } catch (error) {
    await releaseStorage(db, "uploads", objectKey);
    throw error;
  }

  // The row is written only while every rule still holds (an owner may block the user, or a parallel upload of the same
  // bytes may finish first, while this one ran), and the audit row only with it. The caps were taken with the
  // reservation: its ledger row, which must still be there, holds this upload's place in the person's quota.
  const insert = db
    .prepare(
      "INSERT INTO uploads (id, user_id, object_key, sha256, format, size, width, height, created_at) " +
        "SELECT ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE EXISTS (SELECT 1 FROM users WHERE id = ? AND status = 'active' AND access_granted_at IS NOT NULL AND uploads_blocked = 0) " +
        "AND EXISTS (SELECT 1 FROM storage_objects WHERE area = 'uploads' AND key = ? AND owner = ? AND released = 0) " +
        "AND NOT EXISTS (SELECT 1 FROM uploads WHERE user_id = ? AND sha256 = ?)",
    )
    .bind(id, session.userId, objectKey, sha, format, bytes.length, width, height, ctx.now, session.userId, objectKey, session.userId, session.userId, sha);
  const [inserted] = await writeAudited(
    db,
    ctx.now,
    { actor: `user:${session.userId}`, action: "upload.create", target: `upload:${id}`, after: { format, size: bytes.length, width, height } },
    [insert],
    { sql: "EXISTS (SELECT 1 FROM uploads WHERE id = ?)", binds: [id] },
  );
  if (inserted!.meta.changes < 1) {
    // The file was written but never got its row: release its bytes and delete it (a failed delete stays a tombstone).
    await db.prepare("UPDATE storage_objects SET released = 1, owner = NULL WHERE area = 'uploads' AND key = ?").bind(objectKey).run();
    await deleteUploadObjects(db, ctx.env.UPLOADS, [objectKey]);
    const same = await db.prepare("SELECT * FROM uploads WHERE user_id = ? AND sha256 = ?").bind(session.userId, sha).first<UploadRow>();
    if (same) return json({ upload: uploadView(ctx.env, same), existing: true });
    const now = await db
      .prepare("SELECT status, access_granted_at, uploads_blocked FROM users WHERE id = ?")
      .bind(session.userId)
      .first<{ status: string; access_granted_at: number | null; uploads_blocked: number }>();
    if (!now) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
    if (now.status === "revoked") return revokedResponse(ctx, session.userId, session.thumbprint);
    if (now.uploads_blocked === 1) throw new HttpError(403, "uploads_blocked");
    if (now.access_granted_at === null) throw new HttpError(403, "invite_required");
    // Only a reservation removed while this request ran (the hourly sweep, the owner's purge) is left: nothing was stored.
    throw new HttpError(503, "upload_interrupted");
  }
  const row = (await db.prepare("SELECT * FROM uploads WHERE id = ?").bind(id).first<UploadRow>())!;
  return json({ upload: uploadView(ctx.env, row), existing: false }, 201);
}

/**
 * Removes one upload row (its address answers 404 from then on) and then its file. Null when there was no row to remove;
 * otherwise whether the file is gone from R2 too (false: a tombstone the daily sweep retries).
 */
async function removeUpload(ctx: Context, row: UploadRow, actor: string, action: string, extra: Record<string, unknown>): Promise<{ fileRemoved: boolean } | null> {
  const db = ctx.env.DB;
  const [deleted] = await writeAudited(
    db,
    ctx.now,
    { actor, action, target: `upload:${row.id}`, before: { format: row.format, size: row.size, ...extra } },
    [db.prepare("DELETE FROM uploads WHERE id = ?").bind(row.id)],
    { sql: "NOT EXISTS (SELECT 1 FROM uploads WHERE id = ?)", binds: [row.id] },
  );
  if (deleted!.meta.changes < 1) return null;
  return { fileRemoved: await deleteUploadObjects(ctx.env.DB, ctx.env.UPLOADS, [row.object_key]) };
}

export async function deleteUpload(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  const id = uuid(ctx.params["id"]);
  const row = await ctx.env.DB.prepare("SELECT * FROM uploads WHERE id = ? AND user_id = ?").bind(id, session.userId).first<UploadRow>();
  const removed = row ? await removeUpload(ctx, row, `user:${session.userId}`, "upload.delete", {}) : null;
  if (!removed) throw new HttpError(404, "not_found");
  return json({ deleted: true, fileRemoved: removed.fileRemoved });
}

// ---------------------------------------------------------------------------------------------------------------
// Admin

interface AdminUploadRow extends UploadRow {
  display_name: string;
  uploads_blocked: number;
}

export async function listUploadsAdmin(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const db = ctx.env.DB;
  const { results } = await db
    .prepare("SELECT u.*, users.display_name, users.uploads_blocked FROM uploads u JOIN users ON users.id = u.user_id ORDER BY u.created_at DESC, u.id LIMIT ?")
    .bind(LIST_LIMIT)
    .all<AdminUploadRow>();
  const { results: blockedUsers } = await db.prepare("SELECT id, display_name FROM users WHERE uploads_blocked = 1 ORDER BY display_name").all<{ id: string; display_name: string }>();
  const total = await db.prepare("SELECT COUNT(*) AS n FROM uploads").first<{ n: number }>();
  const settings = await getSettings(db);
  return json({
    uploads: results.map((r) => ({ ...uploadView(ctx.env, r), user: { id: r.user_id, displayName: r.display_name, uploadsBlocked: r.uploads_blocked === 1 } })),
    total: total?.n ?? 0,
    blockedUsers: blockedUsers.map((u) => ({ id: u.id, displayName: u.display_name })),
    settings: { uploadsBlocked: settings.uploadsBlocked, uploadLimitBytes: settings.uploadLimitBytes, uploadLimitFiles: settings.uploadLimitFiles },
    storage: await storageUsage(db, ctx.env.ENVIRONMENT),
  });
}

/**
 * The owner's takedown. The address answers 404 at once (/u/ serves only files with a row); `fileRemoved: false` says the
 * stored file is still in R2 and the daily sweep will retry. Discord's media proxy may keep showing a copy for a while.
 */
export async function takeDownUpload(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = uuid(ctx.params["id"]);
  object(await readJsonBody(ctx.request, 256), []);
  const row = await ctx.env.DB.prepare("SELECT * FROM uploads WHERE id = ?").bind(id).first<UploadRow>();
  const removed = row ? await removeUpload(ctx, row, actor, "upload.takedown", { user: row.user_id }) : null;
  if (!removed) throw new HttpError(404, "not_found");
  return json({ takenDown: true, fileRemoved: removed.fileRemoved, storage: await storageUsage(ctx.env.DB, ctx.env.ENVIRONMENT) });
}

/** Blocks (or allows again) new uploads for one user. Their stored uploads stay; take them down one by one if needed. */
export async function setUserUploadBlock(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = uuid(ctx.params["id"]);
  const blocked = boolean(object(await readJsonBody(ctx.request, 256), ["blocked"])["blocked"]);
  const db = ctx.env.DB;
  const user = await db.prepare("SELECT uploads_blocked FROM users WHERE id = ?").bind(id).first<{ uploads_blocked: number }>();
  if (!user) throw new HttpError(404, "not_found");
  if ((user.uploads_blocked === 1) !== blocked) {
    await writeAudited(db, ctx.now, { actor, action: blocked ? "user.uploads_block" : "user.uploads_allow", target: `user:${id}`, before: { uploadsBlocked: !blocked }, after: { uploadsBlocked: blocked } }, [
      db.prepare("UPDATE users SET uploads_blocked = ? WHERE id = ?").bind(blocked ? 1 : 0, id),
    ]);
  }
  return json({ user: { id, uploadsBlocked: blocked } });
}

export async function getStorage(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  return json({ storage: await storageUsage(ctx.env.DB, ctx.env.ENVIRONMENT) });
}
