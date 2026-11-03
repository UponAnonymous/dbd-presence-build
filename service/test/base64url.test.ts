// Canonical base64url only (security finding 1: device-key aliasing). Every place the service accepts base64url uses
// decodeBase64Url, so these rules hold for device keys, proofs, Access JWTs, key secrets and signed documents.

import { describe, expect, it } from "vitest";
import { decodeBase64Url, encodeBase64Url, isBase64UrlOfLength } from "../src/lib/base64url.ts";
import { respell } from "./helpers.ts";

describe("decodeBase64Url", () => {
  it("round-trips every length", () => {
    for (let n = 0; n < 70; n++) {
      const bytes = crypto.getRandomValues(new Uint8Array(n));
      const text = encodeBase64Url(bytes);
      expect(decodeBase64Url(text)).toEqual(bytes);
    }
  });

  it("refuses a value whose unused low bits are not zero", () => {
    const one = encodeBase64Url(new Uint8Array([0xff]));
    const two = encodeBase64Url(new Uint8Array([0xff, 0xee]));
    expect(decodeBase64Url(one)).not.toBeNull();
    expect(decodeBase64Url(respell(one))).toBeNull();
    expect(decodeBase64Url(two)).not.toBeNull();
    expect(decodeBase64Url(respell(two))).toBeNull();
    expect(decodeBase64Url("AB")).toBeNull();
    expect(decodeBase64Url("AAB")).toBeNull();
  });

  it("refuses padding, whitespace, the standard alphabet and impossible lengths", () => {
    for (const bad of ["AA==", "AAA=", "AA=", "QUJD\n", " QUJD", "ab+/", "A", "AAAAA", "QUJ%"]) expect(decodeBase64Url(bad)).toBeNull();
  });

  it("accepts a value exactly when re-encoding its bytes gives it back", () => {
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    const canonical = encodeBase64Url(bytes);
    let accepted = 0;
    for (const c of "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") {
      const candidate = canonical.slice(0, -1) + c;
      const decoded = decodeBase64Url(candidate);
      if (decoded === null) continue;
      accepted++;
      expect(encodeBase64Url(decoded)).toBe(candidate);
    }
    // 43 characters carry 258 bits for 256: the last character has 2 spare bits, so 16 of 64 endings are canonical.
    expect(accepted).toBe(16);
    expect(isBase64UrlOfLength(canonical, 32)).toBe(true);
    expect(isBase64UrlOfLength(respell(canonical), 32)).toBe(false);
  });
});
