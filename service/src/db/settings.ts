// Server settings (FR-ONL-06, PLAN_V2 §6.4). Stored as JSON text per key; a missing or unreadable value falls back
// to its safe default. The feature switches can only *disable* capture and detection (SAFETY_AND_PRIVACY §2.3).

import { writeAudited } from "./audit.ts";

export type AccessMode = "invite-only" | "public";
export const ACCESS_MODES: readonly AccessMode[] = ["invite-only", "public"];

export interface Settings {
  accessMode: AccessMode;
  deviceLimit: number;
  disableCapture: boolean;
  disableDetection: boolean;
  emergencyStop: boolean;
  minimumVersion: string;
  banner: string | null;
  /** Global switch: refuse every new upload (FR-ADM-09). Uploads already stored stay. */
  uploadsBlocked: boolean;
  /** Most bytes of pictures one person may have stored (D28, FR-ONL-15; 60 MB = 62 914 560 bytes by default). */
  uploadLimitBytes: number;
  /** Most pictures one person may have stored, a backstop behind the byte cap (D28, FR-ONL-15; 300 by default). */
  uploadLimitFiles: number;
}

export const DEFAULT_SETTINGS: Readonly<Settings> = {
  accessMode: "invite-only",
  deviceLimit: 3,
  disableCapture: false,
  disableDetection: false,
  emergencyStop: false,
  minimumVersion: "0.1.0",
  banner: null,
  uploadsBlocked: false,
  uploadLimitBytes: 60 * 1024 * 1024,
  uploadLimitFiles: 300,
};

export const DEVICE_LIMIT_MAX = 10;
export const BANNER_MAX = 280;
/** The owner may raise the per-person byte cap up to this (D18: 50 people at this cap still stay under the 6 GB upload stop). */
export const UPLOAD_LIMIT_BYTES_MAX = 120 * 1024 * 1024;
export const UPLOAD_LIMIT_FILES_MAX = 1000;
export const VERSION_PATTERN = /^\d{1,5}\.\d{1,5}\.\d{1,5}$/;

const COLUMNS: Readonly<Record<keyof Settings, string>> = {
  accessMode: "access_mode",
  deviceLimit: "device_limit",
  disableCapture: "disable_capture",
  disableDetection: "disable_detection",
  emergencyStop: "emergency_stop",
  minimumVersion: "minimum_version",
  banner: "banner",
  uploadsBlocked: "uploads_blocked",
  uploadLimitBytes: "upload_limit_bytes",
  uploadLimitFiles: "upload_limit_files",
};

/** Settings the signed config does not carry: changing them does not raise the config version. */
const NOT_IN_CONFIG: ReadonlySet<keyof Settings> = new Set<keyof Settings>(["deviceLimit", "uploadsBlocked", "uploadLimitBytes", "uploadLimitFiles"]);

function isValid<K extends keyof Settings>(key: K, value: unknown): value is Settings[K] {
  switch (key) {
    case "accessMode":
      return typeof value === "string" && (ACCESS_MODES as readonly string[]).includes(value);
    case "deviceLimit":
      return typeof value === "number" && Number.isInteger(value) && value >= 1 && value <= DEVICE_LIMIT_MAX;
    case "uploadLimitBytes":
      return typeof value === "number" && Number.isInteger(value) && value >= 0 && value <= UPLOAD_LIMIT_BYTES_MAX;
    case "uploadLimitFiles":
      return typeof value === "number" && Number.isInteger(value) && value >= 0 && value <= UPLOAD_LIMIT_FILES_MAX;
    case "disableCapture":
    case "disableDetection":
    case "emergencyStop":
    case "uploadsBlocked":
      return typeof value === "boolean";
    case "minimumVersion":
      return typeof value === "string" && VERSION_PATTERN.test(value);
    case "banner":
      return value === null || (typeof value === "string" && value.length <= BANNER_MAX);
    default:
      return false;
  }
}

function parseStored(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch (error) {
    if (error instanceof SyntaxError) return undefined;
    throw error;
  }
}

/**
 * The version of the signed config document is stored as the settings row `config_version` and raised by every change
 * the document carries (settings and the Discord application list), in the same transaction as the change. It becomes
 * MAX(old + 1, now in seconds): it always increases, and a database restored from an older backup still issues
 * versions above any a client has seen, so the client's rollback rule never locks clients out.
 */
export const CONFIG_VERSION_KEY = "config_version";

export function bumpConfigVersion(db: D1Database, at: number): D1PreparedStatement {
  return db
    .prepare(
      "INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) " +
        "ON CONFLICT (key) DO UPDATE SET value = CAST(MAX(CAST(value AS INTEGER) + 1, CAST(excluded.value AS INTEGER)) AS TEXT), updated_at = excluded.updated_at",
    )
    .bind(CONFIG_VERSION_KEY, String(Math.floor(at / 1000)), at);
}

export type SettingsRow = { key: string; value: string };

export function settingsQuery(db: D1Database): D1PreparedStatement {
  return db.prepare("SELECT key, value FROM settings");
}

/** The config version in the rows, or 0 before the first change. */
export function configVersionFromRows(rows: readonly SettingsRow[]): number {
  const text = rows.find((r) => r.key === CONFIG_VERSION_KEY)?.value;
  const value = text === undefined ? 0 : Number(text);
  return Number.isSafeInteger(value) && value >= 0 ? value : 0;
}

export async function getSettings(db: D1Database): Promise<Settings> {
  return settingsFromRows((await settingsQuery(db).all<SettingsRow>()).results);
}

export function settingsFromRows(results: readonly SettingsRow[]): Settings {
  const stored = new Map(results.map((r) => [r.key, r.value]));
  const settings: Settings = { ...DEFAULT_SETTINGS };
  for (const key of Object.keys(COLUMNS) as (keyof Settings)[]) {
    const text = stored.get(COLUMNS[key]);
    if (text === undefined) continue;
    const value = parseStored(text);
    if (isValid(key, value)) (settings as unknown as Record<string, unknown>)[key] = value;
  }
  return settings;
}

/** Applies `patch` (already validated by the caller) and writes one audit row with before and after. */
export async function updateSettings(db: D1Database, at: number, actor: string, patch: Partial<Settings>): Promise<Settings> {
  const before = await getSettings(db);
  const after: Settings = { ...before, ...patch };
  const changed = (Object.keys(patch) as (keyof Settings)[]).filter((k) => before[k] !== after[k]);
  if (changed.length === 0) return before;
  const statements = changed.map((k) =>
    db
      .prepare("INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at")
      .bind(COLUMNS[k], JSON.stringify(after[k]), at),
  );
  // Every setting except the device and upload limits and the upload switch is carried by the signed config.
  if (changed.some((k) => !NOT_IN_CONFIG.has(k))) statements.push(bumpConfigVersion(db, at));
  const pick = (s: Settings) => Object.fromEntries(changed.map((k) => [k, s[k]]));
  await writeAudited(db, at, { actor, action: "settings.update", target: "settings", before: pick(before), after: pick(after) }, statements);
  return after;
}
