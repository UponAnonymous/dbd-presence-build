// Decisions for the backup workflow (.github/workflows/backup-service.yml, scripts/backup.mjs). No Node APIs, so the
// Vitest suite (which runs in the Workers runtime) can check every rule: the backup bucket and its 30-day expiry, the
// object names, the size cap that protects the free storage tier, the backup key rules, how a D1 dump is read to count
// its rows, how a restore drill is judged, the temporary drill database's name guard, and the reports for the owner.
// scripts/backup.mjs does the I/O (wrangler, gpg, Cloudflare API, files).
//
// NFR-SVC-10 (backup and restore drill, gate V7.2), NFR-SVC-07 and PLAN_V2 §6 (encrypted exports kept 30 days),
// NFR-SVC-11 / D18 (stay inside the free storage tier), FR-ADM-06 (separate environments).

import { assertEnvironment, lifecycleRule, lockRule } from "./provision-plan.mjs";

/** Encrypted exports are kept this long (PLAN_V2 §6, privacy notice "How long data is kept"). */
export const BACKUP_RETENTION_DAYS = 30;
export const BACKUP_LIFECYCLE_RULE_ID = "expire-after-30-days";
/**
 * The backups bucket's lock (threat-model review 2026-10-25, TM-03): no backup can be deleted or overwritten until it is
 * 29 days old, not even with a Cloudflare token that can edit R2, so a stolen token cannot wipe the backups. One day
 * shorter than the 30-day expiry, so the expiry rule and the job's own clean-up (expiredBackups) can still delete it.
 */
export const BACKUP_LOCK_DAYS = 29;
export const BACKUP_LOCK_RULE_ID = "backups-29-days";

/**
 * Largest encrypted backup the job uploads (decimal bytes), in either environment. Staging and production share one
 * Cloudflare account and so R2's one 10 GB free tier (D18): the Workers' own data is capped at 8 GB in production and
 * 750 MB in staging (src/storage.ts storageLimits), and the backup budgets below add 800 MB and 200 MB, so the worst
 * case is 8 + 0.75 + 0.8 + 0.2 = 9.75 GB. In production 31 copies (30 days plus the day the expiry rule may lag) of at
 * most 25 MB are 775 MB, inside its 800 MB budget; staging's 200 MB holds 31 copies of up to 6.4 MB, and a staging
 * backup that would pass its budget is refused like any other.
 */
export const BACKUP_MAX_BYTES = 25_000_000;
/** Most bytes of backups each environment may hold at once. */
export const BACKUP_BUDGET_BYTES = Object.freeze({ staging: 200_000_000, production: 800_000_000 });

/** The backup budget of `env` ("staging" or "production"; anything else throws). */
export function backupBudgetBytes(env) {
  return BACKUP_BUDGET_BYTES[assertEnvironment(env)];
}
/** backup_runs keeps this many rows (migration 0009 trims older ones). */
export const BACKUP_RUNS_KEPT = 100;
/** The owner guide for backups. */
export const BACKUP_GUIDE = "docs/owner/backups.md";

/**
 * The GitHub environment each backup run uses. Production's own environment has a required reviewer, so a scheduled
 * backup there would wait for an approval every day; `production-backups` has the same main-only branch rule but no
 * reviewer, and holds a Cloudflare token that can only reach D1 and R2.
 */
export const BACKUP_GITHUB_ENVIRONMENTS = { staging: "staging", production: "production-backups" };

const DAY_MS = 86_400_000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const TABLE_NAME = /^[A-Za-z_][A-Za-z0-9_]{0,63}$/;
const MIGRATION_NAME = /^\d{4}_[a-z0-9_]+\.sql$/;
export const BACKUP_OBJECT_KEY = /^d1\/\d{4}-\d{2}-\d{2}T\d{6}Z\.sql\.gz\.gpg$/;

export function backupBucketName(env) {
  assertEnvironment(env);
  return `dbd-presence-${env}-backups`;
}

export function backupLifecycleRule() {
  return lifecycleRule(BACKUP_LIFECYCLE_RULE_ID, BACKUP_RETENTION_DAYS);
}

export function backupLockRule() {
  if (BACKUP_LOCK_DAYS >= BACKUP_RETENTION_DAYS) throw new Error("the backup lock must end before the backup expires");
  return lockRule(BACKUP_LOCK_RULE_ID, BACKUP_LOCK_DAYS);
}

/** `d1/<UTC date>T<hhmmss>Z.sql.gz.gpg`: a gzip-compressed SQL dump, encrypted. */
export function backupObjectKey(atMs) {
  const date = new Date(atMs);
  if (!Number.isFinite(date.getTime())) throw new Error("not a time");
  const iso = date.toISOString();
  return `d1/${iso.slice(0, 10)}T${iso.slice(11, 13)}${iso.slice(14, 16)}${iso.slice(17, 19)}Z.sql.gz.gpg`;
}

// ---------------------------------------------------------------------------------------------------------------
// The backup key (GitHub environment secret BACKUP_KEY)

/**
 * The passphrase from the BACKUP_KEY secret. Surrounding spaces and line breaks (a careless paste) are dropped; the key
 * itself must be at least 32 characters with no spaces or line breaks inside (gpg reads the passphrase up to the first
 * line break). The error never contains any part of the value.
 */
export function readBackupKey(raw, env) {
  assertEnvironment(env);
  const where = `GitHub → Settings → Environments → ${BACKUP_GITHUB_ENVIRONMENTS[env]} → Environment secrets`;
  if (typeof raw !== "string" || raw.trim().length === 0) {
    throw new Error(`BACKUP_KEY is missing for ${env}. Nothing was exported. Create it as described in ${BACKUP_GUIDE}, step 1 "Make the backup key" (${where}).`);
  }
  const key = raw.trim();
  if (key.length < 32 || /[\s\u0000-\u001f\u007f]/.test(key)) {
    throw new Error(`BACKUP_KEY for ${env} is too short or has spaces or line breaks inside. Nothing was exported. Make it again as described in ${BACKUP_GUIDE}, step 1 (${where}).`);
  }
  return key;
}

/**
 * Whether a scheduled run should go ahead. Before the owner has made the key (BACKUP_KEY absent or empty) a scheduled
 * run is skipped with a notice instead of failing every day; a key that is present but malformed still fails, and a
 * run started by hand always needs the key.
 */
export function scheduledRunReady(raw) {
  return typeof raw === "string" && raw.trim().length > 0;
}

/** True only when gpg finished cleanly, decrypted the data and confirmed its integrity check (GOODMDC). */
export function decryptionVerified(exitCode, statusText) {
  const lines = String(statusText ?? "").split(/\r?\n/).map((l) => l.trim());
  const has = (word) => lines.some((l) => l === `[GNUPG:] ${word}` || l.startsWith(`[GNUPG:] ${word} `));
  return exitCode === 0 && has("DECRYPTION_OKAY") && has("GOODMDC") && !has("BADMDC") && !has("DECRYPTION_FAILED");
}

// ---------------------------------------------------------------------------------------------------------------
// Storage: the size cap and the per-environment budget (D18)

function mb(bytes) {
  return `${(bytes / 1_000_000).toFixed(1)} MB`;
}

/** Backups older than the retention, which the job deletes itself when the bucket's expiry rule cannot be confirmed. */
export function expiredBackups(exports, now) {
  return exports.filter((e) => e.at < now - BACKUP_RETENTION_DAYS * DAY_MS && typeof e.object_key === "string" && BACKUP_OBJECT_KEY.test(e.object_key)).map((e) => e.object_key);
}

/**
 * Whether a new backup of `newBytes` may be uploaded to `env`'s bucket. `exports` are the successful exports recorded in
 * backup_runs ({at, bytes, object_key}); those younger than 31 days are counted as stored (the expiry rule may lag by a
 * day), and so are older ones listed in `stillStored` (an expired backup the job could not delete). The total must stay
 * within `env`'s budget (`backupBudgetBytes`).
 */
export function storageCheck({ env, newBytes, exports, now, stillStored = [] }) {
  const budget = backupBudgetBytes(env);
  if (!Number.isInteger(newBytes) || newBytes <= 0) return { ok: false, storedBytes: 0, reason: "The encrypted backup is empty. It was not uploaded." };
  const keep = new Set(stillStored);
  const storedBytes = exports.filter((e) => e.at >= now - (BACKUP_RETENTION_DAYS + 1) * DAY_MS || keep.has(e.object_key)).reduce((sum, e) => sum + (Number.isFinite(e.bytes) ? e.bytes : 0), 0);
  if (newBytes > BACKUP_MAX_BYTES) {
    return {
      ok: false,
      storedBytes,
      reason: `The encrypted backup is ${mb(newBytes)}, more than the ${mb(BACKUP_MAX_BYTES)} limit for one backup. It was not uploaded, so the free storage limit stays safe (D18). D1 Time Travel still covers the last 7 days. Tell the developer.`,
    };
  }
  if (storedBytes + newBytes > budget) {
    return {
      ok: false,
      storedBytes,
      reason: `The backups already stored (${mb(storedBytes)}) plus this one (${mb(newBytes)}) would pass the ${mb(budget)} budget for ${env} backups. It was not uploaded, so the free storage limit stays safe (D18). D1 Time Travel still covers the last 7 days. Tell the developer.`,
    };
  }
  return { ok: true, storedBytes, reason: null };
}

// ---------------------------------------------------------------------------------------------------------------
// backup_runs (migration 0009): the SQL the job runs with `wrangler d1 execute --command`

/** Short, safe text for backup_runs.detail: letters, digits and . , : ( ) / _ + = % - only, at most 300 characters. */
export function safeDetail(text) {
  return String(text ?? "")
    .replace(/[^A-Za-z0-9 .,:()/_+=%-]+/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .slice(0, 300);
}

export function insertRunSql({ kind, at, bytes, ok, detail, objectKey = null }) {
  if (kind !== "export" && kind !== "drill") throw new Error(`unknown backup run kind "${kind}"`);
  if (!Number.isSafeInteger(at) || at <= 0) throw new Error("the run time must be a positive whole number of milliseconds");
  if (!Number.isSafeInteger(bytes) || bytes < 0) throw new Error("bytes must be a whole number");
  if (typeof ok !== "boolean") throw new Error("ok must be true or false");
  if (objectKey !== null && !BACKUP_OBJECT_KEY.test(objectKey)) throw new Error("not a backup object key");
  const key = objectKey === null ? "NULL" : `'${objectKey}'`;
  return `INSERT INTO backup_runs (kind, at, bytes, ok, detail, object_key) VALUES ('${kind}', ${at}, ${bytes}, ${ok ? 1 : 0}, '${safeDetail(detail)}', ${key})`;
}

export const RECENT_EXPORTS_SQL = `SELECT at, bytes, object_key FROM backup_runs WHERE kind = 'export' AND ok = 1 AND object_key IS NOT NULL ORDER BY at DESC, id DESC LIMIT ${BACKUP_RUNS_KEPT}`;

/** The rows of the first statement in `wrangler d1 execute --json` output. */
export function d1JsonResults(stdout) {
  const text = String(stdout ?? "");
  // wrangler may print a warning line before the JSON; the result is the first line-start "[" that parses to the end.
  let parsed;
  for (const match of text.matchAll(/^[ \t]*\[/gm)) {
    try {
      parsed = JSON.parse(text.slice(match.index));
      break;
    } catch {
      parsed = undefined; // not the start of the JSON result; try the next candidate
    }
  }
  if (parsed === undefined) throw new Error("wrangler printed no JSON result");
  if (!Array.isArray(parsed) || parsed.length === 0 || !Array.isArray(parsed[0]?.results)) throw new Error("wrangler's JSON result has no rows list");
  if (parsed.some((p) => p?.success === false)) throw new Error("the query failed");
  return parsed[0].results;
}

// ---------------------------------------------------------------------------------------------------------------
// What may reach the Actions log from a wrangler call

// Error fragments that can be shown: SQLite and Cloudflare error codes, line numbers, HTTP statuses, constraint and
// missing-table messages (which name only tables and columns) and fixed phrases. Nothing else of a line is kept.
const SAFE_FRAGMENTS = [
  /\bSQLITE_[A-Z_]+\b/g,
  /\b(?:code|status):? ?\d{1,5}\b/gi,
  /\bline \d{1,9}\b/gi,
  /\bHTTP \d{3}\b/g,
  /\b(?:UNIQUE|NOT NULL|CHECK|FOREIGN KEY) constraint failed(?:: [A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)?/g,
  /\bno such (?:table|column): [A-Za-z_][A-Za-z0-9_.]*/g,
  /\b(?:syntax error|too many terms in compound SELECT|database is locked|Authentication error|timed out|not found|too large|exceeded|Unauthorized|Forbidden)\b/gi,
];

/**
 * What may be shown of wrangler's output from a call that touches database rows (the export, the drill's load, any
 * query): for each line, only the known-safe error fragments above, joined; a line without one is dropped. At most 20
 * lines, no repeats. Row contents (names, Discord ids), quoted or not, therefore never reach the log, even on a failure.
 */
export function safeLogLines(text, max = 20) {
  const out = [];
  for (const line of String(text ?? "").replace(/\x1b\[[0-9;]*m/g, "").split(/\r?\n/)) {
    const fragments = SAFE_FRAGMENTS.flatMap((pattern) => line.match(pattern) ?? []);
    const shown = [...new Set(fragments)].join(", ");
    if (shown.length > 0 && !out.includes(shown)) out.push(shown);
    if (out.length >= max) break;
  }
  return out;
}

/**
 * The text the backup job prints for a finished wrangler call. A call that may carry rows (`sensitive`) prints nothing
 * on success and only `safeLogLines` on failure; the other calls (R2 uploads, downloads and deletes of encrypted files)
 * print wrangler's output as it is.
 */
export function wranglerLog({ what, status, stdout, stderr, sensitive }) {
  if (!sensitive) return `${stdout ?? ""}${stderr ?? ""}`;
  if (status === 0) return "";
  const lines = safeLogLines(`${stdout ?? ""}\n${stderr ?? ""}`);
  const shown = lines.length > 0 ? `; its error lines, with values removed:\n${lines.map((l) => `  ${l}`).join("\n")}` : ".";
  return `${what}: wrangler exit ${status}. Its output is not shown because it can contain database rows${shown}\n`;
}

// ---------------------------------------------------------------------------------------------------------------
// Reading a D1 dump (`wrangler d1 export` output) without running it

/** Index just past the quoted text that starts at `i` (`'…'`, `"…"` or `` `…` ``, doubled quotes inside). */
function skipQuoted(text, i) {
  const quote = text[i];
  let j = i + 1;
  for (;;) {
    if (j >= text.length) throw new Error("the dump ends inside quoted text");
    if (text[j] === quote) {
      if (text[j + 1] === quote) {
        j += 2;
        continue;
      }
      return j + 1;
    }
    j++;
  }
}

/** Index just past the comment that starts at `i`, or `i` when there is none. */
function skipComment(text, i) {
  if (text[i] === "-" && text[i + 1] === "-") {
    const end = text.indexOf("\n", i);
    return end < 0 ? text.length : end + 1;
  }
  if (text[i] === "/" && text[i + 1] === "*") {
    const end = text.indexOf("*/", i + 2);
    if (end < 0) throw new Error("the dump ends inside a comment");
    return end + 2;
  }
  return i;
}

function stripLeading(statement) {
  let i = 0;
  for (;;) {
    while (i < statement.length && /\s/.test(statement[i])) i++;
    const next = skipComment(statement, i);
    if (next === i) return statement.slice(i);
    i = next;
  }
}

/**
 * Splits SQL text into statements at top-level semicolons, ignoring semicolons in quoted text and comments. A CREATE
 * TRIGGER statement runs to the END that closes its BEGIN (CASE … END inside it is counted too).
 */
export function splitSqlStatements(text) {
  const statements = [];
  let start = 0;
  let i = 0;
  let depth = 0;
  let words = 0;
  let trigger = false;
  const push = (end) => {
    const statement = stripLeading(text.slice(start, end)).trim();
    if (statement.length > 0) statements.push(statement);
    start = end + 1;
    depth = 0;
    words = 0;
    trigger = false;
  };
  while (i < text.length) {
    const c = text[i];
    if (c === "'" || c === '"' || c === "`") {
      i = skipQuoted(text, i);
      continue;
    }
    if (c === "[") {
      const end = text.indexOf("]", i + 1);
      if (end < 0) throw new Error("the dump ends inside a bracketed name");
      i = end + 1;
      continue;
    }
    const afterComment = skipComment(text, i);
    if (afterComment !== i) {
      i = afterComment;
      continue;
    }
    if (/[A-Za-z_]/.test(c) && (i === 0 || !/[A-Za-z0-9_$]/.test(text[i - 1]))) {
      let j = i + 1;
      while (j < text.length && /[A-Za-z0-9_$]/.test(text[j])) j++;
      const word = text.slice(i, j).toUpperCase();
      // CREATE [TEMP|TEMPORARY] TRIGGER: decided by the first three words of the statement.
      if (++words <= 3 && !trigger) trigger = /^CREATE\s+(TEMP\s+|TEMPORARY\s+)?TRIGGER$/i.test(stripLeading(text.slice(start, j)));
      if (trigger) {
        if (word === "BEGIN" || word === "CASE") depth++;
        else if (word === "END" && depth > 0) depth--;
      }
      i = j;
      continue;
    }
    if (c === ";" && !(trigger && depth > 0)) push(i);
    i++;
  }
  push(text.length);
  return statements;
}

const NAME = String.raw`("(?:[^"]|"")+"|\x60[^\x60]+\x60|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)`;
const CREATE_TABLE = new RegExp(String.raw`^CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?` + NAME, "i");
const INSERT_INTO = new RegExp(String.raw`^(?:INSERT|REPLACE)\s+(?:OR\s+[A-Za-z]+\s+)?INTO\s+` + NAME, "i");

function unquoteName(name) {
  if (name.startsWith('"')) return name.slice(1, -1).replace(/""/g, '"');
  if (name.startsWith("`") || name.startsWith("[")) return name.slice(1, -1);
  return name;
}

/** The number of value rows in one INSERT statement (`VALUES (…), (…)`); 0 for any other form. */
function valueRows(statement) {
  let i = 0;
  let depth = 0;
  let afterValues = false;
  let rows = 0;
  while (i < statement.length) {
    const c = statement[i];
    if (c === "'" || c === '"' || c === "`") {
      i = skipQuoted(statement, i);
      continue;
    }
    if (c === "(") {
      if (depth === 0 && afterValues) rows++;
      depth++;
    } else if (c === ")") {
      depth--;
    } else if (depth === 0 && /[Vv]/.test(c) && /^VALUES\b/i.test(statement.slice(i, i + 7)) && (i === 0 || !/[A-Za-z0-9_]/.test(statement[i - 1]))) {
      afterValues = true;
      i += 6;
      continue;
    }
    i++;
  }
  return rows;
}

function stringLiterals(statement) {
  const out = [];
  let i = 0;
  while (i < statement.length) {
    if (statement[i] === "'") {
      const end = skipQuoted(statement, i);
      out.push(statement.slice(i + 1, end - 1).replace(/''/g, "'"));
      i = end;
    } else if (statement[i] === '"' || statement[i] === "`") {
      i = skipQuoted(statement, i);
    } else {
      i++;
    }
  }
  return out;
}

const internal = (name) => name.startsWith("sqlite_") || name.startsWith("_cf_");

/**
 * What a dump holds: each table (created or filled by it, SQLite's and Cloudflare's internal tables left out) with its
 * number of rows, and the applied migrations from its d1_migrations rows in order. Only names and counts leave this
 * function, never row contents.
 */
export function analyseDump(text) {
  const counts = new Map();
  const migrations = [];
  const statements = splitSqlStatements(text);
  for (const statement of statements) {
    const created = CREATE_TABLE.exec(statement);
    if (created) {
      const name = unquoteName(created[1]);
      if (!internal(name) && !counts.has(name)) counts.set(name, 0);
      continue;
    }
    const inserted = INSERT_INTO.exec(statement);
    if (!inserted) continue;
    const name = unquoteName(inserted[1]);
    const rows = valueRows(statement);
    if (rows === 0) throw new Error(`the dump fills ${name} in a form this check cannot count`);
    if (internal(name)) continue;
    counts.set(name, (counts.get(name) ?? 0) + rows);
    if (name === "d1_migrations") migrations.push(...stringLiterals(statement).filter((s) => MIGRATION_NAME.test(s)));
  }
  const tables = [...counts.entries()].map(([name, rows]) => ({ name, rows })).sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  return { tables, migrations, statements: statements.length, rows: tables.reduce((s, t) => s + t.rows, 0) };
}

// ---------------------------------------------------------------------------------------------------------------
// The restore drill

export function drillDatabaseName(env, runId) {
  assertEnvironment(env);
  if (!/^[0-9]{1,20}(-[0-9]{1,3})?$/.test(String(runId ?? ""))) throw new Error("the drill needs the workflow run id");
  return `dbd-presence-${env}-restore-drill-${runId}`;
}

/**
 * Throws unless `target` is a temporary drill database of this environment, and not the live one: the drill imports
 * into and deletes only such a database.
 */
export function assertDrillTarget({ env, name, id, liveName, liveId }) {
  if (!drillDatabasePattern(env).test(String(name ?? ""))) throw new Error(`"${name}" is not a restore-drill database name; the drill never touches any other database`);
  if (!UUID.test(String(id ?? ""))) throw new Error(`"${id}" is not a D1 database id`);
  if (name === liveName || (liveId && id.toLowerCase() === String(liveId).toLowerCase())) throw new Error("the drill database is the live database; stopping without changing anything");
  return true;
}

/** The names `drillDatabaseName` makes for `env`, and no other. */
export function drillDatabasePattern(env) {
  assertEnvironment(env);
  return new RegExp(`^dbd-presence-${env}-restore-drill-[0-9]{1,20}(-[0-9]{1,3})?$`);
}

/** A drill database left behind (a cancelled or timed-out drill) is deleted once it is this old (threat-model review TM-14). */
export const DRILL_SWEEP_AGE_MS = DAY_MS;

/**
 * The leftover restore-drill databases to delete. `databases` are entries of Cloudflare's D1 database list
 * ({ uuid, name, created_at }). Only databases of `env` named exactly like a drill database (`drillDatabasePattern`) and
 * created at least `DRILL_SWEEP_AGE_MS` before `now` are removed; the live database never is. An entry whose creation
 * time cannot be read is kept and reported, never guessed at. Returns { remove: [{ name, id, createdAt }], kept: [{ name, reason }] }.
 */
export function staleDrillDatabases({ env, databases, now, liveName, liveId }) {
  const pattern = drillDatabasePattern(env);
  const remove = [];
  const kept = [];
  for (const d of Array.isArray(databases) ? databases : []) {
    const name = String(d?.name ?? "");
    if (!pattern.test(name)) continue;
    const id = String(d?.uuid ?? "");
    const createdAt = typeof d?.created_at === "string" ? Date.parse(d.created_at) : NaN;
    if (!UUID.test(id)) kept.push({ name, reason: "it has no valid database id" });
    else if (name === liveName || (liveId && id.toLowerCase() === String(liveId).toLowerCase())) kept.push({ name, reason: "it is the live database" });
    else if (!Number.isFinite(createdAt)) kept.push({ name, reason: "its creation time is unknown" });
    else if (now - createdAt < DRILL_SWEEP_AGE_MS) kept.push({ name, reason: "it is less than a day old (a drill may still be using it)" });
    else remove.push({ name, id, createdAt });
  }
  return { remove, kept };
}

/** A wrangler config that knows only the drill database (binding RESTORE), so no command can reach the live one. */
export function drillWranglerConfig(name, id) {
  if (!UUID.test(String(id ?? ""))) throw new Error(`"${id}" is not a D1 database id`);
  return { name: "dbd-presence-restore-drill", compatibility_date: "2026-10-01", d1_databases: [{ binding: "RESTORE", database_name: name, database_id: id }] };
}

export const RESTORED_TABLES_SQL = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
export const RESTORED_MIGRATIONS_SQL = "SELECT name FROM d1_migrations ORDER BY id";

/**
 * Queries that count the rows of every named table: one row per query, one column per table, at most 50 tables each.
 * D1 refuses compound SELECTs (UNION ALL) of more than a few terms, so the counts are scalar subqueries instead.
 */
export function rowCountQueries(tables) {
  if (tables.length === 0 || tables.length > 400) throw new Error("expected 1 to 400 tables");
  for (const name of tables) if (!TABLE_NAME.test(name)) throw new Error(`unexpected table name "${name}"`);
  const queries = [];
  for (let i = 0; i < tables.length; i += 50) queries.push(`SELECT ${tables.slice(i, i + 50).map((name) => `(SELECT COUNT(*) FROM "${name}") AS "${name}"`).join(", ")}`);
  return queries;
}

/** The [{name, n}] list from the single row a rowCountQueries query returns. */
export function countsFromRow(row) {
  if (!row || typeof row !== "object") throw new Error("the count query returned no row");
  return Object.entries(row).map(([name, n]) => ({ name, n: Number(n) }));
}

/**
 * Judges a restore: every table of the dump exists in the restored copy with the same number of rows, the copy has no
 * other tables, and its d1_migrations list equals the dump's and is the start of the repository's migrations list.
 */
export function compareRestore({ dump, restoredTables, restoredCounts, restoredMigrations, repoMigrations }) {
  const problems = [];
  const restored = new Set(restoredTables.filter((n) => !internal(n)));
  const counts = new Map(restoredCounts.map((r) => [r.name, Number(r.n)]));
  const names = [...new Set([...dump.tables.map((t) => t.name), ...restored])].sort();
  const rows = names.map((name) => {
    const inDump = dump.tables.find((t) => t.name === name);
    const expected = inDump ? inDump.rows : null;
    const got = restored.has(name) ? (counts.get(name) ?? null) : null;
    const ok = expected !== null && got !== null && expected === got;
    if (expected === null) problems.push(`table ${name} is in the restored copy but not in the backup`);
    else if (!restored.has(name)) problems.push(`table ${name} is missing from the restored copy`);
    else if (got !== expected) problems.push(`table ${name}: ${expected} rows in the backup, ${got ?? "unknown"} restored`);
    return { table: name, expected, restored: got, ok };
  });
  if (dump.migrations.length === 0) problems.push("the backup has no applied migrations (d1_migrations)");
  if (JSON.stringify(restoredMigrations) !== JSON.stringify(dump.migrations)) problems.push("the restored migrations list differs from the backup's");
  const prefix = repoMigrations.slice(0, dump.migrations.length);
  if (JSON.stringify(prefix) !== JSON.stringify(dump.migrations)) problems.push("the backup's migrations do not match the start of service/migrations");
  return { ok: problems.length === 0, rows, problems, newerMigrations: Math.max(0, repoMigrations.length - dump.migrations.length) };
}

// ---------------------------------------------------------------------------------------------------------------
// Reports for the owner (job summary): names, counts and sizes only, never row contents or the key

const ENV_LABEL = { staging: "Staging (the test copy)", production: "Production (the real service)" };

export function renderBackupSummary({ env, exportRun = null, drill = null, sweep = null }) {
  assertEnvironment(env);
  const lines = [`# ${ENV_LABEL[env]}: backup report`, ""];
  if (!exportRun && !drill && !sweep) {
    lines.push(`**Nothing was backed up.** The run stopped before the backup started, for example because \`BACKUP_KEY\` is missing (${BACKUP_GUIDE}, step 1). Open the red step to see why.`);
    return lines.join("\n");
  }
  if (exportRun) {
    lines.push("## Encrypted export", "");
    if (exportRun.uploaded) {
      lines.push(`**Backup made.** \`${exportRun.key}\` (${mb(exportRun.bytes)} encrypted) is in the bucket \`${backupBucketName(env)}\`. It holds ${exportRun.tables} tables with ${exportRun.rows} rows and is deleted after ${BACKUP_RETENTION_DAYS} days.`, "");
      lines.push(`- Backups stored for ${env} before this one: ${mb(exportRun.storedBytes ?? 0)} of the ${mb(backupBudgetBytes(env))} budget.`);
      lines.push(`- 30-day expiry rule on the bucket: ${exportRun.lifecycle === "keep" ? "confirmed" : `**not confirmed** (${exportRun.lifecycle ?? "not checked"}); this run deleted ${exportRun.deleted?.length ?? 0} expired backup(s) itself. Run **Deploy service** with provision ticked to set the rule`}.`);
      if (exportRun.error) lines.push(`- **Problem:** ${exportRun.error}`);
    } else {
      lines.push(`**No backup was made.** ${exportRun.error ?? "A step failed; open the red step in the log."}`);
    }
    lines.push("");
  }
  if (drill) {
    lines.push("## Restore drill", "");
    if (drill.comparison?.ok) lines.push(`**The drill passed.** \`${drill.key}\` was downloaded, its integrity check passed, it was decrypted and loaded into a temporary database, and every table has the same number of rows as the backup.`, "");
    else lines.push(`**The drill failed.** ${drill.error ?? ((drill.comparison?.problems ?? []).join("; ") || "See the log.")}`, "");
    if (drill.comparison) {
      lines.push("| Table | Rows in the backup | Rows restored | Match |", "| --- | --- | --- | --- |");
      for (const r of drill.comparison.rows) lines.push(`| ${r.table} | ${r.expected ?? "missing"} | ${r.restored ?? "missing"} | ${r.ok ? "yes" : "**no**"} |`);
      lines.push("");
      lines.push(`- Migrations in the backup: ${drill.migrations ?? 0}${drill.comparison.newerMigrations > 0 ? ` (the code has ${drill.comparison.newerMigrations} newer one(s); a real restore applies them with the deploy workflow)` : ""}.`);
    }
    lines.push(`- Temporary database \`${drill.database ?? "not created"}\`: ${drill.deleted === true ? "deleted" : drill.deleted === false ? "**could not be deleted; delete it in the dashboard (Storage & databases → D1 SQL database → the name → Settings → Delete)**" : "not created"}.`);
    lines.push("- The live database was only read (the backup list) and given one line in its backup history.", "");
  }
  if (sweep) {
    lines.push("## Leftover drill databases", "");
    if ((sweep.deleted ?? []).length === 0 && (sweep.failed ?? []).length === 0) lines.push("- None older than a day was found.");
    for (const name of sweep.deleted ?? []) lines.push(`- \`${name}\`: deleted (a drill that stopped early left it behind).`);
    for (const name of sweep.failed ?? []) lines.push(`- \`${name}\`: **could not be deleted; delete it in the dashboard (Storage & databases → D1 SQL database → the name → Settings → Delete)**.`);
    lines.push("");
  }
  lines.push(`Paste this page into \`docs/evidence/restore-drill.md\` after a drill (${BACKUP_GUIDE}, step 3).`);
  return lines.join("\n");
}
