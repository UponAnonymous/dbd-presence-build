// The backup workflow's I/O (.github/workflows/backup-service.yml). Plain Node, no dependencies beyond wrangler in
// service/node_modules and gpg, which the GitHub runner image has. The rules are in scripts/backup-plan.mjs.
//
//   node scripts/backup.mjs check-key     --env staging
//   node scripts/backup.mjs export        --env staging --state DIR
//   node scripts/backup.mjs drill         --env staging --state DIR --run-id N
//   node scripts/backup.mjs drill-cleanup --env staging --state DIR --run-id N
//   node scripts/backup.mjs sweep-drills  --env staging --state DIR
//   node scripts/backup.mjs summary       --env staging --state DIR
//
// `export` and `drill` need `node scripts/provision.mjs resources --env <env> --state DIR` first (without --create): it
// finds the live database, writes wrangler.generated.<env>.json for this run and records whether the backup bucket and
// its 30-day expiry rule exist.
//
// The backup key comes from BACKUP_KEY (a GitHub environment secret). It is read once and removed from this process's
// environment, so no child process inherits it; gpg gets it on standard input (--passphrase-fd 0), never as an argument,
// and it is never written to a file or printed. Work files are made with umask 077 in a private temporary folder, and
// the unencrypted dump is deleted as soon as it has been compressed and encrypted (or, in the drill, loaded).
// Cloudflare is reached with CLOUDFLARE_API_TOKEN and CLOUDFLARE_ACCOUNT_ID from the environment (never printed).

import { spawnSync } from "node:child_process";
import { appendFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { gunzipSync, gzipSync } from "node:zlib";
import {
  BACKUP_OBJECT_KEY,
  RECENT_EXPORTS_SQL,
  RESTORED_MIGRATIONS_SQL,
  RESTORED_TABLES_SQL,
  analyseDump,
  assertDrillTarget,
  backupBucketName,
  backupObjectKey,
  compareRestore,
  countsFromRow,
  d1JsonResults,
  decryptionVerified,
  drillDatabaseName,
  drillWranglerConfig,
  expiredBackups,
  insertRunSql,
  wranglerLog,
  readBackupKey,
  scheduledRunReady,
  staleDrillDatabases,
  BACKUP_GUIDE,
  renderBackupSummary,
  rowCountQueries,
  storageCheck,
} from "./backup-plan.mjs";
import { cf, describe, findDatabase, listDatabases, must } from "./cloudflare-api.mjs";
import { D1_LOCATION, assertEnvironment } from "./provision-plan.mjs";

const rawKey = process.env.BACKUP_KEY;
delete process.env.BACKUP_KEY;
process.umask(0o077);

const serviceDir = join(import.meta.dirname, "..");
const migrationsDir = join(serviceDir, "migrations");
const wranglerBin = join(serviceDir, "node_modules", "wrangler", "bin", "wrangler.js");
const inActions = process.env.GITHUB_ACTIONS === "true";

const args = process.argv.slice(2);
const command = args[0];
const option = (name) => {
  const i = args.indexOf(name);
  if (i < 0) return undefined;
  const value = args[i + 1];
  if (value === undefined || value.startsWith("--")) throw new Error(`${name} needs a value`);
  return value;
};
const stateDir = option("--state") ?? join(process.env.RUNNER_TEMP ?? join(serviceDir, ".wrangler"), "backup-state");

const message = (error) => (error instanceof Error ? error.message : String(error));

function warn(text) {
  console.log(inActions ? `::warning::${text}` : `warning: ${text}`);
}

function setOutput(name, value) {
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${value}\n`);
  console.log(`output ${name}=${value}`);
}

function readJson(file) {
  return existsSync(file) ? JSON.parse(readFileSync(file, "utf8")) : {};
}

function saveBackupState(key, value) {
  mkdirSync(stateDir, { recursive: true });
  const file = join(stateDir, "backup.json");
  const state = readJson(file);
  state[key] = value;
  writeFileSync(file, JSON.stringify(state, null, 2));
}

/** What `provision.mjs resources` recorded: the live database and the generated wrangler config for this run. */
function liveResources(env) {
  const resources = readJson(join(stateDir, "state.json")).resources;
  if (!resources?.database?.id || !resources.generatedConfig || !existsSync(resources.generatedConfig)) {
    throw new Error(`run "node scripts/provision.mjs resources --env ${env} --state ${stateDir}" first; it finds the live database for this run`);
  }
  return resources;
}

/**
 * Runs wrangler. Every D1 call (`sensitive`, the default) may carry database rows, so its output is never echoed: on
 * success nothing is printed, on failure only filtered error lines (backup-plan.mjs wranglerLog). The R2 calls move
 * only encrypted files and pass `sensitive: false`.
 */
function wrangler(argv, what, { sensitive = true } = {}) {
  const r = spawnSync(process.execPath, [wranglerBin, ...argv], { cwd: serviceDir, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], maxBuffer: 64 * 1024 * 1024 });
  const status = r.status ?? r.signal;
  process.stdout.write(wranglerLog({ what, status, stdout: r.stdout, stderr: r.stderr, sensitive }));
  if (r.status !== 0) throw new Error(`${what} failed (wrangler exit ${status})`);
  return r.stdout ?? "";
}

function liveQuery(env, config, sql, what) {
  return d1JsonResults(wrangler(["d1", "execute", "DB", "--remote", "--env", env, "--config", config, "--json", "--command", sql], what));
}

function recordRun(env, config, run) {
  wrangler(["d1", "execute", "DB", "--remote", "--env", env, "--config", config, "--yes", "--command", insertRunSql(run)], "Recording the run in backup_runs");
}

// ---------------------------------------------------------------------------------------------------------------
// gpg: AES-256, passphrase on standard input only, a private home folder per run, no passphrase cache

function withGpg(key, use) {
  const home = mkdtempSync(join(tmpdir(), "dbdp-gpg-"));
  const run = (argv) =>
    spawnSync("gpg", ["--homedir", home, "--batch", "--yes", "--no-tty", "--pinentry-mode", "loopback", "--no-symkey-cache", "--passphrase-fd", "0", ...argv], {
      input: `${key}\n`,
      encoding: "utf8",
      stdio: ["pipe", "pipe", "pipe"],
    });
  try {
    return use(run);
  } finally {
    const stop = spawnSync("gpgconf", ["--homedir", home, "--kill", "gpg-agent"], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
    if (stop.status !== 0) warn(`could not stop the temporary gpg agent (gpgconf exit ${stop.status ?? stop.signal}); its folder is deleted anyway`);
    rmSync(home, { recursive: true, force: true });
  }
}

export function encrypt(key, input, output) {
  withGpg(key, (gpg) => {
    const r = gpg(["--quiet", "--symmetric", "--cipher-algo", "AES256", "--s2k-mode", "3", "--s2k-digest-algo", "SHA512", "--s2k-count", "65011712", "--compress-algo", "none", "--output", output, input]);
    if (r.status !== 0) {
      process.stderr.write(r.stderr ?? "");
      throw new Error(`encrypting the backup failed (gpg exit ${r.status ?? r.signal})`);
    }
  });
}

/** Decrypts `input` to `output` and checks gpg's integrity check; on any doubt the output is deleted and it throws. */
export function decrypt(key, input, output) {
  withGpg(key, (gpg) => {
    const r = gpg(["--status-fd", "1", "--decrypt", "--output", output, input]);
    if (!decryptionVerified(r.status, r.stdout)) {
      rmSync(output, { force: true });
      process.stderr.write(r.stderr ?? "");
      throw new Error("the backup failed its integrity check: the key is wrong or the file is damaged");
    }
  });
}

// ---------------------------------------------------------------------------------------------------------------
// export

async function exportBackup(env) {
  const key = readBackupKey(rawKey, env);
  const resources = liveResources(env);
  const config = resources.generatedConfig;
  const bucket = backupBucketName(env);
  const lifecycle = (resources.rules ?? []).find((r) => r.kind === "lifecycle" && r.bucket === bucket)?.action ?? "not checked";
  const at = Date.now();
  const record = { at, key: backupObjectKey(at), bytes: 0, tables: 0, rows: 0, uploaded: false, recorded: false, lifecycle, deleted: [], storedBytes: 0, error: null };
  const work = mkdtempSync(join(process.env.RUNNER_TEMP ?? tmpdir(), "dbdp-backup-"));
  const dumpPath = join(work, "dump.sql");
  const gzPath = join(work, "dump.sql.gz");
  const gpgPath = join(work, "dump.sql.gz.gpg");
  const checkPath = join(work, "check.sql.gz");
  let historyRead = false;
  try {
    // Reading the history first proves the live database (and backup_runs) is reachable before anything else.
    const exports = liveQuery(env, config, RECENT_EXPORTS_SQL, "Reading the backup history");
    historyRead = true;

    wrangler(["d1", "export", "DB", "--remote", "--env", env, "--config", config, "--output", dumpPath, "--skip-confirmation"], "Exporting the database");
    const text = readFileSync(dumpPath, "utf8");
    const dump = analyseDump(text);
    if (dump.tables.length === 0 || dump.migrations.length === 0) throw new Error("the export has no tables or no migrations list, so it was not uploaded");
    record.tables = dump.tables.length;
    record.rows = dump.rows;
    writeFileSync(gzPath, gzipSync(Buffer.from(text, "utf8"), { level: 9 }));
    rmSync(dumpPath);

    encrypt(key, gzPath, gpgPath);
    // Round trip: the file that is uploaded must decrypt with the key and give back exactly the compressed dump.
    decrypt(key, gpgPath, checkPath);
    if (!readFileSync(checkPath).equals(readFileSync(gzPath))) throw new Error("the encrypted backup did not decrypt to the same data");
    rmSync(checkPath);
    rmSync(gzPath);
    record.bytes = statSync(gpgPath).size;

    // Belt and braces: when the bucket's 30-day expiry rule is not confirmed, delete expired backups here.
    const stillStored = [];
    if (lifecycle !== "keep") {
      warn(`the 30-day expiry rule on ${bucket} is not confirmed (${lifecycle}); this run deletes expired backups itself. Run Deploy service with provision ticked to set it.`);
      for (const old of expiredBackups(exports, at)) {
        try {
          wrangler(["r2", "object", "delete", `${bucket}/${old}`, "--remote"], `Deleting the expired backup ${old}`, { sensitive: false });
          record.deleted.push(old);
        } catch (error) {
          warn(`${message(error)}; it is counted as still stored`);
          stillStored.push(old);
        }
      }
    }
    const check = storageCheck({ env, newBytes: record.bytes, exports, now: at, stillStored });
    record.storedBytes = check.storedBytes;
    if (!check.ok) throw new Error(check.reason);

    wrangler(["r2", "object", "put", `${bucket}/${record.key}`, "--file", gpgPath, "--remote", "--content-type", "application/pgp-encrypted"], "Uploading the backup", { sensitive: false });
    record.uploaded = true;
    recordRun(env, config, { kind: "export", at, bytes: record.bytes, ok: true, detail: `${dump.tables.length} tables, ${dump.rows} rows, ${dump.migrations.length} migrations`, objectKey: record.key });
    record.recorded = true;
    console.log(`Backup ${record.key} (${record.bytes} bytes encrypted) uploaded to ${bucket} and recorded.`);
    setOutput("key", record.key);
    setOutput("bytes", String(record.bytes));
  } catch (error) {
    record.error = record.uploaded ? `The backup was uploaded but could not be recorded in the database: ${message(error)}` : message(error);
    if (historyRead && !record.uploaded) {
      try {
        recordRun(env, config, { kind: "export", at, bytes: record.bytes, ok: false, detail: `failed: ${message(error)}` });
      } catch (recordError) {
        warn(`the failure could not be recorded in backup_runs either: ${message(recordError)}`);
      }
    }
    throw new Error(record.error);
  } finally {
    rmSync(work, { recursive: true, force: true });
    saveBackupState("export", record);
  }
}

// ---------------------------------------------------------------------------------------------------------------
// drill: newest backup → decrypt and check → temporary database → compare → delete (drill-cleanup)

async function drill(env) {
  const key = readBackupKey(rawKey, env);
  const resources = liveResources(env);
  const config = resources.generatedConfig;
  const name = drillDatabaseName(env, option("--run-id"));
  const bucket = backupBucketName(env);
  const record = { at: Date.now(), key: null, bytes: 0, database: null, databaseId: null, migrations: 0, comparison: null, deleted: null, error: null, recorded: false };
  const work = mkdtempSync(join(process.env.RUNNER_TEMP ?? tmpdir(), "dbdp-drill-"));
  const gpgPath = join(work, "backup.sql.gz.gpg");
  const gzPath = join(work, "backup.sql.gz");
  const sqlPath = join(work, "backup.sql");
  const drillConfig = join(work, "wrangler.drill.json");
  try {
    const newest = liveQuery(env, config, RECENT_EXPORTS_SQL, "Reading the backup history").find((e) => BACKUP_OBJECT_KEY.test(String(e.object_key)));
    if (!newest) throw new Error("no backup is recorded yet: run the workflow once without the drill first");
    record.key = newest.object_key;
    wrangler(["r2", "object", "get", `${bucket}/${record.key}`, "--file", gpgPath, "--remote"], "Downloading the newest backup", { sensitive: false });
    record.bytes = statSync(gpgPath).size;

    decrypt(key, gpgPath, gzPath);
    const text = gunzipSync(readFileSync(gzPath)).toString("utf8"); // gunzip also checks the gzip checksum
    rmSync(gzPath);
    const dump = analyseDump(text);
    record.migrations = dump.migrations.length;
    writeFileSync(sqlPath, text);

    if ((await findDatabase(name)) !== null) throw new Error(`a database called ${name} already exists; delete it in the dashboard and run the drill again`);
    record.database = name;
    saveBackupState("drill", record);
    const created = must(await cf("POST", "/d1/database", { name, primary_location_hint: D1_LOCATION }), `Creating the temporary database ${name}`);
    record.databaseId = created?.uuid ?? null;
    saveBackupState("drill", record);
    assertDrillTarget({ env, name, id: record.databaseId, liveName: resources.database.name, liveId: resources.database.id });
    writeFileSync(drillConfig, JSON.stringify(drillWranglerConfig(name, record.databaseId), null, 2));

    wrangler(["d1", "execute", "RESTORE", "--remote", "--config", drillConfig, "--yes", "--file", sqlPath], "Loading the backup into the temporary database");
    rmSync(sqlPath);
    const drillQuery = (sql, what) => d1JsonResults(wrangler(["d1", "execute", "RESTORE", "--remote", "--config", drillConfig, "--json", "--command", sql], what));
    const restoredTables = drillQuery(RESTORED_TABLES_SQL, "Listing the restored tables").map((r) => String(r.name));
    const countable = restoredTables.filter((n) => !n.startsWith("sqlite_") && !n.startsWith("_cf_"));
    const restoredCounts = countable.length > 0 ? rowCountQueries(countable).flatMap((sql) => countsFromRow(drillQuery(sql, "Counting the restored rows")[0])) : [];
    const restoredMigrations = restoredTables.includes("d1_migrations") ? drillQuery(RESTORED_MIGRATIONS_SQL, "Reading the restored migrations").map((r) => String(r.name)) : [];
    const repoMigrations = readdirSync(migrationsDir).filter((f) => f.endsWith(".sql")).sort();
    record.comparison = compareRestore({ dump, restoredTables, restoredCounts, restoredMigrations, repoMigrations });
    if (!record.comparison.ok) throw new Error(`the restored copy does not match the backup: ${record.comparison.problems.join("; ")}`);
    console.log(`Restore drill passed: ${dump.tables.length} tables, ${dump.rows} rows, ${dump.migrations.length} migrations.`);
  } catch (error) {
    record.error = message(error);
  } finally {
    rmSync(work, { recursive: true, force: true });
    const ok = record.error === null && record.comparison?.ok === true;
    const detail = ok
      ? `passed: ${record.comparison.rows.length} tables, ${record.migrations} migrations, backup ${record.key}`
      : `failed: ${record.error ?? "the restored copy does not match"}`;
    try {
      recordRun(env, config, { kind: "drill", at: record.at, bytes: record.bytes, ok, detail });
      record.recorded = true;
    } catch (recordError) {
      record.error = `${record.error ? `${record.error}. ` : ""}The drill result could not be recorded in backup_runs: ${message(recordError)}`;
    }
    saveBackupState("drill", record);
  }
  if (record.error !== null) throw new Error(record.error);
}

async function drillCleanup(env) {
  const name = drillDatabaseName(env, option("--run-id"));
  const state = readJson(join(stateDir, "backup.json"));
  const record = state.drill ?? { at: Date.now(), key: null, bytes: 0, database: null, databaseId: null, migrations: 0, comparison: null, deleted: null, error: "the drill stopped before it started" };
  const live = readJson(join(stateDir, "state.json")).resources?.database ?? {};
  // The id recorded by the drill, or a lookup by this run's name if the drill stopped between creating and recording.
  const id = record.databaseId ?? (await findDatabase(name));
  if (!id) {
    console.log(`No temporary database ${name} exists; nothing to delete.`);
    saveBackupState("drill", record);
    return;
  }
  record.database = name;
  assertDrillTarget({ env, name, id, liveName: live.name ?? `dbd-presence-${env}`, liveId: live.id ?? null });
  const r = await cf("DELETE", `/d1/database/${encodeURIComponent(id)}`);
  record.deleted = r.ok && (await findDatabase(name)) === null;
  saveBackupState("drill", record);
  if (!record.deleted) {
    throw new Error(`the temporary database ${name} could not be deleted (${describe(r)}). Delete it in the Cloudflare dashboard: Storage & databases → D1 SQL database → ${name} → Settings → Delete.`);
  }
  console.log(`Deleted the temporary database ${name}.`);
}

/**
 * Deletes restore-drill databases of `env` older than a day (threat-model review TM-14): a drill that was cancelled or
 * timed out before its clean-up step leaves a copy of the decrypted data behind. Only names the drill makes, never the
 * live database (backup-plan.mjs staleDrillDatabases and assertDrillTarget). Needs `provision.mjs resources` first.
 */
async function sweepDrills(env) {
  const live = liveResources(env).database;
  const record = { at: Date.now(), deleted: [], failed: [] };
  try {
    const { remove, kept } = staleDrillDatabases({ env, databases: await listDatabases(`dbd-presence-${env}-restore-drill-`), now: record.at, liveName: live.name, liveId: live.id });
    for (const k of kept) console.log(`Left ${k.name} alone: ${k.reason}.`);
    for (const d of remove) {
      assertDrillTarget({ env, name: d.name, id: d.id, liveName: live.name, liveId: live.id });
      const r = await cf("DELETE", `/d1/database/${encodeURIComponent(d.id)}`);
      if (r.ok) {
        record.deleted.push(d.name);
        console.log(`Deleted the leftover drill database ${d.name} (made ${new Date(d.createdAt).toISOString()}).`);
      } else {
        record.failed.push(d.name);
        warn(`could not delete the leftover drill database ${d.name} (${describe(r)})`);
      }
    }
    if (remove.length === 0) console.log(`No restore-drill database of ${env} older than a day.`);
  } finally {
    saveBackupState("sweep", record);
  }
  if (record.failed.length > 0) {
    throw new Error(`${record.failed.length} leftover drill database(s) could not be deleted: ${record.failed.join(", ")}. Delete them in the Cloudflare dashboard: Storage & databases → D1 SQL database → the name → Settings → Delete.`);
  }
}

function summary(env) {
  const state = readJson(join(stateDir, "backup.json"));
  process.stdout.write(renderBackupSummary({ env, exportRun: state.export ?? null, drill: state.drill ?? null, sweep: state.sweep ?? null }) + "\n");
}

// ---------------------------------------------------------------------------------------------------------------

async function main() {
  const env = assertEnvironment(option("--env") ?? "");
  switch (command) {
    case "check-key":
      if (args.includes("--skip-if-missing") && !scheduledRunReady(rawKey)) {
        console.log(`${inActions ? "::notice::" : ""}No BACKUP_KEY for ${env} yet, so this scheduled backup is skipped. Make it as described in ${BACKUP_GUIDE}, step 1.`);
        setOutput("ready", "false");
        return;
      }
      readBackupKey(rawKey, env);
      console.log(`BACKUP_KEY is set for ${env} (its value is never shown).`);
      setOutput("ready", "true");
      return;
    case "export":
      return exportBackup(env);
    case "drill":
      return drill(env);
    case "drill-cleanup":
      return drillCleanup(env);
    case "sweep-drills":
      return sweepDrills(env);
    case "summary":
      return summary(env);
    default:
      throw new Error("usage: node scripts/backup.mjs check-key|export|drill|drill-cleanup|sweep-drills|summary --env staging|production [--state DIR] [--run-id N]");
  }
}

// Run as a command (the workflow); importing the module (a local rehearsal of encrypt and decrypt) runs nothing.
if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  main().catch((error) => {
    const text = message(error);
    const title = /^BACKUP_KEY/.test(text) ? "BACKUP_KEY is missing or unusable" : "Backup";
    console.log(inActions ? `::error title=${title}::${text}` : `error: ${text}`);
    process.exitCode = 1;
  });
}
