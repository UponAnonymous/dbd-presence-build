// Test harness: an injectable clock, a fake Discord, a fake Cloudflare Access key set and device keys.
// Every outbound request goes to the fake; anything unexpected fails the test, so nothing reaches the network.

import { env } from "cloudflare:test";
import { handleRequest } from "../src/app.ts";
import { AccessKeyCache } from "../src/auth/access-jwt.ts";
import { ConfigCache } from "../src/config-cache.ts";
import { proofMessage } from "../src/auth/device.ts";
import type { Deps } from "../src/context.ts";
import { decodeBase64Url, encodeBase64Url } from "../src/lib/base64url.ts";
import { sha256Base64Url, utf8 } from "../src/lib/crypto.ts";
import { TrustedKeySet } from "../src/signing/document.ts";
import { inject } from "vitest";

export const API = "https://api.arcavale.com";
export const ADMIN = "https://admin.arcavale.com";
export const CDN = "https://cdn.arcavale.com";
export const OWNER = "owner@example.com";
export const TEAM = "https://dbdp-test.cloudflareaccess.com";
export const AUD = "test-access-aud";
export const REDIRECT = "http://127.0.0.1:53682/callback";
export const T0 = Date.parse("2026-11-02T10:00:00.000Z");

const TABLES = [
  "uploads",
  "refresh_tokens",
  "token_families",
  "devices",
  "users",
  "invites",
  "proof_nonces",
  "rate_limits",
  "audit_log",
  "settings",
  "banned_subjects",
  "audit_export_state",
  "pack_entries",
  "packs",
  "published_entries",
  "published_packs",
  "pack_index",
  "pack_objects",
  "discord_apps",
  "storage_objects",
  "roster_versions",
  "roster_sightings",
  "roster_blocks",
  "release_feed_packages",
  "release_feeds",
  "release_packages",
  "key_replacements",
  "backup_runs",
];

/** Empties every table and the PACKS, UPLOADS and RELEASES buckets, and restores the migrations' default rows (the roster keeps only its seed). */
export async function resetDatabase(): Promise<void> {
  await env.DB.batch([
    ...TABLES.map((t) => env.DB.prepare(`DELETE FROM ${t}`)),
    env.DB.prepare("DELETE FROM roster_characters WHERE source <> 'seed'"),
    env.DB.prepare("DELETE FROM sqlite_sequence WHERE name = 'audit_log'"),
    env.DB.prepare("UPDATE storage_usage SET bytes = 0, objects = 0"),
    env.DB.prepare("INSERT INTO settings (key, value, updated_at) VALUES ('access_mode', '\"invite-only\"', 0), ('device_limit', '3', 0)"),
    env.DB.prepare(
      "INSERT INTO discord_apps (key, application_id, name, description, is_default, position) VALUES ('dbd', '1563291325107937340', 'Dead by Daylight', 'Unofficial fan-made presence — not affiliated with Behaviour Interactive', 1, 0)",
    ),
  ]);
  for (const bucket of [env.PACKS, env.UPLOADS, env.RELEASES]) {
    for (;;) {
      const page = await bucket.list({ limit: 1000 });
      if (page.objects.length === 0) break;
      await bucket.delete(page.objects.map((o) => o.key));
    }
  }
}

interface DiscordUser {
  id: string;
  username: string;
  global_name: string | null;
}

interface PendingCode {
  user: DiscordUser;
  verifier: string;
  redirectUri: string;
}

export interface OutboundCall {
  method: string;
  url: string;
  headers: Headers;
  body: string;
}

export class Harness {
  now = T0;
  readonly calls: OutboundCall[] = [];
  readonly codes = new Map<string, PendingCode>();
  discordDown = false;
  jwksDown = false;
  private readonly tokens = new Map<string, DiscordUser>();
  accessKeys = new AccessKeyCache();
  /** A Cache API cache of its own per harness, so tests never see each other's cached config. */
  readonly cacheName = `config-${crypto.randomUUID()}`;
  configCache = new ConfigCache(() => caches.open(this.cacheName));
  jwks: { kid: string; privateKey: CryptoKey; publicJwk: JsonWebKey } | null = null;
  ip = "203.0.113.7";
  private counter = 0;

  readonly deps: Deps = {
    fetch: (input, init) => this.fakeFetch(input, init),
    now: () => this.now,
    accessKeys: this.accessKeys,
    configCache: this.configCache,
  };

  private async fakeFetch(input: Request | string | URL, init?: RequestInit): Promise<Response> {
    const request = new Request(input, init);
    const body = request.method === "GET" ? "" : new TextDecoder().decode(await request.arrayBuffer());
    this.calls.push({ method: request.method, url: request.url, headers: request.headers, body });
    if (request.url === "https://discord.com/api/v10/oauth2/token" && request.method === "POST") {
      if (this.discordDown) return new Response("unavailable", { status: 503 });
      const expected = `Basic ${btoa(`${env.DISCORD_CLIENT_ID}:test-client-secret`)}`;
      if (request.headers.get("Authorization") !== expected) return Response.json({ error: "invalid_client" }, { status: 401 });
      const form = new URLSearchParams(body);
      const pending = this.codes.get(form.get("code") ?? "");
      if (
        form.get("grant_type") !== "authorization_code" ||
        !pending ||
        pending.verifier !== form.get("code_verifier") ||
        pending.redirectUri !== form.get("redirect_uri")
      ) {
        return Response.json({ error: "invalid_grant" }, { status: 400 });
      }
      this.codes.delete(form.get("code")!);
      const token = `discord-access-${++this.counter}`;
      this.tokens.set(token, pending.user);
      return Response.json({ access_token: token, token_type: "Bearer", expires_in: 604800, refresh_token: "unused", scope: "identify" });
    }
    if (request.url === "https://discord.com/api/v10/users/@me" && request.method === "GET") {
      const user = this.tokens.get((request.headers.get("Authorization") ?? "").replace(/^Bearer /, ""));
      return user ? Response.json({ ...user, discriminator: "0", avatar: null }) : Response.json({ message: "401: Unauthorized" }, { status: 401 });
    }
    if (request.url === `${TEAM}/cdn-cgi/access/certs` && request.method === "GET" && this.jwksDown) return new Response("unavailable", { status: 503 });
    if (request.url === `${TEAM}/cdn-cgi/access/certs` && request.method === "GET" && this.jwks) {
      return Response.json({ keys: [{ ...this.jwks.publicJwk, kid: this.jwks.kid, alg: "RS256", use: "sig" }] });
    }
    throw new Error(`unexpected outbound request ${request.method} ${request.url}`);
  }

  /** Calls the Worker the way the platform would. */
  async call(method: string, url: string, options: { body?: unknown; rawBody?: string; headers?: Record<string, string> } = {}): Promise<Response> {
    const headers: Record<string, string> = { "CF-Connecting-IP": this.ip, ...options.headers };
    let body: string | undefined;
    if (options.rawBody !== undefined) body = options.rawBody;
    else if (options.body !== undefined) {
      body = JSON.stringify(options.body);
      headers["Content-Type"] ??= "application/json";
    }
    return handleRequest(new Request(url, { method, headers, body }), env, this.deps);
  }

  /** Registers an authorization code with the fake Discord, as if the user had approved the app. */
  discordCode(user: { id: string; name?: string }, verifier: string, redirectUri = REDIRECT): string {
    const code = `code${++this.counter}x${user.id}`;
    this.codes.set(code, { user: { id: user.id, username: `user${user.id}`, global_name: user.name ?? null }, verifier, redirectUri });
    return code;
  }

  async signIn(discordId: string, device: Device, name = "Test Player"): Promise<{ status: number; body: Record<string, unknown> }> {
    const verifier = "v".repeat(43) + discordId;
    const code = this.discordCode({ id: discordId, name }, verifier);
    const response = await this.call("POST", `${API}/v1/auth/discord`, {
      body: { code, codeVerifier: verifier, redirectUri: REDIRECT, devicePublicKeyJwk: device.publicJwk },
    });
    return { status: response.status, body: (await response.json()) as Record<string, unknown> };
  }

  async refresh(refreshToken: string, device: Device, proof: Partial<{ timestamp: number; nonce: string; signature: string }> = {}): Promise<Response> {
    return this.call("POST", `${API}/v1/auth/refresh`, { body: { refreshToken, proof: await device.proof(refreshToken, Math.floor(this.now / 1000), proof) } });
  }

  // --- Cloudflare Access -------------------------------------------------------------------------------------

  async enableAccessKeys(): Promise<void> {
    const pair = (await crypto.subtle.generateKey(
      { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
      true,
      ["sign", "verify"],
    )) as CryptoKeyPair;
    const publicJwk = (await crypto.subtle.exportKey("jwk", pair.publicKey)) as JsonWebKey;
    this.jwks = { kid: "access-key-1", privateKey: pair.privateKey, publicJwk };
  }

  async accessJwt(claims: Partial<{ aud: string | string[]; iss: string; exp: number; email: string; nbf: number }> = {}, kid?: string, key?: CryptoKey): Promise<string> {
    if (!this.jwks) await this.enableAccessKeys();
    const nowSeconds = Math.floor(this.now / 1000);
    const header = { alg: "RS256", kid: kid ?? this.jwks!.kid, typ: "JWT" };
    const payload = { aud: [AUD], iss: TEAM, exp: nowSeconds + 3600, iat: nowSeconds, nbf: nowSeconds, email: OWNER, type: "app", ...claims };
    const encode = (value: unknown) => encodeBase64Url(utf8(JSON.stringify(value)));
    const signingInput = `${encode(header)}.${encode(payload)}`;
    const signature = new Uint8Array(await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key ?? this.jwks!.privateKey, utf8(signingInput)));
    return `${signingInput}.${encodeBase64Url(signature)}`;
  }

  async adminHeaders(overrides: Record<string, string | null> = {}): Promise<Record<string, string>> {
    const base: Record<string, string | null> = {
      "Cf-Access-Jwt-Assertion": await this.accessJwt(),
      Origin: ADMIN,
      "Sec-Fetch-Site": "same-origin",
      "X-DBDP-Admin": "1",
      ...overrides,
    };
    return Object.fromEntries(Object.entries(base).filter((e): e is [string, string] => e[1] !== null));
  }

  async admin(method: string, path: string, body?: unknown, overrides: Record<string, string | null> = {}): Promise<Response> {
    return this.call(method, `${ADMIN}${path}`, { body, headers: await this.adminHeaders(overrides) });
  }

  /** Uploads raw image bytes through the admin API. */
  async upload(bytes: Uint8Array, contentType: string, overrides: Record<string, string | null> = {}): Promise<Response> {
    const headers = { ...(await this.adminHeaders(overrides)), "Content-Type": contentType, "CF-Connecting-IP": this.ip };
    return handleRequest(new Request(`${ADMIN}/api/pack-files`, { method: "POST", headers, body: bytes }), env, this.deps);
  }
}

export interface Device {
  publicJwk: { kty: string; crv: string; x: string; y: string };
  privateKey: CryptoKey;
  proof(refreshToken: string, timestamp: number, overrides?: Partial<{ timestamp: number; nonce: string; signature: string }>): Promise<{ timestamp: number; nonce: string; signature: string }>;
}

export async function newDevice(): Promise<Device> {
  const pair = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const jwk = (await crypto.subtle.exportKey("jwk", pair.publicKey)) as JsonWebKey;
  const publicJwk = { kty: "EC", crv: "P-256", x: jwk.x!, y: jwk.y! };
  return {
    publicJwk,
    privateKey: pair.privateKey,
    async proof(refreshToken, timestamp, overrides = {}) {
      const ts = overrides.timestamp ?? timestamp;
      const nonce = overrides.nonce ?? encodeBase64Url(crypto.getRandomValues(new Uint8Array(18)));
      const message = proofMessage(await sha256Base64Url(refreshToken), ts, nonce);
      const signature = overrides.signature ?? encodeBase64Url(new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, pair.privateKey, message)));
      return { timestamp: ts, nonce, signature };
    },
  };
}

/** The trusted set from tests/fixtures/signing/keys/keys.json. */
export function trustedKeys(): Promise<TrustedKeySet> {
  return TrustedKeySet.fromJson(JSON.parse(inject("signingKeys")) as { keys: { kid: string; spki: string; purposes: string[] }[] });
}

/** Decodes the envelope of a signed document without verifying it (for assertions after verification). */
export function envelopeOf(document: string): Record<string, unknown> {
  const outer = JSON.parse(document) as { signed: string };
  return JSON.parse(new TextDecoder().decode(decodeBase64Url(outer.signed)!)) as Record<string, unknown>;
}

export async function rowCount(table: string, where = "1 = 1", ...binds: unknown[]): Promise<number> {
  const row = await env.DB.prepare(`SELECT COUNT(*) AS n FROM ${table} WHERE ${where}`)
    .bind(...binds)
    .first<{ n: number }>();
  return row!.n;
}

const BASE64URL_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

/**
 * Another spelling of the same bytes: flips the lowest unused bit of the last character of unpadded base64url whose
 * length leaves spare bits (length % 4 of 2 or 3). A lenient decoder returns the same bytes for both.
 */
export function respell(text: string): string {
  if (text.length % 4 !== 2 && text.length % 4 !== 3) throw new Error("no spare bits at this length");
  const last = BASE64URL_ALPHABET.indexOf(text.at(-1)!);
  return text.slice(0, -1) + BASE64URL_ALPHABET[last ^ 1]!;
}

const SQL = Symbol("sql");
const INNER = Symbol("inner");

/**
 * Wraps D1 so a test can act just before or just after a batch whose statements match, to simulate another request
 * (an owner revoking a user, say) landing in the middle of this one.
 */
export function interceptBatches(db: D1Database, hooks: { match: RegExp; before?: () => Promise<void>; after?: () => Promise<void> }): D1Database {
  const wrap = (statement: D1PreparedStatement, sql: string): D1PreparedStatement =>
    new Proxy(statement, {
      get(target, property) {
        if (property === SQL) return sql;
        if (property === INNER) return target;
        if (property === "bind") return (...values: unknown[]) => wrap(target.bind(...values), sql);
        const value = Reflect.get(target, property, target) as unknown;
        return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
      },
    });
  return new Proxy(db, {
    get(target, property) {
      if (property === "prepare") return (query: string) => wrap(target.prepare(query), query);
      if (property === "batch") {
        return async (statements: D1PreparedStatement[]) => {
          const tagged = statements as unknown as Record<symbol, unknown>[];
          const hit = tagged.some((s) => hooks.match.test(String(s[SQL] ?? "")));
          if (hit && hooks.before) await hooks.before();
          const results = await target.batch(tagged.map((s) => (s[INNER] ?? s) as D1PreparedStatement));
          if (hit && hooks.after) await hooks.after();
          return results;
        };
      }
      const value = Reflect.get(target, property, target) as unknown;
      return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
    },
  });
}
