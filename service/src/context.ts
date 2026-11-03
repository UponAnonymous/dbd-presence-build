// What every route handler receives. `deps` is injectable so tests replace the clock and the outbound fetch
// (Discord and the Access key set are never called from tests).

import type { AccessKeyCache } from "./auth/access-jwt.ts";
import type { FetchLike } from "./auth/discord.ts";
import type { ConfigCache } from "./config-cache.ts";
import type { Env } from "./env.ts";

export interface Deps {
  fetch: FetchLike;
  now: () => number;
  accessKeys: AccessKeyCache;
  configCache: ConfigCache;
}

export interface Context {
  request: Request;
  env: Env;
  deps: Deps;
  url: URL;
  /** Milliseconds since the Unix epoch, read once per request. */
  now: number;
  params: Readonly<Record<string, string>>;
}

export type Handler = (ctx: Context) => Promise<Response>;
