// FR-ADM-05 (daily append-only audit export, truncation detection), FR-ONL-11 and NFR-SVC-07 (deletion and
// pseudonymisation), ADR-0011 (bans survive deletion; a revoked user's sign-in writes nothing about them).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { exportLinks, runAuditExport, verifyExportAgainstD1, type ExportedRow } from "../src/audit-export.ts";
import { sha256Hex } from "../src/lib/crypto.ts";
import { verifyChain } from "../src/db/audit.ts";
import { INACTIVE_ACCOUNT_MS, runRetention } from "../src/retention.ts";
import { DELETED_REDEEMER_NOTE } from "../src/routes/me.ts";
import { loadBanKey } from "../src/bans.ts";
import { verifyDocument } from "../src/signing/document.ts";
import { randomBytes } from "../src/lib/crypto.ts";
import { encodeBase64Url } from "../src/lib/base64url.ts";
import { handleRequest } from "../src/app.ts";
import { API, Harness, REDIRECT, interceptBatches, newDevice, resetDatabase, rowCount, trustedKeys } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
  const listed = await env.AUDIT_EXPORT.list();
  if (listed.objects.length > 0) await env.AUDIT_EXPORT.delete(listed.objects.map((o) => o.key));
});

interface ExportObject {
  kind: string;
  environment: string;
  previousHead: { seq: number; hash: string } | null;
  head: { seq: number; hash: string } | null;
  linksToPreviousHead: boolean | null;
  rows: ExportedRow[];
}

async function exports(): Promise<ExportObject[]> {
  const listed = await env.AUDIT_EXPORT.list({ prefix: "audit/test/" });
  const out: ExportObject[] = [];
  for (const o of listed.objects.sort((a, b) => a.key.localeCompare(b.key))) out.push((await (await env.AUDIT_EXPORT.get(o.key))!.json()) as ExportObject);
  return out;
}

describe("daily audit export", () => {
  it("appends the new rows with the chain head to the export bucket, run after run", async () => {
    await h.admin("POST", "/api/invites", {});
    await h.admin("PUT", "/api/settings", { deviceLimit: 4 });
    expect(await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now)).toMatchObject({ objects: 1, rows: 2, head: { seq: 2 } });
    await h.admin("PUT", "/api/settings", { deviceLimit: 5 });
    h.now += 86_400_000;
    expect(await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now)).toMatchObject({ objects: 1, rows: 1, head: { seq: 3 } });
    h.now += 86_400_000;
    expect(await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now)).toMatchObject({ objects: 1, rows: 0, head: { seq: 3 } });

    const [first, second, third] = (await exports()) as [ExportObject, ExportObject, ExportObject];
    expect(first).toMatchObject({ kind: "dbdp-audit-export", environment: "test", previousHead: null, linksToPreviousHead: null });
    expect(first.rows.map((r) => r.seq)).toEqual([1, 2]);
    expect(first.head).toEqual({ seq: 2, hash: first.rows[1]!.hash });
    expect(second.previousHead).toEqual(first.head);
    expect(second.rows[0]!.prevHash).toBe(first.head!.hash);
    expect(second.linksToPreviousHead).toBe(true);
    expect(third).toMatchObject({ head: second.head, rows: [] });
    // The exports link up on their own, and every exported row checks out against D1.
    expect(exportLinks([...first.rows, ...second.rows], null)).toBeNull();
    expect(exportLinks([first.rows[0]!, second.rows[0]!], null)).toBe(second.rows[0]!.seq);
    expect(await verifyExportAgainstD1(env.DB, [...first.rows, ...second.rows])).toBeNull();
    const forged = { ...second.rows[0]!, action: "settings.read" };
    expect(await verifyExportAgainstD1(env.DB, [...first.rows, forged])).toBe(forged.seq);
    expect(await verifyChain(env.DB)).toMatchObject({ ok: true, exportedHead: { seq: 3, present: true } });
  });

  it("exports no payloads: only the hash inputs and action metadata", async () => {
    await h.admin("PUT", "/api/settings", { banner: "Maintenance with Alice on Tuesday" });
    await h.admin("POST", "/api/invites", { note: "for Bob" });
    await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now);
    const [exported] = (await exports()) as [ExportObject];
    const text = JSON.stringify(exported);
    expect(text).not.toContain("Alice");
    expect(text).not.toContain("Bob");
    for (const row of exported.rows) expect(Object.keys(row).sort()).toEqual(["action", "actor", "afterDigest", "at", "beforeDigest", "hash", "prevHash", "seq", "target"]);
    expect(await verifyExportAgainstD1(env.DB, exported.rows)).toBeNull();
  });

  it("salts every digest with a per-row salt kept in D1: no plain SHA-256 to brute-force, still verifiable against D1", async () => {
    await h.admin("PUT", "/api/settings", { deviceLimit: 4 });
    await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now);
    const [exported] = (await exports()) as [ExportObject];
    const row = await env.DB.prepare("SELECT before_json, after_json, salt FROM audit_log WHERE seq = 1").first<{ before_json: string; after_json: string; salt: string }>();
    expect(row!.salt).toMatch(/^[0-9a-f]{32}$/);
    expect(exported.rows[0]!.afterDigest).not.toBe(await sha256Hex(row!.after_json));
    expect(exported.rows[0]!.beforeDigest).not.toBe(await sha256Hex(row!.before_json));
    expect(JSON.stringify(exported)).not.toContain(row!.salt);
    expect(await verifyExportAgainstD1(env.DB, exported.rows)).toBeNull();
    // A payload changed in D1 after the export no longer matches it.
    await env.DB.prepare("UPDATE audit_log SET after_json = '{\"deviceLimit\":9}' WHERE seq = 1").run();
    expect(await verifyExportAgainstD1(env.DB, exported.rows)).toBe(1);
  });

  it("makes truncating the newest rows detectable against the last export", async () => {
    for (let i = 0; i < 3; i++) await h.admin("PUT", "/api/settings", { deviceLimit: 4 + i });
    await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now);
    // Someone with database access removes the newest row; the remaining chain still links up by itself.
    await env.DB.prepare("DELETE FROM audit_log WHERE seq = 3").run();
    const report = (await (await h.admin("GET", "/api/audit/verify")).json()) as { chain: Record<string, unknown> };
    expect(report.chain).toMatchObject({ ok: false, brokenAtSeq: null, exportedHead: { seq: 3, present: false } });
  });

  it("is run by the daily cron before retention", async () => {
    await h.admin("PUT", "/api/settings", { deviceLimit: 4 });
    const worker = (await import("../src/index.ts")).default;
    const waits: Promise<unknown>[] = [];
    worker.scheduled({ scheduledTime: h.now, cron: "23 3 * * *", noRetry() {} } as ScheduledController, env, {
      waitUntil: (p: Promise<unknown>) => waits.push(p),
      passThroughOnException() {},
      props: {},
    } as unknown as ExecutionContext);
    await Promise.all(waits);
    const listed = await env.AUDIT_EXPORT.list({ prefix: "audit/production/" });
    expect(listed.objects).toHaveLength(1);
    await env.AUDIT_EXPORT.delete(listed.objects.map((o) => o.key));
  });
});

describe("deletion and pseudonymisation", () => {
  async function redeemedBy(discordId: string, note: string): Promise<{ inviteId: string; userId: string; accessToken: string }> {
    const created = (await (await h.admin("POST", "/api/invites", { note })).json()) as { invites: { id: string; code: string }[] };
    const { body } = await h.signIn(discordId, await newDevice());
    const accessToken = body["accessToken"] as string;
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: created.invites[0]!.code }, headers: { Authorization: `Bearer ${accessToken}` } })).status).toBe(200);
    return { inviteId: created.invites[0]!.id, userId: (body["user"] as { id: string }).id, accessToken };
  }

  it("replaces a redeemed invite's note and drops the link when the owner deletes the user", async () => {
    const { inviteId, userId } = await redeemedBy("661111111111111111", "for Alice from the Tuesday group");
    const unrelated = (await (await h.admin("POST", "/api/invites", { note: "spare code" })).json()) as { invites: { id: string }[] };
    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: userId })).status).toBe(200);
    const row = await env.DB.prepare("SELECT note, redeemed_by, redeemed_at FROM invites WHERE id = ?").bind(inviteId).first<Record<string, unknown>>();
    expect(row).toEqual({ note: DELETED_REDEEMER_NOTE, redeemed_by: null, redeemed_at: h.now });
    expect((await env.DB.prepare("SELECT note FROM invites WHERE id = ?").bind(unrelated.invites[0]!.id).first<{ note: string }>())!.note).toBe("spare code");
    const everything = JSON.stringify((await env.DB.prepare("SELECT * FROM audit_log").all()).results) + JSON.stringify((await env.DB.prepare("SELECT * FROM invites").all()).results);
    expect(everything).not.toContain("Alice");
  });

  it("does the same when the user deletes their own account", async () => {
    const { inviteId, accessToken } = await redeemedBy("662111111111111111", "for Bob");
    expect((await h.call("DELETE", `${API}/v1/me`, { headers: { Authorization: `Bearer ${accessToken}` } })).status).toBe(200);
    expect(await rowCount("invites", "id = ? AND note = ? AND redeemed_by IS NULL", inviteId, DELETED_REDEEMER_NOTE)).toBe(1);
  });
});

describe("bans survive deletion", () => {
  async function revokedUser(discordId: string): Promise<{ userId: string }> {
    const { body } = await h.signIn(discordId, await newDevice());
    const userId = (body["user"] as { id: string }).id;
    expect((await h.admin("POST", `/api/users/${userId}/revoke`)).status).toBe(200);
    return { userId };
  }

  it("keeps only a keyed hash of the Discord id and refuses the person at sign-in after the owner deletes them", async () => {
    const { userId } = await revokedUser("671111111111111111");
    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: userId })).status).toBe(200);
    expect(await rowCount("users")).toBe(0);
    expect(await rowCount("banned_subjects")).toBe(1);
    expect(JSON.stringify((await env.DB.prepare("SELECT * FROM banned_subjects").all()).results)).not.toContain("671111111111111111");

    const again = await h.signIn("671111111111111111", await newDevice());
    expect(again.status).toBe(403);
    expect(again.body["error"]).toBe("revoked");
    const revocation = await verifyDocument(again.body["revocation"] as string, "licence", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
    expect(JSON.parse(revocation.envelope!.payloadJson)).toMatchObject({ status: "revoked", features: [] });
    expect(await rowCount("users")).toBe(0);
  });

  it("keeps the ban through the 12-month purge of a revoked account", async () => {
    await revokedUser("672111111111111111");
    const report = await runRetention(env.DB, env.UPLOADS, loadBanKey(env.BAN_HMAC_KEY), h.now + INACTIVE_ACCOUNT_MS + 1);
    expect(report.accounts).toBe(1);
    expect(await rowCount("banned_subjects")).toBe(1);
    h.now += INACTIVE_ACCOUNT_MS + 2;
    expect((await h.signIn("672111111111111111", await newDevice())).status).toBe(403);
  });

  it("keeps bans in force when the invite key ring is rotated", async () => {
    const { userId } = await revokedUser("675111111111111111");
    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: userId })).status).toBe(200);
    const rotated = JSON.stringify({ current: "rotated-1", keys: { "rotated-1": encodeBase64Url(randomBytes(32)) } });
    const verifier = "r".repeat(43);
    const code = h.discordCode({ id: "675111111111111111" }, verifier);
    const device = await newDevice();
    const response = await handleRequest(
      new Request(`${API}/v1/auth/discord`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "CF-Connecting-IP": h.ip },
        body: JSON.stringify({ code, codeVerifier: verifier, redirectUri: REDIRECT, devicePublicKeyJwk: device.publicJwk }),
      }),
      { ...env, INVITE_HMAC_KEYS: rotated },
      h.deps,
    );
    expect(response.status).toBe(403);
    expect(((await response.json()) as Record<string, string>)["error"]).toBe("revoked");
    expect(await rowCount("users")).toBe(0);
  });

  it("deletes nothing when the user is revoked between the status read and the delete", async () => {
    const { body } = await h.signIn("676111111111111111", await newDevice());
    const userId = (body["user"] as { id: string }).id;
    const racing = interceptBatches(env.DB, {
      match: /DELETE FROM users/,
      before: async () => {
        await env.DB.prepare("UPDATE users SET status = 'revoked', revoked_at = ? WHERE id = ?").bind(h.now, userId).run();
      },
    });
    const headers = { ...(await h.adminHeaders()), "Content-Type": "application/json" };
    const response = await handleRequest(new Request(`https://admin.arcavale.com/api/users/${userId}`, { method: "DELETE", headers, body: JSON.stringify({ confirm: userId }) }), { ...env, DB: racing }, h.deps);
    expect(response.status).toBe(409);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "user_changed" });
    expect(await rowCount("users", "id = ? AND status = 'revoked'", userId)).toBe(1);
    expect(await rowCount("audit_log", "action = 'user.delete'")).toBe(0);
    expect(await rowCount("banned_subjects")).toBe(0);
    // Deleting again now that it is revoked deletes it and keeps the ban.
    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: userId })).status).toBe(200);
    expect(await rowCount("banned_subjects")).toBe(1);
  });

  it("the 12-month purge skips a user revoked while it runs, and records nothing for it", async () => {
    await h.signIn("677111111111111111", await newDevice());
    const racing = interceptBatches(env.DB, {
      match: /DELETE FROM users/,
      before: async () => {
        await env.DB.prepare("UPDATE users SET status = 'revoked' WHERE discord_id = '677111111111111111'").run();
      },
    });
    const report = await runRetention(racing, env.UPLOADS, loadBanKey(env.BAN_HMAC_KEY), h.now + INACTIVE_ACCOUNT_MS + 1);
    expect(report.accounts).toBe(0);
    expect(await rowCount("users")).toBe(1);
    expect(await rowCount("audit_log", "action = 'account.expire_inactive'")).toBe(0);
  });

  it("bans nobody whose account was active when deleted", async () => {
    const { body } = await h.signIn("673111111111111111", await newDevice());
    const userId = (body["user"] as { id: string }).id;
    expect((await h.admin("DELETE", `/api/users/${userId}`, { confirm: userId })).status).toBe(200);
    expect(await rowCount("banned_subjects")).toBe(0);
    expect((await h.signIn("673111111111111111", await newDevice())).status).toBe(200);
  });

  it("does not refresh a revoked user's last-seen time or name at sign-in", async () => {
    const { userId } = await revokedUser("674111111111111111");
    const before = await env.DB.prepare("SELECT last_seen_at, display_name FROM users WHERE id = ?").bind(userId).first();
    h.now += 30 * 86_400_000;
    expect((await h.signIn("674111111111111111", await newDevice(), "New Name")).status).toBe(403);
    expect(await env.DB.prepare("SELECT last_seen_at, display_name FROM users WHERE id = ?").bind(userId).first()).toEqual(before);
  });
});
