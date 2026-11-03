// Generates a secret value for `wrangler secret put` on the owner's own PC. The secret goes to standard output, so it
// can be piped straight into Cloudflare, and with --out also to a backup file that the owner keeps offline (an
// encrypted USB drive or a password manager's secure note), because Cloudflare never shows a secret again:
//
//   node scripts/new-secret.mjs signing-key licence-prod-1 --out E:\dbdp-keys\licence-prod-1.json | npx wrangler secret put LICENCE_SIGNING_KEY --env production
//   node scripts/new-secret.mjs signing-key pack-index-prod-1 --out E:\dbdp-keys\pack-index-prod-1.json | npx wrangler secret put CONTENT_SIGNING_KEY --env production
//   node scripts/new-secret.mjs invite-keys invite-prod-1  --out E:\dbdp-keys\invite-prod-1.json  | npx wrangler secret put INVITE_HMAC_KEYS --env production
//   node scripts/new-secret.mjs ban-key ban-prod-1         --out E:\dbdp-keys\ban-prod-1.txt      | npx wrangler secret put BAN_HMAC_KEY --env production
//
// Rotating INVITE_HMAC_KEYS without losing the old key (codes made under it stay redeemable): build the merged ring
// from the backup of the current one, then store it:
//
//   node scripts/new-secret.mjs rotate-invite-keys invite-prod-2 --from E:\dbdp-keys\invite-prod-1.json --out E:\dbdp-keys\invite-prod-2.json | npx wrangler secret put INVITE_HMAC_KEYS --env production
//
// For a signing key the matching PUBLIC key (not secret) is printed on standard error as the exact line to paste into
// src/DbdPresence.Online/TrustedServiceKeys.json, with the list ("staging" or "production") it belongs in and the one
// purpose it may sign, taken from the name: licence-*, config-* or pack-index-* (the CONTENT_SIGNING_KEY secret).
// Use a new kid for every key and every environment (for example licence-staging-1, config-prod-1, pack-index-prod-1).
// BAN_HMAC_KEY is made once per environment and never rotated: replacing it would lift every recorded ban.

import { webcrypto } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { mergeInviteKeyRing } from "./key-ring.mjs";
import { listForKid, publicKeyNotice, purposeForKid } from "./trusted-key-entry.mjs";

const KINDS = ["signing-key", "invite-keys", "ban-key", "rotate-invite-keys"];
const args = process.argv.slice(2);
const [kind, kid] = args;
const option = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const out = option("--out");
const from = option("--from");
if (!kind || !kid || !/^[A-Za-z0-9._-]{1,32}$/.test(kid) || !KINDS.includes(kind) || (kind === "rotate-invite-keys" && !from) || (args.includes("--out") && !out)) {
  console.error("usage: node scripts/new-secret.mjs signing-key|invite-keys|ban-key <kid> [--out <backup file>]");
  console.error("       node scripts/new-secret.mjs rotate-invite-keys <new kid> --from <backup of the current ring> [--out <backup file>]");
  process.exit(2);
}

const b64url = (bytes) => Buffer.from(bytes).toString("base64url");

function emit(secret) {
  // The backup is written first, and never over an existing file, so an old key cannot be lost by mistake.
  if (out) writeFileSync(out, secret, { encoding: "utf8", flag: "wx", mode: 0o600 });
  process.stdout.write(secret);
  if (out) console.error(`Backup written to ${out}. Keep it offline; it is the only copy you can read again.`);
}

if (kind === "signing-key" && (purposeForKid(kid) === null || listForKid(kid) === null)) {
  console.error("signing-key: name the key licence-, config- or pack-index-, then staging- or prod-, then a number (for example pack-index-prod-1)");
  process.exit(2);
}

if (kind === "signing-key") {
  const pair = await webcrypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const pkcs8 = new Uint8Array(await webcrypto.subtle.exportKey("pkcs8", pair.privateKey));
  const spki = new Uint8Array(await webcrypto.subtle.exportKey("spki", pair.publicKey));
  emit(JSON.stringify({ kid, pkcs8: b64url(pkcs8) }));
  console.error(publicKeyNotice(kid, b64url(spki)));
} else if (kind === "ban-key") {
  emit(b64url(webcrypto.getRandomValues(new Uint8Array(32))));
  console.error(`Ban key "${kid}" generated. Never rotate it; back it up offline.`);
} else if (kind === "invite-keys") {
  emit(JSON.stringify({ current: kid, keys: { [kid]: b64url(webcrypto.getRandomValues(new Uint8Array(32))) } }));
  console.error(`Invite HMAC key "${kid}" generated.`);
} else {
  let merged;
  try {
    merged = mergeInviteKeyRing(readFileSync(from, "utf8"), kid, b64url(webcrypto.getRandomValues(new Uint8Array(32))));
  } catch (error) {
    console.error(`rotate-invite-keys: ${error.message}`);
    process.exit(1);
  }
  emit(merged);
  console.error(`Invite key ring rotated: "${kid}" is now current; every older key is kept.`);
}
