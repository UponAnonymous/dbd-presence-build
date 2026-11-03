// Backup status for the Owner Panel (NFR-SVC-10, FR-ADM-01 Dashboard and Settings). The backup workflow
// (.github/workflows/backup-service.yml, service/scripts/backup.mjs) records each daily encrypted export and each restore
// drill in backup_runs (migrations/0009_backup_runs.sql); the Worker only reads that table. D1 Time Travel runs inside
// Cloudflare and the Worker cannot see it. Until a run is recorded every value is null and the panel shows "Not recorded
// yet"; no value is ever guessed.

import type { Env } from "./env.ts";

export interface BackupStatus {
  /** When the newest successful encrypted database export finished (ISO 8601), or null when none is recorded. */
  lastExportAt: string | null;
  /** Size of that export in bytes (encrypted), or null. */
  lastExportBytes: number | null;
  /** When the newest restore drill was done (ISO 8601), passed or not, or null when none is recorded. */
  lastDrillAt: string | null;
  /** Whether that drill restored the data correctly, or null. */
  lastDrillOk: boolean | null;
}

interface RunRow {
  at: number;
  bytes: number;
  ok: number;
}

export async function readBackupStatus(env: Env): Promise<BackupStatus> {
  // A failed export is not a backup, so the export fields name the newest successful one; a failed drill is reported.
  const [exports, drills] = await env.DB.batch<RunRow>([
    env.DB.prepare("SELECT at, bytes, ok FROM backup_runs WHERE kind = 'export' AND ok = 1 ORDER BY at DESC, id DESC LIMIT 1"),
    env.DB.prepare("SELECT at, bytes, ok FROM backup_runs WHERE kind = 'drill' ORDER BY at DESC, id DESC LIMIT 1"),
  ]);
  const lastExport = exports?.results[0] ?? null;
  const lastDrill = drills?.results[0] ?? null;
  return {
    lastExportAt: lastExport ? new Date(lastExport.at).toISOString() : null,
    lastExportBytes: lastExport ? lastExport.bytes : null,
    lastDrillAt: lastDrill ? new Date(lastDrill.at).toISOString() : null,
    lastDrillOk: lastDrill ? lastDrill.ok === 1 : null,
  };
}
