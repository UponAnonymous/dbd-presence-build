// Base64url (RFC 4648 §5) without padding, as used by the signed-document format (ADR-0012).

const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
const LOOKUP = new Int16Array(128).fill(-1);
for (let i = 0; i < ALPHABET.length; i++) {
  LOOKUP[ALPHABET.charCodeAt(i)] = i;
}

/** Encodes bytes as base64url with no padding. */
export function encodeBase64Url(bytes: Uint8Array): string {
  let out = "";
  let i = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8) | bytes[i + 2]!;
    out += ALPHABET[(n >> 18) & 63]! + ALPHABET[(n >> 12) & 63]! + ALPHABET[(n >> 6) & 63]! + ALPHABET[n & 63]!;
  }
  const rest = bytes.length - i;
  if (rest === 1) {
    const n = bytes[i]! << 16;
    out += ALPHABET[(n >> 18) & 63]! + ALPHABET[(n >> 12) & 63]!;
  } else if (rest === 2) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8);
    out += ALPHABET[(n >> 18) & 63]! + ALPHABET[(n >> 12) & 63]! + ALPHABET[(n >> 6) & 63]!;
  }
  return out;
}

/**
 * Decodes canonical base64url: unpadded, alphabet `A-Z a-z 0-9 - _` only, and the unused low bits of the last character
 * zero, so that `encodeBase64Url(decodeBase64Url(s)) === s`. Anything else (padding, whitespace, `+`, `/`, impossible
 * lengths, non-zero spare bits) returns null instead of throwing.
 *
 * Canonical-only matters wherever the text itself is hashed, compared or stored (device keys, thumbprints): with
 * non-zero spare bits accepted, one key would have up to 16 spellings per coordinate.
 */
export function decodeBase64Url(text: string): Uint8Array | null {
  if (text.length % 4 === 1) return null;
  const out = new Uint8Array(Math.floor((text.length * 3) / 4));
  let buffer = 0;
  let bits = 0;
  let o = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    const value = code < 128 ? LOOKUP[code]! : -1;
    if (value < 0) return null;
    buffer = ((buffer << 6) | value) & 0xffffff;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[o++] = (buffer >> bits) & 0xff;
    }
  }
  // The bits left over after the last whole byte must be zero (RFC 4648 §3.5, canonical encoding).
  if ((buffer & ((1 << bits) - 1)) !== 0) return null;
  return out;
}

/** True when `text` is canonical unpadded base64url of exactly `byteLength` bytes. */
export function isBase64UrlOfLength(text: string, byteLength: number): boolean {
  const decoded = decodeBase64Url(text);
  return decoded !== null && decoded.length === byteLength;
}
