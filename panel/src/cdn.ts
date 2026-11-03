// Image addresses on the owner's content host (ADR-0014: the panel's CSP allows images only from 'self' and the cdn
// host of its own environment). The panel loads an image only when its address is exactly a pack file on that host,
// /packs/<64 hex>.(png|jpg), or a user upload, /u/<32 hex>.(png|jpg); any other address, including one the service
// returned, is never loaded.

import type { Environment } from "./env.ts";

export const CDN_ORIGIN: Readonly<Record<Environment, string>> = {
  production: "https://cdn.arcavale.com",
  staging: "https://cdn-staging.arcavale.com",
};

const PACK_FILE = /^\/packs\/[0-9a-f]{64}\.(png|jpg)$/;

/** True when `url` is a pack file on this environment's content host, with no query, fragment or credentials. */
export function isPackImageUrl(url: string, environment: Environment): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch (error) {
    if (error instanceof TypeError) return false;
    throw error;
  }
  return parsed.origin === CDN_ORIGIN[environment] && url === `${parsed.origin}${parsed.pathname}` && PACK_FILE.test(parsed.pathname);
}

const UPLOAD_FILE = /^\/u\/[0-9a-f]{32}\.(png|jpg)$/;

/** True when `url` is a user upload on this environment's content host, with no query, fragment or credentials. */
export function isUploadImageUrl(url: string, environment: Environment): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch (error) {
    if (error instanceof TypeError) return false;
    throw error;
  }
  return parsed.origin === CDN_ORIGIN[environment] && url === `${parsed.origin}${parsed.pathname}` && UPLOAD_FILE.test(parsed.pathname);
}
