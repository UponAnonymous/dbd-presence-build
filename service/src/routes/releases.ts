// Signed updates on the service (V2-S6; FR-ONL-10, FR-ADM-10, NFR-SVC-05, NFR-SVC-11, owner decision D18; PLAN_V2 §6.2, §6.5; ADR-0012).
//
// The Worker never holds the release key or the recovery key, and never signs a feed or a key replacement. The owner signs
// both offline (tools/DbdPresence.ReleaseSigner) and uploads them in the Owner Panel; this module checks each against the
// public keys in OFFLINE_PUBLIC_KEYS with the app's own rules (signature before parsing, release key only, channel bound,
// not expired, not rolled back, payload rules in src/releases/feed.ts) and serves the signed text exactly as uploaded.
//
// Public:  GET /v1/releases/{stable|beta}  the published feed, { schemaVersion, document }; 404 no_release
//          GET /v1/keys                    the newest key replacement; 404 no_key_replacement
// Admin:   GET  /api/releases                          channels (published and staged feed), packages, key status, storage
//          POST /api/releases/feeds                    { channel, document }: stage a signed feed (replaces a staged one)
//          PUT  /api/releases/packages/{sha256}        the package bytes of a staged or published feed (exact size and hash)
//          POST /api/releases/feeds/{id}/publish       make the staged feed the published one
//          POST /api/releases/feeds/{id}/discard       drop the staged feed
//          POST /api/keys/replacement                  { document }: a key replacement signed by the recovery key
// Packages are stored in R2 bucket RELEASES as <sha256>.nupkg (served by src/routes/cdn.ts while their row exists), reserved
// under the storage cap first (src/storage.ts), and pruned as soon as no staged or published feed lists them: with at most 3
// packages per feed, only the last 3 packages of each channel are kept (D18).

import { CONFIG_CACHE_CONTROL } from "../config-cache.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import type { Env } from "../env.ts";
import { HttpError, json, readJsonBody } from "../http.ts";
import { decodeBase64Url } from "../lib/base64url.ts";
import { newId } from "../lib/crypto.ts";
import { object, oneOf, string, uuid } from "../lib/validate.ts";
import {
  KEY_REPLACEMENT_CHANNEL,
  MAX_PACKAGE_BYTES,
  MAX_RELEASES,
  RELEASE_CHANNELS,
  compareVersions,
  parseKeyReplacement,
  parseReleaseFeed,
  type KeyReplacement,
  type ReleaseChannel,
  type ReleaseFeed,
} from "../releases/feed.ts";
import { TrustedKeySet, verifyDocument, type SignedEnvelope } from "../signing/document.ts";
import { deleteReleased, putStreamWithinCap, releaseStorage, storageUsage } from "../storage.ts";
import { requireAdmin } from "./admin.ts";

/** Largest signed document accepted (a feed of 3 packages with notes is a few KB). */
export const MAX_RELEASE_DOCUMENT_BYTES = 64 * 1024;
const SHA256 = /^[0-9a-f]{64}$/;
const TICKS_PER_MS = 10_000n;
/** A reservation whose upload never finished is given back after this (the daily sweep). */
const RESERVATION_GRACE_MS = 24 * 60 * 60 * 1000;

// ---------------------------------------------------------------------------------------------------------------
// Offline public keys

interface OfflineKeys {
  set: TrustedKeySet;
  release: string[];
  recovery: string[];
  entries: { kid: string; spki: string; purposes: string[] }[];
}

const offlineKeyCache = new Map<string, Promise<OfflineKeys>>();

function notConfigured(): never {
  throw new HttpError(503, "not_configured");
}

/** Parses OFFLINE_PUBLIC_KEYS; 503 not_configured when it is missing, malformed or lists a key with any other purpose. */
export function loadOfflineKeys(env: Env): Promise<OfflineKeys> {
  const text = env.OFFLINE_PUBLIC_KEYS ?? "";
  const cached = offlineKeyCache.get(text);
  if (cached) return cached;
  let entries: unknown;
  try {
    entries = JSON.parse(text);
  } catch (error) {
    if (error instanceof SyntaxError) notConfigured();
    throw error;
  }
  if (!Array.isArray(entries)) notConfigured();
  const keys: { kid: string; spki: string; purposes: string[] }[] = [];
  for (const entry of entries as unknown[]) {
    const e = entry as { kid?: unknown; spki?: unknown; purposes?: unknown };
    const purposes = Array.isArray(e.purposes) ? e.purposes : [];
    if (typeof e.kid !== "string" || typeof e.spki !== "string" || purposes.length !== 1 || (purposes[0] !== "releases" && purposes[0] !== "key-replacement")) notConfigured();
    keys.push({ kid: e.kid, spki: e.spki, purposes: [purposes[0] as string] });
  }
  const loading = TrustedKeySet.fromJson({ keys }).then(
    (set) => ({
      set,
      release: keys.filter((k) => k.purposes[0] === "releases").map((k) => k.kid),
      recovery: keys.filter((k) => k.purposes[0] === "key-replacement").map((k) => k.kid),
      entries: keys,
    }),
    (error: unknown) => {
      offlineKeyCache.delete(text);
      if (error instanceof Error && (error.name === "DataError" || error.name === "SigningError")) notConfigured();
      throw error;
    },
  );
  offlineKeyCache.set(text, loading);
  return loading;
}

/** The payload of the newest stored key replacement (verified when it was uploaded), or null when there is none. */
async function newestReplacement(db: D1Database): Promise<KeyReplacement | null> {
  const row = await db.prepare("SELECT document FROM key_replacements ORDER BY version DESC LIMIT 1").first<{ document: string }>();
  if (!row) return null;
  const outer = JSON.parse(row.document) as { signed: string };
  const envelope = JSON.parse(new TextDecoder().decode(decodeBase64Url(outer.signed)!)) as { payload: unknown };
  const parsed = parseKeyReplacement(JSON.stringify(envelope.payload));
  if (!parsed.ok) throw new Error("stored key replacement does not parse");
  return parsed.value;
}

interface ReleaseKeys {
  set: TrustedKeySet;
  release: string[];
  revoked: string[];
}

/**
 * The release keys the service accepts now (security review 2026-10-25, L3): the release keys of OFFLINE_PUBLIC_KEYS without the ones the
 * newest key replacement revoked, plus the release keys it added, so the service refuses exactly what an updated app refuses.
 */
async function releaseKeys(ctx: Context): Promise<ReleaseKeys> {
  const offline = await loadOfflineKeys(ctx.env);
  const replacement = await newestReplacement(ctx.env.DB);
  const revoked = replacement?.revoke ?? [];
  // The recovery keys stay in the set (they can never be revoked), so a feed they signed is refused as KeyNotAllowedForPurpose.
  const entries = offline.entries.filter((k) => k.purposes[0] !== "releases" || !revoked.includes(k.kid));
  for (const added of replacement?.add ?? []) {
    if (added.purposes.join() === "releases" && !revoked.includes(added.kid) && !entries.some((k) => k.kid === added.kid)) entries.push(added);
  }
  return { set: await TrustedKeySet.fromJson({ keys: entries }), release: entries.filter((k) => k.purposes[0] === "releases").map((k) => k.kid), revoked };
}

// ---------------------------------------------------------------------------------------------------------------
// Rows and views

interface FeedRow {
  id: string;
  channel: ReleaseChannel;
  feed_version: number;
  kid: string;
  issued_at: number;
  expires_at: number;
  newest: string | null;
  document: string;
  status: "staged" | "published" | "superseded" | "discarded";
  uploaded_at: number;
  uploaded_by: string;
  published_at: number | null;
  published_by: string | null;
}

interface FeedPackageRow {
  feed_id: string;
  sha256: string;
  version: string;
  file_name: string;
  size: number;
}

const iso = (ms: number | null): string | null => (ms === null ? null : new Date(ms).toISOString());

function ticksToMs(ticks: bigint): number {
  return Number(ticks / TICKS_PER_MS);
}

function releaseFeedCacheKey(apiHost: string, channel: string): string {
  return `https://${apiHost}/__cache/v1/releases/${channel}`;
}

async function feedView(db: D1Database, row: FeedRow, now: number): Promise<Record<string, unknown>> {
  const { results } = await db
    .prepare("SELECT p.*, (SELECT 1 FROM release_packages r WHERE r.sha256 = p.sha256) AS present FROM release_feed_packages p WHERE p.feed_id = ?")
    .bind(row.id)
    .all<FeedPackageRow & { present: number | null }>();
  results.sort((a, b) => compareVersions(b.version, a.version));
  return {
    id: row.id,
    channel: row.channel,
    feedVersion: row.feed_version,
    kid: row.kid,
    issuedAt: iso(row.issued_at),
    expiresAt: iso(row.expires_at),
    expired: now >= row.expires_at,
    newest: row.newest,
    status: row.status,
    uploadedAt: iso(row.uploaded_at),
    uploadedBy: row.uploaded_by,
    publishedAt: iso(row.published_at),
    packages: results.map((p) => ({ version: p.version, fileName: p.file_name, size: p.size, sha256: p.sha256, present: p.present === 1 })),
  };
}

async function feedOf(db: D1Database, channel: string, status: "staged" | "published"): Promise<FeedRow | null> {
  return db.prepare("SELECT * FROM release_feeds WHERE channel = ? AND status = ?").bind(channel, status).first<FeedRow>();
}

// ---------------------------------------------------------------------------------------------------------------
// Public

export async function getReleaseFeed(ctx: Context): Promise<Response> {
  const channel = oneOf(ctx.params["id"], RELEASE_CHANNELS);
  const cacheKey = releaseFeedCacheKey(ctx.env.API_HOST, channel);
  const respond = (body: string) => new Response(body, { status: 200, headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": CONFIG_CACHE_CONTROL } });
  const cached = await ctx.deps.configCache.get(cacheKey, ctx.now);
  if (cached !== null) return respond(cached);
  const row = await feedOf(ctx.env.DB, channel, "published");
  if (!row) throw new HttpError(404, "no_release");
  const body = JSON.stringify({ schemaVersion: 1, document: row.document });
  await ctx.deps.configCache.put(cacheKey, body, ctx.now);
  return respond(body);
}

export async function getKeyReplacement(ctx: Context): Promise<Response> {
  const row = await ctx.env.DB.prepare("SELECT document FROM key_replacements ORDER BY version DESC LIMIT 1").first<{ document: string }>();
  if (!row) throw new HttpError(404, "no_key_replacement");
  return new Response(JSON.stringify({ schemaVersion: 1, document: row.document }), {
    status: 200,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": CONFIG_CACHE_CONTROL },
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Admin

export interface OfflineKeyStatus {
  /** Key ids only (public); `configured` is false when OFFLINE_PUBLIC_KEYS is missing or unusable. */
  keys: { release: string[]; recovery: string[]; revoked: string[]; configured: boolean };
  keyReplacement: { version: number; kid: string; uploadedAt: string | null } | null;
}

/** The offline release and recovery keys the service checks uploads against now, and the newest key replacement (also used by the dashboard). */
export async function offlineKeyStatus(ctx: Context): Promise<OfflineKeyStatus> {
  const replacement = await ctx.env.DB.prepare("SELECT version, kid, uploaded_at FROM key_replacements ORDER BY version DESC LIMIT 1").first<{ version: number; kid: string; uploaded_at: number }>();
  let keys: OfflineKeyStatus["keys"];
  try {
    const loaded = await loadOfflineKeys(ctx.env);
    const effective = await releaseKeys(ctx);
    keys = { release: effective.release, recovery: loaded.recovery, revoked: effective.revoked, configured: true };
  } catch (error) {
    if (!(error instanceof HttpError)) throw error;
    keys = { release: [], recovery: [], revoked: [], configured: false };
  }
  return { keys, keyReplacement: replacement ? { version: replacement.version, kid: replacement.kid, uploadedAt: iso(replacement.uploaded_at) } : null };
}

export async function listReleases(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const db = ctx.env.DB;
  const channels: Record<string, unknown> = {};
  for (const channel of RELEASE_CHANNELS) {
    const published = await feedOf(db, channel, "published");
    const staged = await feedOf(db, channel, "staged");
    channels[channel] = { published: published ? await feedView(db, published, ctx.now) : null, staged: staged ? await feedView(db, staged, ctx.now) : null };
  }
  const status = await offlineKeyStatus(ctx);
  return json({
    channels,
    keyReplacement: status.keyReplacement,
    keys: status.keys,
    limits: { maxReleases: MAX_RELEASES, maxPackageBytes: MAX_PACKAGE_BYTES },
    storage: await storageUsage(db, ctx.env.ENVIRONMENT),
  });
}

/** Verifies a feed for `channel` as the app would, against the published one; the parsed feed and its envelope, or a 4xx. */
async function verifiedFeed(ctx: Context, channel: ReleaseChannel, document: string): Promise<{ envelope: SignedEnvelope; feed: ReleaseFeed }> {
  const offline = await loadOfflineKeys(ctx.env);
  if (offline.release.length === 0) throw new HttpError(503, "release_key_not_configured");
  const keys = await releaseKeys(ctx);
  const published = await feedOf(ctx.env.DB, channel, "published");
  const result = await verifyDocument(document, "releases", channel, {
    keys: keys.set,
    now: new Date(ctx.now),
    highestVersion: () => (published ? BigInt(published.feed_version) : undefined),
  });
  if (result.outcome !== "Valid") throw refused("feed_refused", result.outcome);
  const envelope = result.envelope!;
  const parsed = parseReleaseFeed(envelope.payloadJson, channel);
  if (!parsed.ok) throw refused("feed_refused", parsed.reason);
  if (published && envelope.version <= BigInt(published.feed_version)) throw new HttpError(409, "feed_version_not_newer");
  const newest = parsed.value.releases[0]?.version;
  if (published?.newest && (!newest || compareVersions(newest, published.newest) < 0)) throw new HttpError(409, "release_version_lower");
  return { envelope, feed: parsed.value };
}

class RefusedError extends HttpError {
  readonly reason: string;

  constructor(code: string, reason: string) {
    super(422, code);
    this.reason = reason;
  }
}

function refused(code: string, reason: string): RefusedError {
  return new RefusedError(code, reason);
}

async function withRefusal(action: () => Promise<Response>): Promise<Response> {
  try {
    return await action();
  } catch (error) {
    if (error instanceof RefusedError) return json({ error: error.code, reason: error.reason }, 422);
    throw error;
  }
}

export function uploadFeed(ctx: Context): Promise<Response> {
  return withRefusal(async () => {
    const actor = await requireAdmin(ctx);
    const body = object(await readJsonBody(ctx.request, MAX_RELEASE_DOCUMENT_BYTES + 1024), ["channel", "document"]);
    const channel = oneOf(body["channel"], RELEASE_CHANNELS);
    const document = string(body["document"], { max: MAX_RELEASE_DOCUMENT_BYTES });
    const { envelope, feed } = await verifiedFeed(ctx, channel, document);
    const db = ctx.env.DB;
    const id = newId();
    const statements = [
      db.prepare("UPDATE release_feeds SET status = 'discarded' WHERE channel = ? AND status = 'staged'").bind(channel),
      db
        .prepare(
          "INSERT INTO release_feeds (id, channel, feed_version, kid, issued_at, expires_at, newest, document, status, uploaded_at, uploaded_by) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'staged', ?, ?)",
        )
        .bind(id, channel, Number(envelope.version), envelope.kid, ticksToMs(envelope.issuedAtTicks), ticksToMs(envelope.expiresAtTicks), feed.releases[0]?.version ?? null, document, ctx.now, actor),
      ...feed.releases.map((p) =>
        db.prepare("INSERT INTO release_feed_packages (feed_id, sha256, version, file_name, size) VALUES (?, ?, ?, ?, ?)").bind(id, p.sha256, p.version, p.fileName, p.size),
      ),
    ];
    await writeAudited(
      db,
      ctx.now,
      { actor, action: "release.feed_upload", target: `release-feed:${id}`, after: { channel, feedVersion: Number(envelope.version), kid: envelope.kid, releases: feed.releases.map((p) => ({ version: p.version, sha256: p.sha256, size: p.size })) } },
      statements,
    );
    await prunePackages(ctx.env);
    const row = (await db.prepare("SELECT * FROM release_feeds WHERE id = ?").bind(id).first<FeedRow>())!;
    return json({ feed: await feedView(db, row, ctx.now) });
  });
}

export async function uploadPackage(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const sha = ctx.params["id"] ?? "";
  if (!SHA256.test(sha)) throw new HttpError(400, "invalid_request");
  const type = (ctx.request.headers.get("Content-Type") ?? "").split(";")[0]!.trim().toLowerCase();
  if (type !== "application/octet-stream") throw new HttpError(415, "unsupported_media_type");
  const db = ctx.env.DB;
  const listed = await db
    .prepare("SELECT p.size FROM release_feed_packages p JOIN release_feeds f ON f.id = p.feed_id WHERE p.sha256 = ? AND f.status IN ('staged', 'published') LIMIT 1")
    .bind(sha)
    .first<{ size: number }>();
  if (!listed) throw new HttpError(409, "package_not_in_feed");
  if (await db.prepare("SELECT 1 FROM release_packages WHERE sha256 = ?").bind(sha).first()) return json({ package: { sha256: sha, size: listed.size }, existing: true });
  const declared = ctx.request.headers.get("Content-Length");
  if (declared === null || !/^\d{1,10}$/.test(declared)) throw new HttpError(411, "length_required");
  if (Number(declared) !== listed.size) throw new HttpError(422, "package_size_mismatch");
  if (!ctx.request.body) throw new HttpError(400, "invalid_request");
  const key = `${sha}.nupkg`;
  try {
    // R2 checks the SHA-256 itself and refuses the write when the bytes do not match the signed feed.
    await putStreamWithinCap(db, ctx.env.RELEASES, "releases", key, ctx.request.body, listed.size, { sha256: sha, httpMetadata: { contentType: "application/octet-stream" } }, ctx.now, ctx.env.ENVIRONMENT);
  } catch (error) {
    if (error instanceof HttpError) throw error;
    console.error(JSON.stringify({ event: "release_package_put_failed", type: error instanceof Error ? error.name : typeof error }));
    throw new HttpError(422, "package_not_stored");
  }
  const stored = await ctx.env.RELEASES.head(key);
  if (!stored || stored.size !== listed.size) {
    await ctx.env.RELEASES.delete(key);
    await releaseStorage(db, "releases", key);
    throw new HttpError(422, "package_not_stored");
  }
  await writeAudited(db, ctx.now, { actor, action: "release.package_upload", target: `release-package:${sha}`, after: { size: listed.size } }, [
    db.prepare("INSERT OR IGNORE INTO release_packages (sha256, size, uploaded_at, uploaded_by) VALUES (?, ?, ?, ?)").bind(sha, listed.size, ctx.now, actor),
  ]);
  return json({ package: { sha256: sha, size: listed.size }, existing: false });
}

export function publishFeed(ctx: Context): Promise<Response> {
  return withRefusal(async () => {
    const actor = await requireAdmin(ctx);
    object(await readJsonBody(ctx.request, 256), []);
    const id = uuid(ctx.params["id"]);
    const db = ctx.env.DB;
    const row = await db.prepare("SELECT * FROM release_feeds WHERE id = ?").bind(id).first<FeedRow>();
    if (!row) throw new HttpError(404, "not_found");
    if (row.status !== "staged") throw new HttpError(409, "feed_not_staged");
    const { results: missing } = await db
      .prepare("SELECT p.sha256 FROM release_feed_packages p WHERE p.feed_id = ? AND NOT EXISTS (SELECT 1 FROM release_packages r WHERE r.sha256 = p.sha256)")
      .bind(id)
      .all<{ sha256: string }>();
    if (missing.length > 0) return json({ error: "packages_missing", missing: missing.map((m) => m.sha256) }, 409);
    // Checked again now: it may have expired, or another feed may have been published, since it was staged.
    const { envelope } = await verifiedFeed(ctx, row.channel, row.document);
    const previous = await feedOf(db, row.channel, "published");
    await writeAudited(
      db,
      ctx.now,
      {
        actor,
        action: "release.publish",
        target: `release-feed:${id}`,
        before: previous ? { feedVersion: previous.feed_version, newest: previous.newest } : null,
        after: { channel: row.channel, feedVersion: Number(envelope.version), newest: row.newest },
      },
      [
        db.prepare("UPDATE release_feeds SET status = 'superseded' WHERE channel = ? AND status = 'published'").bind(row.channel),
        db.prepare("UPDATE release_feeds SET status = 'published', published_at = ?, published_by = ? WHERE id = ? AND status = 'staged'").bind(ctx.now, actor, id),
      ],
    );
    await ctx.deps.configCache.clear(releaseFeedCacheKey(ctx.env.API_HOST, row.channel));
    const pruned = await prunePackages(ctx.env);
    const published = (await db.prepare("SELECT * FROM release_feeds WHERE id = ?").bind(id).first<FeedRow>())!;
    return json({ feed: await feedView(db, published, ctx.now), pruned });
  });
}

export async function discardFeed(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  object(await readJsonBody(ctx.request, 256), []);
  const id = uuid(ctx.params["id"]);
  const db = ctx.env.DB;
  const row = await db.prepare("SELECT channel, status FROM release_feeds WHERE id = ?").bind(id).first<{ channel: string; status: string }>();
  if (!row) throw new HttpError(404, "not_found");
  if (row.status !== "staged") throw new HttpError(409, "feed_not_staged");
  await writeAudited(db, ctx.now, { actor, action: "release.discard", target: `release-feed:${id}`, after: { channel: row.channel } }, [
    db.prepare("UPDATE release_feeds SET status = 'discarded' WHERE id = ? AND status = 'staged'").bind(id),
  ]);
  const pruned = await prunePackages(ctx.env);
  return json({ discarded: true, pruned });
}

export function uploadKeyReplacement(ctx: Context): Promise<Response> {
  return withRefusal(async () => {
    const actor = await requireAdmin(ctx);
    const body = object(await readJsonBody(ctx.request, MAX_RELEASE_DOCUMENT_BYTES + 1024), ["document"]);
    const document = string(body["document"], { max: MAX_RELEASE_DOCUMENT_BYTES });
    const keys = await loadOfflineKeys(ctx.env);
    if (keys.recovery.length === 0) throw new HttpError(503, "recovery_key_not_configured");
    const db = ctx.env.DB;
    const newest = await db.prepare("SELECT MAX(version) AS v FROM key_replacements").first<{ v: number | null }>();
    const result = await verifyDocument(document, "key-replacement", KEY_REPLACEMENT_CHANNEL, {
      keys: keys.set,
      now: new Date(ctx.now),
      highestVersion: () => (newest?.v === null || newest?.v === undefined ? undefined : BigInt(newest.v)),
    });
    if (result.outcome !== "Valid") throw refused("key_replacement_refused", result.outcome);
    const envelope = result.envelope!;
    const parsed = parseKeyReplacement(envelope.payloadJson);
    if (!parsed.ok) throw refused("key_replacement_refused", parsed.reason);
    if (parsed.value.revoke.some((kid) => keys.recovery.includes(kid))) throw refused("key_replacement_refused", "revokes_recovery_key");
    // Replacements are cumulative (security review 2026-10-25, M1): one that leaves out an earlier revocation would trust that key again.
    const earlier = await newestReplacement(db);
    if (earlier && !earlier.revoke.every((kid) => parsed.value.revoke.includes(kid))) throw refused("key_replacement_refused", "not_cumulative");
    if (newest?.v !== null && newest?.v !== undefined && envelope.version <= BigInt(newest.v)) throw new HttpError(409, "key_replacement_not_newer");
    await writeAudited(
      db,
      ctx.now,
      { actor, action: "keys.replace", target: `key-replacement:${envelope.version}`, after: { kid: envelope.kid, revoke: parsed.value.revoke, add: parsed.value.add.map((k) => ({ kid: k.kid, purposes: k.purposes })) } },
      [db.prepare("INSERT INTO key_replacements (version, kid, document, uploaded_at, uploaded_by) VALUES (?, ?, ?, ?, ?)").bind(Number(envelope.version), envelope.kid, document, ctx.now, actor)],
    );
    return json({ keyReplacement: { version: Number(envelope.version), kid: envelope.kid, revoke: parsed.value.revoke, add: parsed.value.add.map((k) => k.kid) } });
  });
}

// ---------------------------------------------------------------------------------------------------------------
// Pruning (D18: keep only the packages a staged or published feed lists, at most the last 3 per channel)

/** Deletes every package that no staged or published feed lists; the R2 delete follows its ledger row (tombstone on failure). */
export async function prunePackages(env: Env): Promise<{ deleted: number; pending: number }> {
  const { results } = await env.DB.prepare(
    "DELETE FROM release_packages WHERE NOT EXISTS (SELECT 1 FROM release_feed_packages p JOIN release_feeds f ON f.id = p.feed_id " +
      "WHERE p.sha256 = release_packages.sha256 AND f.status IN ('staged', 'published')) RETURNING sha256",
  ).all<{ sha256: string }>();
  if (results.length === 0) return { deleted: 0, pending: 0 };
  return deleteReleased(env.DB, "releases", env.RELEASES, results.map((r) => `${r.sha256}.nupkg`));
}

/** The daily sweep: prune, retry failed deletes, and give back reservations of uploads that never finished. */
export async function sweepReleases(env: Env, now: number): Promise<{ deleted: number; pending: number; reservations: number }> {
  const pruned = await prunePackages(env);
  const tombstones = await deleteReleased(env.DB, "releases", env.RELEASES);
  const { results } = await env.DB.prepare(
    "UPDATE storage_objects SET released = 1 WHERE area = 'releases' AND released = 0 AND created_at <= ? " +
      "AND NOT EXISTS (SELECT 1 FROM release_packages r WHERE r.sha256 || '.nupkg' = storage_objects.key) RETURNING key",
  )
    .bind(now - RESERVATION_GRACE_MS)
    .all<{ key: string }>();
  const leftovers = results.length > 0 ? await deleteReleased(env.DB, "releases", env.RELEASES, results.map((r) => r.key)) : { deleted: 0, pending: 0 };
  return { deleted: pruned.deleted + tombstones.deleted + leftovers.deleted, pending: tombstones.pending + leftovers.pending, reservations: results.length };
}
