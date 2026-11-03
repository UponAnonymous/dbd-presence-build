// GET/HEAD /privacy and /terms on the API host: the public privacy notice and terms of use (Microsoft Store listing).
// Static: the bodies are generated from docs/legal/*.md into src/legal-pages.ts (scripts/build-legal.mjs) with every
// character escaped. No authentication, no database, no rate-limit storage, no script and no external resource.

import { HttpError } from "../http.ts";
import { PRIVACY_PAGE, TERMS_PAGE, type LegalPage } from "../legal-pages.ts";

export const LEGAL_PATHS: Readonly<Record<string, LegalPage>> = { "/privacy": PRIVACY_PAGE, "/terms": TERMS_PAGE };

/** Strict CSP for these pages: only inline CSS is allowed; nothing is loaded and nothing can run. */
export const LEGAL_CSP = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

const CSS =
  "body{font:16px/1.6 system-ui,-apple-system,'Segoe UI',sans-serif;max-width:46rem;margin:0 auto;padding:1.5rem 1rem 3rem;color:#1b1b1f;background:#fff}" +
  "h1{font-size:1.75rem;line-height:1.25}h2{font-size:1.25rem;margin-top:2rem}li{margin:.25rem 0}code{background:#f0f0f3;padding:0 .25em;border-radius:3px}" +
  "a{color:#0b57d0}nav{font-size:.9rem;margin-bottom:1.5rem}footer{margin-top:3rem;font-size:.9rem;color:#555}" +
  "@media(prefers-color-scheme:dark){body{color:#e8e8ec;background:#16161a}code{background:#2a2a30}a{color:#8ab4f8}footer{color:#aaa}}";

const DISCLAIMER = "DBD Presence is an unofficial, fan-made app. It is not affiliated with or endorsed by Behaviour Interactive or Discord.";

/** The full page. Title and body are already escaped by the generator; the rest is constant. */
export function renderLegalPage(page: LegalPage): string {
  return (
    `<!doctype html>\n<html lang="en-GB"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">` +
    `<meta name="robots" content="index,follow"><title>${page.title}</title><style>${CSS}</style></head><body>` +
    `<nav><a href="/privacy">Privacy notice</a> · <a href="/terms">Terms of use</a></nav>` +
    `<main><h1>${page.title}</h1>\n${page.body}</main><footer>${DISCLAIMER}</footer></body></html>\n`
  );
}

/** Builds the response for a legal path, or null if the path is not one. */
export function legalResponse(method: string, path: string): Response | null {
  const page = LEGAL_PATHS[path];
  if (page === undefined || !Object.hasOwn(LEGAL_PATHS, path)) return null;
  if (method !== "GET" && method !== "HEAD") throw new HttpError(405, "method_not_allowed", { Allow: "GET, HEAD" });
  const html = renderLegalPage(page);
  return new Response(method === "HEAD" ? null : html, {
    status: 200,
    headers: {
      "Content-Type": "text/html; charset=utf-8",
      "Content-Security-Policy": LEGAL_CSP,
      "Cache-Control": "public, max-age=3600",
      "Content-Length": String(new TextEncoder().encode(html).byteLength),
    },
  });
}
