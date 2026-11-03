// Worker entry point: the DBD Presence online service (PLAN_V2 §1, §5; ADR-0010). Never shipped in the desktop app.

import { defaultDeps, handleRequest } from "./app.ts";
import type { Env } from "./env.ts";
import { runAuditExport } from "./audit-export.ts";
import { runRetention } from "./retention.ts";
import { loadBanKey } from "./bans.ts";
import { sweepUploads } from "./db/uploads.ts";
import { deleteReleased } from "./storage.ts";
import { runRosterMaintenance } from "./db/roster.ts";
import { sweepReleases } from "./routes/releases.ts";

export default {
  fetch(request, env) {
    return handleRequest(request, env, defaultDeps());
  },
  scheduled(controller, env, ctx) {
    ctx.waitUntil(daily(env, controller.scheduledTime));
  },
} satisfies ExportedHandler<Env>;

/** The daily cron: the audit export first, then retention (which deletes only audit rows already exported), the upload sweep and the roster re-sign. */
async function daily(env: Env, now: number): Promise<void> {
  try {
    const exported = await runAuditExport(env.DB, env.AUDIT_EXPORT, env.ENVIRONMENT, now);
    console.log(JSON.stringify({ event: "audit_export", objects: exported.objects, rows: exported.rows, headSeq: exported.head?.seq ?? null }));
  } catch (error) {
    // Retention still runs: it keeps every row that has not been exported.
    console.error(JSON.stringify({ event: "audit_export_failed", type: error instanceof Error ? error.name : typeof error }));
  }
  const report = await runRetention(env.DB, env.UPLOADS, loadBanKey(env.BAN_HMAC_KEY), now);
  console.log(JSON.stringify({ event: "retention", ...report }));
  // After retention, which may have deleted accounts and with them their uploads.
  const swept = await sweepUploads(env.DB, env.UPLOADS, now);
  console.log(JSON.stringify({ event: "upload_sweep", ...swept }));
  // Pack files whose R2 delete failed earlier (tombstones).
  const packs = await deleteReleased(env.DB, "packs", env.PACKS);
  console.log(JSON.stringify({ event: "pack_tombstones", ...packs }));
  // Update packages no feed lists any more, failed deletes and unfinished uploads (V2-S6, D18).
  const releaseSweep = await sweepReleases(env, now);
  console.log(JSON.stringify({ event: "release_sweep", ...releaseSweep }));
  // The weekly re-sign of the character roster (PLAN_V2_S5B §2.3) and the expiry of old sightings (D35).
  const roster = await runRosterMaintenance(env.DB, now);
  console.log(JSON.stringify({ event: "roster_maintenance", ...roster }));
}
