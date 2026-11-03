// GET /v1/config (FR-ONL-08, PLAN_V2 §6.4): the signed config document. Public, unauthenticated and cacheable, so it
// is served from ConfigCache (src/config-cache.ts) for up to 60 seconds: no D1 read and no signature per request.
// Floods are shed at the edge by the WAF rule over the whole API host (docs/owner/cloudflare-setup.md §10).

import { CONFIG_CACHE_CONTROL, configCacheKey } from "../config-cache.ts";
import type { Context } from "../context.ts";
import { appListFromRows, appsQuery, type AppRow } from "../db/apps.ts";
import { configVersionFromRows, settingsFromRows, settingsQuery, type SettingsRow } from "../db/settings.ts";
import { SCHEMA_VERSION } from "../http.ts";
import { MAX_LIFETIME_MS, signDocument } from "../signing/document.ts";
import { loadSigningKey } from "../signing/keys.ts";
import { BUILT_IN_APPS, configPayload } from "./config-payload.ts";

export const CONFIG_CHANNEL = "stable";

function configResponse(body: string): Response {
  return new Response(body, { status: 200, headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": CONFIG_CACHE_CONTROL } });
}

export async function getConfig(ctx: Context): Promise<Response> {
  // A missing or broken key is reported even while a document signed earlier is still cached.
  const key = await loadSigningKey(ctx.env.CONFIG_SIGNING_KEY);
  const cacheKey = configCacheKey(ctx.env.API_HOST);
  const cached = await ctx.deps.configCache.get(cacheKey, ctx.now);
  if (cached !== null) return configResponse(cached);

  // One batch is one read transaction: the settings, the application list and the version always belong together.
  const [settingsRows, appRows] = await ctx.env.DB.batch([settingsQuery(ctx.env.DB), appsQuery(ctx.env.DB)]);
  const rows = (settingsRows!.results ?? []) as SettingsRow[];
  const apps = appListFromRows((appRows!.results ?? []) as AppRow[]) ?? BUILT_IN_APPS;
  const issuedAt = new Date(ctx.now);
  // The stored config version: raised by every change the document carries (src/db/settings.ts), so the client's
  // rollback rule refuses an older config while re-signed copies of the same config keep the same version.
  const document = await signDocument("config", CONFIG_CHANNEL, configVersionFromRows(rows), configPayload(settingsFromRows(rows), apps), key.key, key.kid, {
    issuedAt,
    expiresAt: new Date(ctx.now + MAX_LIFETIME_MS.config),
  });
  const body = JSON.stringify({ schemaVersion: SCHEMA_VERSION, document });
  await ctx.deps.configCache.put(cacheKey, body, ctx.now);
  return configResponse(body);
}
