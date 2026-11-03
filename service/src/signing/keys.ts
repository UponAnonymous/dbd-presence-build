// Parsing of key secrets. A missing or unusable secret makes the dependent route answer 503 `not_configured`;
// the secret's value is never echoed or logged.

import { HttpError } from "../http.ts";
import { decodeBase64Url } from "../lib/base64url.ts";
import { importSigningKey } from "./document.ts";

export interface SigningKey {
  kid: string;
  key: CryptoKey;
}

const signingKeyCache = new Map<string, Promise<SigningKey>>();

function notConfigured(): never {
  throw new HttpError(503, "not_configured");
}

function parseJsonSecret(secret: string | undefined): Record<string, unknown> {
  if (!secret) notConfigured();
  let value: unknown;
  try {
    value = JSON.parse(secret);
  } catch (error) {
    if (error instanceof SyntaxError) notConfigured();
    throw error;
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) notConfigured();
  return value as Record<string, unknown>;
}

/** Loads a signing-key secret of the form `{"kid": "...", "pkcs8": base64url(PKCS#8 DER)}`. */
export function loadSigningKey(secret: string | undefined): Promise<SigningKey> {
  const cached = secret ? signingKeyCache.get(secret) : undefined;
  if (cached) return cached;
  const parsed = parseJsonSecret(secret);
  const kid = parsed["kid"];
  const pkcs8 = typeof parsed["pkcs8"] === "string" ? decodeBase64Url(parsed["pkcs8"]) : null;
  if (typeof kid !== "string" || kid.length === 0 || kid.length > 128 || !pkcs8) notConfigured();
  const loading = importSigningKey(pkcs8).then(
    (key) => ({ kid, key }),
    (error: unknown) => {
      signingKeyCache.delete(secret!);
      if (error instanceof Error && error.name === "DataError") notConfigured();
      throw error;
    },
  );
  signingKeyCache.set(secret!, loading);
  return loading;
}

export interface HmacKeyRing {
  currentKid: string;
  keys: ReadonlyMap<string, Uint8Array>;
}

/** Parses `INVITE_HMAC_KEYS`: `{"current": kid, "keys": {kid: base64url(>= 32 bytes)}}`. Old kids stay for rotation. */
export function loadHmacKeyRing(secret: string | undefined): HmacKeyRing {
  const parsed = parseJsonSecret(secret);
  const current = parsed["current"];
  const rawKeys = parsed["keys"];
  if (typeof current !== "string" || typeof rawKeys !== "object" || rawKeys === null || Array.isArray(rawKeys)) notConfigured();
  const keys = new Map<string, Uint8Array>();
  for (const [kid, value] of Object.entries(rawKeys as Record<string, unknown>)) {
    const bytes = typeof value === "string" ? decodeBase64Url(value) : null;
    if (!/^[A-Za-z0-9._-]{1,32}$/.test(kid) || !bytes || bytes.length < 32) notConfigured();
    keys.set(kid, bytes);
  }
  if (!keys.has(current)) notConfigured();
  return { currentKid: current, keys };
}
