// Discord OAuth2 code exchange (FR-ONL-01, ADR-0011). The Worker is the confidential client: the client secret never
// leaves it. The user id is read from Discord's own `/users/@me` answer, never from the client. Scope `identify` only.
// Discord's access token is used for that one call and then dropped; it is never stored.

import { HttpError } from "../http.ts";
import { cleanDisplayText } from "../lib/validate.ts";

export const DISCORD_API = "https://discord.com/api/v10";
/** The loopback redirect the desktop app listens on (ADR-0011; any port, spike V2-S1 D22). */
export const REDIRECT_URI_PATTERN = /^http:\/\/127\.0\.0\.1:([1-9]\d{0,4})\/callback$/;
const TIMEOUT_MS = 8000;
const MAX_RESPONSE_BYTES = 64 * 1024;

export type FetchLike = (input: Request | string | URL, init?: RequestInit) => Promise<Response>;

export interface DiscordIdentity {
  id: string;
  displayName: string;
}

export function isAllowedRedirectUri(value: string): boolean {
  const match = REDIRECT_URI_PATTERN.exec(value);
  return match !== null && Number(match[1]) <= 65535;
}

async function readCapped(response: Response): Promise<unknown> {
  const text = await response.text();
  if (text.length > MAX_RESPONSE_BYTES) throw new HttpError(502, "discord_unavailable");
  try {
    return JSON.parse(text) as unknown;
  } catch (error) {
    if (error instanceof SyntaxError) throw new HttpError(502, "discord_unavailable");
    throw error;
  }
}

async function call(fetchFn: FetchLike, url: string, init: RequestInit): Promise<Response> {
  try {
    return await fetchFn(url, { ...init, redirect: "manual", signal: AbortSignal.timeout(TIMEOUT_MS) });
  } catch (error) {
    if (error instanceof Error && (error.name === "TimeoutError" || error.name === "AbortError" || error.name === "TypeError")) {
      throw new HttpError(502, "discord_unavailable");
    }
    throw error;
  }
}

export async function exchangeDiscordCode(
  fetchFn: FetchLike,
  clientId: string,
  clientSecret: string,
  code: string,
  codeVerifier: string,
  redirectUri: string,
): Promise<DiscordIdentity> {
  const form = new URLSearchParams({ grant_type: "authorization_code", code, redirect_uri: redirectUri, code_verifier: codeVerifier });
  const tokenResponse = await call(fetchFn, `${DISCORD_API}/oauth2/token`, {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded",
      Accept: "application/json",
      Authorization: `Basic ${btoa(`${clientId}:${clientSecret}`)}`,
    },
    body: form.toString(),
  });
  if (tokenResponse.status === 400 || tokenResponse.status === 401) {
    await tokenResponse.body?.cancel();
    throw new HttpError(400, "discord_code_rejected");
  }
  if (!tokenResponse.ok) {
    await tokenResponse.body?.cancel();
    throw new HttpError(502, "discord_unavailable");
  }
  const token = (await readCapped(tokenResponse)) as Record<string, unknown> | null;
  const accessToken = token?.["access_token"];
  const tokenType = token?.["token_type"];
  const scope = token?.["scope"];
  if (typeof accessToken !== "string" || accessToken.length === 0 || accessToken.length > 512 || typeof tokenType !== "string" || tokenType.toLowerCase() !== "bearer") {
    throw new HttpError(502, "discord_unavailable");
  }
  if (typeof scope !== "string" || !scope.split(" ").includes("identify")) throw new HttpError(400, "discord_scope_missing");

  const meResponse = await call(fetchFn, `${DISCORD_API}/users/@me`, {
    method: "GET",
    headers: { Authorization: `Bearer ${accessToken}`, Accept: "application/json" },
  });
  if (!meResponse.ok) {
    await meResponse.body?.cancel();
    throw new HttpError(502, "discord_unavailable");
  }
  const me = (await readCapped(meResponse)) as Record<string, unknown> | null;
  const id = me?.["id"];
  if (typeof id !== "string" || !/^\d{15,22}$/.test(id)) throw new HttpError(502, "discord_unavailable");
  const globalName = me?.["global_name"];
  const username = me?.["username"];
  const rawName = typeof globalName === "string" && globalName.length > 0 ? globalName : typeof username === "string" ? username : "";
  const displayName = cleanDisplayText(rawName, 64) || "Discord user";
  return { id, displayName };
}
