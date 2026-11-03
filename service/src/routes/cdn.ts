// The content host (PLAN_V2 §1 host hardening, §3.5, §6.3; FR-ONL-07, FR-ONL-09, NFR-SVC-02):
// - https://<CDN_HOST>/packs/<sha256>.<png|jpg>: pack files from R2 bucket PACKS by their content hash, so a name
//   always means the same bytes: cached for a year as immutable.
// - https://<CDN_HOST>/u/<32 hex>.<png|jpg>: user uploads from R2 bucket UPLOADS, unlisted (a 128-bit random name, no
//   user id). A file is served only while its upload row exists in D1, so a delete or takedown answers 404 at once even
//   if the R2 delete failed (the daily sweep retries it). An upload can be removed, so it is cached for an hour only, and
//   marked noindex.
// - https://<CDN_HOST>/releases/<sha256>.nupkg: signed update packages from R2 bucket RELEASES (V2-S6, FR-ONL-10), only while
//   a published or staged release feed lists them (src/routes/releases.ts). Content-addressed and verified by the app against
//   the signed feed, so cached as immutable; never shown inline.
// Only those three path shapes are served, with GET or HEAD; everything else is 404 (405 for another method), and an
// unexpected failure is 500. Every response, errors included, carries the
// hardening below and never a cookie. The Content-Type is fixed by the file extension, never taken from the object.

import type { Env } from "../env.ts";

const PACK_PATH = /^\/packs\/([0-9a-f]{64})\.(png|jpg)$/;
const UPLOAD_PATH = /^\/u\/([0-9a-f]{32})\.(png|jpg)$/;
const RELEASE_PATH = /^\/releases\/([0-9a-f]{64})\.nupkg$/;

/** Headers on every response from the content host. */
export const CDN_HEADERS: Readonly<Record<string, string>> = {
  "Strict-Transport-Security": "max-age=63072000; includeSubDomains",
  "X-Content-Type-Options": "nosniff",
  "Content-Security-Policy": "default-src 'none'; sandbox",
  "Referrer-Policy": "no-referrer",
  // The Owner Panel (admin.<domain>, same site) shows previews; Discord fetches the file server-side.
  "Cross-Origin-Resource-Policy": "same-site",
  "X-Frame-Options": "DENY",
};

/** Content-addressed: a file never changes under its name. */
export const IMMUTABLE = "public, max-age=31536000, immutable";
/** An upload may be deleted or taken down: downstream caches keep it an hour at most. */
export const UPLOAD_CACHE = "public, max-age=3600";
/** A missing file may appear later (published after the request) or be taken down; cache that answer briefly. */
const NOT_FOUND_CACHE = "public, max-age=60";

function respond(status: number, body: BodyInit | null, headers: Record<string, string>): Response {
  const out = new Response(body, { status, headers: { ...CDN_HEADERS, ...headers } });
  out.headers.delete("Set-Cookie");
  return out;
}

function textError(status: number, text: string, cacheControl: string, extra: Record<string, string> = {}): Response {
  return respond(status, text, { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": cacheControl, ...extra });
}

async function serveRelease(request: Request, env: Env, sha: string): Promise<Response> {
  if (request.method !== "GET" && request.method !== "HEAD") return textError(405, "Method not allowed\n", "no-store", { Allow: "GET, HEAD" });
  if (!(await env.DB.prepare("SELECT 1 FROM release_packages WHERE sha256 = ?").bind(sha).first())) return textError(404, "Not found\n", NOT_FOUND_CACHE);
  const key = `${sha}.nupkg`;
  const object: R2Object | R2ObjectBody | null = request.method === "HEAD" ? await env.RELEASES.head(key) : await env.RELEASES.get(key);
  if (!object) return textError(404, "Not found\n", NOT_FOUND_CACHE);
  const body = "body" in object ? (object as R2ObjectBody).body : null;
  return respond(200, body, {
    "Content-Type": "application/octet-stream",
    "Content-Length": String(object.size),
    "Content-Disposition": `attachment; filename="${sha}.nupkg"`,
    "Cache-Control": IMMUTABLE,
    ETag: `"${sha}"`,
  });
}

async function serve(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  const release = url.protocol === "https:" ? RELEASE_PATH.exec(url.pathname) : null;
  if (release) return serveRelease(request, env, release[1]!);
  const pack = url.protocol === "https:" ? PACK_PATH.exec(url.pathname) : null;
  const upload = url.protocol === "https:" && !pack ? UPLOAD_PATH.exec(url.pathname) : null;
  const match = pack ?? upload;
  if (!match) return textError(404, "Not found\n", NOT_FOUND_CACHE);
  if (request.method !== "GET" && request.method !== "HEAD") return textError(405, "Method not allowed\n", "no-store", { Allow: "GET, HEAD" });
  const [, name, extension] = match;
  const key = `${name}.${extension}`;
  const bucket = pack ? env.PACKS : env.UPLOADS;
  if (upload && !(await env.DB.prepare("SELECT 1 FROM uploads WHERE object_key = ?").bind(key).first())) return textError(404, "Not found\n", NOT_FOUND_CACHE);
  const object: R2Object | R2ObjectBody | null = request.method === "HEAD" ? await bucket.head(key) : await bucket.get(key);
  if (!object) return textError(404, "Not found\n", NOT_FOUND_CACHE);
  const headers: Record<string, string> = {
    "Content-Type": extension === "png" ? "image/png" : "image/jpeg",
    "Content-Length": String(object.size),
  };
  if (pack) {
    headers["Cache-Control"] = IMMUTABLE;
    headers["ETag"] = `"${name}"`;
  } else {
    headers["Cache-Control"] = UPLOAD_CACHE;
    headers["X-Robots-Tag"] = "noindex, nofollow";
  }
  const body = "body" in object ? (object as R2ObjectBody).body : null;
  return respond(200, body, headers);
}

/** Serves one request on the content host. A failure is logged by error type only and answered 500. */
export async function handleCdn(request: Request, env: Env): Promise<Response> {
  try {
    return await serve(request, env);
  } catch (error) {
    console.error(JSON.stringify({ event: "cdn_error", type: error instanceof Error ? error.name : typeof error }));
    return textError(500, "Internal error\n", "no-store");
  }
}
