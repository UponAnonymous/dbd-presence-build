// Cloudflare Access JWT check for the admin API (FR-ADM-02, ADR-0014). On every admin call the Worker verifies
// `Cf-Access-Jwt-Assertion`: RS256 signature against the team's key set, `aud`, `iss`, `exp` (and `nbf`/`iat` when
// present), then the owner email allow-list. The key set is fetched from the team domain and cached; a failed fetch
// is remembered for FAILURE_TTL_MS so an outage or a flood cannot make every admin call fetch again.
// Emails are compared ASCII-only on both sides: an address with any non-ASCII character never matches, and case is
// folded for A-Z only, so a Unicode look-alike (KELVIN SIGN lower-cases to "k") cannot pass for an owner's address.

import type { FetchLike } from "./discord.ts";
import { HttpError } from "../http.ts";
import { decodeBase64Url } from "../lib/base64url.ts";
import { utf8 } from "../lib/crypto.ts";

const RS256 = { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" } as const;
const KEYS_TTL_MS = 10 * 60 * 1000;
const MIN_REFETCH_MS = 60 * 1000;
export const FAILURE_TTL_MS = 30 * 1000;
const CLOCK_SKEW_SECONDS = 60;
const MAX_JWT_LENGTH = 8192;
const MAX_JWKS_BYTES = 64 * 1024;
const TEAM_DOMAIN = /^(?:https:\/\/)?([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)\.cloudflareaccess\.com\/?$/;

interface CachedKeys {
  issuer: string;
  keys: Map<string, CryptoKey>;
  fetchedAt: number;
}

/** Per-isolate cache of the team key set, and of the last failure to fetch it. */
export class AccessKeyCache {
  private entry: CachedKeys | null = null;
  private failure: { issuer: string; at: number } | null = null;

  get(issuer: string): CachedKeys | null {
    return this.entry?.issuer === issuer ? this.entry : null;
  }

  set(entry: CachedKeys): void {
    this.entry = entry;
    this.failure = null;
  }

  /** True while a fetch for `issuer` failed less than FAILURE_TTL_MS ago. */
  failedRecently(issuer: string, now: number): boolean {
    return this.failure !== null && this.failure.issuer === issuer && now >= this.failure.at && now - this.failure.at < FAILURE_TTL_MS;
  }

  setFailure(issuer: string, at: number): void {
    this.failure = { issuer, at };
  }
}

export interface AccessIdentity {
  email: string;
}

function unauthenticated(): never {
  throw new HttpError(401, "unauthenticated");
}

function notConfigured(): never {
  throw new HttpError(503, "not_configured");
}

/** `https://<team>.cloudflareaccess.com` from the ACCESS_TEAM_DOMAIN secret, or null when it is not a team domain. */
export function teamIssuer(teamDomain: string | undefined): string | null {
  const match = teamDomain ? TEAM_DOMAIN.exec(teamDomain.trim().toLowerCase()) : null;
  return match ? `https://${match[1]}.cloudflareaccess.com` : null;
}

const ASCII_EMAIL = /^[\x21-\x7e]+@[\x21-\x7e]+$/;

/** Lower-cases A-Z only; null unless `email` is printable ASCII with one `@`. */
export function asciiEmail(email: string): string | null {
  if (!ASCII_EMAIL.test(email) || email.indexOf("@") !== email.lastIndexOf("@")) return null;
  return email.replace(/[A-Z]/g, (c) => String.fromCharCode(c.charCodeAt(0) + 32));
}

/** The OWNER_EMAILS allow-list; null when any entry is not a plain ASCII address (the admin API is then not configured). */
export function ownerEmails(value: string | undefined): Set<string> | null {
  const entries = (value ?? "")
    .split(",")
    .map((e) => e.trim())
    .filter((e) => e.length > 0);
  const owners = new Set<string>();
  for (const entry of entries) {
    const email = asciiEmail(entry);
    if (email === null) return null;
    owners.add(email);
  }
  return owners;
}

async function fetchKeys(fetchFn: FetchLike, issuer: string, now: number): Promise<CachedKeys> {
  let response: Response;
  try {
    response = await fetchFn(`${issuer}/cdn-cgi/access/certs`, { redirect: "manual", signal: AbortSignal.timeout(5000), headers: { Accept: "application/json" } });
  } catch (error) {
    if (error instanceof Error && (error.name === "TimeoutError" || error.name === "AbortError" || error.name === "TypeError")) {
      throw new HttpError(503, "access_keys_unavailable");
    }
    throw error;
  }
  if (!response.ok) {
    await response.body?.cancel();
    throw new HttpError(503, "access_keys_unavailable");
  }
  const text = await response.text();
  let body: unknown;
  try {
    body = text.length <= MAX_JWKS_BYTES ? (JSON.parse(text) as unknown) : null;
  } catch (error) {
    if (error instanceof SyntaxError) throw new HttpError(503, "access_keys_unavailable");
    throw error;
  }
  const list = (body as { keys?: unknown } | null)?.keys;
  if (!Array.isArray(list)) throw new HttpError(503, "access_keys_unavailable");
  const keys = new Map<string, CryptoKey>();
  for (const jwk of list.slice(0, 16) as Record<string, unknown>[]) {
    if (jwk["kty"] !== "RSA" || typeof jwk["kid"] !== "string" || typeof jwk["n"] !== "string" || typeof jwk["e"] !== "string") continue;
    if (jwk["alg"] !== undefined && jwk["alg"] !== "RS256") continue;
    try {
      keys.set(jwk["kid"], await crypto.subtle.importKey("jwk", { kty: "RSA", n: jwk["n"], e: jwk["e"], alg: "RS256", ext: true }, RS256, false, ["verify"]));
    } catch (error) {
      // A key that does not import is left out of the set, so a token naming it is refused as unauthenticated.
      if (!(error instanceof Error && error.name === "DataError")) throw error;
    }
  }
  return { issuer, keys, fetchedAt: now };
}

async function keyFor(fetchFn: FetchLike, cache: AccessKeyCache, issuer: string, kid: string, now: number): Promise<CryptoKey | undefined> {
  let entry = cache.get(issuer);
  const fresh = entry !== null && now - entry.fetchedAt <= KEYS_TTL_MS;
  if (!entry || !fresh || (!entry.keys.has(kid) && now - entry.fetchedAt > MIN_REFETCH_MS)) {
    if (cache.failedRecently(issuer, now)) {
      // Do not fetch again yet. A still-fresh key set keeps serving (the refetch was only for an unknown kid).
      if (entry && fresh) return entry.keys.get(kid);
      throw new HttpError(503, "access_keys_unavailable");
    }
    try {
      entry = await fetchKeys(fetchFn, issuer, now);
    } catch (error) {
      if (error instanceof HttpError) cache.setFailure(issuer, now);
      throw error;
    }
    cache.set(entry);
  }
  return entry.keys.get(kid);
}

function decodeJsonPart(part: string): Record<string, unknown> | null {
  const bytes = decodeBase64Url(part);
  if (!bytes) return null;
  try {
    const value = JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes)) as unknown;
    return typeof value === "object" && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch (error) {
    if (error instanceof SyntaxError || error instanceof TypeError) return null;
    throw error;
  }
}

export interface AccessConfig {
  audience: string | undefined;
  teamDomain: string | undefined;
  owners: string | undefined;
}

/** Verifies the Access assertion on `request` and returns the owner's email, or throws 401/403/503. */
export async function verifyAccess(request: Request, config: AccessConfig, fetchFn: FetchLike, cache: AccessKeyCache, now: number): Promise<AccessIdentity> {
  const issuer = teamIssuer(config.teamDomain);
  const owners = ownerEmails(config.owners);
  if (!issuer || !config.audience || owners === null || owners.size === 0) notConfigured();

  const assertion = request.headers.get("Cf-Access-Jwt-Assertion");
  if (!assertion || assertion.length > MAX_JWT_LENGTH) unauthenticated();
  const parts = assertion.split(".");
  if (parts.length !== 3) unauthenticated();
  const [headerPart, payloadPart, signaturePart] = parts as [string, string, string];
  const header = decodeJsonPart(headerPart);
  const payload = decodeJsonPart(payloadPart);
  const signature = decodeBase64Url(signaturePart);
  if (!header || !payload || !signature) unauthenticated();
  if (header["alg"] !== "RS256" || typeof header["kid"] !== "string") unauthenticated();

  const key = await keyFor(fetchFn, cache, issuer, header["kid"], now);
  if (!key) unauthenticated();
  if (!(await crypto.subtle.verify(RS256, key, signature, utf8(`${headerPart}.${payloadPart}`)))) unauthenticated();

  const nowSeconds = Math.floor(now / 1000);
  const aud = payload["aud"];
  const audiences = Array.isArray(aud) ? aud : [aud];
  if (!audiences.includes(config.audience)) unauthenticated();
  if (payload["iss"] !== issuer) unauthenticated();
  const exp = payload["exp"];
  if (typeof exp !== "number" || exp <= nowSeconds) unauthenticated();
  const nbf = payload["nbf"];
  if (nbf !== undefined && (typeof nbf !== "number" || nbf > nowSeconds + CLOCK_SKEW_SECONDS)) unauthenticated();
  const iat = payload["iat"];
  if (iat !== undefined && (typeof iat !== "number" || iat > nowSeconds + CLOCK_SKEW_SECONDS)) unauthenticated();

  const email = typeof payload["email"] === "string" ? asciiEmail(payload["email"]) : null;
  if (email === null || !owners.has(email)) throw new HttpError(403, "forbidden");
  return { email };
}
