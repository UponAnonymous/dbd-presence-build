// Bans that survive account deletion (ADR-0011, PLAN_V2 §4.3). Deleting a revoked account keeps only a keyed hash of
// its Discord user id in `banned_subjects`; sign-in checks it, so a revoked person cannot come back by having the
// account deleted or by waiting for the 12-month purge.
//
// The hash key is BAN_HMAC_KEY, a secret of its own that never rotates: if it were replaced, every recorded ban would
// silently stop matching. It is deliberately not part of the INVITE_HMAC_KEYS ring, which does rotate.

import { HttpError } from "./http.ts";
import { decodeBase64Url } from "./lib/base64url.ts";
import { hmacSha256Hex } from "./lib/crypto.ts";

/** BAN_HMAC_KEY: canonical base64url of at least 32 random bytes. A missing or unusable key is 503 not_configured. */
export function loadBanKey(secret: string | undefined): Uint8Array {
  const bytes = secret ? decodeBase64Url(secret.trim()) : null;
  if (!bytes || bytes.length < 32) throw new HttpError(503, "not_configured");
  return bytes;
}

export function banHash(key: Uint8Array, discordId: string): Promise<string> {
  return hmacSha256Hex(key, `dbdp-ban-v1|${discordId}`);
}

/** True when this Discord user id was banned. */
export async function isBanned(db: D1Database, key: Uint8Array, discordId: string): Promise<boolean> {
  const row = await db.prepare("SELECT 1 AS banned FROM banned_subjects WHERE subject_hash = ?").bind(await banHash(key, discordId)).first<{ banned: number }>();
  return row !== null;
}
