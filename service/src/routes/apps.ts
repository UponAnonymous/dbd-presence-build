// The Discord application list in the signed config (D2, FR-ADM-08, FR-ONL-08). The owner replaces the whole list at
// once; the change raises the config version and drops the cached config here, like a settings change.

import { configCacheKey } from "../config-cache.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import { getAppList, replaceAppsStatements, validateAppList } from "../db/apps.ts";
import { bumpConfigVersion, configVersionFromRows, settingsQuery, type SettingsRow } from "../db/settings.ts";
import { json, readJsonBody } from "../http.ts";
import { BUILT_IN_APPS } from "./config-payload.ts";
import { requireAdmin } from "./admin.ts";

async function configVersion(db: D1Database): Promise<number> {
  return configVersionFromRows((await settingsQuery(db).all<SettingsRow>()).results);
}

export async function getApps(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const list = (await getAppList(ctx.env.DB)) ?? BUILT_IN_APPS;
  return json({ ...list, configVersion: await configVersion(ctx.env.DB) });
}

export async function putApps(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const list = validateAppList(await readJsonBody(ctx.request, 8192));
  const db = ctx.env.DB;
  const before = (await getAppList(db)) ?? BUILT_IN_APPS;
  if (JSON.stringify(before) !== JSON.stringify(list)) {
    // Application keys and IDs are public identifiers; names and descriptions are left out of the audit row.
    const summary = (l: typeof list) => ({ applications: l.applications.map((a) => ({ key: a.key, applicationId: a.applicationId })), defaultApplication: l.defaultApplication });
    await writeAudited(db, ctx.now, { actor, action: "apps.update", target: "config", before: summary(before), after: summary(list) }, [...replaceAppsStatements(db, list), bumpConfigVersion(db, ctx.now)]);
    await ctx.deps.configCache.clear(configCacheKey(ctx.env.API_HOST));
  }
  return json({ ...((await getAppList(db)) ?? BUILT_IN_APPS), configVersion: await configVersion(db) });
}
