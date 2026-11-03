// NFR-SVC-10 / gate V7.2 (daily encrypted export and restore drill), PLAN_V2 §6 (exports kept 30 days), NFR-SVC-11 / D18
// (the backups stay inside the free storage tier), FR-ADM-06 (separate environments). Covers migration 0009
// (backup_runs), readBackupStatus (src/backup.ts), the backup workflow's rules (scripts/backup-plan.mjs) and the
// workflow and owner guide text.

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, inject, it } from "vitest";
import {
  BACKUP_BUDGET_BYTES,
  BACKUP_GITHUB_ENVIRONMENTS,
  BACKUP_MAX_BYTES,
  BACKUP_OBJECT_KEY,
  analyseDump,
  assertDrillTarget,
  backupBucketName,
  backupBudgetBytes,
  backupLifecycleRule,
  backupLockRule,
  BACKUP_LOCK_DAYS,
  BACKUP_RETENTION_DAYS,
  backupObjectKey,
  compareRestore,
  countsFromRow,
  d1JsonResults,
  decryptionVerified,
  drillDatabaseName,
  drillDatabasePattern,
  DRILL_SWEEP_AGE_MS,
  staleDrillDatabases,
  drillWranglerConfig,
  expiredBackups,
  insertRunSql,
  readBackupKey,
  scheduledRunReady,
  renderBackupSummary,
  rowCountQueries,
  safeDetail,
  safeLogLines,
  splitSqlStatements,
  storageCheck,
  wranglerLog,
} from "../scripts/backup-plan.mjs";
import { auditLifecycleRule, auditLockRule, planLifecycleRules, planLockRules, renderSummary } from "../scripts/provision-plan.mjs";
import { readBackupStatus } from "../src/backup.ts";
import { PRODUCTION_STORAGE_LIMITS, STAGING_STORAGE_LIMITS } from "../src/storage.ts";
import { resetDatabase } from "./helpers.ts";

const DAY = 86_400_000;
const NOW = Date.parse("2026-11-02T04:41:00.000Z");
const KEY = "q3X9mB2kV7pL0sD4fG8hJ1zC6nR5tY2uW9eA3oI7xQ=";
const LIVE_ID = "3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8";
const DRILL_ID = "9e8d7c6b-5a49-4382-9170-6f5e4d3c2b1a";

async function run(sql: string): Promise<void> {
  await env.DB.prepare(sql).run();
}

async function count(): Promise<number> {
  return (await env.DB.prepare("SELECT COUNT(*) AS n FROM backup_runs").first<{ n: number }>())!.n;
}

beforeEach(resetDatabase);

describe("migration 0009 (backup_runs)", () => {
  it("creates the table with the run kinds, sizes, results and backup names", async () => {
    const { results } = await env.DB.prepare("SELECT name, type, \"notnull\" AS required FROM pragma_table_info('backup_runs') ORDER BY cid").all<{ name: string; type: string; required: number }>();
    expect(results.map((c) => [c.name, c.type, c.required])).toEqual([
      ["id", "INTEGER", 0],
      ["kind", "TEXT", 1],
      ["at", "INTEGER", 1],
      ["bytes", "INTEGER", 1],
      ["ok", "INTEGER", 1],
      ["detail", "TEXT", 1],
      ["object_key", "TEXT", 0],
    ]);
  });

  it("refuses an unknown kind, a non-boolean result, a negative size, a long note and a foreign object name", async () => {
    const insert = (kind: string, at: number, bytes: number, ok: number, detail: string, key: string | null) =>
      env.DB.prepare("INSERT INTO backup_runs (kind, at, bytes, ok, detail, object_key) VALUES (?, ?, ?, ?, ?, ?)").bind(kind, at, bytes, ok, detail, key).run();
    await expect(insert("restore", NOW, 1, 1, "", null)).rejects.toThrow(/CHECK/);
    await expect(insert("export", NOW, 1, 2, "", null)).rejects.toThrow(/CHECK/);
    await expect(insert("export", NOW, -1, 1, "", null)).rejects.toThrow(/CHECK/);
    await expect(insert("export", 0, 1, 1, "", null)).rejects.toThrow(/CHECK/);
    await expect(insert("drill", NOW, 1, 1, "x".repeat(301), null)).rejects.toThrow(/CHECK/);
    await expect(insert("export", NOW, 1, 1, "", "packs/abc.png")).rejects.toThrow(/CHECK/);
    await insert("export", NOW, 1, 1, "x".repeat(300), backupObjectKey(NOW));
    expect(await count()).toBe(1);
  });

  it("keeps only the newest 100 rows", async () => {
    for (let i = 1; i <= 105; i++) await run(insertRunSql({ kind: i % 10 === 0 ? "drill" : "export", at: NOW + i, bytes: i, ok: true, detail: `run ${i}` }));
    expect(await count()).toBe(100);
    const oldest = await env.DB.prepare("SELECT MIN(bytes) AS b, MAX(bytes) AS m FROM backup_runs").first<{ b: number; m: number }>();
    expect(oldest).toEqual({ b: 6, m: 105 });
  });
});

describe("readBackupStatus", () => {
  it("is all null while nothing is recorded", async () => {
    expect(await readBackupStatus(env)).toEqual({ lastExportAt: null, lastExportBytes: null, lastDrillAt: null, lastDrillOk: null });
  });

  it("reports the newest successful export, not a later failed one, and the newest drill even when it failed", async () => {
    await run(insertRunSql({ kind: "export", at: NOW - 2 * DAY, bytes: 6000, ok: true, detail: "31 tables", objectKey: backupObjectKey(NOW - 2 * DAY) }));
    await run(insertRunSql({ kind: "export", at: NOW - DAY, bytes: 6410, ok: true, detail: "31 tables", objectKey: backupObjectKey(NOW - DAY) }));
    await run(insertRunSql({ kind: "export", at: NOW, bytes: 0, ok: false, detail: "failed: Exporting the database failed" }));
    await run(insertRunSql({ kind: "drill", at: NOW - 3 * DAY, bytes: 6000, ok: true, detail: "passed" }));
    expect(await readBackupStatus(env)).toEqual({ lastExportAt: new Date(NOW - DAY).toISOString(), lastExportBytes: 6410, lastDrillAt: new Date(NOW - 3 * DAY).toISOString(), lastDrillOk: true });
    await run(insertRunSql({ kind: "drill", at: NOW - 3600_000, bytes: 6410, ok: false, detail: "failed: table users is missing" }));
    expect(await readBackupStatus(env)).toMatchObject({ lastDrillAt: new Date(NOW - 3600_000).toISOString(), lastDrillOk: false });
  });

  it("reads exactly what the job's SQL writes, and the job's history query finds the recorded backups newest first", async () => {
    await run(insertRunSql({ kind: "export", at: NOW - DAY, bytes: 10, ok: true, detail: "a", objectKey: backupObjectKey(NOW - DAY) }));
    await run(insertRunSql({ kind: "export", at: NOW, bytes: 20, ok: true, detail: "b", objectKey: backupObjectKey(NOW) }));
    const { RECENT_EXPORTS_SQL } = await import("../scripts/backup-plan.mjs");
    const { results } = await env.DB.prepare(RECENT_EXPORTS_SQL).all<{ at: number; bytes: number; object_key: string }>();
    expect(results).toEqual([
      { at: NOW, bytes: 20, object_key: "d1/2026-11-02T044100Z.sql.gz.gpg" },
      { at: NOW - DAY, bytes: 10, object_key: "d1/2026-11-01T044100Z.sql.gz.gpg" },
    ]);
  });
});

describe("backup key", () => {
  it("is required, long enough and free of spaces and line breaks, and its value never appears in an error", () => {
    expect(() => readBackupKey(undefined, "staging")).toThrow(/BACKUP_KEY is missing for staging[\s\S]*docs\/owner\/backups\.md, step 1/);
    expect(() => readBackupKey("   ", "production")).toThrow(/production-backups/);
    for (const bad of ["short-key", `${KEY.slice(0, 20)} ${KEY.slice(20)}`, `${KEY}\nsecond line`]) {
      let text = "";
      try {
        readBackupKey(bad, "staging");
      } catch (error) {
        text = (error as Error).message;
      }
      expect(text).toMatch(/too short or has spaces or line breaks/);
      expect(text).not.toContain(bad.slice(0, 8));
    }
    expect(readBackupKey(`${KEY}\r\n`, "staging")).toBe(KEY);
    expect(BACKUP_GITHUB_ENVIRONMENTS).toEqual({ staging: "staging", production: "production-backups" });
  });

  it("accepts a decryption only with gpg's integrity check passed", () => {
    const good = "[GNUPG:] NEED_PASSPHRASE_SYM 9 3 10\n[GNUPG:] BEGIN_DECRYPTION\n[GNUPG:] DECRYPTION_OKAY\n[GNUPG:] GOODMDC\n[GNUPG:] END_DECRYPTION\n";
    expect(decryptionVerified(0, good)).toBe(true);
    expect(decryptionVerified(2, good)).toBe(false);
    expect(decryptionVerified(0, good.replace("[GNUPG:] GOODMDC\n", ""))).toBe(false);
    // What gpg 2.4 prints for a manipulated file (it still writes the output, so the status decides).
    expect(decryptionVerified(0, "[GNUPG:] PLAINTEXT 62 0 a.gz\n[GNUPG:] BADMDC\n[GNUPG:] DECRYPTION_FAILED\n")).toBe(false);
    expect(decryptionVerified(0, "[GNUPG:] ERROR symkey_decrypt.maybe_error 11_BAD_PASSPHRASE\n[GNUPG:] DECRYPTION_FAILED\n")).toBe(false);
  });
});

describe("backup bucket, names and storage (D18)", () => {
  it("uses a separate bucket per environment with a 30-day expiry, next to the audit rule", () => {
    expect(backupBucketName("staging")).toBe("dbd-presence-staging-backups");
    expect(backupBucketName("production")).toBe("dbd-presence-production-backups");
    expect(() => backupBucketName("prod")).toThrow();
    expect(backupLifecycleRule()).toEqual({ id: "expire-after-30-days", enabled: true, conditions: {}, deleteObjectsTransition: { condition: { type: "Age", maxAge: 30 * 86400 } } });
    const plan = planLifecycleRules([auditLifecycleRule()], backupLifecycleRule());
    expect(plan).toEqual({ action: "add", rules: [auditLifecycleRule(), backupLifecycleRule()] });
    expect(planLifecycleRules([backupLifecycleRule()], backupLifecycleRule()).action).toBe("keep");
    expect(planLifecycleRules([{ ...backupLifecycleRule(), deleteObjectsTransition: { condition: { type: "Age", maxAge: 86400 } } }], backupLifecycleRule()).action).toBe("conflict");
  });

  it("is not a Worker binding, so the Worker can neither read nor delete backups", () => {
    for (const config of [inject("wranglerProduction"), inject("wranglerStaging")]) expect(config.r2.map((b) => b.name).filter((n) => n.includes("backups"))).toEqual([]);
  });

  it("names objects d1/<UTC date>T<time>Z.sql.gz.gpg", () => {
    expect(backupObjectKey(Date.parse("2026-10-25T04:41:07.999Z"))).toBe("d1/2026-10-25T044107Z.sql.gz.gpg");
    expect(BACKUP_OBJECT_KEY.test(backupObjectKey(NOW))).toBe(true);
    expect(BACKUP_OBJECT_KEY.test("d1/../packs/x.png")).toBe(false);
    expect(() => backupObjectKey(Number.NaN)).toThrow();
  });

  it("gives each environment its own backup budget: 800 MB for production, 200 MB for staging, 25 MB per backup", () => {
    expect(BACKUP_MAX_BYTES).toBe(25_000_000);
    expect(BACKUP_BUDGET_BYTES).toEqual({ staging: 200_000_000, production: 800_000_000 });
    expect(backupBudgetBytes("production")).toBe(800_000_000);
    expect(backupBudgetBytes("staging")).toBe(200_000_000);
    for (const env of ["", "prod", "Production", "test"]) expect(() => backupBudgetBytes(env)).toThrow(/staging or production/);
    expect(() => storageCheck({ env: "test", newBytes: 1, exports: [], now: NOW })).toThrow(/staging or production/);
    // Production keeps 31 maximum-size copies (30 days plus the day the expiry rule may lag) inside its budget.
    expect(31 * BACKUP_MAX_BYTES).toBeLessThanOrEqual(backupBudgetBytes("production"));
  });

  it("keeps both environments' storage caps plus both backup budgets inside the one 10 GB free tier (9.75 GB worst case)", () => {
    const total = PRODUCTION_STORAGE_LIMITS.capBytes + STAGING_STORAGE_LIMITS.capBytes + backupBudgetBytes("production") + backupBudgetBytes("staging");
    expect(total).toBe(9_750_000_000);
    expect(total).toBeLessThanOrEqual(10_000_000_000);
  });

  it("refuses a backup over 25 MB, or one that would pass the budget, with a clear reason", () => {
    expect(storageCheck({ env: "production", newBytes: 6410, exports: [], now: NOW })).toEqual({ ok: true, storedBytes: 0, reason: null });
    for (const env of ["staging", "production"]) {
      const big = storageCheck({ env, newBytes: BACKUP_MAX_BYTES + 1, exports: [], now: NOW });
      expect(big.ok).toBe(false);
      expect(big.reason).toMatch(/more than the 25\.0 MB limit[\s\S]*not uploaded[\s\S]*Time Travel/);
      expect(storageCheck({ env, newBytes: BACKUP_MAX_BYTES, exports: [], now: NOW }).ok).toBe(true);
    }
    // 31 copies of 25 MB leave room for one more of 25 MB (the day the expiry rule lags), and not a byte more.
    const full = Array.from({ length: 31 }, (_, i) => ({ at: NOW - i * DAY, bytes: 25_000_000, object_key: backupObjectKey(NOW - i * DAY) }));
    expect(storageCheck({ env: "production", newBytes: 25_000_000, exports: full, now: NOW })).toMatchObject({ ok: true, storedBytes: 775_000_000 });
    const fuller = [...full, { at: NOW - 31 * DAY, bytes: 25_000_000, object_key: backupObjectKey(NOW - 31 * DAY) }];
    const over = storageCheck({ env: "production", newBytes: 1, exports: fuller, now: NOW });
    expect(over).toMatchObject({ ok: false, storedBytes: 800_000_000 });
    expect(over.reason).toMatch(/would pass the 800\.0 MB budget for production backups/);
    expect(storageCheck({ env: "production", newBytes: 0, exports: [], now: NOW }).ok).toBe(false);
  });

  it("holds staging's backups to its 200 MB budget, which production's total would pass", () => {
    const stored = Array.from({ length: 8 }, (_, i) => ({ at: NOW - i * DAY, bytes: 24_000_000, object_key: backupObjectKey(NOW - i * DAY) }));
    const staging = storageCheck({ env: "staging", newBytes: 9_000_000, exports: stored, now: NOW });
    expect(staging).toMatchObject({ ok: false, storedBytes: 192_000_000 });
    expect(staging.reason).toMatch(/would pass the 200\.0 MB budget for staging backups/);
    expect(storageCheck({ env: "staging", newBytes: 8_000_000, exports: stored, now: NOW }).ok).toBe(true);
    expect(storageCheck({ env: "production", newBytes: 9_000_000, exports: stored, now: NOW }).ok).toBe(true);
  });

  it("counts a backup as stored until it is 31 days old, or longer when the job could not delete it", () => {
    const old = { at: NOW - 40 * DAY, bytes: 500_000_000, object_key: backupObjectKey(NOW - 40 * DAY) };
    const recent = { at: NOW - 30.5 * DAY, bytes: 1_000, object_key: backupObjectKey(NOW - 30.5 * DAY) };
    expect(storageCheck({ env: "production", newBytes: 1, exports: [old, recent], now: NOW }).storedBytes).toBe(1_000);
    expect(storageCheck({ env: "production", newBytes: 1, exports: [old, recent], now: NOW, stillStored: [old.object_key] }).storedBytes).toBe(500_001_000);
    expect(expiredBackups([old, recent, { at: NOW - 50 * DAY, bytes: 1, object_key: "packs/x.png" }], NOW)).toEqual([old.object_key, recent.object_key]);
  });
});

describe("recording runs", () => {
  it("builds only safe SQL: fixed kinds, whole numbers, a cleaned note and a checked object name", async () => {
    const sql = insertRunSql({ kind: "drill", at: NOW, bytes: 5, ok: false, detail: "failed: x'); DROP TABLE users; --\nnext", objectKey: null });
    expect(sql).not.toMatch(/;|\n/);
    expect(sql.split("'").length - 1).toBe(4);
    await run(sql);
    expect(await env.DB.prepare("SELECT detail FROM backup_runs").first<{ detail: string }>()).toEqual({ detail: "failed: x ) DROP TABLE users -- next" });
    expect(await env.DB.prepare("SELECT COUNT(*) AS n FROM users").first<{ n: number }>()).toEqual({ n: 0 });
    expect(() => insertRunSql({ kind: "restore", at: NOW, bytes: 1, ok: true, detail: "" })).toThrow();
    expect(() => insertRunSql({ kind: "export", at: 1.5, bytes: 1, ok: true, detail: "" })).toThrow();
    expect(() => insertRunSql({ kind: "export", at: NOW, bytes: 1, ok: true, detail: "", objectKey: "d1/x'; --" })).toThrow();
    expect(safeDetail("a".repeat(400)).length).toBe(300);
  });

  it("reads wrangler's --json output, also after a warning line, and notices a failed query", () => {
    const out = '▲ [WARNING] Proxy environment variables detected.\n[\n  {\n    "results": [{ "name": "users" }],\n    "success": true,\n    "meta": {}\n  }\n]\n';
    expect(d1JsonResults(out)).toEqual([{ name: "users" }]);
    expect(() => d1JsonResults("Error: no such table")).toThrow(/no JSON/);
    expect(() => d1JsonResults('[{"results": [], "success": false}]')).toThrow(/failed/);
  });
});

// The shape `wrangler d1 export` writes (checked against a local export of the real migrations), with the hard cases: a
// quote and a semicolon inside a value, a line break inside a value, an apostrophe in a schema comment, a trigger with
// CASE … END inside BEGIN … END, a multi-row INSERT and SQLite's sequence table.
const DUMP = `PRAGMA defer_foreign_keys=TRUE;
CREATE TABLE IF NOT EXISTS "d1_migrations"(
		id         INTEGER PRIMARY KEY AUTOINCREMENT,
		name       TEXT UNIQUE,
		applied_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP NOT NULL
);
INSERT INTO "d1_migrations" ("id","name","applied_at") VALUES(1,'0001_initial.sql','2026-10-25 09:22:34');
INSERT INTO "d1_migrations" ("id","name","applied_at") VALUES(2,'0002_packs_and_config.sql','2026-10-25 09:22:34');
CREATE TABLE users (
  id           TEXT PRIMARY KEY,   -- the user's random id; it's never the Discord id
  display_name TEXT NOT NULL
);
INSERT INTO "users" ("id","display_name") VALUES('u1','O''Brien; DROP TABLE users; --');
INSERT INTO "users" ("id","display_name") VALUES('u2','two
lines'),('u3','three');
CREATE TABLE pack_objects (key TEXT, kind TEXT);
CREATE TRIGGER pack_objects_removed AFTER DELETE ON pack_objects
BEGIN
  UPDATE storage_objects SET released = 1 WHERE key = OLD.key || CASE OLD.kind WHEN 'png' THEN '.png' ELSE '.jpg' END;
  INSERT INTO users (id, display_name) VALUES ('never', 'counted');
END;
CREATE TABLE "empty table" (x INTEGER);
DELETE FROM sqlite_sequence;
INSERT INTO "sqlite_sequence" ("name","seq") VALUES('d1_migrations',2);
CREATE INDEX users_name ON users (display_name);
`;

describe("reading a dump", () => {
  it("splits statements without being fooled by quotes, comments, line breaks or trigger bodies", () => {
    const statements = splitSqlStatements(DUMP);
    expect(statements.filter((s) => s.startsWith("CREATE TRIGGER"))).toHaveLength(1);
    expect(statements.find((s) => s.startsWith("CREATE TRIGGER"))!.endsWith("END")).toBe(true);
    expect(statements.some((s) => s.startsWith("END"))).toBe(false);
    expect(statements).toHaveLength(13);
    expect(() => splitSqlStatements("INSERT INTO x VALUES('open")).toThrow(/quoted/);
  });

  it("counts the rows of each table and lists the migrations, leaving out SQLite's own tables", () => {
    const dump = analyseDump(DUMP);
    expect(dump.tables).toEqual([
      { name: "d1_migrations", rows: 2 },
      { name: "empty table", rows: 0 },
      { name: "pack_objects", rows: 0 },
      { name: "users", rows: 3 },
    ]);
    expect(dump.migrations).toEqual(["0001_initial.sql", "0002_packs_and_config.sql"]);
    expect(dump.rows).toBe(5);
    expect(() => analyseDump('CREATE TABLE t (x);\nINSERT INTO "t" SELECT 1;')).toThrow(/cannot count/);
  });
});

describe("restore drill", () => {
  const dump = analyseDump(DUMP);
  const tables = ["d1_migrations", "empty table", "pack_objects", "sqlite_sequence", "users"];
  const counts = [
    { name: "d1_migrations", n: 2 },
    { name: "empty table", n: 0 },
    { name: "pack_objects", n: 0 },
    { name: "users", n: 3 },
  ];
  const repo = ["0001_initial.sql", "0002_packs_and_config.sql", "0003_uploads_and_storage.sql"];

  it("passes when every table and the migrations list match, and says the code has newer migrations", () => {
    const result = compareRestore({ dump, restoredTables: tables, restoredCounts: counts, restoredMigrations: dump.migrations, repoMigrations: repo });
    expect(result).toMatchObject({ ok: true, problems: [], newerMigrations: 1 });
    expect(result.rows.map((r) => r.table)).toEqual(["d1_migrations", "empty table", "pack_objects", "users"]);
  });

  it("fails on a missing table, an extra table, a different count or a different migrations list", () => {
    const check = (over: Partial<Parameters<typeof compareRestore>[0]>) =>
      compareRestore({ dump, restoredTables: tables, restoredCounts: counts, restoredMigrations: dump.migrations, repoMigrations: repo, ...over });
    expect(check({ restoredTables: tables.filter((t) => t !== "users") }).problems).toEqual(["table users is missing from the restored copy"]);
    expect(check({ restoredTables: [...tables, "extra"], restoredCounts: [...counts, { name: "extra", n: 0 }] }).problems).toEqual(["table extra is in the restored copy but not in the backup"]);
    expect(check({ restoredCounts: counts.map((c) => (c.name === "users" ? { name: "users", n: 2 } : c)) }).problems).toEqual(["table users: 3 rows in the backup, 2 restored"]);
    expect(check({ restoredMigrations: ["0001_initial.sql"] }).ok).toBe(false);
    expect(check({ repoMigrations: ["0001_initial.sql", "0002_other.sql"] }).problems).toEqual(["the backup's migrations do not match the start of service/migrations"]);
    expect(compareRestore({ dump: { tables: [], migrations: [], statements: 0, rows: 0 }, restoredTables: [], restoredCounts: [], restoredMigrations: [], repoMigrations: repo }).ok).toBe(false);
  });

  it("counts rows with plain subqueries (D1 refuses long UNION ALL chains) and only for real table names", async () => {
    const queries = rowCountQueries(["users", "settings", "roster_characters"]);
    expect(queries).toHaveLength(1);
    expect(queries[0]).not.toContain("UNION");
    const row = await env.DB.prepare(queries[0]!).first<Record<string, number>>();
    expect(countsFromRow(row)).toEqual([
      { name: "users", n: 0 },
      { name: "settings", n: 2 },
      { name: "roster_characters", n: expect.any(Number) as number },
    ]);
    expect(rowCountQueries(Array.from({ length: 120 }, (_, i) => `t${i}`))).toHaveLength(3);
    expect(() => rowCountQueries(['users"; DROP TABLE users; --'])).toThrow(/table name/);
  });

  it("only ever targets a temporary drill database of the same environment, never the live one", () => {
    const name = drillDatabaseName("staging", "123456789-1");
    expect(name).toBe("dbd-presence-staging-restore-drill-123456789-1");
    expect(() => drillDatabaseName("staging", "1; rm")).toThrow();
    expect(assertDrillTarget({ env: "staging", name, id: DRILL_ID, liveName: "dbd-presence-staging", liveId: LIVE_ID })).toBe(true);
    expect(() => assertDrillTarget({ env: "staging", name: "dbd-presence-staging", id: DRILL_ID, liveName: "dbd-presence-staging", liveId: LIVE_ID })).toThrow(/not a restore-drill/);
    expect(() => assertDrillTarget({ env: "production", name, id: DRILL_ID, liveName: "dbd-presence-production", liveId: LIVE_ID })).toThrow(/not a restore-drill/);
    expect(() => assertDrillTarget({ env: "staging", name, id: LIVE_ID.toUpperCase(), liveName: "dbd-presence-staging", liveId: LIVE_ID })).toThrow(/live database/);
    expect(drillWranglerConfig(name, DRILL_ID)).toEqual({ name: "dbd-presence-restore-drill", compatibility_date: "2026-10-01", d1_databases: [{ binding: "RESTORE", database_name: name, database_id: DRILL_ID }] });
  });

  it("sweeps only this environment's drill databases older than a day, never the live one or other names (TM-14)", () => {
    expect(DRILL_SWEEP_AGE_MS).toBe(86_400_000);
    const day = 86_400_000;
    const iso = (ms: number) => new Date(ms).toISOString().replace("Z", "123Z"); // Cloudflare lists microseconds
    const id = (n: number) => `9e8d7c6b-5a49-4382-9170-6f5e4d3c2b${String(n).padStart(2, "0")}`;
    const databases = [
      { uuid: id(1), name: "dbd-presence-staging-restore-drill-123456789-1", created_at: iso(NOW - day - 1000) },
      { uuid: id(2), name: "dbd-presence-staging-restore-drill-555", created_at: iso(NOW - 3 * day) },
      { uuid: id(3), name: "dbd-presence-staging-restore-drill-777-2", created_at: iso(NOW - day + 60_000) },
      { uuid: id(4), name: "dbd-presence-production-restore-drill-123", created_at: iso(NOW - 5 * day) },
      { uuid: id(5), name: "dbd-presence-staging", created_at: iso(NOW - 90 * day) },
      { uuid: id(6), name: "dbd-presence-staging-restore-drill-12x", created_at: iso(NOW - 5 * day) },
      { uuid: id(7), name: "my-dbd-presence-staging-restore-drill-1", created_at: iso(NOW - 5 * day) },
      { uuid: id(8), name: "dbd-presence-staging-restore-drill-9", created_at: "not a date" },
      { uuid: id(9), name: "dbd-presence-staging-restore-drill-10" },
      { uuid: "not-a-uuid", name: "dbd-presence-staging-restore-drill-11", created_at: iso(NOW - 5 * day) },
      { uuid: LIVE_ID, name: "dbd-presence-staging-restore-drill-12", created_at: iso(NOW - 5 * day) },
      null,
    ];
    const { remove, kept } = staleDrillDatabases({ env: "staging", databases, now: NOW, liveName: "dbd-presence-staging", liveId: LIVE_ID });
    expect(remove.map((d) => d.name)).toEqual(["dbd-presence-staging-restore-drill-123456789-1", "dbd-presence-staging-restore-drill-555"]);
    expect(remove[0]).toEqual({ name: "dbd-presence-staging-restore-drill-123456789-1", id: id(1), createdAt: NOW - day - 1000 });
    for (const d of remove) expect(assertDrillTarget({ env: "staging", name: d.name, id: d.id, liveName: "dbd-presence-staging", liveId: LIVE_ID })).toBe(true);
    expect(kept).toEqual([
      { name: "dbd-presence-staging-restore-drill-777-2", reason: "it is less than a day old (a drill may still be using it)" },
      { name: "dbd-presence-staging-restore-drill-9", reason: "its creation time is unknown" },
      { name: "dbd-presence-staging-restore-drill-10", reason: "its creation time is unknown" },
      { name: "dbd-presence-staging-restore-drill-11", reason: "it has no valid database id" },
      { name: "dbd-presence-staging-restore-drill-12", reason: "it is the live database" },
    ]);
    expect(staleDrillDatabases({ env: "production", databases, now: NOW, liveName: "dbd-presence-production", liveId: null }).remove.map((d) => d.name)).toEqual(["dbd-presence-production-restore-drill-123"]);
    expect(staleDrillDatabases({ env: "staging", databases: undefined, now: NOW, liveName: "dbd-presence-staging", liveId: LIVE_ID })).toEqual({ remove: [], kept: [] });
    expect(() => staleDrillDatabases({ env: "prod", databases, now: NOW, liveName: "x", liveId: null })).toThrow();
    // The sweep pattern is exactly the names the drill makes.
    expect(drillDatabasePattern("staging").test(drillDatabaseName("staging", "123456789-1"))).toBe(true);
    expect(drillDatabasePattern("staging").test("dbd-presence-staging")).toBe(false);
  });

  it("reports the swept drill databases", () => {
    const text = renderBackupSummary({ env: "production", sweep: { deleted: ["dbd-presence-production-restore-drill-5"], failed: ["dbd-presence-production-restore-drill-6"] } });
    expect(text).toContain("## Leftover drill databases");
    expect(text).toContain("`dbd-presence-production-restore-drill-5`: deleted");
    expect(text).toContain("`dbd-presence-production-restore-drill-6`: **could not be deleted;");
    expect(renderBackupSummary({ env: "staging", sweep: { deleted: [], failed: [] } })).toContain("None older than a day was found.");
  });

  it("reports table names and counts only, never row contents", () => {
    const comparison = compareRestore({ dump, restoredTables: tables, restoredCounts: counts, restoredMigrations: dump.migrations, repoMigrations: repo });
    const text = renderBackupSummary({ env: "staging", drill: { key: backupObjectKey(NOW), bytes: 6410, database: drillDatabaseName("staging", "1"), migrations: 2, comparison, deleted: true, error: null } });
    expect(text).toContain("**The drill passed.**");
    expect(text).toContain("| users | 3 | 3 | yes |");
    expect(text).toContain("deleted");
    for (const secret of ["Brien", "lines", "three", "u1"]) expect(text).not.toContain(secret);
    const failed = renderBackupSummary({ env: "production", drill: { key: null, bytes: 0, database: "dbd-presence-production-restore-drill-1", migrations: 0, comparison: null, deleted: false, error: "no backup is recorded yet" } });
    expect(failed).toMatch(/The drill failed\.\*\* no backup is recorded yet/);
    expect(failed).toMatch(/could not be deleted; delete it in the dashboard/);
  });

  it("tells the owner plainly whether a backup was made", () => {
    const made = renderBackupSummary({ env: "production", exportRun: { key: backupObjectKey(NOW), bytes: 6410, tables: 31, rows: 116, uploaded: true, lifecycle: "keep", storedBytes: 12_820, deleted: [], error: null } });
    expect(made).toMatch(/\*\*Backup made\.\*\* `d1\/2026-11-02T044100Z\.sql\.gz\.gpg`/);
    expect(made).toContain("dbd-presence-production-backups");
    expect(made).toMatch(/expiry rule on the bucket: confirmed/);
    expect(made).toContain("Backups stored for production before this one: 0.0 MB of the 800.0 MB budget.");
    const madeStaging = renderBackupSummary({ env: "staging", exportRun: { key: backupObjectKey(NOW), bytes: 6410, tables: 31, rows: 116, uploaded: true, lifecycle: "keep", storedBytes: 12_820, deleted: [], error: null } });
    expect(madeStaging).toContain("Backups stored for staging before this one: 0.0 MB of the 200.0 MB budget.");
    const refused = renderBackupSummary({ env: "staging", exportRun: { key: backupObjectKey(NOW), bytes: 40_000_000, tables: 31, rows: 1, uploaded: false, lifecycle: "missing", deleted: [], error: storageCheck({ env: "staging", newBytes: 40_000_000, exports: [], now: NOW }).reason } });
    expect(refused).toMatch(/No backup was made\.\*\* The encrypted backup is 40\.0 MB/);
    expect(renderBackupSummary({ env: "staging" })).toMatch(/Nothing was backed up[\s\S]*BACKUP_KEY/);
  });

  it("locks every backup for 29 days, one day less than its 30-day expiry (TM-03)", () => {
    expect(BACKUP_LOCK_DAYS).toBe(29);
    expect(BACKUP_LOCK_DAYS).toBeLessThan(BACKUP_RETENTION_DAYS);
    expect(backupLockRule()).toEqual({ id: "backups-29-days", enabled: true, condition: { type: "Age", maxAgeSeconds: 29 * 86400 } });
    expect(backupLockRule()).not.toHaveProperty("prefix");
    // Planned on its own bucket like the audit lock: added when missing, kept when equal, never weakened when different.
    expect(planLockRules([], backupLockRule())).toEqual({ action: "add", rules: [backupLockRule()] });
    expect(planLockRules([auditLockRule()], backupLockRule())).toEqual({ action: "add", rules: [auditLockRule(), backupLockRule()] });
    expect(planLockRules([backupLockRule()], backupLockRule()).action).toBe("keep");
    const shorter = { ...backupLockRule(), condition: { type: "Age", maxAgeSeconds: 86400 } };
    expect(planLockRules([shorter], backupLockRule())).toEqual({ action: "conflict", rules: [shorter] });
    expect(planLockRules([{ ...backupLockRule(), enabled: false }], backupLockRule()).action).toBe("conflict");
    // The job's own clean-up deletes only backups older than the retention, which the lock no longer holds.
    expect(expiredBackups([{ at: NOW - BACKUP_LOCK_DAYS * 86_400_000, bytes: 1, object_key: backupObjectKey(NOW - BACKUP_LOCK_DAYS * 86_400_000) }], NOW)).toEqual([]);
  });

  it("the deploy report names the backup bucket's lock and where to fix it", () => {
    const text = renderSummary({
      env: "production",
      state: { resources: { workerName: "w", buckets: [], rules: [{ kind: "lock", bucket: "dbd-presence-production-backups", id: "backups-29-days", days: 29, guide: "docs/owner/backups.md, step 2", action: "missing" }] } },
      outcomes: { verify: "success" },
    });
    expect(text).toContain("Lock rule (nothing can be changed or deleted for 29 days) `backups-29-days` on `dbd-presence-production-backups`: **missing**");
    expect(text).toContain("Check the bucket lock rule on `dbd-presence-production-backups`");
    expect(text).toContain("It must be 29 days for all objects (docs/owner/backups.md, step 2).");
  });

  it("the deploy report names the backup bucket's 30-day expiry and where to fix it", () => {
    const text = renderSummary({
      env: "staging",
      state: { resources: { workerName: "w", buckets: [{ name: "dbd-presence-staging-backups", created: true }], rules: [{ kind: "lifecycle", bucket: "dbd-presence-staging-backups", id: "expire-after-30-days", days: 30, guide: "docs/owner/backups.md, step 2", action: "failed", error: "HTTP 403" }] } },
      outcomes: { verify: "success" },
    });
    expect(text).toContain("Storage bucket `dbd-presence-staging-backups`: **created**");
    expect(text).toContain("Expiry rule (exports are deleted after 30 days) `expire-after-30-days`");
    expect(text).toContain("It must be 30 days for all objects (docs/owner/backups.md, step 2).");
  });
});

describe("job log (no database rows, even on a failure)", () => {
  // What a failing `wrangler d1 execute --file` or `d1 export` can print: the statement that failed, with row values.
  const failure = [
    "\x1b[31m✘ [ERROR]\x1b[0m A request to the Cloudflare API failed.",
    "  UNIQUE constraint failed: users.discord_id: SQLITE_CONSTRAINT (code: 7500)",
    `INSERT INTO "users" ("id","discord_id","display_name") VALUES('3b1f9c2e','183920471234567890','Jane O''Brien');`,
    `{"error":{"text":"near \\"Brien\\": syntax error"}} at line 412 in 'Jane O''Brien'`,
    "Error: failed to import, row 183920471234567890 Jane",
  ].join("\n");
  const rowText = ["183920471234567890", "Brien", "Jane", "3b1f9c2e"];

  it("keeps only error lines, without quoted text, values or long numbers", () => {
    const lines = safeLogLines(failure);
    expect(lines.length).toBeGreaterThan(0);
    for (const line of lines) for (const secret of rowText) expect(line).not.toContain(secret);
    expect(lines).toEqual(["SQLITE_CONSTRAINT, code: 7500, UNIQUE constraint failed: users.discord_id", "line 412, syntax error"]);
    expect(safeLogLines(Array.from({ length: 50 }, (_, i) => `HTTP 500 line ${i}`).join("\n"))).toHaveLength(20);
  });

  it("prints nothing of a database call's output on success, and no row on failure", () => {
    expect(wranglerLog({ what: "Exporting the database", status: 0, stdout: failure, stderr: failure, sensitive: true })).toBe("");
    const text = wranglerLog({ what: "Loading the backup into the temporary database", status: 1, stdout: "", stderr: failure, sensitive: true });
    expect(text).toMatch(/^Loading the backup into the temporary database: wrangler exit 1\. Its output is not shown because it can contain database rows/);
    for (const secret of rowText) expect(text).not.toContain(secret);
    expect(text).not.toMatch(/VALUES\(/);
    // R2 calls move only the encrypted file; their output is shown as it is.
    expect(wranglerLog({ what: "Uploading the backup", status: 0, stdout: "Upload complete.", stderr: "", sensitive: false })).toBe("Upload complete.");
  });

  it("routes every wrangler call through that filter, and only R2 calls are not treated as sensitive", () => {
    const script = inject("backupScript");
    expect(script).toContain("process.stdout.write(wranglerLog({ what, status, stdout: r.stdout, stderr: r.stderr, sensitive }));");
    // The only other raw output is gpg's own error text in encrypt and decrypt (file names and messages, no data).
    expect(script.match(/process\.(stdout|stderr)\.write\(r\.(stdout|stderr)/g)).toEqual(["process.stderr.write(r.stderr", "process.stderr.write(r.stderr"]);
    expect(script.match(/process\.stderr\.write\(r\.stderr \?\? ""\);\n\s+throw new Error\(`encrypting|process\.stderr\.write\(r\.stderr \?\? ""\);\n\s+throw new Error\("the backup failed its integrity/g)).toHaveLength(2);
    for (const call of script.match(/wrangler\(\[[^\n]*/g) ?? []) {
      if (call.includes('"r2"')) expect(call).toContain("{ sensitive: false }");
      else expect(call).not.toContain("sensitive: false");
    }
  });
});

describe("backup workflow and owner guide", () => {
  const workflow = inject("backupWorkflow");
  const script = inject("backupScript");
  const guide = inject("backupGuide");

  it("runs daily at a minute other than :00 and by hand with an environment choice and the drill switch", () => {
    const cron = /\n  schedule:\n(?:    #.*\n)*    - cron: '(\d+) (\d+) \* \* \*'\n/.exec(workflow);
    expect(cron).not.toBeNull();
    expect(Number(cron![1])).not.toBe(0);
    expect(workflow).toMatch(/workflow_dispatch:\n    inputs:\n      environment:[\s\S]*options: \[staging, production\][\s\S]*      drill:\n[\s\S]*type: boolean/);
    expect(workflow).toContain(`fromJSON('["staging","production"]')`);
  });

  it("backs up production only from main, in its own environment without a reviewer", () => {
    expect(workflow.match(/\[ "\$DEPLOY_ENV" = production \] && \[ "\$GITHUB_REF" != refs\/heads\/main \]/g)).toHaveLength(2);
    expect(workflow.match(/matrix\.environment == 'production' && 'production-backups' \|\| 'staging'|inputs\.environment == 'production' && 'production-backups' \|\| 'staging'/g)).toHaveLength(2);
  });

  it("never puts the backup key on a command line, in a file or in the log", () => {
    expect(workflow).not.toMatch(/--passphrase(?!-fd)|passphrase-file|echo[^\n]*BACKUP_KEY/);
    expect(workflow.match(/BACKUP_KEY: \$\{\{ secrets\.BACKUP_KEY \}\}/g)).toHaveLength(4);
    expect(script).toContain('"--passphrase-fd", "0"');
    expect(script).toContain("input: `${key}\\n`");
    expect(script).not.toMatch(/--passphrase(?!-fd)|passphrase-file/);
    expect(script).toContain("delete process.env.BACKUP_KEY;");
    expect(script).toContain("process.umask(0o077);");
    expect(script).toContain('"--cipher-algo", "AES256"');
  });

  it("skips a scheduled run while no key exists, but a run by hand or a malformed key still fails", () => {
    expect(scheduledRunReady(undefined)).toBe(false);
    expect(scheduledRunReady("  \n")).toBe(false);
    expect(scheduledRunReady("short-key")).toBe(true); // present: readBackupKey then refuses it loudly
    expect(workflow).toContain("SKIP_FLAG: ${{ github.event_name == 'schedule' && '--skip-if-missing' || '' }}");
    expect(script).toMatch(/args\.includes\("--skip-if-missing"\) && !scheduledRunReady\(rawKey\)/);
    for (const step of ["Install (exact versions from the lock file)", "Find the database and check the backup storage (read only)", "Export, encrypt, check and upload"]) {
      expect(workflow).toContain(`- name: ${step}\n        if: steps.key.outputs.ready == 'true'`);
    }
  });

  it("always deletes the temporary drill database, and the drill never restores into the live database", () => {
    expect(workflow).toMatch(/- name: Delete the temporary database \(always\)\n        if: always\(\)\n[\s\S]*backup\.mjs drill-cleanup/);
    expect(script).toMatch(/d1", "execute", "RESTORE", "--remote", "--config", drillConfig, "--yes", "--file"/);
    expect(script).not.toMatch(/"execute", "DB"[^\n]*"--file"/);
  });

  it("deletes leftover drill databases at the start of every drill and in the daily job once the key is ready (TM-14)", () => {
    expect(workflow).toMatch(/- name: Delete leftover restore-drill databases older than a day\n        if: \$\{\{ !cancelled\(\) && steps\.key\.outputs\.ready == 'true' \}\}\n[\s\S]*?backup\.mjs sweep-drills --env "\$DEPLOY_ENV"/);
    const drillJob = workflow.slice(workflow.indexOf("\n  drill:\n"));
    expect(drillJob.indexOf("backup.mjs sweep-drills")).toBeGreaterThan(drillJob.indexOf("provision.mjs resources"));
    expect(drillJob.indexOf("backup.mjs sweep-drills")).toBeLessThan(drillJob.indexOf("backup.mjs drill --env"));
    expect(script).toMatch(/case "sweep-drills":\n      return sweepDrills\(env\);/);
    expect(script).toContain("assertDrillTarget({ env, name: d.name, id: d.id, liveName: live.name, liveId: live.id });");
  });

  it("guides the owner through the key, the storage, the drill and a real restore", () => {
    expect(guide).toContain("[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) | Set-Clipboard");
    expect(guide).toMatch(/password manager[\s\S]{0,300}USB stick next to your release keys/);
    expect(guide).toMatch(/Without this key the backups cannot be read/);
    expect(guide).toContain("## 1. Make the backup key");
    expect(guide).toContain("production-backups");
    expect(guide).toMatch(/## 3\. Run the restore drill once on staging/);
    expect(guide).toMatch(/Option A \(recommended\): D1 Time Travel/);
    expect(guide).toContain("npx wrangler d1 time-travel restore dbd-presence-production --timestamp");
    expect(guide).toContain("npx wrangler d1 execute dbd-presence-r20261102-production --remote --file backup.sql");
    // A restored database must still match the deploy rule that a database name ends in -<environment>.
    expect(guide).not.toMatch(/d1 create dbd-presence-production-restore/);
  });
});
