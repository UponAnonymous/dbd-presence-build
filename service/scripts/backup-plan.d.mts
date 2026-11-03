// Types for scripts/backup-plan.mjs (used by test/backups.test.ts).

import type { Environment } from "./provision-plan.mjs";

export interface RecordedExport {
  at: number;
  bytes: number;
  object_key: string | null;
}

export interface StorageCheck {
  ok: boolean;
  storedBytes: number;
  reason: string | null;
}

export interface DumpAnalysis {
  tables: { name: string; rows: number }[];
  migrations: string[];
  statements: number;
  rows: number;
}

export interface RestoreComparison {
  ok: boolean;
  rows: { table: string; expected: number | null; restored: number | null; ok: boolean }[];
  problems: string[];
  newerMigrations: number;
}

export interface ExportRecord {
  key: string | null;
  bytes: number;
  tables: number;
  rows: number;
  uploaded: boolean;
  lifecycle?: string;
  storedBytes?: number;
  deleted?: string[];
  error: string | null;
}

export interface DrillRecord {
  key: string | null;
  bytes: number;
  database: string | null;
  migrations?: number;
  comparison: RestoreComparison | null;
  deleted: boolean | null;
  error: string | null;
}

export const BACKUP_RETENTION_DAYS: number;
export const BACKUP_LIFECYCLE_RULE_ID: string;
export const BACKUP_LOCK_DAYS: number;
export const BACKUP_LOCK_RULE_ID: string;
export const BACKUP_MAX_BYTES: number;
export const BACKUP_BUDGET_BYTES: Readonly<Record<Environment, number>>;
export function backupBudgetBytes(env: string): number;
export const BACKUP_RUNS_KEPT: number;
export const BACKUP_GUIDE: string;
export const BACKUP_GITHUB_ENVIRONMENTS: Record<Environment, string>;
export const BACKUP_OBJECT_KEY: RegExp;
export const RECENT_EXPORTS_SQL: string;
export const RESTORED_TABLES_SQL: string;
export const RESTORED_MIGRATIONS_SQL: string;

export function backupBucketName(env: string): string;
export function backupLifecycleRule(): Record<string, unknown>;
export function backupLockRule(): Record<string, unknown>;
export function backupObjectKey(atMs: number): string;
export function readBackupKey(raw: string | undefined, env: string): string;
export function decryptionVerified(exitCode: number | null, statusText: string): boolean;
export function expiredBackups(exports: RecordedExport[], now: number): string[];
export function storageCheck(input: { env: string; newBytes: number; exports: RecordedExport[]; now: number; stillStored?: string[] }): StorageCheck;
export function safeDetail(text: string): string;
export function insertRunSql(run: { kind: string; at: number; bytes: number; ok: boolean; detail: string; objectKey?: string | null }): string;
export function d1JsonResults(stdout: string): Record<string, unknown>[];
export function safeLogLines(text: string, max?: number): string[];
export function wranglerLog(input: { what: string; status: number | string | null; stdout?: string; stderr?: string; sensitive: boolean }): string;
export function splitSqlStatements(text: string): string[];
export function analyseDump(text: string): DumpAnalysis;
export function drillDatabaseName(env: string, runId: string | number | undefined): string;
export function drillDatabasePattern(env: string): RegExp;
export const DRILL_SWEEP_AGE_MS: number;
export interface SweepRecord {
  deleted?: string[];
  failed?: string[];
}
export function staleDrillDatabases(input: { env: string; databases: unknown; now: number; liveName: string; liveId: string | null }): {
  remove: { name: string; id: string; createdAt: number }[];
  kept: { name: string; reason: string }[];
};
export function assertDrillTarget(input: { env: string; name: string; id: string; liveName: string; liveId: string | null }): true;
export function drillWranglerConfig(name: string, id: string): Record<string, unknown>;
export function rowCountQueries(tables: string[]): string[];
export function countsFromRow(row: Record<string, unknown> | null | undefined): { name: string; n: number }[];
export function compareRestore(input: {
  dump: DumpAnalysis;
  restoredTables: string[];
  restoredCounts: { name: string; n: number }[];
  restoredMigrations: string[];
  repoMigrations: string[];
}): RestoreComparison;
export function renderBackupSummary(input: { env: string; exportRun?: ExportRecord | null; drill?: DrillRecord | null; sweep?: SweepRecord | null }): string;
export function scheduledRunReady(raw: unknown): boolean;
