// FR-ADM-01 (Dashboard and Settings sections), NFR-SVC-10 (usage, key status and backup status visible in the panel),
// NFR-SVC-11 (storage against the 8 GB cap): GET /api/overview is admin-only, read-only, reports only what the service holds,
// names key ids but never key material, and reports backups as not recorded (null) rather than guessing.

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { ADMIN, Harness, T0, TEAM, resetDatabase, rowCount } from "./helpers.ts";

const DAY = 24 * 60 * 60 * 1000;

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
  await h.enableAccessKeys();
});

async function overview(): Promise<Record<string, unknown>> {
  const response = await h.admin("GET", "/api/overview");
  expect(response.status).toBe(200);
  return (await response.json()) as Record<string, unknown>;
}

function user(id: string, status: "active" | "revoked", granted: boolean, lastSeenAt: number) {
  return env.DB.prepare("INSERT INTO users (id, discord_id, display_name, status, access_granted_at, access_via, created_at, last_seen_at, revoked_at) VALUES (?, ?, 'Player', ?, ?, ?, ?, ?, ?)").bind(
    id,
    `3100000000000000${id.slice(0, 2)}`,
    status,
    granted ? T0 - 20 * DAY : null,
    granted ? "invite" : null,
    T0 - 30 * DAY,
    lastSeenAt,
    status === "revoked" ? T0 - DAY : null,
  );
}

function invite(id: string, fields: { expiresAt: number; redeemedAt?: number; revokedAt?: number }) {
  return env.DB.prepare("INSERT INTO invites (id, hmac_kid, code_hash, created_at, created_by, expires_at, redeemed_at, revoked_at) VALUES (?, 'test-2', ?, ?, 'test', ?, ?, ?)").bind(
    id,
    `hash-${id}`,
    T0 - 5 * DAY,
    fields.expiresAt,
    fields.redeemedAt ?? null,
    fields.revokedAt ?? null,
  );
}

function upload(id: string, userId: string, createdAt: number) {
  return env.DB.prepare("INSERT INTO uploads (id, user_id, object_key, sha256, format, size, width, height, created_at) VALUES (?, ?, ?, ?, 'png', 77, 1, 1, ?)").bind(id, userId, `u/${id}.png`, id.replaceAll("-", "").padEnd(64, "0"), createdAt);
}

describe("GET /api/overview", () => {
  it("is guarded like every admin route: 401 without an Access token, 403 for someone not on the owner list", async () => {
    expect((await h.call("GET", `${ADMIN}/api/overview`, { headers: { "X-DBDP-Admin": "1" } })).status).toBe(401);
    expect((await h.admin("GET", "/api/overview", undefined, { "Cf-Access-Jwt-Assertion": await h.accessJwt({ email: "intruder@example.com" }) })).status).toBe(403);
    expect((await h.admin("GET", "/api/overview", undefined, { "X-DBDP-Admin": null })).status).toBe(403);
    expect((await h.admin("POST", "/api/overview", {})).status).toBe(405);
  });

  it("on an empty service: zero counts, empty storage against the 8 GB cap, and backups not recorded (all null)", async () => {
    const body = await overview();
    expect(body["environment"]).toBe("production");
    expect(body["hosts"]).toEqual({ api: "api.arcavale.com", admin: "admin.arcavale.com", cdn: "cdn.arcavale.com" });
    expect(body["users"]).toEqual({ total: 0, withAccess: 0, waitingForInvite: 0, revoked: 0, activeThisWeek: 0 });
    expect(body["invites"]).toEqual({ pending: 0 });
    expect(body["uploads"]).toEqual({ total: 0, thisWeek: 0 });
    const storage = body["storage"] as Record<string, unknown>;
    expect(storage["usedBytes"]).toBe(0);
    expect(storage["capBytes"]).toBe(8_000_000_000);
    expect(storage["warnBytes"]).toBe(6_000_000_000);
    expect(body["auditExport"]).toEqual({ lastAt: null });
    expect(body["backup"]).toEqual({ lastExportAt: null, lastExportBytes: null, lastDrillAt: null, lastDrillOk: null });
  });

  it("reports the database size D1 gives with the query, against the 500 MB free-tier size", async () => {
    const database = (await overview())["database"] as { sizeBytes: number | null; freeLimitBytes: number };
    expect(database.freeLimitBytes).toBe(500_000_000);
    expect(Number.isSafeInteger(database.sizeBytes)).toBe(true);
    expect(database.sizeBytes!).toBeGreaterThan(0);
  });

  it("counts users, people active in the last 7 days, pending invites and this week's uploads", async () => {
    const ids = ["a1111111-1111-4111-8111-111111111111", "b2222222-2222-4222-8222-222222222222", "c3333333-3333-4333-8333-333333333333", "d4444444-4444-4444-8444-444444444444"];
    await env.DB.batch([
      user(ids[0]!, "active", true, T0 - 2 * DAY), // active this week
      user(ids[1]!, "active", true, T0 - 8 * DAY), // has access, not seen this week
      user(ids[2]!, "active", false, T0 - DAY), // waiting for an invite, seen this week
      user(ids[3]!, "revoked", true, T0 - 3 * DAY), // revoked: never counted as active
      invite("e5555555-5555-4555-8555-555555555551", { expiresAt: T0 + 10 * DAY }),
      invite("e5555555-5555-4555-8555-555555555552", { expiresAt: T0 + 20 * DAY }),
      invite("e5555555-5555-4555-8555-555555555553", { expiresAt: T0 - DAY }),
      invite("e5555555-5555-4555-8555-555555555554", { expiresAt: T0 + DAY, redeemedAt: T0 - DAY }),
      invite("e5555555-5555-4555-8555-555555555555", { expiresAt: T0 + DAY, revokedAt: T0 - DAY }),
      upload("f6666666-6666-4666-8666-666666666661", ids[0]!, T0 - DAY),
      upload("f6666666-6666-4666-8666-666666666662", ids[0]!, T0 - 10 * DAY),
      upload("f6666666-6666-4666-8666-666666666663", ids[1]!, T0 - 6 * DAY),
      env.DB.prepare("INSERT INTO audit_export_state (id, last_seq, last_hash, exported_at) VALUES (1, 4, ?, ?)").bind("c".repeat(64), T0 - 3_600_000),
    ]);
    const body = await overview();
    expect(body["users"]).toEqual({ total: 4, withAccess: 2, waitingForInvite: 1, revoked: 1, activeThisWeek: 2 });
    expect(body["invites"]).toEqual({ pending: 2 });
    expect(body["uploads"]).toEqual({ total: 3, thisWeek: 2 });
    expect(body["auditExport"]).toEqual({ lastAt: new Date(T0 - 3_600_000).toISOString() });
  });

  it("names every service key by its public id, and the offline release and recovery keys, never any key material", async () => {
    const response = await h.admin("GET", "/api/overview");
    const text = await response.text();
    const keys = (JSON.parse(text) as Record<string, unknown>)["keys"] as Record<string, unknown>;
    expect(keys["service"]).toEqual([
      { name: "licence", configured: true, kid: "test-content-1" },
      { name: "config", configured: true, kid: "test-content-1" },
      { name: "content", configured: true, kid: "test-content-1" },
      { name: "invite", configured: true, kid: "test-2" },
      { name: "ban", configured: true, kid: null },
    ]);
    expect(keys["offline"]).toEqual({ release: ["release-test-1"], recovery: ["recovery-test-1"], revoked: [], configured: true });
    expect(keys["keyReplacement"]).toBeNull();
    const secrets = [
      JSON.parse(env.CONTENT_SIGNING_KEY!).pkcs8 as string,
      ...Object.values(JSON.parse(env.INVITE_HMAC_KEYS!).keys as Record<string, string>),
      env.BAN_HMAC_KEY!,
      env.DISCORD_CLIENT_SECRET!,
    ];
    for (const secret of secrets) expect(text).not.toContain(secret);
  });

  it("reports a missing or unusable key as not configured instead of failing", async () => {
    const broken = { ...env, CONFIG_SIGNING_KEY: undefined, INVITE_HMAC_KEYS: "{not json", BAN_HMAC_KEY: "short", OFFLINE_PUBLIC_KEYS: "[1]" };
    const response = await handleRequest(new Request(`${ADMIN}/api/overview`, { headers: await h.adminHeaders() }), broken, h.deps);
    expect(response.status).toBe(200);
    const keys = ((await response.json()) as Record<string, Record<string, unknown>>)["keys"]!;
    const service = keys["service"] as { name: string; configured: boolean; kid: string | null }[];
    expect(service.filter((k) => !k.configured).map((k) => k.name)).toEqual(["config", "invite", "ban"]);
    expect(service.find((k) => k.name === "config")).toEqual({ name: "config", configured: false, kid: null });
    expect(keys["offline"]).toEqual({ release: [], recovery: [], revoked: [], configured: false });
  });

  it("is read-only and makes no outbound call (only the Access key fetch)", async () => {
    const before = await rowCount("audit_log");
    await overview();
    expect(await rowCount("audit_log")).toBe(before);
    expect(h.calls.every((c) => c.url === `${TEAM}/cdn-cgi/access/certs`)).toBe(true);
  });
});
