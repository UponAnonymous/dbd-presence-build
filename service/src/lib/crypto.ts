// Small WebCrypto helpers. No third-party crypto.

import { encodeBase64Url } from "./base64url.ts";

const encoder = new TextEncoder();

export function utf8(text: string): Uint8Array {
  return encoder.encode(text);
}

export function toHex(bytes: Uint8Array): string {
  let out = "";
  for (const b of bytes) out += b.toString(16).padStart(2, "0");
  return out;
}

export async function sha256(data: Uint8Array | string): Promise<Uint8Array> {
  const bytes = typeof data === "string" ? utf8(data) : data;
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
}

export async function sha256Hex(data: Uint8Array | string): Promise<string> {
  return toHex(await sha256(data));
}

export async function sha256Base64Url(data: Uint8Array | string): Promise<string> {
  return encodeBase64Url(await sha256(data));
}

export async function hmacSha256(key: Uint8Array, message: string): Promise<Uint8Array> {
  const cryptoKey = await crypto.subtle.importKey("raw", key, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return new Uint8Array(await crypto.subtle.sign("HMAC", cryptoKey, utf8(message)));
}

export async function hmacSha256Hex(key: Uint8Array, message: string): Promise<string> {
  return toHex(await hmacSha256(key, message));
}

export function randomBytes(length: number): Uint8Array {
  const bytes = new Uint8Array(length);
  crypto.getRandomValues(bytes);
  return bytes;
}

/** An opaque bearer secret: 256 random bits, base64url, with a short type prefix. */
export function newOpaqueToken(prefix: string): string {
  return `${prefix}_${encodeBase64Url(randomBytes(32))}`;
}

export function newId(): string {
  return crypto.randomUUID();
}
