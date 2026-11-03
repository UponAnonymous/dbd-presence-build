// POST /v1/invites/redeem (FR-ONL-02, FR-ONL-06, PLAN_V2 §3.2).
// Redemption is one atomic UPDATE that sets `redeemed_by` and succeeds only when exactly one row changed. It runs in
// the same transaction as the access grant, and it cannot burn a code for an account that already has access.
// Rate limits (src/ratelimit.ts): the per-IP counter first, then the session's account is checked against its cap.
// The account and global counters are counted only once the presented code is redeemable (unused, unrevoked,
// unexpired) or public mode grants access; a redeemable code counts on the shared cap, a public-mode grant on the guest
// cap (TM-07), so neither throwaway accounts nor public-mode grants can starve invite redemption. Unknown, used, revoked and expired codes are refused; their failed attempt
// is counted on the account and audited while the failed-attempts ceiling has room. So an account holding no valid
// invite cannot exhaust redemption for everyone.

import { issueLicence } from "../auth/licence.ts";
import { requireSession } from "../auth/session.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import { getSettings } from "../db/settings.ts";
import { HttpError, clientIp, json, readJsonBody } from "../http.ts";
import { candidateHashes, normalizeInviteCode } from "../invites/codes.ts";
import { object, string } from "../lib/validate.ts";
import { loadHmacKeyRing, loadSigningKey } from "../signing/keys.ts";
import type { RateLimiter } from "../ratelimit.ts";
import { limiterFor, revokedResponse } from "./common.ts";

/**
 * Answers with an active licence, after reading the account's status again: an owner may have revoked it while this
 * request was running, and a licence is never issued to a revoked account (it gets the signed revocation instead).
 */
async function granted(ctx: Context, userId: string, thumbprint: string, alreadyHadAccess: boolean): Promise<Response> {
  const user = await ctx.env.DB.prepare("SELECT status FROM users WHERE id = ?").bind(userId).first<{ status: "active" | "revoked" }>();
  if (!user) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
  if (user.status === "revoked") return revokedResponse(ctx, userId, thumbprint);
  return json({ access: "granted", alreadyHadAccess, licence: await issueLicence(ctx.env, ctx.now, userId, thumbprint, "active") });
}

type FailureReason = "unknown" | "used" | "revoked" | "expired";

async function failureReason(db: D1Database, match: string, binds: string[], now: number): Promise<{ reason: FailureReason; inviteId: string | null }> {
  const row = await db
    .prepare(`SELECT id, redeemed_at, revoked_at, expires_at FROM invites WHERE ${match}`)
    .bind(...binds)
    .first<{ id: string; redeemed_at: number | null; revoked_at: number | null; expires_at: number }>();
  if (!row) return { reason: "unknown", inviteId: null };
  if (row.revoked_at !== null) return { reason: "revoked", inviteId: row.id };
  if (row.redeemed_at !== null) return { reason: "used", inviteId: row.id };
  if (row.expires_at <= now) return { reason: "expired", inviteId: row.id };
  return { reason: "used", inviteId: row.id };
}

export async function redeem(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  const body = object(await readJsonBody(ctx.request, 1024), ["code"]);
  const code = normalizeInviteCode(string(body["code"], { max: 32 }));
  if (code === null) throw new HttpError(400, "invalid_request");

  const { env } = ctx;
  await loadSigningKey(env.LICENCE_SIGNING_KEY);
  const ring = loadHmacKeyRing(env.INVITE_HMAC_KEYS);
  const limiter = limiterFor(ctx);
  await limiter.ip("redeem", clientIp(ctx.request));
  // The session's account is a verified principal: refused now if it is at its cap (read only).
  await limiter.checkAccount("redeem", session.userId);

  if (session.status === "revoked") return revokedResponse(ctx, session.userId, session.thumbprint);
  if (session.hasAccess) return granted(ctx, session.userId, session.thumbprint, true);

  const db = env.DB;
  const now = ctx.now;
  const settings = await getSettings(db);
  if (settings.accessMode === "public") {
    // No invite code is involved, so this is counted on the guest cap: it can never use up invite redemptions (TM-07).
    await limiter.verified("redeem", session.userId, "guest");
    await db.prepare("UPDATE users SET access_granted_at = ?, access_via = 'public' WHERE id = ? AND access_granted_at IS NULL").bind(now, session.userId).run();
    return granted(ctx, session.userId, session.thumbprint, false);
  }

  const candidates = await candidateHashes(ring, code);
  const match = candidates.map(() => "(hmac_kid = ? AND code_hash = ?)").join(" OR ");
  const binds = candidates.flatMap((c) => [c.kid, c.hash]);
  const redeemable = await db
    .prepare(`SELECT id FROM invites WHERE (${match}) AND redeemed_at IS NULL AND revoked_at IS NULL AND expires_at > ?`)
    .bind(...binds, now)
    .first<{ id: string }>();
  if (!redeemable) return refuse(db, limiter, session.userId, match, binds, now);
  // A code the owner issued: the shared cap, which accounts without a redeemable code cannot reach.
  await limiter.verified("redeem", session.userId, "member");
  // One transaction. Both statements carry the same conditions on the account (active, no access yet), so they
  // succeed or fail together; the code is used up only when access is granted with it.
  const accountReady = "EXISTS (SELECT 1 FROM users WHERE id = ? AND status = 'active' AND access_granted_at IS NULL)";
  const [redeemed, grant] = await db.batch([
    db
      .prepare(`UPDATE invites SET redeemed_by = ?, redeemed_at = ? WHERE id = ? AND redeemed_at IS NULL AND revoked_at IS NULL AND expires_at > ? AND ${accountReady}`)
      .bind(session.userId, now, redeemable.id, now, session.userId),
    db
      .prepare(
        "UPDATE users SET access_granted_at = ?, access_via = 'invite' WHERE id = ? AND status = 'active' AND access_granted_at IS NULL " +
          "AND EXISTS (SELECT 1 FROM invites WHERE id = ? AND redeemed_by = ? AND redeemed_at = ?)",
      )
      .bind(now, session.userId, redeemable.id, session.userId, now),
  ]);

  if (redeemed!.meta.changes === 1 && grant!.meta.changes === 1) {
    await writeAudited(db, now, { actor: `user:${session.userId}`, action: "invite.redeem", target: `invite:${redeemable.id}` });
    return granted(ctx, session.userId, session.thumbprint, false);
  }
  if (redeemed!.meta.changes === 1) {
    // The code was marked used but access was not granted. The shared conditions make this unreachable; should it
    // happen anyway, the code is given back rather than burnt.
    await db
      .prepare("UPDATE invites SET redeemed_by = NULL, redeemed_at = NULL WHERE id = ? AND redeemed_by = ? AND redeemed_at = ?")
      .bind(redeemable.id, session.userId, now)
      .run();
  }

  // Not granted. Find out why from the account first: revoked meanwhile, or access granted by a concurrent request.
  const after = await db.prepare("SELECT status, access_granted_at FROM users WHERE id = ?").bind(session.userId).first<{ status: string; access_granted_at: number | null }>();
  if (!after) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
  if (after.status === "revoked") return revokedResponse(ctx, session.userId, session.thumbprint);
  if (after.access_granted_at !== null) return granted(ctx, session.userId, session.thumbprint, true);
  if (redeemed!.meta.changes === 1) throw new HttpError(409, "redeem_conflict");
  return refuse(db, limiter, session.userId, match, binds, now);
}

/**
 * An unknown, used, revoked or expired code: the failed attempt is counted on the account's failure counter first
 * (atomic conditional upsert; at its cap this throws 429 and nothing is audited), then audited and refused. So the
 * audit rows stay within the per-account cap even under concurrent requests.
 */
async function refuse(db: D1Database, limiter: RateLimiter, userId: string, match: string, binds: string[], now: number): Promise<never> {
  const failure = await failureReason(db, match, binds, now);
  await limiter.failure("redeem", userId);
  try {
    await writeAudited(db, now, {
      actor: `user:${userId}`,
      action: "invite.redeem_failed",
      target: failure.inviteId ? `invite:${failure.inviteId}` : null,
      after: { reason: failure.reason },
    });
  } catch (error) {
    // The refusal does not depend on it; should the chain keep conflicting, the failure is logged by type.
    console.error(JSON.stringify({ event: "audit_write_failed", action: "invite.redeem_failed", type: error instanceof Error ? error.name : typeof error }));
  }
  throw new HttpError(400, "invite_invalid");
}
