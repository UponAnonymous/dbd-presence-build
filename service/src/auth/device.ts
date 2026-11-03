// Device keys and refresh proofs (FR-ONL-03, ADR-0011).
//
// Contract for the desktop client:
// - The device key is ECDSA P-256. Sign-in sends the public key as a JWK `{"kty":"EC","crv":"P-256","x","y"}`
//   (x and y: canonical unpadded base64url of 32 bytes, unused low bits zero; no other members, never `d`).
//   A non-canonical spelling is refused (400 invalid_device_key), so one key can never register as several devices.
// - The device thumbprint (`cnf` in the licence) is the RFC 7638 JWK SHA-256 thumbprint, base64url, computed over the
//   canonical JWK rebuilt from the decoded coordinate bytes.
// - Every refresh carries `proof: {timestamp, nonce, signature}`:
//     timestamp  Unix time in whole seconds, within ±5 minutes of the server clock
//     nonce      16 to 64 base64url characters, never reused (the server refuses a nonce it has seen)
//     signature  unpadded base64url of the 64-byte IEEE P1363 r||s ECDSA P-256 SHA-256 signature over the UTF-8 bytes of
//                "dbdp-refresh-proof-v1\n" + base64url(SHA-256(refreshToken)) + "\n" + timestamp + "\n" + nonce

import { HttpError } from "../http.ts";
import { decodeBase64Url, encodeBase64Url } from "../lib/base64url.ts";
import { sha256Base64Url, utf8 } from "../lib/crypto.ts";
import { object, string } from "../lib/validate.ts";

const ECDSA_P256 = { name: "ECDSA", namedCurve: "P-256" } as const;
const ES256 = { name: "ECDSA", hash: "SHA-256" } as const;

export const PROOF_MAX_SKEW_SECONDS = 5 * 60;
export const NONCE_PATTERN = /^[A-Za-z0-9_-]{16,64}$/;
const COORDINATE = /^[A-Za-z0-9_-]{43}$/;

export interface DevicePublicKey {
  /** Canonical JWK text stored in `devices.public_key_jwk`. */
  jwkJson: string;
  thumbprint: string;
}

function badKey(): never {
  throw new HttpError(400, "invalid_device_key");
}

/** Validates and imports a device public key; rejects private keys, other curves and points not on P-256. */
export async function parseDevicePublicKey(value: unknown): Promise<DevicePublicKey> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) badKey();
  const jwk = object(value, ["kty", "crv", "x", "y"]);
  if (jwk["kty"] !== "EC" || jwk["crv"] !== "P-256") badKey();
  const x = string(jwk["x"], { max: 43, pattern: COORDINATE });
  const y = string(jwk["y"], { max: 43, pattern: COORDINATE });
  const xBytes = decodeBase64Url(x);
  const yBytes = decodeBase64Url(y);
  if (!xBytes || xBytes.length !== 32 || !yBytes || yBytes.length !== 32) badKey();
  // RFC 7638: required members only, in lexicographic order, no whitespace; the coordinates re-encoded from their bytes,
  // so the stored key and its thumbprint never depend on how the client spelled them.
  const canonical = `{"crv":"P-256","kty":"EC","x":"${encodeBase64Url(xBytes)}","y":"${encodeBase64Url(yBytes)}"}`;
  try {
    await importDeviceKey(canonical);
  } catch (error) {
    if (error instanceof Error && (error.name === "DataError" || error.name === "OperationError")) badKey();
    throw error;
  }
  return { jwkJson: canonical, thumbprint: await sha256Base64Url(canonical) };
}

export function importDeviceKey(jwkJson: string): Promise<CryptoKey> {
  const jwk = JSON.parse(jwkJson) as JsonWebKey;
  return crypto.subtle.importKey("jwk", { kty: jwk.kty, crv: jwk.crv, x: jwk.x, y: jwk.y, ext: true }, ECDSA_P256, false, ["verify"]);
}

export function proofMessage(refreshTokenHash: string, timestamp: number, nonce: string): Uint8Array {
  return utf8(`dbdp-refresh-proof-v1\n${refreshTokenHash}\n${timestamp}\n${nonce}`);
}

/** Verifies a proof signature. Malformed signatures are simply invalid. */
export async function verifyProofSignature(key: CryptoKey, message: Uint8Array, signature: string): Promise<boolean> {
  const bytes = decodeBase64Url(signature);
  if (!bytes || bytes.length !== 64) return false;
  return crypto.subtle.verify(ES256, key, bytes, message);
}
