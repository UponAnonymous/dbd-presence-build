// Admin API at https://<admin host>/api/* (FR-ADM-02/03/05/07, ADR-0014). Same origin as the Owner Panel, behind
// Cloudflare Access. Every call: Access JWT and owner allow-list (401/403), then the custom header; state-changing calls
// also need the exact Origin and `Sec-Fetch-Site: same-origin`. No CORS. Every privileged action writes an audit row.

import { verifyAccess } from "../auth/access-jwt.ts";
import { configCacheKey } from "../config-cache.ts";
import type { Context } from "../context.ts";
import { verifyChain, writeAudited, type AuditRow } from "../db/audit.ts";
import { ACCESS_MODES, BANNER_MAX, DEVICE_LIMIT_MAX, UPLOAD_LIMIT_BYTES_MAX, UPLOAD_LIMIT_FILES_MAX, VERSION_PATTERN, configVersionFromRows, settingsFromRows, settingsQuery, updateSettings, type Settings, type SettingsRow } from "../db/settings.ts";
import { HttpError, json, readJsonBody } from "../http.ts";
import { generateInviteCode, inviteCodeHash, normalizeInviteCode } from "../invites/codes.ts";
import { newId } from "../lib/crypto.ts";
import { object, oneOf, optionalBoolean, optionalInteger, optionalString, string, uuid } from "../lib/validate.ts";
import { loadHmacKeyRing } from "../signing/keys.ts";
import { loadBanKey } from "../bans.ts";
import { deleteUser } from "./me.ts";
import { uploadView, type UploadRow } from "../db/uploads.ts";

export const ADMIN_HEADER = "X-DBDP-Admin";
const DAY_MS = 24 * 60 * 60 * 1000;
const LIST_LIMIT = 500;

const STATE_CHANGING = new Set(["POST", "PUT", "PATCH", "DELETE"]);

/** Authenticates and checks request protections; returns the audit actor (`owner:<email>`). */
export async function requireAdmin(ctx: Context): Promise<string> {
  const { request, env } = ctx;
  const identity = await verifyAccess(
    request,
    { audience: env.ACCESS_AUD, teamDomain: env.ACCESS_TEAM_DOMAIN, owners: env.OWNER_EMAILS },
    ctx.deps.fetch,
    ctx.deps.accessKeys,
    ctx.now,
  );
  if (request.headers.get(ADMIN_HEADER) !== "1") throw new HttpError(403, "admin_header_required");
  const expectedOrigin = `https://${env.ADMIN_HOST}`;
  const origin = request.headers.get("Origin");
  const fetchSite = request.headers.get("Sec-Fetch-Site");
  if (STATE_CHANGING.has(request.method)) {
    if (origin !== expectedOrigin) throw new HttpError(403, "bad_origin");
    if (fetchSite !== "same-origin") throw new HttpError(403, "bad_fetch_site");
  } else {
    if (origin !== null && origin !== expectedOrigin) throw new HttpError(403, "bad_origin");
    if (fetchSite !== null && fetchSite !== "same-origin" && fetchSite !== "none") throw new HttpError(403, "bad_fetch_site");
  }
  return `owner:${identity.email}`;
}

const iso = (ms: number | null): string | null => (ms === null ? null : new Date(ms).toISOString());

// ---------------------------------------------------------------------------------------------------------------
// Invites

interface InviteRow {
  id: string;
  hmac_kid: string;
  note: string | null;
  created_at: number;
  created_by: string;
  expires_at: number;
  redeemed_by: string | null;
  redeemed_at: number | null;
  revoked_at: number | null;
}

function inviteStatus(row: InviteRow, now: number): "redeemed" | "revoked" | "expired" | "active" {
  if (row.redeemed_at !== null) return "redeemed";
  if (row.revoked_at !== null) return "revoked";
  if (row.expires_at <= now) return "expired";
  return "active";
}

function inviteView(row: InviteRow, now: number): Record<string, unknown> {
  return {
    id: row.id,
    keyId: row.hmac_kid,
    note: row.note,
    status: inviteStatus(row, now),
    createdAt: iso(row.created_at),
    createdBy: row.created_by,
    expiresAt: iso(row.expires_at),
    redeemedBy: row.redeemed_by,
    redeemedAt: iso(row.redeemed_at),
    revokedAt: iso(row.revoked_at),
  };
}

export async function listInvites(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const { results } = await ctx.env.DB.prepare("SELECT * FROM invites ORDER BY created_at DESC LIMIT ?").bind(LIST_LIMIT).all<InviteRow>();
  return json({ invites: results.map((r) => inviteView(r, ctx.now)) });
}

/** Creates one or a batch of invites. The codes are returned once and never stored or logged. */
export async function createInvites(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const body = object(await readJsonBody(ctx.request, 4096), ["count", "note", "expiresInDays"], []);
  const count = optionalInteger(body["count"], 1, 100) ?? 1;
  const note = optionalString(body["note"], { max: 200 }) ?? null;
  const expiresInDays = optionalInteger(body["expiresInDays"], 1, 90) ?? 30;
  const ring = loadHmacKeyRing(ctx.env.INVITE_HMAC_KEYS);
  const key = ring.keys.get(ring.currentKid)!;
  const db = ctx.env.DB;
  const expiresAt = ctx.now + expiresInDays * DAY_MS;

  const created: { id: string; code: string }[] = [];
  const statements: D1PreparedStatement[] = [];
  for (let i = 0; i < count; i++) {
    const code = generateInviteCode();
    const id = newId();
    const hash = await inviteCodeHash(key, normalizeInviteCode(code)!);
    created.push({ id, code });
    statements.push(
      db
        .prepare("INSERT INTO invites (id, hmac_kid, code_hash, note, created_at, created_by, expires_at) VALUES (?, ?, ?, ?, ?, ?, ?)")
        .bind(id, ring.currentKid, hash, note, ctx.now, actor, expiresAt),
    );
  }
  // The note itself stays out of the audit chain (it may name a person, and audit rows cannot be rewritten later).
  await writeAudited(db, ctx.now, { actor, action: "invite.create", target: null, after: { ids: created.map((c) => c.id), hasNote: note !== null, expiresAt: iso(expiresAt) } }, statements);
  return json({ invites: created.map((c) => ({ id: c.id, code: c.code, note, expiresAt: iso(expiresAt) })) });
}

export async function revokeInvite(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = uuid(ctx.params["id"]);
  const db = ctx.env.DB;
  const row = await db.prepare("SELECT * FROM invites WHERE id = ?").bind(id).first<InviteRow>();
  if (!row) throw new HttpError(404, "not_found");
  if (row.redeemed_at !== null || row.revoked_at !== null) throw new HttpError(409, "invite_not_revocable");
  const [result] = await writeAudited(
    db,
    ctx.now,
    { actor, action: "invite.revoke", target: `invite:${id}`, before: { status: inviteStatus(row, ctx.now) }, after: { status: "revoked" } },
    [db.prepare("UPDATE invites SET revoked_at = ? WHERE id = ? AND redeemed_at IS NULL AND revoked_at IS NULL").bind(ctx.now, id)],
  );
  if (result!.meta.changes !== 1) throw new HttpError(409, "invite_not_revocable");
  return json({ invite: inviteView({ ...row, revoked_at: ctx.now }, ctx.now) });
}

// ---------------------------------------------------------------------------------------------------------------
// Users

interface UserRow {
  id: string;
  discord_id: string;
  display_name: string;
  status: "active" | "revoked";
  access_granted_at: number | null;
  access_via: string | null;
  created_at: number;
  last_seen_at: number;
  revoked_at: number | null;
  uploads_blocked: number;
  roster_reporter: number;
}

interface DeviceRow {
  id: string;
  user_id: string;
  thumbprint: string;
  created_at: number;
  last_seen_at: number;
  revoked_at: number | null;
}

function deviceView(d: DeviceRow): Record<string, unknown> {
  return { id: d.id, thumbprint: d.thumbprint, createdAt: iso(d.created_at), lastSeenAt: iso(d.last_seen_at), revokedAt: iso(d.revoked_at) };
}

function userView(u: UserRow, devices: DeviceRow[], uploads: number): Record<string, unknown> {
  return {
    id: u.id,
    discordId: u.discord_id,
    displayName: u.display_name,
    status: u.status,
    access: u.access_granted_at !== null ? "granted" : "invite_required",
    accessVia: u.access_via,
    createdAt: iso(u.created_at),
    lastSeenAt: iso(u.last_seen_at),
    revokedAt: iso(u.revoked_at),
    devices: devices.map(deviceView),
    uploads,
    uploadsBlocked: u.uploads_blocked === 1,
    rosterReporter: u.roster_reporter === 1,
  };
}

export async function listUsers(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const db = ctx.env.DB;
  const { results: users } = await db.prepare("SELECT * FROM users ORDER BY created_at DESC LIMIT ?").bind(LIST_LIMIT).all<UserRow>();
  const { results: devices } = await db.prepare("SELECT id, user_id, thumbprint, created_at, last_seen_at, revoked_at FROM devices").all<DeviceRow>();
  const { results: counts } = await db.prepare("SELECT user_id, COUNT(*) AS n FROM uploads GROUP BY user_id").all<{ user_id: string; n: number }>();
  const uploadsByUser = new Map(counts.map((c) => [c.user_id, c.n]));
  const byUser = new Map<string, DeviceRow[]>();
  for (const d of devices) byUser.set(d.user_id, [...(byUser.get(d.user_id) ?? []), d]);
  return json({ users: users.map((u) => userView(u, byUser.get(u.id) ?? [], uploadsByUser.get(u.id) ?? 0)) });
}

export async function revokeUser(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = uuid(ctx.params["id"]);
  const db = ctx.env.DB;
  const user = await db.prepare("SELECT * FROM users WHERE id = ?").bind(id).first<UserRow>();
  if (!user) throw new HttpError(404, "not_found");
  if (user.status === "revoked") throw new HttpError(409, "already_revoked");
  await writeAudited(db, ctx.now, { actor, action: "user.revoke", target: `user:${id}`, before: { status: "active" }, after: { status: "revoked" } }, [
    db.prepare("UPDATE users SET status = 'revoked', revoked_at = ? WHERE id = ?").bind(ctx.now, id),
    db.prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'user_revoked' WHERE user_id = ? AND revoked_at IS NULL").bind(ctx.now, id),
  ]);
  return json({ user: { id, status: "revoked", revokedAt: iso(ctx.now) } });
}

/** Revokes one device: its sessions end and it no longer counts against the device limit; the same key cannot sign in again. */
export async function revokeDevice(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const userId = uuid(ctx.params["id"]);
  const deviceId = uuid(ctx.params["deviceId"]);
  const db = ctx.env.DB;
  const device = await db.prepare("SELECT id, revoked_at FROM devices WHERE id = ? AND user_id = ?").bind(deviceId, userId).first<{ id: string; revoked_at: number | null }>();
  if (!device) throw new HttpError(404, "not_found");
  if (device.revoked_at !== null) throw new HttpError(409, "already_revoked");
  await writeAudited(db, ctx.now, { actor, action: "device.revoke", target: `user:${userId}`, before: { device: deviceId, status: "active" }, after: { device: deviceId, status: "revoked" } }, [
    db.prepare("UPDATE devices SET revoked_at = ? WHERE id = ?").bind(ctx.now, deviceId),
    db.prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'device_revoked' WHERE device_id = ? AND revoked_at IS NULL").bind(ctx.now, deviceId),
  ]);
  return json({ device: { id: deviceId, revokedAt: iso(ctx.now) } });
}

/** Deletion needs the user id repeated in the body (a destructive action is confirmed). */
export async function removeUser(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = uuid(ctx.params["id"]);
  const body = object(await readJsonBody(ctx.request, 1024), ["confirm"]);
  if (string(body["confirm"], { max: 36 }) !== id) throw new HttpError(400, "confirmation_mismatch");
  const { outcome, filesRemoved } = await deleteUser(ctx.env.DB, ctx.env.UPLOADS, loadBanKey(ctx.env.BAN_HMAC_KEY), ctx.now, id, actor, "user.delete");
  if (outcome === "not_found") throw new HttpError(404, "not_found");
  // The user's status changed while deleting (a concurrent revoke): nothing was deleted; the owner can retry.
  if (outcome === "changed") throw new HttpError(409, "user_changed");
  return json({ deleted: true, filesRemoved });
}

/** Everything held about one user, for a subject-access request (PLAN_V2 §4.3). The export itself is audited. */
export async function exportUser(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = uuid(ctx.params["id"]);
  const db = ctx.env.DB;
  const user = await db.prepare("SELECT * FROM users WHERE id = ?").bind(id).first<UserRow>();
  if (!user) throw new HttpError(404, "not_found");
  const { results: devices } = await db.prepare("SELECT id, user_id, thumbprint, created_at, last_seen_at, revoked_at FROM devices WHERE user_id = ?").bind(id).all<DeviceRow>();
  const { results: sessions } = await db
    .prepare("SELECT id, device_id, created_at, revoked_at, revoked_reason FROM token_families WHERE user_id = ? ORDER BY created_at")
    .bind(id)
    .all<{ id: string; device_id: string; created_at: number; revoked_at: number | null; revoked_reason: string | null }>();
  const { results: invites } = await db.prepare("SELECT id, redeemed_at FROM invites WHERE redeemed_by = ?").bind(id).all<{ id: string; redeemed_at: number }>();
  const { results: audit } = await db.prepare("SELECT * FROM audit_log WHERE actor = ? OR target = ? ORDER BY seq").bind(`user:${id}`, `user:${id}`).all<AuditRow>();
  const { results: uploads } = await db.prepare("SELECT * FROM uploads WHERE user_id = ? ORDER BY created_at").bind(id).all<UploadRow>();
  await writeAudited(db, ctx.now, { actor, action: "user.export", target: `user:${id}` });
  return json({
    exportedAt: iso(ctx.now),
    user: userView(user, devices, uploads.length),
    uploads: uploads.map((u) => uploadView(ctx.env, u)),
    sessions: sessions.map((s) => ({ id: s.id, deviceId: s.device_id, createdAt: iso(s.created_at), revokedAt: iso(s.revoked_at), revokedReason: s.revoked_reason })),
    invitesRedeemed: invites.map((i) => ({ id: i.id, redeemedAt: iso(i.redeemed_at) })),
    auditEntries: audit.map(auditView),
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Audit

function auditView(row: AuditRow): Record<string, unknown> {
  const parse = (text: string | null): unknown => (text === null ? null : (JSON.parse(text) as unknown));
  return {
    seq: row.seq,
    at: iso(row.at),
    actor: row.actor,
    action: row.action,
    target: row.target,
    before: parse(row.before_json),
    after: parse(row.after_json),
    prevHash: row.prev_hash,
    hash: row.hash,
  };
}

export async function listAudit(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const beforeParam = ctx.url.searchParams.get("before");
  const limitParam = ctx.url.searchParams.get("limit");
  if ((beforeParam !== null && !/^\d{1,15}$/.test(beforeParam)) || (limitParam !== null && !/^\d{1,3}$/.test(limitParam))) {
    throw new HttpError(400, "invalid_request");
  }
  const before = beforeParam === null ? Number.MAX_SAFE_INTEGER : Number(beforeParam);
  const limit = Math.min(Math.max(limitParam === null ? 100 : Number(limitParam), 1), 200);
  const { results } = await ctx.env.DB.prepare("SELECT * FROM audit_log WHERE seq < ? ORDER BY seq DESC LIMIT ?").bind(before, limit).all<AuditRow>();
  return json({ entries: results.map(auditView) });
}

export async function verifyAudit(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  return json({ chain: await verifyChain(ctx.env.DB) });
}

// ---------------------------------------------------------------------------------------------------------------
// Settings

/** The settings and the version of the signed config that carries them. */
async function settingsBody(db: D1Database): Promise<Record<string, unknown>> {
  const rows = (await settingsQuery(db).all<SettingsRow>()).results;
  return { settings: settingsFromRows(rows), configVersion: configVersionFromRows(rows) };
}

export async function readSettings(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  return json(await settingsBody(ctx.env.DB));
}

export async function writeSettings(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const allowed = ["accessMode", "deviceLimit", "disableCapture", "disableDetection", "emergencyStop", "minimumVersion", "banner", "uploadsBlocked", "uploadLimitBytes", "uploadLimitFiles"];
  const body = object(await readJsonBody(ctx.request, 2048), allowed, []);
  const patch: Partial<Settings> = {};
  if (body["accessMode"] !== undefined) patch.accessMode = oneOf(body["accessMode"], ACCESS_MODES);
  if (body["deviceLimit"] !== undefined) patch.deviceLimit = optionalInteger(body["deviceLimit"], 1, DEVICE_LIMIT_MAX)!;
  if (body["disableCapture"] !== undefined) patch.disableCapture = optionalBoolean(body["disableCapture"])!;
  if (body["disableDetection"] !== undefined) patch.disableDetection = optionalBoolean(body["disableDetection"])!;
  if (body["emergencyStop"] !== undefined) patch.emergencyStop = optionalBoolean(body["emergencyStop"])!;
  if (body["minimumVersion"] !== undefined) patch.minimumVersion = string(body["minimumVersion"], { max: 17, pattern: VERSION_PATTERN });
  if (body["banner"] !== undefined) patch.banner = body["banner"] === null ? null : string(body["banner"], { max: BANNER_MAX });
  if (body["uploadsBlocked"] !== undefined) patch.uploadsBlocked = optionalBoolean(body["uploadsBlocked"])!;
  if (body["uploadLimitBytes"] !== undefined) patch.uploadLimitBytes = optionalInteger(body["uploadLimitBytes"], 0, UPLOAD_LIMIT_BYTES_MAX)!;
  if (body["uploadLimitFiles"] !== undefined) patch.uploadLimitFiles = optionalInteger(body["uploadLimitFiles"], 0, UPLOAD_LIMIT_FILES_MAX)!;
  await updateSettings(ctx.env.DB, ctx.now, actor, patch);
  // The signed config carries these settings: drop the cached copy here so the change is served at once in this data
  // centre (elsewhere within the 60-second cache lifetime).
  await ctx.deps.configCache.clear(configCacheKey(ctx.env.API_HOST));
  return json(await settingsBody(ctx.env.DB));
}
