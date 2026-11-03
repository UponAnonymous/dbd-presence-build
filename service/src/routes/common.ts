import { issueLicence } from "../auth/licence.ts";
import type { Context } from "../context.ts";
import { HttpError, json } from "../http.ts";
import { RateLimiter, type AccountRoute, type RouteClass } from "../ratelimit.ts";
import { loadHmacKeyRing } from "../signing/keys.ts";

/** The rate limiter, keyed with the current invite HMAC key (domain-separated inside the limiter). */
export function limiterFor(ctx: Context): RateLimiter {
  const ring = loadHmacKeyRing(ctx.env.INVITE_HMAC_KEYS);
  return new RateLimiter(ctx.env.DB, ctx.now, ring.keys.get(ring.currentKid)!);
}

function isRefusal(status: number): boolean {
  return status >= 400 && status < 500 && status !== 429;
}

/**
 * Runs the rest of a request once its account is known. Any refusal it ends in (a 4xx other than 429, thrown or
 * returned) is counted as a failed attempt on the failure counter of `scope` (the account, or the token family for
 * refresh), which refuses (429) at its cap.
 */
export async function countingFailures(limiter: RateLimiter, route: RouteClass | AccountRoute, scope: string, run: () => Promise<Response>): Promise<Response> {
  let response: Response;
  try {
    response = await run();
  } catch (error) {
    if (error instanceof HttpError && isRefusal(error.status)) await limiter.failure(route, scope);
    throw error;
  }
  if (isRefusal(response.status)) await limiter.failure(route, scope);
  return response;
}

/** 403 with a signed revocation the app can act on (D8: presence stops only on a received revocation). */
export async function revokedResponse(ctx: Context, userId: string, thumbprint: string): Promise<Response> {
  return json({ error: "revoked", revocation: await issueLicence(ctx.env, ctx.now, userId, thumbprint, "revoked") }, 403);
}
