// FR-ADM-02 (Access JWT), FR-ADM-03 (request protection), FR-ADM-05 (audit chain), FR-ADM-07 (invites and users),
// FR-ONL-06 (access mode), QUALITY_GATES V3.2 and V3.4 (unauthenticated admin call refused).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { ADMIN_ROUTES, handleRequest } from "../src/app.ts";
import { FAILURE_TTL_MS } from "../src/auth/access-jwt.ts";
import { auditHash } from "../src/db/audit.ts";
import { ADMIN, API, AUD, Harness, OWNER, TEAM, newDevice, resetDatabase, respell, rowCount } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
  await h.enableAccessKeys();
});

describe("admin authentication (Cloudflare Access)", () => {
  it("refuses an unauthenticated call with 401", async () => {
    const response = await h.call("GET", `${ADMIN}/api/users`, { headers: { "X-DBDP-Admin": "1" } });
    expect(response.status).toBe(401);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "unauthenticated" });
  });

  it("refuses DELETE /api/roster/sightings on the admin host without a token (M1)", async () => {
    const response = await h.call("DELETE", `${ADMIN}/api/roster/sightings`);
    expect(response.status).toBe(401);
  });

  it("guards every admin route: each answers 401 without a token, whatever the method (M1)", async () => {
    const UUID = "0b9b5f4e-1c2d-4e3f-8a4b-5c6d7e8f9a0b";
    const samples: [string, string][] = [
      ["([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", UUID],
      ["([a-z0-9-]{1,64})", "some-pack"],
      ["(e[0-9a-f]{24})", `e${"0".repeat(24)}`],
      ["([KS][0-9]{2})", "K01"],
      ["([a-z0-9-]{2,40})", "some-name"],
      ["([0-9a-f]{64})", "a".repeat(64)],
    ];
    expect(ADMIN_ROUTES.length).toBeGreaterThan(40);
    for (const route of ADMIN_ROUTES) {
      let path = route.pattern.source.replace(/^\^/, "").replace(/\$$/, "").replaceAll("\\/", "/");
      for (const [group, value] of samples) path = path.replaceAll(group, value);
      expect(route.pattern.test(path), `${route.method} ${route.pattern.source} sample ${path}`).toBe(true);
      const response = await h.call(route.method, `${ADMIN}${path}`, { headers: { "X-DBDP-Admin": "1" } });
      expect(response.status, `${route.method} ${path}`).toBe(401);
    }
  });

  it.each([
    ["a wrong aud", { aud: ["someone-else"] }],
    ["a wrong iss", { iss: "https://other-team.cloudflareaccess.com" }],
    ["an expired token", { exp: Math.floor(Date.parse("2026-11-02T09:00:00Z") / 1000) }],
    ["a token not yet valid", { nbf: Math.floor(Date.parse("2026-11-02T11:00:00Z") / 1000) }],
  ])("refuses %s with 401", async (_label, claims) => {
    const response = await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": await h.accessJwt(claims) });
    expect(response.status).toBe(401);
  });

  it("refuses a token signed by another key, an unknown kid and alg none", async () => {
    const other = (await crypto.subtle.generateKey({ name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
    expect((await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": await h.accessJwt({}, undefined, other.privateKey) })).status).toBe(401);
    expect((await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": await h.accessJwt({}, "unknown-kid") })).status).toBe(401);
    const valid = await h.accessJwt();
    const [, payload] = valid.split(".");
    const none = `${btoa(JSON.stringify({ alg: "none", kid: "access-key-1" })).replace(/=+$/, "")}.${payload}.`;
    expect((await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": none })).status).toBe(401);
  });

  it("refuses a JWT whose signature or payload is spelled with non-zero spare bits", async () => {
    const valid = await h.accessJwt();
    expect((await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": valid })).status).toBe(200);
    const [header, payload, signature] = valid.split(".") as [string, string, string];
    expect((await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": `${header}.${payload}.${respell(signature)}` })).status).toBe(401);
  });

  it("refuses an authenticated email that is not on the owner allow-list with 403", async () => {
    const response = await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": await h.accessJwt({ email: "intruder@example.com" }) });
    expect(response.status).toBe(403);
    expect((await h.admin("GET", "/api/users", undefined, { "Cf-Access-Jwt-Assertion": await h.accessJwt({ email: "Second.Owner@example.com" }) })).status).toBe(200);
  });

  it("fetches the team key set from the team domain and caches it", async () => {
    await h.admin("GET", "/api/users");
    await h.admin("GET", "/api/users");
    expect(h.calls.filter((c) => c.url === `${TEAM}/cdn-cgi/access/certs`)).toHaveLength(1);
    h.now += 11 * 60 * 1000;
    await h.admin("GET", "/api/users");
    expect(h.calls.filter((c) => c.url === `${TEAM}/cdn-cgi/access/certs`)).toHaveLength(2);
  });

  it("compares emails ASCII-only: a Unicode look-alike of an owner's address is refused", async () => {
    const withOwner = (owners: string) => ({ ...env, OWNER_EMAILS: owners });
    const call = async (owners: string, email: string) =>
      handleRequest(new Request(`${ADMIN}/api/users`, { headers: await h.adminHeaders({ "Cf-Access-Jwt-Assertion": await h.accessJwt({ email }) }) }), withOwner(owners), h.deps);
    // U+212A KELVIN SIGN lower-cases to an ASCII "k".
    expect("\u212Aate@example.com".toLowerCase()).toBe("kate@example.com");
    expect((await call("kate@example.com", "\u212Aate@example.com")).status).toBe(403);
    expect((await call("kate@example.com", "KATE@Example.com")).status).toBe(200);
    // A non-ASCII entry makes the allow-list unusable rather than matching loosely.
    const misconfigured = await call("kate@example.com, \u212Aate@example.com", "kate@example.com");
    expect(misconfigured.status).toBe(503);
    expect(await misconfigured.json()).toEqual({ schemaVersion: 1, error: "not_configured" });
  });

  it("remembers a failed key-set fetch for 30 seconds instead of fetching on every call", async () => {
    h.jwksDown = true;
    const certs = () => h.calls.filter((c) => c.url === `${TEAM}/cdn-cgi/access/certs`).length;
    for (let i = 0; i < 3; i++) {
      const response = await h.admin("GET", "/api/users");
      expect(response.status).toBe(503);
      expect(await response.json()).toEqual({ schemaVersion: 1, error: "access_keys_unavailable" });
    }
    expect(certs()).toBe(1);
    h.now += FAILURE_TTL_MS;
    h.jwksDown = false;
    expect((await h.admin("GET", "/api/users")).status).toBe(200);
    expect(certs()).toBe(2);
  });

  it("checks the audience it was configured with", () => {
    expect(env.ACCESS_AUD).toBe(AUD);
  });
});

describe("admin request protection", () => {
  it("refuses a wrong Origin with 403", async () => {
    const response = await h.admin("POST", "/api/invites", {}, { Origin: "https://evil.example" });
    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "bad_origin" });
    expect((await h.admin("POST", "/api/invites", {}, { Origin: "https://dbdadmin.arcavale.com.evil.example" })).status).toBe(403);
    expect((await h.admin("POST", "/api/invites", {}, { Origin: "http://dbdadmin.arcavale.com" })).status).toBe(403);
    expect((await h.admin("POST", "/api/invites", {}, { Origin: null })).status).toBe(403);
    expect(await rowCount("invites")).toBe(0);
  });

  it("refuses a missing custom header with 403", async () => {
    const response = await h.admin("POST", "/api/invites", {}, { "X-DBDP-Admin": null });
    expect(response.status).toBe(403);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "admin_header_required" });
    expect((await h.admin("GET", "/api/users", undefined, { "X-DBDP-Admin": null })).status).toBe(403);
  });

  it("refuses Sec-Fetch-Site other than same-origin on state-changing calls", async () => {
    for (const site of ["cross-site", "same-site", "none"]) {
      expect((await h.admin("POST", "/api/invites", {}, { "Sec-Fetch-Site": site })).status).toBe(403);
    }
    expect((await h.admin("POST", "/api/invites", {}, { "Sec-Fetch-Site": null })).status).toBe(403);
    expect((await h.admin("GET", "/api/users", undefined, { "Sec-Fetch-Site": "cross-site" })).status).toBe(403);
    expect(await rowCount("invites")).toBe(0);
  });

  it("sends no CORS headers and answers OPTIONS with 405", async () => {
    const preflight = await h.call("OPTIONS", `${ADMIN}/api/invites`, { headers: { Origin: "https://evil.example", "Access-Control-Request-Method": "POST" } });
    expect(preflight.status).toBe(405);
    const ok = await h.admin("GET", "/api/users");
    for (const response of [preflight, ok]) {
      for (const name of response.headers.keys()) expect(name.startsWith("access-control-")).toBe(false);
    }
  });

  it("is reachable only on the admin host", async () => {
    const response = await h.call("GET", `${API}/api/users`, { headers: await h.adminHeaders() });
    expect(response.status).toBe(404);
  });
});

describe("admin endpoints", () => {
  it("a valid call returns 200 and writes an audit row", async () => {
    const response = await h.admin("POST", "/api/invites", { count: 3, note: "Friends", expiresInDays: 14 });
    expect(response.status).toBe(200);
    const body = (await response.json()) as { invites: { id: string; code: string; note: string; expiresAt: string }[] };
    expect(body.invites).toHaveLength(3);
    expect(body.invites[0]!.expiresAt).toBe(new Date(h.now + 14 * 86_400_000).toISOString());
    const audit = await env.DB.prepare("SELECT * FROM audit_log").all<Record<string, unknown>>();
    expect(audit.results).toHaveLength(1);
    expect(audit.results[0]).toMatchObject({ actor: `owner:${OWNER}`, action: "invite.create" });
    expect(JSON.parse(audit.results[0]!["after_json"] as string)).toEqual({ ids: body.invites.map((i) => i.id), hasNote: true, expiresAt: body.invites[0]!.expiresAt });
    expect(audit.results[0]!["after_json"]).not.toContain("Friends");

    const list = await h.admin("GET", "/api/invites");
    expect(list.status).toBe(200);
    const listed = (await list.json()) as { invites: { id: string; status: string; note: string }[] };
    expect(listed.invites.map((i) => i.status)).toEqual(["active", "active", "active"]);
    expect(JSON.stringify(listed)).not.toContain(body.invites[0]!.code);
  });

  it("validates invite creation input", async () => {
    for (const bad of [{ count: 0 }, { count: 101 }, { expiresInDays: 91 }, { note: "x".repeat(201) }, { extra: true }, { count: "3" }]) {
      expect((await h.admin("POST", "/api/invites", bad)).status).toBe(400);
    }
  });

  it("revokes only unused invites, with an audit row", async () => {
    const created = (await (await h.admin("POST", "/api/invites", {})).json()) as { invites: { id: string }[] };
    const id = created.invites[0]!.id;
    expect((await h.admin("POST", `/api/invites/${id}/revoke`)).status).toBe(200);
    expect((await h.admin("POST", `/api/invites/${id}/revoke`)).status).toBe(409);
    expect((await h.admin("POST", `/api/invites/${crypto.randomUUID()}/revoke`)).status).toBe(404);
    expect(await rowCount("audit_log", "action = 'invite.revoke' AND target = ?", `invite:${id}`)).toBe(1);
  });

  it("lists, revokes, exports and deletes users; audit rows carry no Discord id or name", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();
    const { body } = await h.signIn("131111111111111111", device, "Dwight");
    const userId = (body["user"] as { id: string }).id;

    const users = (await (await h.admin("GET", "/api/users")).json()) as { users: { id: string; discordId: string; displayName: string; devices: unknown[] }[] };
    expect(users.users).toHaveLength(1);
    expect(users.users[0]).toMatchObject({ id: userId, discordId: "131111111111111111", displayName: "Dwight" });
    expect(users.users[0]!.devices).toHaveLength(1);

    expect((await h.admin("POST", `/api/users/${userId}/revoke`)).status).toBe(200);
    expect((await h.admin("POST", `/api/users/${userId}/revoke`)).status).toBe(409);
    const refreshed = await h.refresh(body["refreshToken"] as string, device);
    expect(refreshed.status).toBe(403);

    const exported = await h.admin("GET", `/api/users/${userId}/export`);
    expect(exported.status).toBe(200);
    const data = (await exported.json()) as { user: { discordId: string }; sessions: unknown[]; auditEntries: { action: string }[] };
    expect(data.user.discordId).toBe("131111111111111111");
    expect(data.sessions).toHaveLength(1);
    expect(data.auditEntries.map((e) => e.action)).toContain("user.revoke");

    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: "wrong" })).status).toBe(400);
    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: userId })).status).toBe(200);
    expect(await rowCount("users")).toBe(0);
    expect(await rowCount("devices")).toBe(0);
    expect(await rowCount("token_families")).toBe(0);
    expect(await rowCount("refresh_tokens")).toBe(0);
    const audit = JSON.stringify((await env.DB.prepare("SELECT * FROM audit_log").all()).results);
    expect(audit).toContain(`user:${userId}`);
    expect(audit).not.toContain("131111111111111111");
    expect(audit).not.toContain("Dwight");
    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: userId })).status).toBe(404);
  });

  it("revokes a device: its session ends, the slot is freed and the same key cannot sign in again", async () => {
    await h.admin("PUT", "/api/settings", { deviceLimit: 1 });
    const lost = await newDevice();
    const { body } = await h.signIn("132111111111111111", lost);
    const userId = (body["user"] as { id: string }).id;
    const deviceId = (body["device"] as { id: string }).id;
    expect((await h.signIn("132111111111111111", await newDevice())).status).toBe(409);

    expect((await h.admin("POST", `/api/users/${userId}/devices/${deviceId}/revoke`)).status).toBe(200);
    expect((await h.admin("POST", `/api/users/${userId}/devices/${deviceId}/revoke`)).status).toBe(409);
    expect((await h.admin("POST", `/api/users/${userId}/devices/${crypto.randomUUID()}/revoke`)).status).toBe(404);
    expect(await rowCount("audit_log", "action = 'device.revoke'")).toBe(1);
    expect((await h.call("GET", `${API}/v1/me`, { headers: { Authorization: `Bearer ${body["accessToken"] as string}` } })).status).toBe(401);
    expect((await h.refresh(body["refreshToken"] as string, lost)).status).toBe(401);
    expect((await h.signIn("132111111111111111", lost)).body["error"]).toBe("device_revoked");
    expect((await h.signIn("132111111111111111", await newDevice())).status).toBe(200);
  });

  it("gets and sets settings, rejecting unknown or out-of-range values", async () => {
    const before = (await (await h.admin("GET", "/api/settings")).json()) as { settings: Record<string, unknown> };
    expect(before.settings).toMatchObject({ accessMode: "invite-only", deviceLimit: 3 });
    const updated = await h.admin("PUT", "/api/settings", { accessMode: "public", deviceLimit: 5 });
    expect(updated.status).toBe(200);
    expect(await updated.json()).toMatchObject({ settings: { accessMode: "public", deviceLimit: 5 } });
    const audit = await env.DB.prepare("SELECT before_json, after_json FROM audit_log WHERE action = 'settings.update'").first<{ before_json: string; after_json: string }>();
    expect(JSON.parse(audit!.before_json)).toEqual({ accessMode: "invite-only", deviceLimit: 3 });
    expect(JSON.parse(audit!.after_json)).toEqual({ accessMode: "public", deviceLimit: 5 });
    for (const bad of [{ accessMode: "open" }, { deviceLimit: 0 }, { deviceLimit: 11 }, { enableCapture: true }, { minimumVersion: "1.0" }, { banner: "x".repeat(281) }]) {
      expect((await h.admin("PUT", "/api/settings", bad)).status).toBe(400);
    }
  });

  it("lists the audit log and verifies its hash chain; tampering is detected", async () => {
    await h.admin("POST", "/api/invites", {});
    await h.admin("PUT", "/api/settings", { deviceLimit: 4 });
    await h.admin("PUT", "/api/settings", { deviceLimit: 2 });
    const list = (await (await h.admin("GET", "/api/audit?limit=2")).json()) as { entries: { seq: number; action: string; prevHash: string; hash: string }[] };
    expect(list.entries.map((e) => e.action)).toEqual(["settings.update", "settings.update"]);
    expect(list.entries[0]!.prevHash).toBe(list.entries[1]!.hash);
    expect((await h.admin("GET", "/api/audit?limit=abc")).status).toBe(400);

    const good = (await (await h.admin("GET", "/api/audit/verify")).json()) as { chain: { ok: boolean; checked: number; anchored: boolean } };
    expect(good.chain).toEqual({ ok: true, checked: 3, brokenAtSeq: null, anchored: false, exportedHead: null });

    await env.DB.prepare("UPDATE audit_log SET after_json = '{\"deviceLimit\":10}' WHERE seq = 2").run();
    const bad = (await (await h.admin("GET", "/api/audit/verify")).json()) as { chain: { ok: boolean; brokenAtSeq: number } };
    expect(bad.chain).toMatchObject({ ok: false, brokenAtSeq: 2 });
  });

  it("detects a re-hashed row by the broken link to the next row", async () => {
    await h.admin("PUT", "/api/settings", { deviceLimit: 4 });
    await h.admin("PUT", "/api/settings", { deviceLimit: 5 });
    const row = await env.DB.prepare("SELECT * FROM audit_log WHERE seq = 1").first<{ prev_hash: string; at: number; actor: string; action: string; target: string; before_json: string; salt: string }>();
    const forged = await auditHash(row!.prev_hash, row!.at, row!.actor, row!.action, row!.target, row!.before_json, '{"deviceLimit":9}', row!.salt);
    await env.DB.prepare("UPDATE audit_log SET after_json = '{\"deviceLimit\":9}', hash = ? WHERE seq = 1").bind(forged).run();
    const report = (await (await h.admin("GET", "/api/audit/verify")).json()) as { chain: { ok: boolean; brokenAtSeq: number } };
    expect(report.chain).toMatchObject({ ok: false, brokenAtSeq: 2 });
  });

  it("keeps the chain linear under concurrent privileged actions", async () => {
    await Promise.all(Array.from({ length: 5 }, (_, i) => h.admin("POST", "/api/invites", { note: `batch ${i}` })));
    expect(await rowCount("audit_log")).toBe(5);
    const report = (await (await h.admin("GET", "/api/audit/verify")).json()) as { chain: { ok: boolean; checked: number } };
    expect(report.chain).toMatchObject({ ok: true, checked: 5 });
  });
});
