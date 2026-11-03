// Hash-chained audit log (FR-ADM-05). Each row stores the previous row's hash; `prev_hash` is UNIQUE, so two writers
// can never fork the chain: the loser's whole batch (its action and its audit row) rolls back and is retried.
// Tamper-evident, not tamper-proof: someone with full control of the database could rewrite the whole chain.
// Rows name people only by the service's random user id, never a Discord id or name, so deleting the user row
// pseudonymises every entry about them without rewriting (and breaking) the chain.

import { randomBytes, sha256Hex, toHex } from "../lib/crypto.ts";

export const GENESIS_HASH = "0".repeat(64);
// Enough for a burst of concurrent writers (each retry re-reads the head); the loser of every round retries.
const MAX_ATTEMPTS = 25;

export interface AuditEntry {
  actor: string;
  action: string;
  target: string | null;
  before?: unknown;
  after?: unknown;
}

export interface AuditRow {
  seq: number;
  at: number;
  actor: string;
  action: string;
  target: string | null;
  before_json: string | null;
  after_json: string | null;
  prev_hash: string;
  hash: string;
  /** 16 random bytes, hex; kept only in D1. */
  salt: string;
}

/** Digest (hex) of a row's before/after JSON text with its salt, SHA-256 of salt + "|" + text; null when none. */
export function payloadDigest(json: string | null, salt: string): Promise<string | null> {
  return json === null ? Promise.resolve(null) : sha256Hex(`${salt}|${json}`);
}

export function newAuditSalt(): string {
  return toHex(randomBytes(16));
}

/**
 * The row hash: SHA-256 over [prev_hash, at, actor, action, target, salt, digest(before_json), digest(after_json)] with
 * salted digests. The salt never leaves D1, so neither the exported digests nor the exported hash can be brute-forced
 * back into a payload.
 */
export async function auditHash(
  prev: string,
  at: number,
  actor: string,
  action: string,
  target: string | null,
  beforeJson: string | null,
  afterJson: string | null,
  salt: string,
): Promise<string> {
  const before = await payloadDigest(beforeJson, salt);
  const after = await payloadDigest(afterJson, salt);
  return sha256Hex(JSON.stringify([prev, at, actor, action, target, salt, before, after]));
}

/** Recomputes a stored row's hash. */
export function rowHash(row: AuditRow): Promise<string> {
  return auditHash(row.prev_hash, row.at, row.actor, row.action, row.target, row.before_json, row.after_json, row.salt);
}

function isChainConflict(error: unknown): boolean {
  return error instanceof Error && /UNIQUE constraint failed: audit_log\.prev_hash/.test(error.message);
}

/**
 * Runs `statements` and appends the audit row in one D1 batch (a transaction). Returns the results of `statements`.
 * With `onlyIf` (an SQL condition and its binds, evaluated after `statements`), the audit row is appended only when
 * the condition holds, for actions whose statements may turn out to change nothing.
 */
export async function writeAudited(
  db: D1Database,
  at: number,
  entry: AuditEntry,
  statements: D1PreparedStatement[] = [],
  onlyIf?: { sql: string; binds: unknown[] },
): Promise<D1Result[]> {
  const beforeJson = entry.before === undefined ? null : JSON.stringify(entry.before);
  const afterJson = entry.after === undefined ? null : JSON.stringify(entry.after);
  for (let attempt = 1; ; attempt++) {
    const head = await db.prepare("SELECT hash FROM audit_log ORDER BY seq DESC LIMIT 1").first<{ hash: string }>();
    const prev = head?.hash ?? GENESIS_HASH;
    const salt = newAuditSalt();
    const hash = await auditHash(prev, at, entry.actor, entry.action, entry.target, beforeJson, afterJson, salt);
    const insert = db
      .prepare(
        "INSERT INTO audit_log (at, actor, action, target, before_json, after_json, prev_hash, hash, salt) " +
          `SELECT ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE ${onlyIf?.sql ?? "1"}`,
      )
      .bind(at, entry.actor, entry.action, entry.target, beforeJson, afterJson, prev, hash, salt, ...(onlyIf?.binds ?? []));
    try {
      const results = await db.batch([...statements, insert]);
      return results.slice(0, statements.length);
    } catch (error) {
      if (attempt < MAX_ATTEMPTS && isChainConflict(error)) continue;
      throw error;
    }
  }
}

export interface ChainReport {
  /** False when a row's hash or link does not match, or the last exported head is gone or changed. */
  ok: boolean;
  checked: number;
  /** First row whose hash or link does not match, when not ok. */
  brokenAtSeq: number | null;
  /** True when the oldest remaining row links to a purged row (90-day retention) rather than the genesis hash. */
  anchored: boolean;
  /**
   * The head named by the last daily export (src/audit-export.ts), and whether that row is still in the chain with the
   * same hash. `present: false` means rows were cut off or rewritten after the export. Null before the first export.
   */
  exportedHead: { seq: number; hash: string; present: boolean } | null;
}

/** Recomputes every row's hash and checks every link, oldest first, then checks the last exported head. */
export async function verifyChain(db: D1Database): Promise<ChainReport> {
  const links = await verifyLinks(db);
  const state = await db.prepare("SELECT last_seq, last_hash FROM audit_export_state WHERE id = 1").first<{ last_seq: number; last_hash: string }>();
  if (!state) return { ...links, exportedHead: null };
  const row = await db.prepare("SELECT hash FROM audit_log WHERE seq = ?").bind(state.last_seq).first<{ hash: string }>();
  const present = row !== null && row.hash === state.last_hash;
  return { ...links, ok: links.ok && present, exportedHead: { seq: state.last_seq, hash: state.last_hash, present } };
}

async function verifyLinks(db: D1Database): Promise<Omit<ChainReport, "exportedHead">> {
  const pageSize = 500;
  let lastSeq = 0;
  let previous: string | null = null;
  let checked = 0;
  let anchored = false;
  for (;;) {
    const { results } = await db.prepare("SELECT * FROM audit_log WHERE seq > ? ORDER BY seq ASC LIMIT ?").bind(lastSeq, pageSize).all<AuditRow>();
    for (const row of results) {
      if (previous === null) anchored = row.prev_hash !== GENESIS_HASH;
      else if (row.prev_hash !== previous) return { ok: false, checked, brokenAtSeq: row.seq, anchored };
      const expected = await rowHash(row);
      if (expected !== row.hash) return { ok: false, checked, brokenAtSeq: row.seq, anchored };
      previous = row.hash;
      lastSeq = row.seq;
      checked++;
    }
    if (results.length < pageSize) return { ok: true, checked, brokenAtSeq: null, anchored };
  }
}
