// Sessions (FR-ONL-03, ADR-0011): one token family per device. The access token (1 hour) and refresh token (90 days)
// are opaque 256-bit strings; only their SHA-256 is stored. Refresh tokens rotate on every use.
//
// Sign-in issues random tokens. A rotation derives the successor pair from the presented refresh token with a server
// key (HMAC-SHA-256 under the current INVITE_HMAC_KEYS key, domain-separated): unpredictable without both the token
// and the key, yet the same every time the same token is rotated. That is what lets a retry inside the grace window
// (routes/auth.ts) receive the pair that was already issued, although only hashes are stored.

import type { Context } from "../context.ts";
import { HttpError, bearerToken } from "../http.ts";
import { encodeBase64Url } from "../lib/base64url.ts";
import { hmacSha256, newOpaqueToken, sha256Base64Url } from "../lib/crypto.ts";

export const ACCESS_TOKEN_TTL_MS = 60 * 60 * 1000;
export const REFRESH_TOKEN_TTL_MS = 90 * 24 * 60 * 60 * 1000;
export const ACCESS_TOKEN_PATTERN = /^at_[A-Za-z0-9_-]{43}$/;
export const REFRESH_TOKEN_PATTERN = /^rt_[A-Za-z0-9_-]{43}$/;

export interface Session {
  familyId: string;
  userId: string;
  deviceId: string;
  thumbprint: string;
  status: "active" | "revoked";
  hasAccess: boolean;
}

export interface IssuedTokens {
  accessToken: string;
  accessTokenHash: string;
  accessExpiresAt: number;
  refreshToken: string;
  refreshTokenHash: string;
  refreshExpiresAt: number;
}

export async function newTokens(now: number): Promise<IssuedTokens> {
  return withHashes(newOpaqueToken("at"), newOpaqueToken("rt"), now);
}

/** The successor pair of `presentedRefreshToken` under `key`: the same inputs always give the same pair. */
export async function rotatedTokens(key: Uint8Array, presentedRefreshToken: string, now: number): Promise<IssuedTokens> {
  const accessToken = `at_${encodeBase64Url(await hmacSha256(key, `dbdp-rotate-v1|access|${presentedRefreshToken}`))}`;
  const refreshToken = `rt_${encodeBase64Url(await hmacSha256(key, `dbdp-rotate-v1|refresh|${presentedRefreshToken}`))}`;
  return withHashes(accessToken, refreshToken, now);
}

async function withHashes(accessToken: string, refreshToken: string, now: number): Promise<IssuedTokens> {
  return {
    accessToken,
    accessTokenHash: await sha256Base64Url(accessToken),
    accessExpiresAt: now + ACCESS_TOKEN_TTL_MS,
    refreshToken,
    refreshTokenHash: await sha256Base64Url(refreshToken),
    refreshExpiresAt: now + REFRESH_TOKEN_TTL_MS,
  };
}

/** Resolves the bearer access token to a live session or throws 401. Reads only; never writes. */
export async function requireSession(ctx: Context): Promise<Session> {
  const token = bearerToken(ctx.request);
  if (!token || !ACCESS_TOKEN_PATTERN.test(token)) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
  const row = await ctx.env.DB.prepare(
    "SELECT f.id AS family_id, f.user_id, f.device_id, d.thumbprint, u.status, u.access_granted_at " +
      "FROM token_families f JOIN users u ON u.id = f.user_id JOIN devices d ON d.id = f.device_id " +
      "WHERE f.access_token_hash = ? AND f.revoked_at IS NULL AND f.access_expires_at > ? AND d.revoked_at IS NULL",
  )
    .bind(await sha256Base64Url(token), ctx.now)
    .first<{ family_id: string; user_id: string; device_id: string; thumbprint: string; status: "active" | "revoked"; access_granted_at: number | null }>();
  if (!row) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
  return {
    familyId: row.family_id,
    userId: row.user_id,
    deviceId: row.device_id,
    thumbprint: row.thumbprint,
    status: row.status,
    hasAccess: row.access_granted_at !== null,
  };
}

export function tokenResponseFields(tokens: IssuedTokens): Record<string, unknown> {
  return {
    accessToken: tokens.accessToken,
    accessTokenExpiresAt: new Date(tokens.accessExpiresAt).toISOString(),
    refreshToken: tokens.refreshToken,
    refreshTokenExpiresAt: new Date(tokens.refreshExpiresAt).toISOString(),
  };
}
