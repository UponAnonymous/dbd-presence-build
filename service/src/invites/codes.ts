// Invite codes (FR-ONL-02, PLAN_V2 §3.2): 16 characters from Crockford's base-32 alphabet (no I, L, O or U), so 80 bits
// from the CSPRNG, shown as XXXX-XXXX-XXXX-XXXX. Only an HMAC-SHA-256 under a key id is stored.

import { hmacSha256Hex, randomBytes } from "../lib/crypto.ts";
import type { HmacKeyRing } from "../signing/keys.ts";

export const CODE_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
export const CODE_LENGTH = 16;

/** A fresh code in display form. 10 random bytes = 80 bits = 16 five-bit symbols. */
export function generateInviteCode(): string {
  const bytes = randomBytes(10);
  let bits = 0;
  let buffer = 0;
  let symbols = "";
  for (const byte of bytes) {
    buffer = (buffer << 8) | byte;
    bits += 8;
    while (bits >= 5) {
      bits -= 5;
      symbols += CODE_ALPHABET[(buffer >> bits) & 31]!;
    }
    buffer &= (1 << bits) - 1;
  }
  return formatInviteCode(symbols);
}

export function formatInviteCode(normalized: string): string {
  return normalized.match(/.{4}/g)!.join("-");
}

/**
 * Normalises user input: upper case, separators removed, and the look-alikes Crockford maps (O to 0, I and L to 1).
 * Returns null unless exactly 16 alphabet characters remain.
 */
export function normalizeInviteCode(input: string): string | null {
  const cleaned = input.toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
  if (cleaned.length !== CODE_LENGTH) return null;
  for (const c of cleaned) if (!CODE_ALPHABET.includes(c)) return null;
  return cleaned;
}

export function inviteCodeHash(key: Uint8Array, normalized: string): Promise<string> {
  return hmacSha256Hex(key, `dbdp-invite-v1|${normalized}`);
}

/** The code's hash under every key in the ring (old keys keep their codes redeemable until they expire). */
export async function candidateHashes(ring: HmacKeyRing, normalized: string): Promise<{ kid: string; hash: string }[]> {
  const out: { kid: string; hash: string }[] = [];
  for (const [kid, key] of ring.keys) out.push({ kid, hash: await inviteCodeHash(key, normalized) });
  return out;
}
