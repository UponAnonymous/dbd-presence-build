// Licences (FR-ONL-04, ADR-0011): a signed document of purpose `licence`, issued on sign-in, redemption and every
// refresh, bound to the device key thumbprint. A revoked user receives the same document with `status: "revoked"` and
// no features: the app stops presence only when it has *received* such a signed revocation (D8, FR-ONL-05).

import type { Env } from "../env.ts";
import { MAX_LIFETIME_MS, signDocument } from "../signing/document.ts";
import { loadSigningKey } from "../signing/keys.ts";

export const LICENCE_CHANNEL = "stable";
/** Features granted to every active user today; the list leaves room for paid features later (D1). */
export const ACTIVE_FEATURES: readonly string[] = ["online"];

export type LicenceStatus = "active" | "revoked";

export interface LicencePayload {
  schemaVersion: 1;
  sub: string;
  cnf: string;
  status: LicenceStatus;
  features: string[];
  iat: string;
  nbf: string;
  exp: string;
  serverTime: string;
}

export async function issueLicence(env: Env, now: number, userId: string, thumbprint: string, status: LicenceStatus): Promise<string> {
  const key = await loadSigningKey(env.LICENCE_SIGNING_KEY);
  const issuedAt = new Date(now);
  const expiresAt = new Date(now + MAX_LIFETIME_MS.licence);
  const payload: LicencePayload = {
    schemaVersion: 1,
    sub: userId,
    cnf: thumbprint,
    status,
    features: status === "active" ? [...ACTIVE_FEATURES] : [],
    iat: issuedAt.toISOString(),
    nbf: issuedAt.toISOString(),
    exp: expiresAt.toISOString(),
    serverTime: issuedAt.toISOString(),
  };
  // The signing time in whole seconds. The app requires exactly this (version == floor(issuedAt / 1000), TM-01), so a licence version
  // is never chosen: it stays monotonic for rollback protection and cannot be used to park a version floor out of reach.
  const version = Math.floor(issuedAt.getTime() / 1000);
  return signDocument("licence", LICENCE_CHANNEL, version, payload, key.key, key.kid, { issuedAt, expiresAt });
}
