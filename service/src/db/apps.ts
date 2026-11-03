// The Discord applications listed in the signed config (D2, FR-ONL-08, FR-ADM-08). The owner edits the whole list in
// the Owner Panel; the rules match the app's parser (src/DbdPresence.Online/Config/RemoteConfigService.cs), so a list
// the service accepts is never refused by the app.

import { HttpError } from "../http.ts";

export interface DiscordApp {
  key: string;
  applicationId: string;
  name: string;
  description: string;
}

export interface AppList {
  applications: DiscordApp[];
  defaultApplication: string;
}

export const APP_KEY_PATTERN = /^[a-z0-9-]{1,32}$/;
export const APPLICATION_ID_PATTERN = /^\d{15,22}$/;
export const APP_NAME_MAX = 128;
export const APP_DESCRIPTION_MAX = 256;
export const APPS_MAX = 10;

interface AppRow {
  key: string;
  application_id: string;
  name: string;
  description: string;
  is_default: number;
  position: number;
}

const CONTROL = /[\p{Cc}]/u;

function invalid(): never {
  throw new HttpError(400, "invalid_request");
}

/** Validates a complete list (unique keys and IDs, one default that is in the list). */
export function validateAppList(value: unknown): AppList {
  if (typeof value !== "object" || value === null || Array.isArray(value)) invalid();
  const record = value as Record<string, unknown>;
  for (const k of Object.keys(record)) if (k !== "applications" && k !== "defaultApplication") invalid();
  const list = record["applications"];
  const defaultKey = record["defaultApplication"];
  if (!Array.isArray(list) || list.length < 1 || list.length > APPS_MAX || typeof defaultKey !== "string") invalid();
  const applications: DiscordApp[] = [];
  for (const item of list) {
    if (typeof item !== "object" || item === null || Array.isArray(item)) invalid();
    const app = item as Record<string, unknown>;
    for (const k of Object.keys(app)) if (!["key", "applicationId", "name", "description"].includes(k)) invalid();
    const { key, applicationId, name, description } = app;
    if (typeof key !== "string" || !APP_KEY_PATTERN.test(key)) invalid();
    if (typeof applicationId !== "string" || !APPLICATION_ID_PATTERN.test(applicationId)) invalid();
    if (typeof name !== "string" || name.trim().length === 0 || name.length > APP_NAME_MAX || CONTROL.test(name)) invalid();
    if (typeof description !== "string" || description.length > APP_DESCRIPTION_MAX || CONTROL.test(description)) invalid();
    if (applications.some((a) => a.key === key || a.applicationId === applicationId)) invalid();
    applications.push({ key, applicationId, name: name.trim(), description: description.trim() });
  }
  if (!applications.some((a) => a.key === defaultKey)) invalid();
  return { applications, defaultApplication: defaultKey };
}

export function appsQuery(db: D1Database): D1PreparedStatement {
  return db.prepare("SELECT key, application_id, name, description, is_default, position FROM discord_apps ORDER BY position, key");
}

/** Builds the list from rows; null when the table is empty or has no default (the config then uses the built-in list). */
export function appListFromRows(rows: readonly AppRow[]): AppList | null {
  const applications = rows.map((r) => ({ key: r.key, applicationId: r.application_id, name: r.name, description: r.description }));
  const defaultRow = rows.find((r) => r.is_default === 1);
  if (applications.length === 0 || !defaultRow) return null;
  return { applications, defaultApplication: defaultRow.key };
}

export async function getAppList(db: D1Database): Promise<AppList | null> {
  return appListFromRows((await appsQuery(db).all<AppRow>()).results);
}

/** Statements that replace the stored list with `list`. */
export function replaceAppsStatements(db: D1Database, list: AppList): D1PreparedStatement[] {
  return [
    db.prepare("DELETE FROM discord_apps"),
    ...list.applications.map((a, i) =>
      db
        .prepare("INSERT INTO discord_apps (key, application_id, name, description, is_default, position) VALUES (?, ?, ?, ?, ?, ?)")
        .bind(a.key, a.applicationId, a.name, a.description, a.key === list.defaultApplication ? 1 : 0, i),
    ),
  ];
}

export type { AppRow };
