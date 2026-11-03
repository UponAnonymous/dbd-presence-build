// Writes tests/fixtures/auth/refresh-proof-vector.json: a refresh proof produced with the TypeScript service code
// (service/src/auth/device.ts) for a throw-away TEST device key. The C# suite (tests/DbdPresence.Online.Tests/RefreshProofTests.cs)
// must build the same message and thumbprint, and must verify the TypeScript-made signature, so the two stacks cannot drift.
//
//   node tests/fixtures/auth/make-refresh-proof-vector.mjs     (repository root, Node 22.18 or later: strips the TypeScript types)
//
// ECDSA signatures are randomised, so `signature` changes on every run; everything else is deterministic for the same key.
// The private key below is a test key that nothing trusts; never reuse it.

import { webcrypto } from "node:crypto";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { importDeviceKey, parseDevicePublicKey, proofMessage, verifyProofSignature } from "../../../service/src/auth/device.ts";
import { encodeBase64Url } from "../../../service/src/lib/base64url.ts";
import { sha256Base64Url } from "../../../service/src/lib/crypto.ts";

const pair = await webcrypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const privateJwk = await webcrypto.subtle.exportKey("jwk", pair.privateKey);
const publicJwk = { kty: "EC", crv: "P-256", x: privateJwk.x, y: privateJwk.y };

const device = await parseDevicePublicKey(publicJwk);
const refreshToken = "rt_" + "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE".slice(0, 43);
const timestamp = 1798000000;
const nonce = "nonce_0123456789abcdef";
const tokenHash = await sha256Base64Url(refreshToken);
const message = proofMessage(tokenHash, timestamp, nonce);
const signature = encodeBase64Url(new Uint8Array(await webcrypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, pair.privateKey, message)));

const ok = await verifyProofSignature(await importDeviceKey(device.jwkJson), message, signature);
if (!ok) throw new Error("the TypeScript verifier rejected its own vector");

const vector = {
  description: "Refresh proof made with service/src/auth/device.ts (ES256, IEEE P1363). message = 'dbdp-refresh-proof-v1\\n' + tokenHash + '\\n' + timestamp + '\\n' + nonce",
  refreshToken,
  tokenHash,
  timestamp,
  nonce,
  message: new TextDecoder().decode(message),
  privateJwk: { kty: privateJwk.kty, crv: privateJwk.crv, x: privateJwk.x, y: privateJwk.y, d: privateJwk.d },
  publicJwkCanonical: device.jwkJson,
  thumbprint: device.thumbprint,
  signature,
};
writeFileSync(join(import.meta.dirname, "refresh-proof-vector.json"), JSON.stringify(vector, null, 2) + "\n");
console.log("wrote refresh-proof-vector.json");
