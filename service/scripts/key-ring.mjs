// Merging an INVITE_HMAC_KEYS key ring for rotation (scripts/new-secret.mjs rotate-invite-keys). No Node APIs, so the
// Vitest suite checks that the merged ring is what the Worker accepts (src/signing/keys.ts loadHmacKeyRing).
//
// Format: {"current": "<kid>", "keys": {"<old kid>": "<base64url key>", ..., "<new kid>": "<base64url key>"}}.
// Rotation adds a key and makes it current; every old key stays, so codes made under it remain redeemable until they
// expire. Remove an old kid only after its last code has expired (30 days by default, 90 at most).

const KID = /^[A-Za-z0-9._-]{1,32}$/;
const KEY = /^[A-Za-z0-9_-]{43,}$/;

/** Returns the merged ring as JSON text, or throws an Error saying what is wrong with the input. */
export function mergeInviteKeyRing(oldRingText, newKid, newKeyBase64Url) {
  let ring;
  try {
    ring = JSON.parse(oldRingText);
  } catch {
    throw new Error("the old key ring is not JSON");
  }
  if (typeof ring !== "object" || ring === null || typeof ring.current !== "string" || typeof ring.keys !== "object" || ring.keys === null) {
    throw new Error('the old key ring must look like {"current": kid, "keys": {kid: key}}');
  }
  for (const [kid, key] of Object.entries(ring.keys)) {
    if (!KID.test(kid) || typeof key !== "string" || !KEY.test(key)) throw new Error(`the old key ring has an unusable entry "${kid}"`);
  }
  if (!(ring.current in ring.keys)) throw new Error("the old key ring's current kid has no key");
  if (!KID.test(newKid)) throw new Error("the new kid must be 1 to 32 of A-Z a-z 0-9 . _ -");
  if (newKid in ring.keys) throw new Error(`kid "${newKid}" is already in the ring; choose a new one`);
  if (!KEY.test(newKeyBase64Url)) throw new Error("the new key must be base64url of at least 32 bytes");
  return JSON.stringify({ current: newKid, keys: { ...ring.keys, [newKid]: newKeyBase64Url } });
}
