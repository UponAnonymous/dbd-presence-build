// Cache of the public signed documents: the config (FR-ONL-08) and the pack index (FR-ONL-07). GET /v1/config and
// GET /v1/packs are public and unauthenticated, so the D1 read and the ECDSA signature must not happen per request.
// Each document has its own cache key. Two layers, each serving a document for at most
// CONFIG_CACHE_MS after it was signed:
//   1. an isolate-level copy (one per Worker isolate);
//   2. the Cache API (one per Cloudflare data centre, shared by every isolate there).
// A change through the admin API clears both in the data centre that handled it; elsewhere the old document
// is served for at most CONFIG_CACHE_MS. Freshness is judged by the service clock against the signing time stored
// with the entry, never by the Cache API's own expiry alone.

export const CONFIG_CACHE_MS = 60_000;
/** Response header for clients and intermediaries; equal to the server-side cache lifetime. */
export const CONFIG_CACHE_CONTROL = `public, max-age=${CONFIG_CACHE_MS / 1000}`;
const SIGNED_AT_HEADER = "X-Dbdp-Signed-At";

interface Entry {
  body: string;
  signedAt: number;
}

function isFresh(entry: Entry, now: number): boolean {
  return entry.signedAt <= now && now - entry.signedAt < CONFIG_CACHE_MS;
}

function logCacheFailure(operation: string, error: unknown): void {
  console.error(JSON.stringify({ event: "config_cache_failed", operation, type: error instanceof Error ? error.name : typeof error }));
}

export class ConfigCache {
  private readonly entries = new Map<string, Entry>();
  private readonly openCache: () => Promise<Cache>;

  /** `openCache` returns the Cache API cache to use (`caches.default` in production, a named cache in tests). */
  constructor(openCache: () => Promise<Cache>) {
    this.openCache = openCache;
  }

  /** The cached response body, or null when neither layer holds a document signed less than CONFIG_CACHE_MS ago. */
  async get(key: string, now: number): Promise<string | null> {
    const held = this.entries.get(key);
    if (held && isFresh(held, now)) return held.body;
    let hit: Response | undefined;
    try {
      hit = await (await this.openCache()).match(key);
    } catch (error) {
      // The Cache API is an optimisation: on failure the document is signed afresh.
      logCacheFailure("match", error);
      return null;
    }
    if (!hit) return null;
    const entry = { body: await hit.text(), signedAt: Number(hit.headers.get(SIGNED_AT_HEADER)) };
    if (!Number.isSafeInteger(entry.signedAt) || !isFresh(entry, now)) return null;
    this.entries.set(key, entry);
    return entry.body;
  }

  async put(key: string, body: string, signedAt: number): Promise<void> {
    this.entries.set(key, { body, signedAt });
    const response = new Response(body, {
      headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": CONFIG_CACHE_CONTROL, [SIGNED_AT_HEADER]: String(signedAt) },
    });
    try {
      await (await this.openCache()).put(key, response);
    } catch (error) {
      logCacheFailure("put", error);
    }
  }

  /** Drops both layers here, after the owner changed something that the document carries. */
  async clear(key: string): Promise<void> {
    this.entries.delete(key);
    try {
      await (await this.openCache()).delete(key);
    } catch (error) {
      logCacheFailure("delete", error);
    }
  }
}

/** The Cache API key: an internal path on the API host that no route serves. */
export function configCacheKey(apiHost: string): string {
  return `https://${apiHost}/__cache/v1/config`;
}

/** The Cache API key of a channel's pack index. */
export function packIndexCacheKey(apiHost: string, channel: string): string {
  return `https://${apiHost}/__cache/v1/packs/${channel}`;
}

/** The Cache API key of a channel's signed character roster. */
export function rosterCacheKey(apiHost: string, channel: string): string {
  return `https://${apiHost}/__cache/v1/roster/${channel}`;
}
