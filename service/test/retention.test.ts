// NFR-SVC-07 (retention: counters 24 hours, audit 90 days, inactive accounts 12 months).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { verifyChain } from "../src/db/audit.ts";
import { runAuditExport } from "../src/audit-export.ts";
import { AUDIT_RETENTION_MS, INACTIVE_ACCOUNT_MS, RATE_LIMIT_RETENTION_MS, runRetention } from "../src/retention.ts";
import { loadBanKey } from "../src/bans.ts";

const ring = () => loadBanKey(env.BAN_HMAC_KEY);
import { Harness, newDevice, resetDatabase, rowCount } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

describe("retention job", () => {
  it("deletes rate-limit counters after 24 hours and used nonces after their window", async () => {
    await env.DB.batch([
      env.DB.prepare("INSERT INTO rate_limits (bucket, window_start, count) VALUES ('auth:global', ?, 1), ('auth:global', ?, 1)").bind(h.now - RATE_LIMIT_RETENTION_MS - 1, h.now - 1000),
      env.DB.prepare("INSERT INTO proof_nonces (nonce, expires_at) VALUES ('old-nonce-aaaaaaaaaa', ?), ('new-nonce-aaaaaaaaaa', ?)").bind(h.now - 1, h.now + 1000),
    ]);
    const report = await runRetention(env.DB, env.UPLOADS, ring(), h.now);
    expect(report).toMatchObject({ rateLimits: 1, nonces: 1 });
    expect(await rowCount("rate_limits")).toBe(1);
    expect(await rowCount("proof_nonces")).toBe(1);
  });

  it("deletes audit rows after 90 days and the remaining chain still verifies (anchored)", async () => {
    const start = h.now;
    for (let i = 0; i < 3; i++) {
      h.now = start + i * 30 * 86_400_000;
      await h.admin("PUT", "/api/settings", { deviceLimit: 4 + i });
    }
    // Nothing is deleted before it has been exported.
    expect((await runRetention(env.DB, env.UPLOADS, ring(), start + AUDIT_RETENTION_MS + 1)).auditRows).toBe(0);
    await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now);
    const report = await runRetention(env.DB, env.UPLOADS, ring(), start + AUDIT_RETENTION_MS + 1);
    expect(report.auditRows).toBe(1);
    expect(await verifyChain(env.DB)).toMatchObject({ ok: true, checked: 2, brokenAtSeq: null, anchored: true, exportedHead: { seq: 3, present: true } });
  });

  it("deletes accounts inactive for 12 months, with a pseudonymous audit row", async () => {
    const { body } = await h.signIn("221111111111111111", await newDevice(), "Laurie");
    await h.signIn("231111111111111111", await newDevice());
    await env.DB.prepare("UPDATE users SET last_seen_at = ? WHERE discord_id = '221111111111111111'").bind(h.now - INACTIVE_ACCOUNT_MS - 1).run();
    const report = await runRetention(env.DB, env.UPLOADS, ring(), h.now);
    expect(report.accounts).toBe(1);
    expect(await rowCount("users")).toBe(1);
    const audit = await env.DB.prepare("SELECT actor, action, target FROM audit_log WHERE action = 'account.expire_inactive'").first();
    expect(audit).toEqual({ actor: "system", action: "account.expire_inactive", target: `user:${(body["user"] as { id: string }).id}` });
    expect(JSON.stringify((await env.DB.prepare("SELECT * FROM audit_log").all()).results)).not.toContain("Laurie");
  });

  it("removes expired refresh tokens and long-revoked sessions", async () => {
    const device = await newDevice();
    const { body } = await h.signIn("241111111111111111", device);
    await h.call("POST", "https://api.arcavale.com/v1/auth/logout", { headers: { Authorization: `Bearer ${body["accessToken"] as string}` } });
    const report = await runRetention(env.DB, env.UPLOADS, ring(), h.now + 91 * 86_400_000);
    expect(report.families).toBe(1);
    expect(await rowCount("token_families")).toBe(0);
    expect(await rowCount("refresh_tokens")).toBe(0);
  });
});
