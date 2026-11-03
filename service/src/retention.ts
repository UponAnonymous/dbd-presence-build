// Daily retention job (NFR-SVC-07, PLAN_V2 §4.3), run by the cron trigger in wrangler.toml:
// rate-limit counters 24 hours; used proof nonces once outside the clock-skew window; expired refresh tokens and
// families revoked over 90 days ago; audit rows 90 days, but only once exported (src/audit-export.ts), and never the
// newest exported row, which anchors truncation detection; accounts inactive for 12 months are deleted (audited; a
// revoked account's ban is kept, src/bans.ts) together with their uploads.

import { deleteUser } from "./routes/me.ts";


const DAY_MS = 24 * 60 * 60 * 1000;
export const RATE_LIMIT_RETENTION_MS = DAY_MS;
export const AUDIT_RETENTION_MS = 90 * DAY_MS;
export const SESSION_RETENTION_MS = 90 * DAY_MS;
export const INACTIVE_ACCOUNT_MS = 365 * DAY_MS;
const MAX_ACCOUNTS_PER_RUN = 50;

export interface RetentionReport {
  rateLimits: number;
  nonces: number;
  refreshTokens: number;
  families: number;
  auditRows: number;
  accounts: number;
}

export async function runRetention(db: D1Database, uploads: R2Bucket, banKey: Uint8Array, now: number): Promise<RetentionReport> {
  const [rateLimits, nonces, refreshTokens, families, auditRows] = await db.batch([
    db.prepare("DELETE FROM rate_limits WHERE window_start < ?").bind(now - RATE_LIMIT_RETENTION_MS),
    db.prepare("DELETE FROM proof_nonces WHERE expires_at < ?").bind(now),
    db.prepare("DELETE FROM refresh_tokens WHERE expires_at < ?").bind(now),
    db.prepare("DELETE FROM token_families WHERE revoked_at IS NOT NULL AND revoked_at < ?").bind(now - SESSION_RETENTION_MS),
    db
      .prepare("DELETE FROM audit_log WHERE at < ? AND seq < COALESCE((SELECT last_seq FROM audit_export_state WHERE id = 1), 0)")
      .bind(now - AUDIT_RETENTION_MS),
  ]);
  const { results: inactive } = await db
    .prepare("SELECT id FROM users WHERE last_seen_at < ? ORDER BY last_seen_at LIMIT ?")
    .bind(now - INACTIVE_ACCOUNT_MS, MAX_ACCOUNTS_PER_RUN)
    .all<{ id: string }>();
  let accounts = 0;
  for (const user of inactive) {
    // A user whose status changed meanwhile is left for the next run.
    if ((await deleteUser(db, uploads, banKey, now, user.id, "system", "account.expire_inactive")).outcome === "deleted") accounts++;
  }
  return {
    rateLimits: rateLimits!.meta.changes,
    nonces: nonces!.meta.changes,
    refreshTokens: refreshTokens!.meta.changes,
    families: families!.meta.changes,
    auditRows: auditRows!.meta.changes,
    accounts,
  };
}
