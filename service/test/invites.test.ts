// FR-ONL-02 (invite codes), FR-ONL-06 (access mode), QUALITY_GATES V3.5 (invite race test).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { CODE_ALPHABET, generateInviteCode, inviteCodeHash, normalizeInviteCode } from "../src/invites/codes.ts";
import { loadHmacKeyRing } from "../src/signing/keys.ts";
import { verifyDocument } from "../src/signing/document.ts";
import { handleRequest } from "../src/app.ts";
import { API, Harness, interceptBatches, newDevice, resetDatabase, rowCount, trustedKeys } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

async function createInvite(options: { expiresInDays?: number; note?: string } = {}): Promise<{ id: string; code: string }> {
  const response = await h.admin("POST", "/api/invites", options);
  expect(response.status).toBe(200);
  const body = (await response.json()) as { invites: { id: string; code: string }[] };
  return body.invites[0]!;
}

async function session(discordId: string): Promise<string> {
  const { body } = await h.signIn(discordId, await newDevice());
  return body["accessToken"] as string;
}

function redeem(accessToken: string, code: string): Promise<Response> {
  return h.call("POST", `${API}/v1/invites/redeem`, { body: { code }, headers: { Authorization: `Bearer ${accessToken}` } });
}

describe("invite codes", () => {
  it("are 16 unambiguous base-32 characters shown as XXXX-XXXX-XXXX-XXXX", () => {
    const seen = new Set<string>();
    for (let i = 0; i < 500; i++) {
      const code = generateInviteCode();
      expect(code).toMatch(/^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$/);
      seen.add(code);
    }
    expect(seen.size).toBe(500);
    expect(CODE_ALPHABET).toHaveLength(32);
    for (const ambiguous of ["I", "L", "O", "U"]) expect(CODE_ALPHABET).not.toContain(ambiguous);
  });

  it("use every symbol (80 bits from the CSPRNG, no bias towards part of the alphabet)", () => {
    const counts = new Map<string, number>();
    for (let i = 0; i < 2000; i++) for (const c of generateInviteCode().replace(/-/g, "")) counts.set(c, (counts.get(c) ?? 0) + 1);
    expect(counts.size).toBe(32);
    for (const n of counts.values()) expect(n).toBeGreaterThan(700);
  });

  it("normalise case, separators and look-alikes", () => {
    expect(normalizeInviteCode("abcd-efgh-jkmn-pqrs")).toBe("ABCDEFGHJKMNPQRS");
    expect(normalizeInviteCode(" o0il-1111 2222-3333 ")).toBe("0011111122223333");
    expect(normalizeInviteCode("ABCD-EFGH-JKMN-PQR")).toBeNull();
    expect(normalizeInviteCode("ABCD-EFGH-JKMN-PQRU")).toBeNull();
  });

  it("are stored only as an HMAC under a key id", async () => {
    const invite = await createInvite();
    const row = await env.DB.prepare("SELECT * FROM invites WHERE id = ?").bind(invite.id).first<Record<string, unknown>>();
    expect(JSON.stringify(row)).not.toContain(invite.code.replace(/-/g, ""));
    expect(JSON.stringify(row)).not.toContain(invite.code);
    const ring = loadHmacKeyRing(env.INVITE_HMAC_KEYS);
    expect(row!["hmac_kid"]).toBe(ring.currentKid);
    expect(row!["code_hash"]).toBe(await inviteCodeHash(ring.keys.get(ring.currentKid)!, normalizeInviteCode(invite.code)!));
    expect(JSON.stringify(await env.DB.prepare("SELECT * FROM audit_log").all())).not.toContain(invite.code);
  });
});

describe("POST /v1/invites/redeem", () => {
  it("grants access and returns a licence", async () => {
    const invite = await createInvite();
    const token = await session("111111111111111111");
    const response = await redeem(token, invite.code.toLowerCase());
    expect(response.status).toBe(200);
    const body = (await response.json()) as Record<string, unknown>;
    expect(body).toMatchObject({ access: "granted", alreadyHadAccess: false });
    expect((await verifyDocument(body["licence"] as string, "licence", "stable", { keys: await trustedKeys(), now: new Date(h.now) })).outcome).toBe("Valid");
    const row = await env.DB.prepare("SELECT redeemed_by, redeemed_at FROM invites WHERE id = ?").bind(invite.id).first<{ redeemed_by: string; redeemed_at: number }>();
    const user = await env.DB.prepare("SELECT id, access_via FROM users").first<{ id: string; access_via: string }>();
    expect(row).toEqual({ redeemed_by: user!.id, redeemed_at: h.now });
    expect(user!.access_via).toBe("invite");
    expect(await rowCount("audit_log", "action = 'invite.redeem' AND target = ?", `invite:${invite.id}`)).toBe(1);
  });

  it("is single-use", async () => {
    const invite = await createInvite();
    expect((await redeem(await session("211111111111111111"), invite.code)).status).toBe(200);
    const second = await redeem(await session("311111111111111111"), invite.code);
    expect(second.status).toBe(400);
    expect(await second.json()).toEqual({ schemaVersion: 1, error: "invite_invalid" });
    expect(await rowCount("audit_log", "action = 'invite.redeem_failed' AND after_json = ?", '{"reason":"used"}')).toBe(1);
  });

  it("lets exactly one of many concurrent redemptions of one code win (race test)", async () => {
    const invite = await createInvite();
    const tokens: string[] = [];
    for (let i = 0; i < 6; i++) tokens.push(await session(`41111111111111111${i}`));
    const results = await Promise.all(tokens.map((t) => redeem(t, invite.code)));
    expect(results.filter((r) => r.status === 200)).toHaveLength(1);
    expect(results.filter((r) => r.status === 400)).toHaveLength(5);
    expect(await rowCount("users", "access_granted_at IS NOT NULL")).toBe(1);
    expect(await rowCount("invites", "redeemed_by IS NOT NULL")).toBe(1);
  });

  it("does not burn a code for an account that already has access", async () => {
    const first = await createInvite();
    const second = await createInvite();
    const token = await session("511111111111111111");
    expect((await redeem(token, first.code)).status).toBe(200);
    const again = await redeem(token, second.code);
    expect(again.status).toBe(200);
    expect(await again.json()).toMatchObject({ alreadyHadAccess: true });
    expect(await rowCount("invites", "id = ? AND redeemed_at IS NULL", second.id)).toBe(1);
  });

  it("does not burn a second code when one account redeems two at once", async () => {
    const first = await createInvite();
    const second = await createInvite();
    const token = await session("521111111111111111");
    const results = await Promise.all([redeem(token, first.code), redeem(token, second.code)]);
    expect(results.map((r) => r.status)).toEqual([200, 200]);
    expect(await rowCount("invites", "redeemed_at IS NOT NULL")).toBe(1);
  });

  async function redeemWhileOwnerRevokes(when: "before" | "after"): Promise<{ response: Response; inviteId: string }> {
    const invite = await createInvite();
    const { body } = await h.signIn("651111111111111111", await newDevice());
    const userId = (body["user"] as { id: string }).id;
    const revoke = async () => {
      await env.DB.batch([
        env.DB.prepare("UPDATE users SET status = 'revoked', revoked_at = ? WHERE id = ?").bind(h.now, userId),
        env.DB.prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'user_revoked' WHERE user_id = ?").bind(h.now, userId),
      ]);
    };
    const db = interceptBatches(env.DB, { match: /UPDATE invites SET redeemed_by/, [when]: revoke });
    const request = new Request(`${API}/v1/invites/redeem`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "CF-Connecting-IP": h.ip, Authorization: `Bearer ${body["accessToken"] as string}` },
      body: JSON.stringify({ code: invite.code }),
    });
    return { response: await handleRequest(request, { ...env, DB: db }, h.deps), inviteId: invite.id };
  }

  it("does not burn the code or grant a licence when the owner revokes the user during redemption", async () => {
    const { response, inviteId } = await redeemWhileOwnerRevokes("before");
    expect(response.status).toBe(403);
    const body = (await response.json()) as Record<string, string>;
    expect(body["error"]).toBe("revoked");
    expect(body).not.toHaveProperty("licence");
    const revocation = await verifyDocument(body["revocation"]!, "licence", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
    expect(JSON.parse(revocation.envelope!.payloadJson)).toMatchObject({ status: "revoked", features: [] });
    expect(await rowCount("invites", "id = ? AND redeemed_at IS NULL AND redeemed_by IS NULL", inviteId)).toBe(1);
    expect(await rowCount("users", "access_granted_at IS NOT NULL")).toBe(0);
  });

  it("reads the status again before issuing a licence (revoked just after the grant)", async () => {
    const { response } = await redeemWhileOwnerRevokes("after");
    expect(response.status).toBe(403);
    const body = (await response.json()) as Record<string, string>;
    expect(body["error"]).toBe("revoked");
    expect(body).not.toHaveProperty("licence");
  });

  it("refuses expired and revoked codes", async () => {
    const expiring = await createInvite({ expiresInDays: 1 });
    const revoked = await createInvite();
    expect((await h.admin("POST", `/api/invites/${revoked.id}/revoke`)).status).toBe(200);
    h.now += 24 * 60 * 60 * 1000;
    const token = await session("611111111111111111");
    expect((await redeem(token, expiring.code)).status).toBe(400);
    expect((await redeem(token, revoked.code)).status).toBe(400);
    expect(await rowCount("audit_log", "action = 'invite.redeem_failed' AND after_json = ?", '{"reason":"expired"}')).toBe(1);
    expect(await rowCount("audit_log", "action = 'invite.redeem_failed' AND after_json = ?", '{"reason":"revoked"}')).toBe(1);
    expect(await rowCount("users", "access_granted_at IS NOT NULL")).toBe(0);
  });

  it("keeps codes made under an older HMAC key redeemable after rotation", async () => {
    const ring = loadHmacKeyRing(env.INVITE_HMAC_KEYS);
    const oldKid = [...ring.keys.keys()].find((k) => k !== ring.currentKid)!;
    const code = generateInviteCode();
    await env.DB.prepare("INSERT INTO invites (id, hmac_kid, code_hash, created_at, created_by, expires_at) VALUES (?, ?, ?, ?, 'test', ?)")
      .bind(crypto.randomUUID(), oldKid, await inviteCodeHash(ring.keys.get(oldKid)!, normalizeInviteCode(code)!), h.now, h.now + 86_400_000)
      .run();
    expect((await redeem(await session("711111111111111111"), code)).status).toBe(200);
  });

  it("in public mode grants access without using the code", async () => {
    const invite = await createInvite();
    const token = await session("811111111111111111");
    expect((await h.admin("PUT", "/api/settings", { accessMode: "public" })).status).toBe(200);
    const response = await redeem(token, invite.code);
    expect(response.status).toBe(200);
    expect(await rowCount("invites", "redeemed_at IS NULL")).toBe(1);
    expect(await rowCount("users", "access_via = 'public'")).toBe(1);
  });

  it("leaves existing users unaffected when switching back to invite-only", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();
    expect((await h.signIn("911111111111111111", device)).body["access"]).toBe("granted");
    await h.admin("PUT", "/api/settings", { accessMode: "invite-only" });
    expect((await h.signIn("911111111111111111", device)).body["access"]).toBe("granted");
    expect((await h.signIn("911111111111111112", await newDevice())).body["access"]).toBe("invite_required");
  });

  it("validates input and needs a session", async () => {
    const token = await session("121111111111111111");
    expect((await redeem(token, "short")).status).toBe(400);
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" } })).status).toBe(401);
    const extra = await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS", x: 1 }, headers: { Authorization: `Bearer ${token}` } });
    expect(extra.status).toBe(400);
  });
});
