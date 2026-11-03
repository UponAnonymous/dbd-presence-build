-- Backup history (V2-S7; NFR-SVC-10, gate V7.2, PLAN_V2 §6 "encrypted exports kept 30 days").
-- Forward-only: never edit an applied migration; add a new numbered file.
--
-- The backup workflow (.github/workflows/backup-service.yml, service/scripts/backup.mjs) writes one row per daily
-- encrypted export and one per restore drill, with `wrangler d1 execute`. The Worker only reads it
-- (src/backup.ts readBackupStatus) for the Owner Panel's backup status. A row holds no personal data: the time,
-- the size of the encrypted file, the result, a short note (names and counts only) and, for an export, the name of the
-- backup object in R2 bucket dbd-presence-<env>-backups. The job finds the newest backup and the expired ones from
-- these names, because neither wrangler nor Cloudflare's public REST API lists the objects of an R2 bucket.

CREATE TABLE backup_runs (
  id          INTEGER PRIMARY KEY,
  kind        TEXT NOT NULL CHECK (kind IN ('export', 'drill')),
  at          INTEGER NOT NULL CHECK (at > 0),                  -- epoch milliseconds
  bytes       INTEGER NOT NULL CHECK (bytes >= 0),              -- size of the encrypted backup made (export) or tested (drill)
  ok          INTEGER NOT NULL CHECK (ok IN (0, 1)),
  detail      TEXT NOT NULL CHECK (length(detail) <= 300),
  object_key  TEXT CHECK (object_key IS NULL OR (length(object_key) <= 64 AND object_key LIKE 'd1/%'))
);
CREATE INDEX backup_runs_kind_at ON backup_runs (kind, at);

-- Keep the newest 100 rows (about three months of daily exports plus the drills).
CREATE TRIGGER backup_runs_keep_last_100 AFTER INSERT ON backup_runs
BEGIN
  DELETE FROM backup_runs WHERE id <= (SELECT id FROM backup_runs ORDER BY id DESC LIMIT 1 OFFSET 100);
END;
