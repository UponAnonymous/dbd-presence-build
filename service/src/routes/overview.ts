// The Owner Panel's Dashboard and Settings data (FR-ADM-01, NFR-SVC-10; PLAN_V2 §7): GET /api/overview, read-only.
// Everything comes from what the Worker already holds: counts from D1, the storage ledger (src/storage.ts), the database
// size D1 reports with every query result, which key secrets are present (key ids only, never key material), the offline
// release and recovery keys (src/routes/releases.ts) and the backup status (src/backup.ts). No call leaves the Worker.
// Request counts and D1 reads and writes per day are not visible to the Worker, so they are not reported here.

import { readBackupStatus } from "../backup.ts";
import { loadBanKey } from "../bans.ts";
import type { Context } from "../context.ts";
import { HttpError, json } from "../http.ts";
import { loadHmacKeyRing, loadSigningKey } from "../signing/keys.ts";
import { storageUsage } from "../storage.ts";
import { requireAdmin } from "./admin.ts";
import { offlineKeyStatus } from "./releases.ts";

const WEEK_MS = 7 * 24 * 60 * 60 * 1000;
/** D1's free-tier size per database (ARCHITECTURE, checked 2026-10-23), counted in decimal so the figure is never generous. */
export const DATABASE_FREE_LIMIT_BYTES = 500_000_000;

export type ServiceKeyName = "licence" | "config" | "content" | "invite" | "ban";

export interface ServiceKeyStatus {
  name: ServiceKeyName;
  /** The secret is present and usable. */
  configured: boolean;
  /** The public key id (the one signed documents name), or null when there is none or the key has no id. */
  kid: string | null;
}

interface CountsRow {
  users_total: number;
  users_with_access: number;
  users_waiting: number;
  users_revoked: number;
  users_active_week: number;
  invites_pending: number;
  uploads_total: number;
  uploads_week: number;
  audit_exported_at: number | null;
}

const iso = (ms: number | null): string | null => (ms === null ? null : new Date(ms).toISOString());

/** Runs `load`; a missing or unusable secret (503 not_configured) reads as not configured. */
async function keyStatus(name: ServiceKeyName, load: () => Promise<string | null>): Promise<ServiceKeyStatus> {
  try {
    return { name, configured: true, kid: await load() };
  } catch (error) {
    if (error instanceof HttpError) return { name, configured: false, kid: null };
    throw error;
  }
}

export async function serviceKeyStatus(ctx: Context): Promise<ServiceKeyStatus[]> {
  const { env } = ctx;
  return Promise.all([
    keyStatus("licence", async () => (await loadSigningKey(env.LICENCE_SIGNING_KEY)).kid),
    keyStatus("config", async () => (await loadSigningKey(env.CONFIG_SIGNING_KEY)).kid),
    keyStatus("content", async () => (await loadSigningKey(env.CONTENT_SIGNING_KEY)).kid),
    keyStatus("invite", () => Promise.resolve(loadHmacKeyRing(env.INVITE_HMAC_KEYS).currentKid)),
    keyStatus("ban", () => Promise.resolve((loadBanKey(env.BAN_HMAC_KEY), null))),
  ]);
}

export async function getOverview(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const db = ctx.env.DB;
  const weekAgo = ctx.now - WEEK_MS;
  const result = await db
    .prepare(
      "SELECT " +
        "(SELECT COUNT(*) FROM users) AS users_total, " +
        "(SELECT COUNT(*) FROM users WHERE status = 'active' AND access_granted_at IS NOT NULL) AS users_with_access, " +
        "(SELECT COUNT(*) FROM users WHERE status = 'active' AND access_granted_at IS NULL) AS users_waiting, " +
        "(SELECT COUNT(*) FROM users WHERE status = 'revoked') AS users_revoked, " +
        "(SELECT COUNT(*) FROM users WHERE status = 'active' AND last_seen_at >= ?1) AS users_active_week, " +
        "(SELECT COUNT(*) FROM invites WHERE redeemed_at IS NULL AND revoked_at IS NULL AND expires_at > ?2) AS invites_pending, " +
        "(SELECT COUNT(*) FROM uploads) AS uploads_total, " +
        "(SELECT COUNT(*) FROM uploads WHERE created_at >= ?1) AS uploads_week, " +
        "(SELECT exported_at FROM audit_export_state WHERE id = 1) AS audit_exported_at",
    )
    .bind(weekAgo, ctx.now)
    .all<CountsRow>();
  const counts = result.results[0]!;
  const size = result.meta.size_after;
  const offline = await offlineKeyStatus(ctx);
  return json({
    environment: ctx.env.ENVIRONMENT,
    hosts: { api: ctx.env.API_HOST, admin: ctx.env.ADMIN_HOST, cdn: ctx.env.CDN_HOST },
    users: { total: counts.users_total, withAccess: counts.users_with_access, waitingForInvite: counts.users_waiting, revoked: counts.users_revoked, activeThisWeek: counts.users_active_week },
    invites: { pending: counts.invites_pending },
    uploads: { total: counts.uploads_total, thisWeek: counts.uploads_week },
    storage: await storageUsage(db, ctx.env.ENVIRONMENT),
    database: { sizeBytes: typeof size === "number" && Number.isSafeInteger(size) && size >= 0 ? size : null, freeLimitBytes: DATABASE_FREE_LIMIT_BYTES },
    keys: { service: await serviceKeyStatus(ctx), offline: offline.keys, keyReplacement: offline.keyReplacement },
    auditExport: { lastAt: iso(counts.audit_exported_at) },
    backup: await readBackupStatus(ctx.env),
  });
}
