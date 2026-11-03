// Daily append-only export of the audit log (FR-ADM-05, ADR-0014) to the separate R2 bucket bound as AUDIT_EXPORT,
// run by the cron trigger in wrangler.toml before the retention job. The Worker only ever writes new objects there
// (never lists, reads, overwrites or deletes); the owner adds a bucket lock rule (docs/owner/cloudflare-setup.md), so
// exported rows outlive any later change to D1.
//
// Redaction: the export carries only chain metadata, never a row's before/after payload (which can hold free text
// such as a banner): per row seq, at, actor, action, target, the salted digests of before_json and after_json,
// prev_hash and hash. Each row's 16-byte salt stays in D1 and is folded into both digests and the row
// hash, so nothing can be brute-forced back from the export. What an export proves, therefore, only together with D1
// (`verifyExportAgainstD1`): every exported row still exists in D1 with the same metadata, hash and payload digests, so
// it was neither changed nor removed; and on its own (`exportLinks`): the exported rows form an unbroken chain from the
// previous export's head. Actors are "owner:<email>" (the owner's own address), "user:<random id>" (pseudonymous) or
// "system". ADR-0014 records this.
//
// Every export names the chain head at export time (the newest row's seq and hash) and the previous export's head.
// `audit_export_state` keeps the last head in D1 and the retention job never deletes that row, so GET /api/audit/verify
// can tell when rows after it were cut off or the row itself changed. Against a tampered D1 the R2 objects are the
// reference: the head in the newest export must still be in the chain, with the same hash.

import { payloadDigest, rowHash, type AuditRow } from "./db/audit.ts";
import { putWithinCap } from "./storage.ts";

/** One exported row: chain metadata, with the payloads replaced by their salted digests (the salt is not exported). */
export interface ExportedRow {
  seq: number;
  at: number;
  actor: string;
  action: string;
  target: string | null;
  beforeDigest: string | null;
  afterDigest: string | null;
  prevHash: string;
  hash: string;
}

async function exportedRow(row: AuditRow): Promise<ExportedRow> {
  return {
    seq: row.seq,
    at: row.at,
    actor: row.actor,
    action: row.action,
    target: row.target,
    beforeDigest: await payloadDigest(row.before_json, row.salt),
    afterDigest: await payloadDigest(row.after_json, row.salt),
    prevHash: row.prev_hash,
    hash: row.hash,
  };
}

/** The exported rows' own links, from the previous head on: the seq of the first broken link, or null. */
export function exportLinks(rows: readonly ExportedRow[], previousHash: string | null): number | null {
  let previous = previousHash;
  for (const row of rows) {
    if (previous !== null && row.prevHash !== previous) return row.seq;
    previous = row.hash;
  }
  return null;
}

/**
 * Checks exported rows against D1: each must still exist with the same metadata, hash and salted payload digests,
 * and its stored hash must still be its own. Returns the seq of the first row that does not match, or null.
 */
export async function verifyExportAgainstD1(db: D1Database, rows: readonly ExportedRow[]): Promise<number | null> {
  for (const exported of rows) {
    const row = await db.prepare("SELECT * FROM audit_log WHERE seq = ?").bind(exported.seq).first<AuditRow>();
    if (!row || (await rowHash(row)) !== row.hash) return exported.seq;
    const current = await exportedRow(row);
    if (JSON.stringify(current) !== JSON.stringify(exported)) return exported.seq;
  }
  return null;
}

export const AUDIT_EXPORT_PAGE = 1000;
const MAX_PAGES_PER_RUN = 50;

export interface ExportHead {
  seq: number;
  hash: string;
}

export interface AuditExportReport {
  objects: number;
  rows: number;
  head: ExportHead | null;
}

export async function lastExportedHead(db: D1Database): Promise<ExportHead | null> {
  const state = await db.prepare("SELECT last_seq, last_hash FROM audit_export_state WHERE id = 1").first<{ last_seq: number; last_hash: string }>();
  return state ? { seq: state.last_seq, hash: state.last_hash } : null;
}

function objectKey(environment: string, now: number, fromSeq: number, toSeq: number): string {
  const day = new Date(now).toISOString().slice(0, 10);
  const pad = (n: number) => String(n).padStart(12, "0");
  return `audit/${environment}/${day}/${now}-${pad(fromSeq)}-${pad(toSeq)}.json`;
}

/**
 * Exports every audit row after the last exported one, in pages of AUDIT_EXPORT_PAGE rows, one object per page. A run
 * with nothing new still writes one object naming the unchanged head, so every day leaves a dated anchor.
 */
export async function runAuditExport(db: D1Database, bucket: R2Bucket, environment: string, now: number): Promise<AuditExportReport> {
  let previous = await lastExportedHead(db);
  let objects = 0;
  let rows = 0;
  for (let page = 0; page < MAX_PAGES_PER_RUN; page++) {
    const { results } = await db.prepare("SELECT * FROM audit_log WHERE seq > ? ORDER BY seq ASC LIMIT ?").bind(previous?.seq ?? 0, AUDIT_EXPORT_PAGE).all<AuditRow>();
    if (results.length === 0 && objects > 0) break;
    const last = results[results.length - 1];
    const head = last ? { seq: last.seq, hash: last.hash } : previous;
    const body = JSON.stringify({
      schemaVersion: 1,
      kind: "dbdp-audit-export",
      environment,
      exportedAt: new Date(now).toISOString(),
      previousHead: previous,
      head,
      // The first row must link to the previous head; a mismatch is reported here and by the verify endpoint.
      linksToPreviousHead: results.length === 0 || previous === null ? null : results[0]!.prev_hash === previous.hash,
      rows: await Promise.all(results.map(exportedRow)),
    });
    const fromSeq = results[0]?.seq ?? (previous?.seq ?? 0);
    // Counted against the storage cap like every other R2 write (D18). Over the cap the export stops here (507
    // storage_full) and the head is not advanced, so retention keeps every row that has not been exported.
    await putWithinCap(db, bucket, "audit", objectKey(environment, now, fromSeq, head?.seq ?? 0), body, { httpMetadata: { contentType: "application/json" } }, now, environment);
    objects++;
    rows += results.length;
    if (head) {
      await db
        .prepare(
          "INSERT INTO audit_export_state (id, last_seq, last_hash, exported_at) VALUES (1, ?, ?, ?) " +
            "ON CONFLICT (id) DO UPDATE SET last_seq = excluded.last_seq, last_hash = excluded.last_hash, exported_at = excluded.exported_at",
        )
        .bind(head.seq, head.hash, now)
        .run();
    }
    previous = head;
    if (results.length < AUDIT_EXPORT_PAGE) break;
  }
  return { objects, rows, head: previous };
}
