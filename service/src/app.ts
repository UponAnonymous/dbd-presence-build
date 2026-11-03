// Request routing by host and path. The public API lives on API_HOST under /v1/*, plus the static /privacy and /terms pages; the admin API lives on ADMIN_HOST under
// /api/*; pack files, user uploads and update packages live on CDN_HOST under /packs/, /u/ and /releases/ (src/routes/cdn.ts). Anything else is 404. No CORS anywhere: OPTIONS is answered 405 like any other unsupported method.

import { AccessKeyCache } from "./auth/access-jwt.ts";
import { ConfigCache } from "./config-cache.ts";
import type { Context, Deps, Handler } from "./context.ts";
import type { Env } from "./env.ts";
import { HttpError, errorResponse, finalize } from "./http.ts";
import * as admin from "./routes/admin.ts";
import { getApps, putApps } from "./routes/apps.ts";
import { handleCdn } from "./routes/cdn.ts";
import { legalResponse } from "./routes/legal.ts";
import { getOverview } from "./routes/overview.ts";
import * as packs from "./routes/packs.ts";
import * as releases from "./routes/releases.ts";
import * as roster from "./routes/roster.ts";
import { logout, refresh, signIn } from "./routes/auth.ts";
import { getConfig } from "./routes/config.ts";
import { redeem } from "./routes/invites.ts";
import { deleteMe, getMe } from "./routes/me.ts";
import { labelStudy } from "./routes/study.ts";
import * as uploads from "./routes/uploads.ts";

export interface Route {
  method: string;
  pattern: RegExp;
  handler: Handler;
  /** Names of the capture groups, in order; default `id`, `deviceId`. */
  params?: readonly string[];
}

const UUID = "([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})";
const PACK = "([a-z0-9-]{1,64})";
const ENTRY = "(e[0-9a-f]{24})";
const CODE = "([KS][0-9]{2})";
const FOLDED_SLUG = "([a-z0-9-]{2,40})";
const SHA256 = "([0-9a-f]{64})";

const PUBLIC_ROUTES: Route[] = [
  { method: "POST", pattern: /^\/v1\/auth\/discord$/, handler: signIn },
  { method: "POST", pattern: /^\/v1\/auth\/refresh$/, handler: refresh },
  { method: "POST", pattern: /^\/v1\/auth\/logout$/, handler: logout },
  { method: "POST", pattern: /^\/v1\/invites\/redeem$/, handler: redeem },
  { method: "GET", pattern: /^\/v1\/me$/, handler: getMe },
  { method: "DELETE", pattern: /^\/v1\/me$/, handler: deleteMe },
  { method: "GET", pattern: /^\/v1\/config$/, handler: getConfig },
  { method: "GET", pattern: /^\/v1\/packs$/, handler: packs.getPacks },
  { method: "GET", pattern: /^\/v1\/roster$/, handler: roster.getRoster },
  { method: "GET", pattern: /^\/v1\/releases\/(stable|beta)$/, handler: releases.getReleaseFeed },
  { method: "GET", pattern: /^\/v1\/keys$/, handler: releases.getKeyReplacement },
  { method: "POST", pattern: /^\/v1\/roster\/sightings$/, handler: roster.reportSightings },
  { method: "POST", pattern: /^\/v1\/study\/label$/, handler: labelStudy },
  { method: "POST", pattern: /^\/v1\/uploads$/, handler: uploads.createUpload },
  { method: "DELETE", pattern: new RegExp(`^/v1/uploads/${UUID}$`), handler: uploads.deleteUpload },
];

/** Exported so a test can enumerate every admin route and prove each one is guarded. */
export const ADMIN_ROUTES: Route[] = [
  { method: "GET", pattern: /^\/api\/overview$/, handler: getOverview },
  { method: "GET", pattern: /^\/api\/invites$/, handler: admin.listInvites },
  { method: "POST", pattern: /^\/api\/invites$/, handler: admin.createInvites },
  { method: "POST", pattern: new RegExp(`^/api/invites/${UUID}/revoke$`), handler: admin.revokeInvite },
  { method: "GET", pattern: /^\/api\/users$/, handler: admin.listUsers },
  { method: "POST", pattern: new RegExp(`^/api/users/${UUID}/revoke$`), handler: admin.revokeUser },
  { method: "POST", pattern: new RegExp(`^/api/users/${UUID}/devices/${UUID}/revoke$`), handler: admin.revokeDevice },
  { method: "DELETE", pattern: new RegExp(`^/api/users/${UUID}$`), handler: admin.removeUser },
  { method: "GET", pattern: new RegExp(`^/api/users/${UUID}/export$`), handler: admin.exportUser },
  { method: "POST", pattern: new RegExp(`^/api/users/${UUID}/uploads$`), handler: uploads.setUserUploadBlock },
  { method: "PUT", pattern: new RegExp(`^/api/users/${UUID}/roster-reporter$`), handler: roster.setRosterReporter },
  { method: "GET", pattern: /^\/api\/uploads$/, handler: uploads.listUploadsAdmin },
  { method: "POST", pattern: new RegExp(`^/api/uploads/${UUID}/takedown$`), handler: uploads.takeDownUpload },
  { method: "GET", pattern: /^\/api\/storage$/, handler: uploads.getStorage },
  { method: "GET", pattern: /^\/api\/audit$/, handler: admin.listAudit },
  { method: "GET", pattern: /^\/api\/audit\/verify$/, handler: admin.verifyAudit },
  { method: "GET", pattern: /^\/api\/settings$/, handler: admin.readSettings },
  { method: "PUT", pattern: /^\/api\/settings$/, handler: admin.writeSettings },
  { method: "GET", pattern: /^\/api\/apps$/, handler: getApps },
  { method: "PUT", pattern: /^\/api\/apps$/, handler: putApps },
  { method: "GET", pattern: /^\/api\/packs$/, handler: packs.listPacks },
  { method: "POST", pattern: /^\/api\/packs$/, handler: packs.createPack },
  { method: "POST", pattern: /^\/api\/pack-files$/, handler: packs.uploadObject },
  { method: "POST", pattern: /^\/api\/pack-files\/purge$/, handler: packs.purgeFiles },
  { method: "PUT", pattern: new RegExp(`^/api/packs/${PACK}$`), handler: packs.updatePack },
  { method: "DELETE", pattern: new RegExp(`^/api/packs/${PACK}$`), handler: packs.deletePack },
  { method: "POST", pattern: new RegExp(`^/api/packs/${PACK}/publish$`), handler: packs.publishPack },
  { method: "POST", pattern: new RegExp(`^/api/packs/${PACK}/unpublish$`), handler: packs.unpublishPack },
  { method: "POST", pattern: new RegExp(`^/api/packs/${PACK}/entries$`), handler: packs.addEntry },
  { method: "PUT", pattern: new RegExp(`^/api/packs/${PACK}/entries/${ENTRY}$`), handler: packs.updateEntry, params: ["id", "entryId"] },
  { method: "DELETE", pattern: new RegExp(`^/api/packs/${PACK}/entries/${ENTRY}$`), handler: packs.removeEntry, params: ["id", "entryId"] },
  { method: "POST", pattern: new RegExp(`^/api/packs/${PACK}/entries/${ENTRY}/withdraw$`), handler: packs.withdrawEntry, params: ["id", "entryId"] },
  { method: "PUT", pattern: new RegExp(`^/api/packs/${PACK}/default$`), handler: packs.setDefaultPack },
  { method: "GET", pattern: /^\/api\/roster$/, handler: roster.getRosterAdmin },
  { method: "POST", pattern: /^\/api\/roster\/characters$/, handler: roster.addCharacter },
  { method: "PUT", pattern: new RegExp(`^/api/roster/characters/${CODE}$`), handler: roster.updateCharacter },
  { method: "POST", pattern: new RegExp(`^/api/roster/characters/${CODE}/withdraw$`), handler: roster.withdrawCharacter },
  { method: "DELETE", pattern: new RegExp(`^/api/roster/blocks/${FOLDED_SLUG}$`), handler: roster.allowName },
  { method: "DELETE", pattern: /^\/api\/roster\/sightings$/, handler: roster.clearSightings },
  { method: "GET", pattern: /^\/api\/releases$/, handler: releases.listReleases },
  { method: "POST", pattern: /^\/api\/releases\/feeds$/, handler: releases.uploadFeed },
  { method: "PUT", pattern: new RegExp(`^/api/releases/packages/${SHA256}$`), handler: releases.uploadPackage },
  { method: "POST", pattern: new RegExp(`^/api/releases/feeds/${UUID}/publish$`), handler: releases.publishFeed },
  { method: "POST", pattern: new RegExp(`^/api/releases/feeds/${UUID}/discard$`), handler: releases.discardFeed },
  { method: "POST", pattern: /^\/api\/keys\/replacement$/, handler: releases.uploadKeyReplacement },
];

function select(routes: Route[], method: string, path: string): { handler: Handler; params: Record<string, string> } {
  const allowed: string[] = [];
  for (const route of routes) {
    const match = route.pattern.exec(path);
    if (!match) continue;
    if (route.method === method) {
      const params: Record<string, string> = {};
      (route.params ?? ["id", "deviceId"]).forEach((name, i) => {
        if (match[i + 1] !== undefined) params[name] = match[i + 1]!;
      });
      return { handler: route.handler, params };
    }
    allowed.push(route.method);
  }
  if (allowed.length > 0) throw new HttpError(405, "method_not_allowed", { Allow: allowed.join(", ") });
  throw new HttpError(404, "not_found");
}

const defaultAccessKeys = new AccessKeyCache();
const defaultConfigCache = new ConfigCache(() => Promise.resolve(caches.default));

/** Production dependencies: the platform fetch and clock, and per-isolate Access key and config caches. */
export function defaultDeps(): Deps {
  return { fetch: (input, init) => fetch(input, init), now: () => Date.now(), accessKeys: defaultAccessKeys, configCache: defaultConfigCache };
}

async function dispatch(request: Request, env: Env, deps: Deps): Promise<Response> {
  const url = new URL(request.url);
  let routes: Route[];
  if (url.hostname === env.API_HOST && (url.pathname === "/privacy" || url.pathname === "/terms")) {
    if (url.protocol !== "https:") throw new HttpError(400, "https_required");
    const page = legalResponse(request.method, url.pathname);
    if (page !== null) return page;
  }
  if (url.hostname === env.API_HOST && url.pathname.startsWith("/v1/")) routes = PUBLIC_ROUTES;
  else if (url.hostname === env.ADMIN_HOST && url.pathname.startsWith("/api/")) routes = ADMIN_ROUTES;
  else throw new HttpError(404, "not_found");
  if (url.protocol !== "https:") throw new HttpError(400, "https_required");
  const { handler, params } = select(routes, request.method, url.pathname);
  const ctx: Context = { request, env, deps, url, now: deps.now(), params };
  return handler(ctx);
}

/**
 * Handles one request. Expected failures are `HttpError`s and become `{"error": code}`; anything else is logged by
 * error type only (never a message, token, code or IP) and answered 500 `internal`.
 */
export async function handleRequest(request: Request, env: Env, deps: Deps): Promise<Response> {
  // The content host has its own hardening (image types, a sandbox CSP) and never reaches the JSON routes.
  if (new URL(request.url).hostname === env.CDN_HOST) return handleCdn(request, env);
  let response: Response;
  try {
    response = await dispatch(request, env, deps);
  } catch (error) {
    if (error instanceof HttpError) {
      response = errorResponse(error);
    } else {
      console.error(JSON.stringify({ event: "unhandled_error", type: error instanceof Error ? error.name : typeof error }));
      response = errorResponse(new HttpError(500, "internal"));
    }
  }
  return finalize(response);
}
