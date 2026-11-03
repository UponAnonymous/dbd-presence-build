// Owner guide §8 (rotating INVITE_HMAC_KEYS without losing the old key): the merged ring is what the Worker loads.
// Also the TrustedServiceKeys.json line that new-secret.mjs prints for a signing key (ADR-0012 rule 2a).

import { describe, expect, it } from "vitest";
import { mergeInviteKeyRing } from "../scripts/key-ring.mjs";
import { listForKid, purposeForKid, trustedKeyEntry } from "../scripts/trusted-key-entry.mjs";
import { decodeBase64Url, encodeBase64Url } from "../src/lib/base64url.ts";
import { randomBytes, toHex } from "../src/lib/crypto.ts";
import { loadHmacKeyRing } from "../src/signing/keys.ts";

const key = () => encodeBase64Url(randomBytes(32));

describe("rotate-invite-keys", () => {
  it("adds the new key as current and keeps every old key", () => {
    const old = JSON.stringify({ current: "invite-prod-1", keys: { "invite-prod-1": key() } });
    const merged = mergeInviteKeyRing(old, "invite-prod-2", key());
    const ring = loadHmacKeyRing(merged);
    expect(ring.currentKid).toBe("invite-prod-2");
    expect([...ring.keys.keys()].sort()).toEqual(["invite-prod-1", "invite-prod-2"]);
    expect(encodeBase64Url(ring.keys.get("invite-prod-1")!)).toBe((JSON.parse(old) as { keys: Record<string, string> }).keys["invite-prod-1"]);
    expect(loadHmacKeyRing(mergeInviteKeyRing(merged, "invite-prod-3", key())).keys.size).toBe(3);
  });

  it("refuses a reused kid, a broken old ring and a short key", () => {
    const old = JSON.stringify({ current: "a", keys: { a: key() } });
    expect(() => mergeInviteKeyRing(old, "a", key())).toThrow(/already in the ring/);
    expect(() => mergeInviteKeyRing("not json", "b", key())).toThrow(/not JSON/);
    expect(() => mergeInviteKeyRing(JSON.stringify({ current: "x", keys: { a: key() } }), "b", key())).toThrow(/current kid/);
    expect(() => mergeInviteKeyRing(old, "b", "c2hvcnQ")).toThrow(/at least 32 bytes/);
  });
});

describe("signing-key: the line for TrustedServiceKeys.json", () => {
  it("names the one purpose each key may sign and the list it belongs in", () => {
    expect(purposeForKid("pack-index-prod-1")).toBe("pack-index");
    expect(purposeForKid("pack-index-staging-1")).toBe("pack-index");
    expect(purposeForKid("config-prod-1")).toBe("config");
    expect(purposeForKid("licence-staging-1")).toBe("licence");
    expect(purposeForKid("release-prod-1")).toBeNull();
    expect(purposeForKid("content-prod-1")).toBeNull();
    expect(purposeForKid("packindex-prod-1")).toBeNull();
    expect(listForKid("pack-index-prod-1")).toBe("production");
    expect(listForKid("pack-index-staging-1")).toBe("staging");
    expect(listForKid("pack-index-1")).toBeNull();
  });

  it("is a JSON object with kid, purposes and a P-256 SubjectPublicKeyInfo, as build/check-online-keys.py expects", async () => {
    const pair = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
    const spki = new Uint8Array((await crypto.subtle.exportKey("spki", pair.publicKey)) as ArrayBuffer);
    const line = trustedKeyEntry("pack-index-prod-1", "pack-index", encodeBase64Url(spki));
    const entry = JSON.parse(line) as { kid: string; purposes: string[]; spki: string };
    // The content key also signs the character roster (ADR-0012 addendum).
    expect(entry).toEqual({ kid: "pack-index-prod-1", purposes: ["pack-index", "roster"], spki: encodeBase64Url(spki) });
    expect((JSON.parse(trustedKeyEntry("config-prod-1", "config", encodeBase64Url(spki))) as { purposes: string[] }).purposes).toEqual(["config"]);
    const der = decodeBase64Url(entry.spki)!;
    expect(der.length).toBe(91);
    expect(toHex(der.slice(0, 23))).toBe("3059301306072a8648ce3d020106082a8648ce3d030107");
  });
});
