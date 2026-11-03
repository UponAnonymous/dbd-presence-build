// GET /v1/me and DELETE /v1/me (FR-ONL-11, PLAN_V2 §4.3). Deleting removes the account, its devices, every session
// and every upload (rows, storage ledger and R2 objects; src/db/uploads.ts); audit rows keep only the random user id, which no longer maps to anyone (pseudonymised), and a redeemed invite keeps
// neither a link to the person nor its note.

import { requireSession } from "../auth/session.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import { deleteUploadObjects } from "../db/uploads.ts";
import { getSettings } from "../db/settings.ts";
import { banHash, loadBanKey } from "../bans.ts";
import { HttpError, json } from "../http.ts";
import { uploadsForUser } from "./uploads.ts";

export async function getMe(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  const db = ctx.env.DB;
  const user = await db
    .prepare("SELECT id, display_name, status, access_granted_at, created_at, last_seen_at, uploads_blocked, roster_reporter FROM users WHERE id = ?")
    .bind(session.userId)
    .first<{ id: string; display_name: string; status: string; access_granted_at: number | null; created_at: number; last_seen_at: number; uploads_blocked: number; roster_reporter: number }>();
  if (!user) throw new HttpError(401, "unauthenticated");
  const { results: devices } = await db
    .prepare("SELECT id, thumbprint, created_at, last_seen_at FROM devices WHERE user_id = ? AND revoked_at IS NULL ORDER BY created_at")
    .bind(session.userId)
    .all<{ id: string; thumbprint: string; created_at: number; last_seen_at: number }>();
  const settings = await getSettings(db);
  return json({
    user: {
      id: user.id,
      displayName: user.display_name,
      status: user.status,
      access: user.access_granted_at !== null ? "granted" : "invite_required",
      createdAt: new Date(user.created_at).toISOString(),
      lastSeenAt: new Date(user.last_seen_at).toISOString(),
      // The owner marked this account as their own game account: its app reports unknown character names (D35).
      rosterReporter: user.roster_reporter === 1,
    },
    devices: devices.map((d) => ({
      id: d.id,
      thumbprint: d.thumbprint,
      createdAt: new Date(d.created_at).toISOString(),
      lastSeenAt: new Date(d.last_seen_at).toISOString(),
      current: d.id === session.deviceId,
    })),
    deviceLimit: settings.deviceLimit,
    uploads: await uploadsForUser(ctx, session.userId, user.uploads_blocked === 1, settings),
  });
}

/** What a redeemed invite's note becomes once the person who redeemed it is deleted. */
export const DELETED_REDEEMER_NOTE = "[redeemed by a deleted account]";

export type DeleteOutcome = "deleted" | "not_found" | "changed";

export interface DeleteResult {
  outcome: DeleteOutcome;
  /** False when an uploaded file could not be deleted from R2 yet: its address already answers 404, and the daily sweep retries. */
  filesRemoved: boolean;
}

/**
 * Deletes a user and, through foreign-key cascades, its devices, token families and refresh tokens. Audited.
 * One transaction, every statement conditional on the status read just before, so a concurrent revoke cannot slip
 * between the read and the delete:
 * - a revoked user's ban is kept as a keyed hash of the Discord id (src/bans.ts), inserted from the row only while it
 *   is still revoked, so deletion or the 12-month purge never lifts a ban;
 * - an invite the user redeemed loses its link to them and its note (the owner's text, which may name the person)
 *   becomes a neutral marker;
 * - the DELETE requires the status that was read, and the audit row is appended only if the user is gone.
 * "changed" means the status changed in between: nothing was deleted and the caller fails the request.
 */
export async function deleteUser(db: D1Database, uploads: R2Bucket, banKey: Uint8Array, now: number, userId: string, actor: string, action: string): Promise<DeleteResult> {
  const before = await db
    .prepare("SELECT discord_id, status, access_granted_at, (SELECT COUNT(*) FROM devices WHERE user_id = users.id) AS devices FROM users WHERE id = ?")
    .bind(userId)
    .first<{ discord_id: string; status: string; access_granted_at: number | null; devices: number }>();
  if (!before) return { outcome: "not_found", filesRemoved: true };
  // The upload rows go with the user (cascade) and their ledger rows are marked released (trigger); the files are deleted
  // after the transaction. Every released upload file is tried, so one that committed after this read is caught too.
  const { results: files } = await db.prepare("SELECT object_key FROM uploads WHERE user_id = ?").bind(userId).all<{ object_key: string }>();
  const banKept = before.status === "revoked";
  const statusIsAsRead = "EXISTS (SELECT 1 FROM users WHERE id = ? AND status = ?)";
  const statements = [
    db
      .prepare("INSERT INTO banned_subjects (subject_hash, created_at) SELECT ?, ? FROM users WHERE id = ? AND status = 'revoked' AND status = ? ON CONFLICT DO NOTHING")
      .bind(await banHash(banKey, before.discord_id), now, userId, before.status),
    db.prepare(`UPDATE invites SET redeemed_by = NULL, note = ? WHERE redeemed_by = ? AND ${statusIsAsRead}`).bind(DELETED_REDEEMER_NOTE, userId, userId, before.status),
    db.prepare("DELETE FROM users WHERE id = ? AND status = ?").bind(userId, before.status),
  ];
  const results = await writeAudited(
    db,
    now,
    {
      actor,
      action,
      target: `user:${userId}`,
      before: { status: before.status, access: before.access_granted_at !== null, devices: before.devices, uploads: files.length },
      after: { banKept },
    },
    statements,
    { sql: "NOT EXISTS (SELECT 1 FROM users WHERE id = ?)", binds: [userId] },
  );
  // `changes` also counts the rows removed by the cascades.
  if (results[2]!.meta.changes < 1) return { outcome: "changed", filesRemoved: true };
  return { outcome: "deleted", filesRemoved: await deleteUploadObjects(db, uploads) };
}

export async function deleteMe(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  const { outcome, filesRemoved } = await deleteUser(ctx.env.DB, ctx.env.UPLOADS, loadBanKey(ctx.env.BAN_HMAC_KEY), ctx.now, session.userId, `user:${session.userId}`, "account.delete");
  if (outcome === "changed") throw new HttpError(409, "account_changed");
  return json({ deleted: true, filesRemoved });
}
