// POST /v1/auth/discord, /v1/auth/refresh, /v1/auth/logout (FR-ONL-01, FR-ONL-03, FR-ONL-04, PLAN_V2 §3.1 and §3.3).
// Refresh contract (ADR-0011): each refresh rotates the token; presenting a rotated token again revokes the family,
// except as an idempotent retry within REFRESH_RETRY_GRACE_MS (a new proof, the successor still unused), which returns
// the same successor pair.

import { NONCE_PATTERN, PROOF_MAX_SKEW_SECONDS, importDeviceKey, parseDevicePublicKey, proofMessage, verifyProofSignature } from "../auth/device.ts";
import { exchangeDiscordCode, isAllowedRedirectUri } from "../auth/discord.ts";
import { isBanned, loadBanKey } from "../bans.ts";
import { issueLicence } from "../auth/licence.ts";
import { REFRESH_TOKEN_PATTERN, newTokens, requireSession, rotatedTokens, tokenResponseFields, type IssuedTokens } from "../auth/session.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import { getSettings } from "../db/settings.ts";
import { HttpError, clientIp, json, readJsonBody } from "../http.ts";
import { newId, sha256Base64Url } from "../lib/crypto.ts";
import { integer, object, string } from "../lib/validate.ts";
import type { GlobalPool, RateLimiter } from "../ratelimit.ts";
import { loadHmacKeyRing, loadSigningKey } from "../signing/keys.ts";
import { countingFailures, limiterFor, revokedResponse } from "./common.ts";

const PKCE_VERIFIER = /^[A-Za-z0-9._~-]{43,128}$/;
const AUTH_CODE = /^[A-Za-z0-9._~-]{1,256}$/;
const NONCE_TTL_MS = 2 * PROOF_MAX_SKEW_SECONDS * 1000;

interface UserRow {
  id: string;
  status: "active" | "revoked";
  access_granted_at: number | null;
}

function accessState(hasAccess: boolean): "granted" | "invite_required" {
  return hasAccess ? "granted" : "invite_required";
}

function isForeignKeyFailure(error: unknown): boolean {
  return error instanceof Error && /FOREIGN KEY constraint failed/.test(error.message);
}

export async function signIn(ctx: Context): Promise<Response> {
  const body = object(await readJsonBody(ctx.request, 4096), ["code", "codeVerifier", "redirectUri", "devicePublicKeyJwk"]);
  const code = string(body["code"], { max: 256, pattern: AUTH_CODE });
  const codeVerifier = string(body["codeVerifier"], { min: 43, max: 128, pattern: PKCE_VERIFIER });
  const redirectUri = string(body["redirectUri"], { max: 64 });
  if (!isAllowedRedirectUri(redirectUri)) throw new HttpError(400, "invalid_redirect_uri");
  const device = await parseDevicePublicKey(body["devicePublicKeyJwk"]);

  const { env } = ctx;
  if (!env.DISCORD_CLIENT_ID || !/^\d{15,22}$/.test(env.DISCORD_CLIENT_ID) || !env.DISCORD_CLIENT_SECRET) throw new HttpError(503, "not_configured");
  await loadSigningKey(env.LICENCE_SIGNING_KEY);
  const limiter = limiterFor(ctx);
  await limiter.ip("auth", clientIp(ctx.request));

  // Discord accepted the code: a verified principal from here, limited by its own account counter (read now, counted
  // with the global counter once the rest of the check below passed).
  const identity = await exchangeDiscordCode(ctx.deps.fetch, env.DISCORD_CLIENT_ID, env.DISCORD_CLIENT_SECRET, code, codeVerifier, redirectUri);
  await limiter.checkAccount("auth", identity.id);
  return countingFailures(limiter, "auth", identity.id, async () => {
    // The rest of the credential check, read-only: a revoked account or a revoked device is answered before the global
    // counter moves and before anything about the account is written (a revoked user's last-seen time stays frozen).
    const db = env.DB;
    const known = await db.prepare("SELECT id, status, access_granted_at FROM users WHERE discord_id = ?").bind(identity.id).first<UserRow>();
    if (known?.status === "revoked") return revokedResponse(ctx, known.id, device.thumbprint);
    // A ban outlives the account (src/bans.ts). No row is created for a banned person; the signed revocation names a
    // random id that matches no account.
    if (await isBanned(db, loadBanKey(env.BAN_HMAC_KEY), identity.id)) return revokedResponse(ctx, known?.id ?? newId(), device.thumbprint);
    if (known) {
      const revokedDevice = await db
        .prepare("SELECT 1 AS revoked FROM devices WHERE user_id = ? AND thumbprint = ? AND revoked_at IS NOT NULL")
        .bind(known.id, device.thumbprint)
        .first<{ revoked: number }>();
      if (revokedDevice) throw new HttpError(403, "device_revoked");
    }
    // An account without access (never invited, or not redeemed yet) is counted on the small guest cap, so throwaway
    // Discord accounts cannot use up the sign-ins of the people the owner let in (TM-07, src/ratelimit.ts).
    const settings = await getSettings(db);
    const member = (known?.access_granted_at ?? null) !== null || settings.accessMode === "public";
    await limiter.verified("auth", identity.id, member ? "member" : "guest");

    const now = ctx.now;
    const grantPublic = settings.accessMode === "public" ? now : null;
    await db
      .prepare(
        "INSERT INTO users (id, discord_id, display_name, status, access_granted_at, access_via, created_at, last_seen_at) VALUES (?, ?, ?, 'active', ?, ?, ?, ?) " +
          "ON CONFLICT (discord_id) DO UPDATE SET " +
          "display_name = CASE WHEN users.status = 'active' THEN excluded.display_name ELSE users.display_name END, " +
          "last_seen_at = CASE WHEN users.status = 'active' THEN excluded.last_seen_at ELSE users.last_seen_at END, " +
          "access_granted_at = CASE WHEN users.status = 'active' THEN COALESCE(users.access_granted_at, excluded.access_granted_at) ELSE users.access_granted_at END, " +
          "access_via = CASE WHEN users.status = 'active' THEN COALESCE(users.access_via, excluded.access_via) ELSE users.access_via END",
      )
      .bind(newId(), identity.id, identity.displayName, grantPublic, grantPublic === null ? null : "public", now, now)
      .run();
    const user = await db.prepare("SELECT id, status, access_granted_at FROM users WHERE discord_id = ?").bind(identity.id).first<UserRow>();
    if (!user) throw new HttpError(500, "internal");
    if (user.status === "revoked") return revokedResponse(ctx, user.id, device.thumbprint);

    const existing = await db
      .prepare("SELECT id, revoked_at FROM devices WHERE user_id = ? AND thumbprint = ?")
      .bind(user.id, device.thumbprint)
      .first<{ id: string; revoked_at: number | null }>();
    if (existing?.revoked_at != null) throw new HttpError(403, "device_revoked");

    const tokens = await newTokens(now);
    const familyId = newId();
    const deviceId = existing?.id ?? newId();
    const statements: D1PreparedStatement[] = [];
    if (existing) {
      statements.push(db.prepare("UPDATE devices SET last_seen_at = ? WHERE id = ?").bind(now, deviceId));
      statements.push(
        db.prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'replaced' WHERE device_id = ? AND revoked_at IS NULL").bind(now, deviceId),
      );
    } else {
      // Inserted only while the user is under the device limit; otherwise the family insert below fails its foreign key
      // and the whole batch rolls back, so two concurrent sign-ins cannot both slip past the limit.
      statements.push(
        db
          .prepare(
            "INSERT INTO devices (id, user_id, public_key_jwk, thumbprint, created_at, last_seen_at) " +
              "SELECT ?, ?, ?, ?, ?, ? WHERE (SELECT COUNT(*) FROM devices WHERE user_id = ? AND revoked_at IS NULL) < ?",
          )
          .bind(deviceId, user.id, device.jwkJson, device.thumbprint, now, now, user.id, settings.deviceLimit),
      );
    }
    statements.push(
      db
        .prepare("INSERT INTO token_families (id, user_id, device_id, access_token_hash, access_expires_at, created_at) VALUES (?, ?, ?, ?, ?, ?)")
        .bind(familyId, user.id, deviceId, tokens.accessTokenHash, tokens.accessExpiresAt, now),
      db
        .prepare("INSERT INTO refresh_tokens (token_hash, family_id, created_at, expires_at) VALUES (?, ?, ?, ?)")
        .bind(tokens.refreshTokenHash, familyId, now, tokens.refreshExpiresAt),
    );
    try {
      await db.batch(statements);
    } catch (error) {
      if (!existing && isForeignKeyFailure(error)) throw new HttpError(409, "device_limit");
      throw error;
    }

    const hasAccess = user.access_granted_at !== null;
    return json({
      ...tokenResponseFields(tokens),
      access: accessState(hasAccess),
      licence: hasAccess ? await issueLicence(env, now, user.id, device.thumbprint, "active") : null,
      user: { id: user.id, displayName: identity.displayName },
      device: { id: deviceId, thumbprint: device.thumbprint },
    });
  });
}

/**
 * How long after a rotation the rotated token may be presented again as a retry (a lost response, or two refreshes of
 * the same token racing). Inside it, with the successor still unused, the same successor pair is returned; anything
 * else is reuse and revokes the family.
 */
export const REFRESH_RETRY_GRACE_MS = 30_000;

interface RefreshRow {
  token_hash: string;
  family_id: string;
  expires_at: number;
  used_at: number | null;
  replaced_by: string | null;
  family_revoked_at: number | null;
  user_id: string;
  device_id: string;
  public_key_jwk: string;
  thumbprint: string;
  device_revoked_at: number | null;
  status: "active" | "revoked";
  access_granted_at: number | null;
}

interface Proof {
  timestamp: number;
  nonce: string;
  signature: string;
}

function loadRefreshRow(db: D1Database, tokenHash: string): Promise<RefreshRow | null> {
  return db
    .prepare(
      "SELECT r.token_hash, r.family_id, r.expires_at, r.used_at, r.replaced_by, f.revoked_at AS family_revoked_at, f.user_id, f.device_id, " +
        "d.public_key_jwk, d.thumbprint, d.revoked_at AS device_revoked_at, u.status, u.access_granted_at " +
        "FROM refresh_tokens r JOIN token_families f ON f.id = r.family_id JOIN devices d ON d.id = f.device_id JOIN users u ON u.id = f.user_id " +
        "WHERE r.token_hash = ?",
    )
    .bind(tokenHash)
    .first<RefreshRow>();
}

/**
 * Revokes the family first, in its own statement, so a failing audit write can never leave it live; then writes the
 * audit row, always, from the call that actually revoked it (so at most once per family). Should the audit write itself
 * fail (the chain kept conflicting), that is logged by type and the revocation stands.
 */
async function revokeFamilyForReuse(ctx: Context, row: RefreshRow): Promise<never> {
  const db = ctx.env.DB;
  const revoked = await db
    .prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'refresh_token_reused' WHERE id = ? AND revoked_at IS NULL")
    .bind(ctx.now, row.family_id)
    .run();
  if (revoked.meta.changes === 1) {
    try {
      await writeAudited(db, ctx.now, { actor: "system", action: "session.refresh_token_reused", target: `user:${row.user_id}`, after: { family: row.family_id } });
    } catch (error) {
      console.error(JSON.stringify({ event: "audit_write_failed", action: "session.refresh_token_reused", type: error instanceof Error ? error.name : typeof error }));
    }
  }
  throw new HttpError(401, "refresh_token_reused");
}

/** Timestamp window and signature of a refresh proof (no state touched). */
async function proofIsValid(ctx: Context, row: RefreshRow, tokenHash: string, proof: Proof): Promise<boolean> {
  if (Math.abs(Math.floor(ctx.now / 1000) - proof.timestamp) > PROOF_MAX_SKEW_SECONDS) return false;
  const deviceKey = await importDeviceKey(row.public_key_jwk);
  return verifyProofSignature(deviceKey, proofMessage(tokenHash, proof.timestamp, proof.nonce), proof.signature);
}

/** Throws 401 `proof_replayed` when the nonce was seen before (read only, so a replay writes nothing). */
async function refuseSeenNonce(ctx: Context, nonce: string): Promise<void> {
  const seen = await ctx.env.DB.prepare("SELECT 1 AS seen FROM proof_nonces WHERE nonce = ?").bind(nonce).first<{ seen: number }>();
  if (seen) throw new HttpError(401, "proof_replayed");
}

/** Consumes the proof nonce, or throws 401 `proof_replayed` when a concurrent request consumed it first. */
async function consumeNonce(ctx: Context, nonce: string): Promise<void> {
  const inserted = await ctx.env.DB.prepare("INSERT INTO proof_nonces (nonce, expires_at) VALUES (?, ?) ON CONFLICT (nonce) DO NOTHING")
    .bind(nonce, ctx.now + NONCE_TTL_MS)
    .run();
  if (inserted.meta.changes !== 1) throw new HttpError(401, "proof_replayed");
}

/** Accounts without access refresh on the guest cap, so they never use up the daily refreshes of users with access (TM-07). */
function refreshPool(row: RefreshRow): GlobalPool {
  return row.access_granted_at !== null ? "member" : "guest";
}

function insideRetryWindow(row: RefreshRow, now: number): boolean {
  return row.used_at !== null && row.replaced_by !== null && row.used_at <= now && now - row.used_at < REFRESH_RETRY_GRACE_MS;
}

/**
 * The pair already issued when `refreshToken` was rotated, re-derived (src/auth/session.ts), or null when the stored
 * successor is not that pair, has been used since, or its family is no longer live.
 */
async function issuedSuccessor(ctx: Context, row: RefreshRow, refreshToken: string): Promise<IssuedTokens | null> {
  const ring = loadHmacKeyRing(ctx.env.INVITE_HMAC_KEYS);
  for (const key of ring.keys.values()) {
    const tokens = await rotatedTokens(key, refreshToken, ctx.now);
    if (tokens.refreshTokenHash !== row.replaced_by) continue;
    const successor = await ctx.env.DB.prepare(
      "SELECT r.expires_at, r.used_at, f.access_token_hash, f.access_expires_at, f.revoked_at FROM refresh_tokens r JOIN token_families f ON f.id = r.family_id " +
        "WHERE r.token_hash = ? AND r.family_id = ?",
    )
      .bind(tokens.refreshTokenHash, row.family_id)
      .first<{ expires_at: number; used_at: number | null; access_token_hash: string; access_expires_at: number; revoked_at: number | null }>();
    if (!successor || successor.used_at !== null || successor.revoked_at !== null || successor.access_token_hash !== tokens.accessTokenHash) return null;
    return { ...tokens, refreshExpiresAt: successor.expires_at, accessExpiresAt: successor.access_expires_at };
  }
  return null;
}

async function rotationResponse(ctx: Context, row: RefreshRow, tokens: IssuedTokens): Promise<Response> {
  const hasAccess = row.access_granted_at !== null;
  return json({
    ...tokenResponseFields(tokens),
    access: accessState(hasAccess),
    licence: hasAccess ? await issueLicence(ctx.env, ctx.now, row.user_id, row.thumbprint, "active") : null,
  });
}

/**
 * A token that was already rotated. Inside the retry window, with a valid proof and fresh nonce, a live session and
 * an unused successor, it is an idempotent retry and gets the same successor pair again. Anything else is reuse:
 * the whole family is revoked (theft detection). A replayed nonce is refused without revoking, so a client that
 * resent the very same request can still retry with a new proof.
 */
async function retryOrReuse(ctx: Context, limiter: RateLimiter, row: RefreshRow, refreshToken: string, tokenHash: string, proof: Proof): Promise<Response> {
  if (!insideRetryWindow(row, ctx.now)) return revokeFamilyForReuse(ctx, row);
  if (!(await proofIsValid(ctx, row, tokenHash, proof))) return revokeFamilyForReuse(ctx, row);
  await refuseSeenNonce(ctx, proof.nonce);
  if (row.status === "revoked") return revokedResponse(ctx, row.user_id, row.thumbprint);
  if (row.family_revoked_at !== null || row.device_revoked_at !== null) throw new HttpError(401, "session_revoked");
  const tokens = await issuedSuccessor(ctx, row, refreshToken);
  if (!tokens) return revokeFamilyForReuse(ctx, row);
  await limiter.verified("refresh", row.user_id, refreshPool(row));
  await consumeNonce(ctx, proof.nonce);
  return rotationResponse(ctx, row, tokens);
}

export async function refresh(ctx: Context): Promise<Response> {
  const body = object(await readJsonBody(ctx.request, 4096), ["refreshToken", "proof"]);
  const refreshToken = string(body["refreshToken"], { max: 46, pattern: REFRESH_TOKEN_PATTERN });
  const proofBody = object(body["proof"], ["timestamp", "nonce", "signature"]);
  const proof: Proof = {
    timestamp: integer(proofBody["timestamp"], 0, 1e11),
    nonce: string(proofBody["nonce"], { min: 16, max: 64, pattern: NONCE_PATTERN }),
    signature: string(proofBody["signature"], { max: 86, pattern: /^[A-Za-z0-9_-]{86}$/ }),
  };

  const { env } = ctx;
  await loadSigningKey(env.LICENCE_SIGNING_KEY);
  const ring = loadHmacKeyRing(env.INVITE_HMAC_KEYS);
  const limiter = limiterFor(ctx);
  await limiter.ip("refresh", clientIp(ctx.request));

  const db = env.DB;
  const tokenHash = await sha256Base64Url(refreshToken);
  const row = await loadRefreshRow(db, tokenHash);
  if (!row) throw new HttpError(401, "invalid_refresh_token");
  // A dead session: answered without counting or writing anything more (a revoked user still gets the signed
  // revocation for a valid proof, so the app can act on it).
  if (row.family_revoked_at !== null || row.device_revoked_at !== null) {
    if (row.status === "revoked" && (await proofIsValid(ctx, row, tokenHash, proof))) return revokedResponse(ctx, row.user_id, row.thumbprint);
    throw new HttpError(401, "session_revoked");
  }
  // The account is known from here: refused now if it is at its cap, or this token family at its failure cap (read
  // only). Failures are scoped to the family, so they never block a valid refresh on the account's other devices.
  await limiter.checkAccount("refresh", row.user_id, row.family_id);
  return countingFailures(limiter, "refresh", row.family_id, async () => {

    if (row.used_at !== null) return retryOrReuse(ctx, limiter, row, refreshToken, tokenHash, proof);
    if (row.expires_at <= ctx.now) throw new HttpError(401, "invalid_refresh_token");
    if (!(await proofIsValid(ctx, row, tokenHash, proof))) throw new HttpError(401, "invalid_proof");
    await refuseSeenNonce(ctx, proof.nonce);
    if (row.status === "revoked") return revokedResponse(ctx, row.user_id, row.thumbprint);
    if (row.family_revoked_at !== null || row.device_revoked_at !== null) throw new HttpError(401, "session_revoked");
    // The full credential check passed: only now do the account and global counters move, then the nonce is consumed.
    await limiter.verified("refresh", row.user_id, refreshPool(row));
    await consumeNonce(ctx, proof.nonce);

    const tokens = await rotatedTokens(ring.keys.get(ring.currentKid)!, refreshToken, ctx.now);
    const rotated = "EXISTS (SELECT 1 FROM refresh_tokens WHERE token_hash = ? AND replaced_by = ?)";
    const results = await db.batch([
      db.prepare("UPDATE refresh_tokens SET used_at = ?, replaced_by = ? WHERE token_hash = ? AND used_at IS NULL").bind(ctx.now, tokens.refreshTokenHash, tokenHash),
      // A concurrent rotation of the same token derives the same successor, so its insert is a no-op here.
      db
        .prepare(`INSERT INTO refresh_tokens (token_hash, family_id, created_at, expires_at) SELECT ?, ?, ?, ? WHERE ${rotated} ON CONFLICT (token_hash) DO NOTHING`)
        .bind(tokens.refreshTokenHash, row.family_id, ctx.now, tokens.refreshExpiresAt, tokenHash, tokens.refreshTokenHash),
      db
        .prepare(`UPDATE token_families SET access_token_hash = ?, access_expires_at = ? WHERE id = ? AND revoked_at IS NULL AND ${rotated}`)
        .bind(tokens.accessTokenHash, tokens.accessExpiresAt, row.family_id, tokenHash, tokens.refreshTokenHash),
      db.prepare("UPDATE devices SET last_seen_at = ? WHERE id = ?").bind(ctx.now, row.device_id),
      db.prepare("UPDATE users SET last_seen_at = ? WHERE id = ?").bind(ctx.now, row.user_id),
    ]);
    if (results[0]!.meta.changes === 1) return rotationResponse(ctx, row, tokens);

    // Another refresh of the same token rotated it first. If that was moments ago and its successor is unused, this is
    // the same client racing itself: answer with the same pair. Otherwise it is reuse.
    const current = await loadRefreshRow(db, tokenHash);
    if (!current) throw new HttpError(401, "invalid_refresh_token");
    const successor = insideRetryWindow(current, ctx.now) ? await issuedSuccessor(ctx, current, refreshToken) : null;
    if (!successor) return revokeFamilyForReuse(ctx, current);
    return rotationResponse(ctx, current, successor);
  });
}

export async function logout(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  await ctx.env.DB.prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'logout' WHERE id = ? AND revoked_at IS NULL")
    .bind(ctx.now, session.familyId)
    .run();
  return json({ ok: true });
}
