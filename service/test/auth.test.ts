// FR-ONL-01 (sign-in), FR-ONL-03 (device keys, rotation, reuse), FR-ONL-04 (licence), FR-ONL-05 (signed revocation),
// QUALITY_GATES V3.2 and V3.5 (token-reuse test). Discord is the fake in helpers.ts; nothing leaves the test.

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { REFRESH_RETRY_GRACE_MS } from "../src/routes/auth.ts";
import { verifyDocument } from "../src/signing/document.ts";
import { API, Harness, REDIRECT, envelopeOf, newDevice, resetDatabase, respell, rowCount, trustedKeys, type Device } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

async function grantPublicMode() {
  await env.DB.prepare("UPDATE settings SET value = '\"public\"' WHERE key = 'access_mode'").run();
}

async function verifiedLicence(document: string, now: number) {
  const result = await verifyDocument(document, "licence", "stable", { keys: await trustedKeys(), now: new Date(now) });
  expect(result.outcome).toBe("Valid");
  return JSON.parse(result.envelope!.payloadJson) as Record<string, unknown>;
}

describe("POST /v1/auth/discord", () => {
  it("exchanges the code server-side as a confidential client and takes the user id from /users/@me", async () => {
    const device = await newDevice();
    const { status, body } = await h.signIn("123456789012345678", device, "Meg");
    expect(status).toBe(200);
    const token = h.calls.find((c) => c.url.endsWith("/oauth2/token"))!;
    const form = new URLSearchParams(token.body);
    expect(form.get("grant_type")).toBe("authorization_code");
    expect(form.get("redirect_uri")).toBe(REDIRECT);
    expect(form.get("code_verifier")).toMatch(/^v{43}/);
    expect(form.has("client_secret")).toBe(false);
    expect(token.headers.get("Authorization")).toBe(`Basic ${btoa("1563291325107937340:test-client-secret")}`);
    expect(h.calls.map((c) => c.url)).toEqual(["https://discord.com/api/v10/oauth2/token", "https://discord.com/api/v10/users/@me"]);

    expect(body["access"]).toBe("invite_required");
    expect(body["licence"]).toBeNull();
    expect(body["accessToken"]).toMatch(/^at_[A-Za-z0-9_-]{43}$/);
    expect(body["refreshToken"]).toMatch(/^rt_[A-Za-z0-9_-]{43}$/);
    const user = await env.DB.prepare("SELECT discord_id, display_name FROM users").first<{ discord_id: string; display_name: string }>();
    expect(user).toEqual({ discord_id: "123456789012345678", display_name: "Meg" });
  });

  it("stores only hashes of the tokens it issues", async () => {
    const { body } = await h.signIn("123456789012345678", await newDevice());
    const dump = JSON.stringify(await env.DB.prepare("SELECT * FROM token_families").all()) + JSON.stringify(await env.DB.prepare("SELECT * FROM refresh_tokens").all());
    expect(dump).not.toContain(body["accessToken"]);
    expect(dump).not.toContain(body["refreshToken"]);
  });

  it("grants access and a signed licence bound to the device at sign-in in public mode", async () => {
    await grantPublicMode();
    const device = await newDevice();
    const { body } = await h.signIn("223456789012345678", device);
    expect(body["access"]).toBe("granted");
    const licence = await verifiedLicence(body["licence"] as string, h.now);
    const deviceInfo = body["device"] as { thumbprint: string };
    expect(licence).toMatchObject({ sub: (body["user"] as { id: string }).id, cnf: deviceInfo.thumbprint, status: "active", features: ["online"] });
    const envelope = envelopeOf(body["licence"] as string);
    expect(Date.parse(envelope["expiresAt"] as string) - Date.parse(envelope["issuedAt"] as string)).toBe(7 * 24 * 3600 * 1000);
    // TM-01: the licence version is exactly the signing time in whole seconds, which the app verifier enforces.
    expect(envelope["version"]).toBe(Math.floor(Date.parse(envelope["issuedAt"] as string) / 1000));
    expect(licence["exp"]).toBe(envelope["expiresAt"]);
    expect(licence["serverTime"]).toBe(new Date(h.now).toISOString());
  });

  it.each([
    ["https://127.0.0.1:5000/callback"],
    ["http://localhost:5000/callback"],
    ["http://127.0.0.1/callback"],
    ["http://127.0.0.1:5000/other"],
    ["http://127.0.0.1:99999/callback"],
    ["http://127.0.0.1:5000/callback?x=1"],
    ["http://evil.example/callback"],
  ])("refuses the redirect URI %s without calling Discord", async (redirectUri) => {
    const device = await newDevice();
    const response = await h.call("POST", `${API}/v1/auth/discord`, {
      body: { code: "abc", codeVerifier: "v".repeat(43), redirectUri, devicePublicKeyJwk: device.publicJwk },
    });
    expect(response.status).toBe(400);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "invalid_redirect_uri" });
    expect(h.calls).toHaveLength(0);
  });

  it("refuses a private key, a wrong curve, an off-curve point and extra members", async () => {
    const device = await newDevice();
    const bad = [
      { ...device.publicJwk, d: "AAAA" },
      { ...device.publicJwk, crv: "P-384" },
      { ...device.publicJwk, x: device.publicJwk.y, y: device.publicJwk.x },
      { kty: "RSA", n: "x", e: "AQAB" },
    ];
    for (const jwk of bad) {
      const response = await h.call("POST", `${API}/v1/auth/discord`, { body: { code: "abc", codeVerifier: "v".repeat(43), redirectUri: REDIRECT, devicePublicKeyJwk: jwk } });
      expect(response.status).toBe(400);
    }
    expect(h.calls).toHaveLength(0);
  });

  it("registers one key once: another spelling of the same coordinates is refused (no device aliasing)", async () => {
    const device = await newDevice();
    const first = await h.signIn("123456789012345679", device);
    expect(first.status).toBe(200);
    for (const variant of [
      { ...device.publicJwk, x: respell(device.publicJwk.x) },
      { ...device.publicJwk, y: respell(device.publicJwk.y) },
    ]) {
      const again = await h.signIn("123456789012345679", { ...device, publicJwk: variant });
      expect(again.status).toBe(400);
      expect(again.body["error"]).toBe("invalid_device_key");
    }
    expect((await h.signIn("123456789012345679", { ...device, publicJwk: { ...device.publicJwk, x: `${device.publicJwk.x}=` } })).status).toBe(400);
    expect(await rowCount("devices")).toBe(1);
  });

  it("keeps a revoked device out whatever spelling of its key is sent", async () => {
    await grantPublicMode();
    const device = await newDevice();
    const { body } = await h.signIn("123456789012345680", device);
    await env.DB.prepare("UPDATE devices SET revoked_at = ? WHERE id = ?").bind(h.now, (body["device"] as { id: string }).id).run();
    const respelled = await h.signIn("123456789012345680", { ...device, publicJwk: { ...device.publicJwk, x: respell(device.publicJwk.x) } });
    expect(respelled.status).toBe(400);
    expect((await h.signIn("123456789012345680", device)).body["error"]).toBe("device_revoked");
    expect(await rowCount("devices", "revoked_at IS NULL")).toBe(0);
  });

  it("maps a code Discord rejects to 400 and an outage to 502", async () => {
    const device = await newDevice();
    const rejected = await h.call("POST", `${API}/v1/auth/discord`, { body: { code: "never-issued", codeVerifier: "v".repeat(43), redirectUri: REDIRECT, devicePublicKeyJwk: device.publicJwk } });
    expect(rejected.status).toBe(400);
    expect(await rejected.json()).toEqual({ schemaVersion: 1, error: "discord_code_rejected" });
    h.discordDown = true;
    const down = await h.signIn("323456789012345678", device);
    expect(down.status).toBe(502);
    expect(down.body).toEqual({ schemaVersion: 1, error: "discord_unavailable" });
    expect(await rowCount("users")).toBe(0);
  });

  it("enforces the device limit (default 3) and reuses the device for the same key", async () => {
    const devices: Device[] = [];
    for (let i = 0; i < 4; i++) devices.push(await newDevice());
    for (const d of devices.slice(0, 3)) expect((await h.signIn("423456789012345678", d)).status).toBe(200);
    const fourth = await h.signIn("423456789012345678", devices[3]!);
    expect(fourth.status).toBe(409);
    expect(fourth.body["error"]).toBe("device_limit");
    expect(await rowCount("devices")).toBe(3);

    // Signing in again on a known device does not count against the limit and replaces that device's session.
    const again = await h.signIn("423456789012345678", devices[0]!);
    expect(again.status).toBe(200);
    expect(await rowCount("devices")).toBe(3);
    expect(await rowCount("token_families", "revoked_at IS NULL")).toBe(3);
    expect(await rowCount("token_families", "revoked_reason = 'replaced'")).toBe(1);
  });

  it("follows an owner-adjusted device limit", async () => {
    await env.DB.prepare("UPDATE settings SET value = '1' WHERE key = 'device_limit'").run();
    expect((await h.signIn("523456789012345678", await newDevice())).status).toBe(200);
    expect((await h.signIn("523456789012345678", await newDevice())).status).toBe(409);
  });

  it("returns a signed revocation to a revoked user", async () => {
    const device = await newDevice();
    await h.signIn("623456789012345678", device);
    await env.DB.prepare("UPDATE users SET status = 'revoked', revoked_at = ?").bind(h.now).run();
    const { status, body } = await h.signIn("623456789012345678", device);
    expect(status).toBe(403);
    expect(body["error"]).toBe("revoked");
    const revocation = await verifiedLicence(body["revocation"] as string, h.now);
    expect(revocation).toMatchObject({ status: "revoked", features: [] });
  });
});

describe("POST /v1/auth/refresh", () => {
  async function signedInWithAccess(discordId = "723456789012345678") {
    await grantPublicMode();
    const device = await newDevice();
    const { body } = await h.signIn(discordId, device);
    return { device, body, refreshToken: body["refreshToken"] as string };
  }

  it("rotates the refresh token, issues a new access token and a new licence", async () => {
    const { device, body, refreshToken } = await signedInWithAccess();
    h.now += 60_000;
    const response = await h.refresh(refreshToken, device);
    expect(response.status).toBe(200);
    const next = (await response.json()) as Record<string, unknown>;
    expect(next["refreshToken"]).not.toBe(refreshToken);
    expect(next["accessToken"]).not.toBe(body["accessToken"]);
    const licence = await verifiedLicence(next["licence"] as string, h.now);
    expect(licence["serverTime"]).toBe(new Date(h.now).toISOString());

    // The old access token is dead, the new one works.
    expect((await h.call("GET", `${API}/v1/me`, { headers: { Authorization: `Bearer ${body["accessToken"] as string}` } })).status).toBe(401);
    expect((await h.call("GET", `${API}/v1/me`, { headers: { Authorization: `Bearer ${next["accessToken"] as string}` } })).status).toBe(200);
  });

  it("revokes the whole family when an old refresh token is reused (token-reuse test)", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const first = (await (await h.refresh(refreshToken, device)).json()) as Record<string, string>;
    h.now += REFRESH_RETRY_GRACE_MS;
    const reuse = await h.refresh(refreshToken, device);
    expect(reuse.status).toBe(401);
    expect(await reuse.json()).toEqual({ schemaVersion: 1, error: "refresh_token_reused" });
    expect(await rowCount("token_families", "revoked_reason = 'refresh_token_reused'")).toBe(1);
    // The token issued by the legitimate rotation is now useless too, and so is its access token.
    expect((await h.refresh(first["refreshToken"]!, device)).status).toBe(401);
    expect((await h.call("GET", `${API}/v1/me`, { headers: { Authorization: `Bearer ${first["accessToken"]!}` } })).status).toBe(401);
    expect(await rowCount("audit_log", "action = 'session.refresh_token_reused'")).toBe(1);
  });

  it("answers two concurrent refreshes of the same token with the same pair and keeps the family", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const results = await Promise.all([h.refresh(refreshToken, device), h.refresh(refreshToken, device)]);
    expect(results.map((r) => r.status)).toEqual([200, 200]);
    const [a, b] = (await Promise.all(results.map((r) => r.json()))) as Record<string, string>[];
    expect(a!["refreshToken"]).toBe(b!["refreshToken"]);
    expect(a!["accessToken"]).toBe(b!["accessToken"]);
    expect(await rowCount("refresh_tokens")).toBe(2);
    expect(await rowCount("token_families", "revoked_at IS NULL")).toBe(1);
    expect((await h.call("GET", `${API}/v1/me`, { headers: { Authorization: `Bearer ${a!["accessToken"]!}` } })).status).toBe(200);
    expect((await h.refresh(a!["refreshToken"]!, device)).status).toBe(200);
  });

  it("returns the same successor pair to a retry inside the grace window (lost response)", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const first = (await (await h.refresh(refreshToken, device)).json()) as Record<string, string>;
    h.now += REFRESH_RETRY_GRACE_MS - 1000;
    const retry = await h.refresh(refreshToken, device);
    expect(retry.status).toBe(200);
    const again = (await retry.json()) as Record<string, string>;
    expect(again["refreshToken"]).toBe(first["refreshToken"]);
    expect(again["accessToken"]).toBe(first["accessToken"]);
    expect(again["refreshTokenExpiresAt"]).toBe(first["refreshTokenExpiresAt"]);
    expect(await rowCount("token_families", "revoked_at IS NULL")).toBe(1);
    expect(await verifiedLicence(again["licence"]!, h.now)).toMatchObject({ status: "active" });
  });

  it("treats a retry as reuse once the successor has been used", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const first = (await (await h.refresh(refreshToken, device)).json()) as Record<string, string>;
    expect((await h.refresh(first["refreshToken"]!, device)).status).toBe(200);
    const late = await h.refresh(refreshToken, device);
    expect(late.status).toBe(401);
    expect(await late.json()).toEqual({ schemaVersion: 1, error: "refresh_token_reused" });
    expect(await rowCount("token_families", "revoked_reason = 'refresh_token_reused'")).toBe(1);
  });

  it("treats a retry with a bad proof as reuse, and refuses a replayed nonce without revoking", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const proof = await device.proof(refreshToken, Math.floor(h.now / 1000));
    expect((await h.call("POST", `${API}/v1/auth/refresh`, { body: { refreshToken, proof } })).status).toBe(200);
    const resent = await h.call("POST", `${API}/v1/auth/refresh`, { body: { refreshToken, proof } });
    expect(resent.status).toBe(401);
    expect(await resent.json()).toEqual({ schemaVersion: 1, error: "proof_replayed" });
    expect(await rowCount("token_families", "revoked_at IS NULL")).toBe(1);
    const stolen = await h.refresh(refreshToken, await newDevice());
    expect(stolen.status).toBe(401);
    expect(await stolen.json()).toEqual({ schemaVersion: 1, error: "refresh_token_reused" });
    expect(await rowCount("token_families", "revoked_at IS NULL")).toBe(0);
  });

  it("revokes the family on reuse even when the audit write keeps failing", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    await h.refresh(refreshToken, device);
    h.now += REFRESH_RETRY_GRACE_MS;
    // Every audit insert now fails as if it lost the chain race, so writeAudited gives up after its retries.
    await env.DB.prepare("CREATE TRIGGER audit_always_conflicts BEFORE INSERT ON audit_log BEGIN SELECT RAISE(ABORT, 'UNIQUE constraint failed: audit_log.prev_hash'); END").run();
    try {
      const reuse = await h.refresh(refreshToken, device);
      expect(reuse.status).toBe(401);
      expect(await reuse.json()).toEqual({ schemaVersion: 1, error: "refresh_token_reused" });
      expect(await rowCount("token_families", "revoked_at IS NULL")).toBe(0);
    } finally {
      await env.DB.prepare("DROP TRIGGER audit_always_conflicts").run();
    }
  });

  it("requires a proof made with the registered device key", async () => {
    const { refreshToken } = await signedInWithAccess();
    const other = await newDevice();
    const response = await h.refresh(refreshToken, other);
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "invalid_proof" });
  });

  it("refuses a proof signature spelled with non-zero spare bits", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const proof = await device.proof(refreshToken, Math.floor(h.now / 1000));
    const response = await h.refresh(refreshToken, device, { nonce: proof.nonce, signature: respell(proof.signature) });
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "invalid_proof" });
  });

  it("accepts the timestamp within five minutes and refuses it outside", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const nowSeconds = Math.floor(h.now / 1000);
    expect((await h.refresh(refreshToken, device, { timestamp: nowSeconds - 301 })).status).toBe(401);
    expect((await h.refresh(refreshToken, device, { timestamp: nowSeconds + 301 })).status).toBe(401);
    expect((await h.refresh(refreshToken, device, { timestamp: nowSeconds - 299 })).status).toBe(200);
  });

  it("refuses a nonce that was already used", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    const first = (await (await h.refresh(refreshToken, device, { nonce: "n".repeat(22) })).json()) as Record<string, string>;
    const replay = await h.refresh(first["refreshToken"]!, device, { nonce: "n".repeat(22) });
    expect(replay.status).toBe(401);
    expect(await replay.json()).toEqual({ schemaVersion: 1, error: "proof_replayed" });
  });

  it("refuses unknown and malformed refresh tokens", async () => {
    const { device } = await signedInWithAccess();
    expect((await h.refresh(`rt_${"A".repeat(43)}`, device)).status).toBe(401);
    expect((await h.refresh("not-a-token", device)).status).toBe(400);
  });

  it("returns a signed revocation once the owner revoked the user (D8)", async () => {
    const { device, refreshToken } = await signedInWithAccess();
    await env.DB.batch([
      env.DB.prepare("UPDATE users SET status = 'revoked', revoked_at = ?").bind(h.now),
      env.DB.prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'user_revoked'").bind(h.now),
    ]);
    const response = await h.refresh(refreshToken, device);
    expect(response.status).toBe(403);
    const body = (await response.json()) as Record<string, string>;
    expect(body["error"]).toBe("revoked");
    expect(await verifiedLicence(body["revocation"]!, h.now)).toMatchObject({ status: "revoked", features: [] });
  });

  it("keeps a user without access on invite_required with no licence", async () => {
    const device = await newDevice();
    const { body } = await h.signIn("823456789012345678", device);
    const response = await h.refresh(body["refreshToken"] as string, device);
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ access: "invite_required", licence: null });
  });
});

describe("POST /v1/auth/logout", () => {
  it("revokes the token family", async () => {
    const device = await newDevice();
    const { body } = await h.signIn("923456789012345678", device);
    const auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
    expect((await h.call("POST", `${API}/v1/auth/logout`, { headers: auth })).status).toBe(200);
    expect((await h.call("GET", `${API}/v1/me`, { headers: auth })).status).toBe(401);
    expect((await h.refresh(body["refreshToken"] as string, device)).status).toBe(401);
    expect(await rowCount("token_families", "revoked_reason = 'logout'")).toBe(1);
  });

  it("needs a session", async () => {
    expect((await h.call("POST", `${API}/v1/auth/logout`)).status).toBe(401);
  });
});
