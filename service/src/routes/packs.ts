// Packs: the public signed index (GET /v1/packs) and the owner's admin API for creating, editing, publishing and
// taking down packs (FR-ADM-08, FR-PACK-01/03/04/05/08, FR-ONL-07; PLAN_V2 §6.3; ADR-0013).
//
// The owner edits a draft copy of each pack. Publish copies the draft into the published tables and signs nothing by
// itself: it stores the next index version, and GET /v1/packs signs the newest version on demand with a fresh 30-day
// lifetime (cached for 60 seconds like the config), so a published index never runs out while the service is up.
// Withdrawing a licensed item takes effect at once (a new index version without it) and leaves the rest of the pack
// as published. Files are stored by SHA-256 after the strict image check (src/images/strict.ts) and deleted when
// nothing refers to them any more.
//
// The audit log records ids, keys, hashes, flags and the names of changed fields, never free text (names, notes,
// rights holders, permission records), so it holds no personal data and stays short.

import { packIndexCacheKey } from "../config-cache.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import { ENTRY_SELECT, commitIndexChange, latestIndex, purgeObjects, releaseObjects, type EntryRow, type PackRow } from "../db/packs.ts";
import { HttpError, SCHEMA_VERSION, json, readBodyBytes, readJsonBody } from "../http.ts";
import { preparePackImage } from "../images/pack-image.ts";
import { MAX_IMAGE_BYTES } from "../images/strict.ts";
import { randomBytes, sha256Hex, toHex } from "../lib/crypto.ts";
import { boolean, object, string } from "../lib/validate.ts";
import {
  ENTRY_ID_PATTERN,
  KEY_PATTERN,
  MAX_ENTRIES,
  MAX_PACK_BYTES,
  MAX_PACKS,
  PACK_ID_PATTERN,
  SHA256_PATTERN,
  objectName,
  type EntryKind,
  type PublishedEntry,
  type PublishedPack,
} from "../packs/model.ts";
import { MAX_LIFETIME_MS, signDocument } from "../signing/document.ts";
import { putWithinCap } from "../storage.ts";
import { loadSigningKey } from "../signing/keys.ts";
import { requireAdmin } from "./admin.ts";

export const PACKS_CHANNEL = "stable";
/** Clients re-check every 6 hours; intermediaries may keep the document for 5 minutes. */
export const PACKS_CACHE_CONTROL = "public, max-age=300";

const iso = (ms: number | null): string | null => (ms === null ? null : new Date(ms).toISOString());

// ---------------------------------------------------------------------------------------------------------------
// GET /v1/packs?channel=stable

export async function getPacks(ctx: Context): Promise<Response> {
  const channel = ctx.url.searchParams.get("channel") ?? PACKS_CHANNEL;
  if (channel !== PACKS_CHANNEL) throw new HttpError(400, "unknown_channel");
  // A missing or broken key is reported even while a document signed earlier is still cached.
  const key = await loadSigningKey(ctx.env.CONTENT_SIGNING_KEY);
  const cacheKey = packIndexCacheKey(ctx.env.API_HOST, channel);
  const respond = (body: string) => new Response(body, { status: 200, headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": PACKS_CACHE_CONTROL } });
  const cached = await ctx.deps.configCache.get(cacheKey, ctx.now);
  if (cached !== null) return respond(cached);

  // Before the first Publish the index is empty, version 0.
  const latest = await latestIndex(ctx.env.DB);
  const version = latest?.version ?? 0;
  const payload: unknown = latest ? JSON.parse(latest.payload) : { schemaVersion: 1, packs: [], entries: [] };
  const document = await signDocument("pack-index", channel, version, payload, key.key, key.kid, {
    issuedAt: new Date(ctx.now),
    expiresAt: new Date(ctx.now + MAX_LIFETIME_MS["pack-index"]),
  });
  const body = JSON.stringify({ schemaVersion: SCHEMA_VERSION, document });
  await ctx.deps.configCache.put(cacheKey, body, ctx.now);
  return respond(body);
}

async function clearIndexCache(ctx: Context): Promise<void> {
  await ctx.deps.configCache.clear(packIndexCacheKey(ctx.env.API_HOST, PACKS_CHANNEL));
}

// ---------------------------------------------------------------------------------------------------------------
// Validation

const TEXT_LIMITS = { name: 80, author: 120, licenceNote: 200, credit: 200, holder: 120, creator: 120, proof: 500 } as const;
const CONTROL = /[\p{Cc}\p{Cf}]/u;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

function invalid(): never {
  throw new HttpError(400, "invalid_request");
}

function text(value: unknown, max: number): string {
  const s = string(value, { max: max + 64 }).trim();
  if (s.length === 0 || s.length > max || CONTROL.test(s)) invalid();
  return s;
}

function optionalText(value: unknown, max: number): string | null {
  return value === null || value === "" ? null : text(value, max);
}

function optionalSha(value: unknown): string | null {
  return value === null ? null : string(value, { max: 64, pattern: SHA256_PATTERN });
}

function packIdParam(ctx: Context): string {
  const id = ctx.params["id"];
  if (id === undefined || !PACK_ID_PATTERN.test(id)) invalid();
  return id;
}

function entryIdParam(ctx: Context): string {
  const id = ctx.params["entryId"];
  if (id === undefined || !ENTRY_ID_PATTERN.test(id)) invalid();
  return id;
}

function permissionDate(value: unknown, now: number): string {
  const s = string(value, { max: 10, pattern: DATE });
  const ms = Date.parse(`${s}T00:00:00Z`);
  if (Number.isNaN(ms) || new Date(ms).toISOString().slice(0, 10) !== s || ms > now) invalid();
  return s;
}

interface PackFields {
  name: string;
  author: string;
  licenceNote: string;
  credit: string | null;
  fallback: boolean;
  behaviourArt: boolean;
  thirdParty: boolean;
  permission: { creator: string; date: string; proof: string } | null;
}

const PACK_FIELDS = ["name", "author", "licenceNote", "credit", "fallback", "behaviourArt", "thirdParty", "permission"] as const;

function fieldsFromRow(row: PackRow): PackFields {
  return {
    name: row.name,
    author: row.author,
    licenceNote: row.licence_note,
    credit: row.credit,
    fallback: row.fallback === 1,
    behaviourArt: row.behaviour_art === 1,
    thirdParty: row.third_party === 1,
    permission:
      row.permission_creator !== null && row.permission_date !== null && row.permission_proof !== null
        ? { creator: row.permission_creator, date: row.permission_date, proof: row.permission_proof }
        : null,
  };
}

/** Reads the members present in `body` over `base`. */
function readPackFields(body: Record<string, unknown>, base: PackFields, now: number): PackFields {
  const out: PackFields = { ...base };
  if (body["name"] !== undefined) out.name = text(body["name"], TEXT_LIMITS.name);
  if (body["author"] !== undefined) out.author = text(body["author"], TEXT_LIMITS.author);
  if (body["licenceNote"] !== undefined) out.licenceNote = text(body["licenceNote"], TEXT_LIMITS.licenceNote);
  if (body["credit"] !== undefined) out.credit = optionalText(body["credit"], TEXT_LIMITS.credit);
  if (body["fallback"] !== undefined) out.fallback = boolean(body["fallback"]);
  if (body["behaviourArt"] !== undefined) out.behaviourArt = boolean(body["behaviourArt"]);
  if (body["thirdParty"] !== undefined) out.thirdParty = boolean(body["thirdParty"]);
  if (body["permission"] !== undefined) {
    if (body["permission"] === null) out.permission = null;
    else {
      const p = object(body["permission"], ["creator", "date", "proof"]);
      out.permission = { creator: text(p["creator"], TEXT_LIMITS.creator), date: permissionDate(p["date"], now), proof: text(p["proof"], TEXT_LIMITS.proof) };
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Views

function cdnUrl(ctx: Context, sha256: string, format: "png" | "jpeg"): string {
  return `https://${ctx.env.CDN_HOST}/packs/${objectName(sha256, format)}`;
}

function entryView(ctx: Context, e: EntryRow, live: ReadonlySet<string>): Record<string, unknown> {
  return {
    id: e.id,
    kind: e.kind,
    key: e.key,
    sha256: e.sha256,
    format: e.format,
    size: e.size,
    width: e.width,
    height: e.height,
    licensedHolder: e.licensed_holder,
    withdrawn: e.withdrawn === 1,
    sourceSha256: e.source_sha256,
    live: live.has(e.id),
    url: cdnUrl(ctx, e.sha256, e.format),
    createdAt: iso(e.created_at),
    updatedAt: iso(e.updated_at),
  };
}

function packView(ctx: Context, p: PackRow, entries: EntryRow[], live: ReadonlySet<string>): Record<string, unknown> {
  const fields = fieldsFromRow(p);
  return {
    id: p.id,
    ...fields,
    default: p.is_default === 1,
    status: p.status,
    version: p.version,
    changedSincePublish: p.changed === 1,
    createdAt: iso(p.created_at),
    updatedAt: iso(p.updated_at),
    publishedAt: iso(p.published_at),
    totalBytes: entries.reduce((sum, e) => sum + e.size, 0),
    entries: entries.map((e) => entryView(ctx, e, live)),
  };
}

async function loadPack(ctx: Context, id: string): Promise<{ pack: PackRow; entries: EntryRow[]; live: Set<string> }> {
  const db = ctx.env.DB;
  const [pack, entries, live] = await db.batch([
    db.prepare("SELECT * FROM packs WHERE id = ?").bind(id),
    db.prepare(`${ENTRY_SELECT} WHERE e.pack_id = ? ORDER BY e.key`).bind(id),
    db.prepare("SELECT id FROM published_entries WHERE pack_id = ? AND withdrawn = 0").bind(id),
  ]);
  const row = pack!.results[0] as PackRow | undefined;
  if (!row) throw new HttpError(404, "not_found");
  return { pack: row, entries: entries!.results as EntryRow[], live: new Set((live!.results as { id: string }[]).map((r) => r.id)) };
}

async function packResponse(ctx: Context, id: string, extra: Record<string, unknown> = {}): Promise<Response> {
  const { pack, entries, live } = await loadPack(ctx, id);
  return json({ pack: packView(ctx, pack, entries, live), ...extra });
}

async function indexSummary(db: D1Database): Promise<Record<string, unknown>> {
  const latest = await latestIndex(db);
  if (!latest) return { version: 0, publishedAt: null, packs: 0, entries: 0 };
  const payload = JSON.parse(latest.payload) as { packs: unknown[]; entries: unknown[] };
  return { version: latest.version, publishedAt: iso(latest.published_at), packs: payload.packs.length, entries: payload.entries.length };
}

// ---------------------------------------------------------------------------------------------------------------
// Packs

export async function listPacks(ctx: Context): Promise<Response> {
  await requireAdmin(ctx);
  const db = ctx.env.DB;
  const [packs, entries, live] = await db.batch([
    db.prepare("SELECT * FROM packs ORDER BY created_at, id"),
    db.prepare(`${ENTRY_SELECT} ORDER BY e.pack_id, e.key`),
    db.prepare("SELECT id FROM published_entries WHERE withdrawn = 0"),
  ]);
  const liveIds = new Set((live!.results as { id: string }[]).map((r) => r.id));
  const byPack = new Map<string, EntryRow[]>();
  for (const e of entries!.results as EntryRow[]) byPack.set(e.pack_id, [...(byPack.get(e.pack_id) ?? []), e]);
  return json({
    packs: (packs!.results as PackRow[]).map((p) => packView(ctx, p, byPack.get(p.id) ?? [], liveIds)),
    index: await indexSummary(db),
    limits: { maxImageBytes: MAX_IMAGE_BYTES, maxPackBytes: MAX_PACK_BYTES, maxPacks: MAX_PACKS, maxEntries: MAX_ENTRIES },
  });
}

async function assertSingleFallback(db: D1Database, id: string, fallback: boolean): Promise<void> {
  if (!fallback) return;
  const other = await db.prepare("SELECT id FROM packs WHERE fallback = 1 AND id <> ?").bind(id).first<{ id: string }>();
  if (other) throw new HttpError(409, "fallback_exists");
}

export async function createPack(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const body = object(await readJsonBody(ctx.request, 4096), ["id", ...PACK_FIELDS], ["id", "name", "author", "licenceNote"]);
  const id = string(body["id"], { max: 64, pattern: PACK_ID_PATTERN });
  const empty: PackFields = { name: "", author: "", licenceNote: "", credit: null, fallback: false, behaviourArt: false, thirdParty: false, permission: null };
  const fields = readPackFields(body, empty, ctx.now);
  const db = ctx.env.DB;
  if (await db.prepare("SELECT 1 FROM packs WHERE id = ?").bind(id).first()) throw new HttpError(409, "pack_exists");
  const count = await db.prepare("SELECT COUNT(*) AS n FROM packs").first<{ n: number }>();
  if ((count?.n ?? 0) >= MAX_PACKS) throw new HttpError(409, "index_limits");
  await assertSingleFallback(db, id, fields.fallback);
  await writeAudited(
    db,
    ctx.now,
    { actor, action: "pack.create", target: `pack:${id}`, after: { fallback: fields.fallback, behaviourArt: fields.behaviourArt, thirdParty: fields.thirdParty, hasPermission: fields.permission !== null } },
    [
      db
        .prepare(
          "INSERT INTO packs (id, name, author, licence_note, credit, fallback, behaviour_art, third_party, permission_creator, permission_date, permission_proof, status, version, changed, created_at, updated_at) " +
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'draft', 0, 0, ?, ?)",
        )
        .bind(id, fields.name, fields.author, fields.licenceNote, fields.credit, fields.fallback ? 1 : 0, fields.behaviourArt ? 1 : 0, fields.thirdParty ? 1 : 0, fields.permission?.creator ?? null, fields.permission?.date ?? null, fields.permission?.proof ?? null, ctx.now, ctx.now),
    ],
  );
  return packResponse(ctx, id);
}

export async function updatePack(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = packIdParam(ctx);
  const body = object(await readJsonBody(ctx.request, 4096), PACK_FIELDS, []);
  const db = ctx.env.DB;
  const row = await db.prepare("SELECT * FROM packs WHERE id = ?").bind(id).first<PackRow>();
  if (!row) throw new HttpError(404, "not_found");
  const before = fieldsFromRow(row);
  const after = readPackFields(body, before, ctx.now);
  const changed = PACK_FIELDS.filter((k) => JSON.stringify(before[k]) !== JSON.stringify(after[k]));
  if (changed.length === 0) return packResponse(ctx, id);
  await assertSingleFallback(db, id, after.fallback);
  await writeAudited(db, ctx.now, { actor, action: "pack.update", target: `pack:${id}`, after: { fields: changed, fallback: after.fallback, behaviourArt: after.behaviourArt, thirdParty: after.thirdParty, hasPermission: after.permission !== null } }, [
    db
      .prepare(
        "UPDATE packs SET name = ?, author = ?, licence_note = ?, credit = ?, fallback = ?, behaviour_art = ?, third_party = ?, permission_creator = ?, permission_date = ?, permission_proof = ?, " +
          "changed = CASE WHEN status = 'published' THEN 1 ELSE changed END, updated_at = ? WHERE id = ?",
      )
      .bind(after.name, after.author, after.licenceNote, after.credit, after.fallback ? 1 : 0, after.behaviourArt ? 1 : 0, after.thirdParty ? 1 : 0, after.permission?.creator ?? null, after.permission?.date ?? null, after.permission?.proof ?? null, ctx.now, id),
  ]);
  return packResponse(ctx, id);
}

/** Deleting needs the pack id repeated in the body, and a pack that is not published (unpublish it first). */
export async function deletePack(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = packIdParam(ctx);
  const body = object(await readJsonBody(ctx.request, 1024), ["confirm"]);
  if (body["confirm"] !== id) throw new HttpError(400, "confirmation_mismatch");
  const db = ctx.env.DB;
  const row = await db.prepare("SELECT status FROM packs WHERE id = ?").bind(id).first<{ status: string }>();
  if (!row) throw new HttpError(404, "not_found");
  if (row.status === "published") throw new HttpError(409, "pack_published");
  const { results } = await db.prepare("SELECT sha256 FROM pack_entries WHERE pack_id = ?").bind(id).all<{ sha256: string }>();
  await writeAudited(
    db,
    ctx.now,
    { actor, action: "pack.delete", target: `pack:${id}`, before: { entries: results.length } },
    [db.prepare("DELETE FROM pack_entries WHERE pack_id = ?").bind(id), db.prepare("DELETE FROM packs WHERE id = ? AND status <> 'published'").bind(id)],
  );
  const filesDeleted = await releaseObjects(db, ctx.env.PACKS, results.map((r) => r.sha256));
  return json({ deleted: true, filesDeleted });
}

export async function publishPack(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = packIdParam(ctx);
  const db = ctx.env.DB;
  const { pack, entries } = await loadPack(ctx, id);
  const fields = fieldsFromRow(pack);
  if (entries.length === 0) throw new HttpError(409, "pack_empty");
  // FR-PACK-08: a third-party pack is published only with the creator's permission on record, and credits them.
  if (fields.thirdParty && (fields.permission === null || fields.credit === null)) throw new HttpError(409, "permission_required");
  if (entries.reduce((sum, e) => sum + e.size, 0) > MAX_PACK_BYTES) throw new HttpError(409, "pack_too_large");
  const version = pack.version + 1;
  const snapshot: PublishedPack = { id, name: fields.name, author: fields.author, licenceNote: fields.licenceNote, credit: fields.credit, fallback: fields.fallback, behaviourArt: fields.behaviourArt, default: pack.is_default === 1, version };
  const snapshotEntries: PublishedEntry[] = entries.map((e) => ({
    id: e.id,
    pack: id,
    kind: e.kind,
    key: e.key,
    sha256: e.sha256,
    format: e.format,
    size: e.size,
    width: e.width,
    height: e.height,
    licensedHolder: e.licensed_holder,
    withdrawn: e.withdrawn === 1,
    sourceSha256: e.source_sha256,
  }));
  // Entries that the previous published copy had and this one drops may leave files unreferenced.
  const { results: previous } = await db.prepare("SELECT sha256 FROM published_entries WHERE pack_id = ?").bind(id).all<{ sha256: string }>();
  const statements = [
    db.prepare("DELETE FROM published_entries WHERE pack_id = ?").bind(id),
    ...snapshotEntries.map((e) =>
      db
        .prepare("INSERT INTO published_entries (id, pack_id, kind, key, sha256, format, size, width, height, licensed_holder, withdrawn, source_sha256) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)")
        .bind(e.id, id, e.kind, e.key, e.sha256, e.format, e.size, e.width, e.height, e.licensedHolder, e.withdrawn ? 1 : 0, e.sourceSha256),
    ),
    db
      .prepare("INSERT OR REPLACE INTO published_packs (pack_id, name, author, licence_note, credit, fallback, behaviour_art, is_default, version) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)")
      .bind(id, snapshot.name, snapshot.author, snapshot.licenceNote, snapshot.credit, snapshot.fallback ? 1 : 0, snapshot.behaviourArt ? 1 : 0, snapshot.default ? 1 : 0, version),
    // An edit that landed after this request read the pack keeps the "changed since publish" mark.
    db.prepare("UPDATE packs SET status = 'published', version = ?, published_at = ?, changed = CASE WHEN updated_at = ? THEN 0 ELSE 1 END WHERE id = ?").bind(version, ctx.now, pack.updated_at, id),
  ];
  const result = await commitIndexChange(db, ctx.now, {
    apply: (packs, published) => ({ packs: [...packs.filter((p) => p.id !== id), snapshot], entries: [...published.filter((e) => e.pack !== id), ...snapshotEntries] }),
    statements,
    audit: {
      actor,
      action: "pack.publish",
      target: `pack:${id}`,
      before: { status: pack.status, version: pack.version },
      after: { status: "published", version, entries: snapshotEntries.length, withdrawn: snapshotEntries.filter((e) => e.withdrawn).length, fallback: fields.fallback, behaviourArt: fields.behaviourArt, default: snapshot.default },
    },
  });
  await clearIndexCache(ctx);
  await releaseObjects(db, ctx.env.PACKS, previous.map((r) => r.sha256));
  return packResponse(ctx, id, { index: { version: result.version, publishedAt: iso(ctx.now) } });
}

export async function unpublishPack(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = packIdParam(ctx);
  const db = ctx.env.DB;
  const row = await db.prepare("SELECT status FROM packs WHERE id = ?").bind(id).first<{ status: string }>();
  if (!row) throw new HttpError(404, "not_found");
  if (row.status !== "published") throw new HttpError(409, "pack_not_published");
  const { results: previous } = await db.prepare("SELECT sha256 FROM published_entries WHERE pack_id = ?").bind(id).all<{ sha256: string }>();
  const result = await commitIndexChange(db, ctx.now, {
    apply: (packs, entries) => ({ packs: packs.filter((p) => p.id !== id), entries: entries.filter((e) => e.pack !== id) }),
    statements: [
      db.prepare("DELETE FROM published_entries WHERE pack_id = ?").bind(id),
      db.prepare("DELETE FROM published_packs WHERE pack_id = ?").bind(id),
      db.prepare("UPDATE packs SET status = 'unpublished', changed = 0 WHERE id = ?").bind(id),
    ],
    audit: { actor, action: "pack.unpublish", target: `pack:${id}`, before: { status: "published" }, after: { status: "unpublished" } },
  });
  await clearIndexCache(ctx);
  await releaseObjects(db, ctx.env.PACKS, previous.map((r) => r.sha256));
  return packResponse(ctx, id, { index: { version: result.version, publishedAt: iso(ctx.now) } });
}

// ---------------------------------------------------------------------------------------------------------------
// Files

const IMAGE_TYPES = new Set(["image/png", "image/jpeg"]);

/**
 * Stores one image (the raw request body, `Content-Type: image/png` or `image/jpeg`). Metadata is removed losslessly
 * and the result must pass the strict check and the app's own rules (src/images/pack-image.ts); the stored file is
 * the cleaned one, named by its SHA-256, and the same bytes uploaded twice are one file. `removed` lists what was
 * taken out. 422 `image_rejected` names the rule that refused it; nothing is stored then.
 */
export async function uploadObject(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const type = (ctx.request.headers.get("Content-Type") ?? "").split(";")[0]!.trim().toLowerCase();
  if (!IMAGE_TYPES.has(type)) throw new HttpError(415, "unsupported_media_type");
  const received = await readBodyBytes(ctx.request, MAX_IMAGE_BYTES);
  const check = preparePackImage(received);
  if (!check.ok) return json({ error: "image_rejected", reason: check.reason }, 422);
  const { bytes, removed } = check;
  // The file as the panel sent it (after the panel's own PNG conversion, if any), before this clean-up: an entry may record it as sourceSha256 (S5b-3).
  const sourceSha = await sha256Hex(received);
  const { format, width, height } = check.info;
  if (type !== (format === "png" ? "image/png" : "image/jpeg")) return json({ error: "image_rejected", reason: "type_mismatch" }, 422);
  const sha = await sha256Hex(bytes);
  const db = ctx.env.DB;
  const existing = await db.prepare("SELECT sha256 FROM pack_objects WHERE sha256 = ?").bind(sha).first();
  // The bytes are reserved under the storage cap (D18, src/storage.ts; 507 storage_full above it), then the object is
  // written, then its row, so a row always has a file (an interrupted upload leaves only a reservation and a file,
  // which the purge removes).
  await putWithinCap(db, ctx.env.PACKS, "packs", objectName(sha, format), bytes, { httpMetadata: { contentType: format === "png" ? "image/png" : "image/jpeg" }, sha256: sha }, ctx.now, ctx.env.ENVIRONMENT);
  if (!existing) {
    await writeAudited(db, ctx.now, { actor, action: "pack.file_upload", target: `file:${sha}`, after: { format, size: bytes.length, width, height, removed } }, [
      db.prepare("INSERT OR IGNORE INTO pack_objects (sha256, format, size, width, height, created_at) VALUES (?, ?, ?, ?, ?, ?)").bind(sha, format, bytes.length, width, height, ctx.now),
    ]);
  }
  return json({ file: { sha256: sha, sourceSha256: sourceSha, format, size: bytes.length, width, height, url: cdnUrl(ctx, sha, format) }, removed });
}

export async function purgeFiles(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  object(await readJsonBody(ctx.request, 256), []);
  const result = await purgeObjects(ctx.env.DB, ctx.env.PACKS, ctx.now);
  await writeAudited(ctx.env.DB, ctx.now, { actor, action: "pack.purge", target: null, after: result });
  return json({ purged: result });
}

// ---------------------------------------------------------------------------------------------------------------
// Entries

function newEntryId(): string {
  return `e${toHex(randomBytes(12))}`;
}

function kindOf(key: string): EntryKind {
  return key.slice(0, key.indexOf(":")) as EntryKind;
}

async function requireFile(ctx: Context, sha: string): Promise<{ format: "png" | "jpeg"; size: number }> {
  const row = await ctx.env.DB.prepare("SELECT format, size FROM pack_objects WHERE sha256 = ?").bind(sha).first<{ format: "png" | "jpeg"; size: number }>();
  if (!row || !(await ctx.env.PACKS.head(objectName(sha, row.format)))) throw new HttpError(409, "file_missing");
  return row;
}

async function draftBytes(db: D1Database, packId: string, exceptEntry: string | null): Promise<number> {
  const row = await db
    .prepare("SELECT COALESCE(SUM(o.size), 0) AS n FROM pack_entries e JOIN pack_objects o ON o.sha256 = e.sha256 WHERE e.pack_id = ? AND e.id <> ?")
    .bind(packId, exceptEntry ?? "")
    .first<{ n: number }>();
  return row?.n ?? 0;
}

function markChanged(db: D1Database, packId: string, now: number): D1PreparedStatement {
  return db.prepare("UPDATE packs SET changed = CASE WHEN status = 'published' THEN 1 ELSE changed END, updated_at = ? WHERE id = ?").bind(now, packId);
}

export async function addEntry(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const packId = packIdParam(ctx);
  const body = object(await readJsonBody(ctx.request, 1024), ["key", "sha256", "licensedHolder", "sourceSha256"], ["key", "sha256"]);
  const key = string(body["key"], { max: 74, pattern: KEY_PATTERN });
  const sha = string(body["sha256"], { max: 64, pattern: SHA256_PATTERN });
  const holder = body["licensedHolder"] === undefined ? null : optionalText(body["licensedHolder"], TEXT_LIMITS.holder);
  const sourceSha = body["sourceSha256"] === undefined ? null : optionalSha(body["sourceSha256"]);
  const db = ctx.env.DB;
  if (!(await db.prepare("SELECT 1 FROM packs WHERE id = ?").bind(packId).first())) throw new HttpError(404, "not_found");
  if (await db.prepare("SELECT 1 FROM pack_entries WHERE pack_id = ? AND key = ?").bind(packId, key).first()) throw new HttpError(409, "key_exists");
  const file = await requireFile(ctx, sha);
  const total = await db.prepare("SELECT COUNT(*) AS n FROM pack_entries").first<{ n: number }>();
  if ((total?.n ?? 0) >= MAX_ENTRIES) throw new HttpError(409, "index_limits");
  if ((await draftBytes(db, packId, null)) + file.size > MAX_PACK_BYTES) throw new HttpError(409, "pack_too_large");
  const id = newEntryId();
  await writeAudited(db, ctx.now, { actor, action: "pack.entry_add", target: `pack:${packId}`, after: { entry: id, key, sha256: sha, sourceSha256: sourceSha, licensed: holder !== null } }, [
    db
      .prepare("INSERT INTO pack_entries (id, pack_id, kind, key, sha256, licensed_holder, withdrawn, source_sha256, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, 0, ?, ?, ?)")
      .bind(id, packId, kindOf(key), key, sha, holder, sourceSha, ctx.now, ctx.now),
    markChanged(db, packId, ctx.now),
  ]);
  return packResponse(ctx, packId);
}

async function loadEntry(ctx: Context, packId: string, entryId: string): Promise<EntryRow> {
  const row = await ctx.env.DB.prepare(`${ENTRY_SELECT} WHERE e.id = ? AND e.pack_id = ?`).bind(entryId, packId).first<EntryRow>();
  if (!row) throw new HttpError(404, "not_found");
  return row;
}

/** Changes an entry's key, image (replace) or rights holder. */
export async function updateEntry(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const packId = packIdParam(ctx);
  const entryId = entryIdParam(ctx);
  const body = object(await readJsonBody(ctx.request, 1024), ["key", "sha256", "licensedHolder", "sourceSha256"], []);
  const entry = await loadEntry(ctx, packId, entryId);
  const key = body["key"] === undefined ? entry.key : string(body["key"], { max: 74, pattern: KEY_PATTERN });
  const sha = body["sha256"] === undefined ? entry.sha256 : string(body["sha256"], { max: 64, pattern: SHA256_PATTERN });
  const holder = body["licensedHolder"] === undefined ? entry.licensed_holder : optionalText(body["licensedHolder"], TEXT_LIMITS.holder);
  // A replaced image keeps no source hash unless one is sent with it.
  const sourceSha = body["sourceSha256"] !== undefined ? optionalSha(body["sourceSha256"]) : sha !== entry.sha256 ? null : entry.source_sha256;
  const db = ctx.env.DB;
  if (key !== entry.key && (await db.prepare("SELECT 1 FROM pack_entries WHERE pack_id = ? AND key = ?").bind(packId, key).first())) throw new HttpError(409, "key_exists");
  // A withdrawn item stays licensed until it is restored, so clearing the holder can never re-publish it by accident.
  if (entry.withdrawn === 1 && holder === null) throw new HttpError(409, "entry_withdrawn");
  if (sha !== entry.sha256) {
    const file = await requireFile(ctx, sha);
    if ((await draftBytes(db, packId, entryId)) + file.size > MAX_PACK_BYTES) throw new HttpError(409, "pack_too_large");
  }
  const changed = [key !== entry.key && "key", sha !== entry.sha256 && "image", holder !== entry.licensed_holder && "licensedHolder", sourceSha !== entry.source_sha256 && "sourceSha256"].filter(
    (x): x is string => typeof x === "string",
  );
  if (changed.length > 0) {
    await writeAudited(
      db,
      ctx.now,
      { actor, action: "pack.entry_update", target: `pack:${packId}`, before: { entry: entryId, key: entry.key, sha256: entry.sha256, licensed: entry.licensed_holder !== null }, after: { entry: entryId, key, sha256: sha, licensed: holder !== null, fields: changed } },
      [
        db.prepare("UPDATE pack_entries SET kind = ?, key = ?, sha256 = ?, licensed_holder = ?, source_sha256 = ?, updated_at = ? WHERE id = ?").bind(kindOf(key), key, sha, holder, sourceSha, ctx.now, entryId),
        markChanged(db, packId, ctx.now),
      ],
    );
    if (sha !== entry.sha256) await releaseObjects(db, ctx.env.PACKS, [entry.sha256]);
  }
  return packResponse(ctx, packId);
}

/**
 * Removes an entry from the draft. A withdrawn entry is already out of the signed index, so it also leaves the
 * published copy at once: its file is then deleted if nothing else uses it (taking a licensed item down for good).
 */
export async function removeEntry(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const packId = packIdParam(ctx);
  const entryId = entryIdParam(ctx);
  const entry = await loadEntry(ctx, packId, entryId);
  const db = ctx.env.DB;
  const statements = [db.prepare("DELETE FROM pack_entries WHERE id = ?").bind(entryId), markChanged(db, packId, ctx.now)];
  if (entry.withdrawn === 1) statements.push(db.prepare("DELETE FROM published_entries WHERE id = ? AND withdrawn = 1").bind(entryId));
  await writeAudited(db, ctx.now, { actor, action: "pack.entry_remove", target: `pack:${packId}`, before: { entry: entryId, key: entry.key, sha256: entry.sha256, withdrawn: entry.withdrawn === 1 } }, statements);
  const filesDeleted = await releaseObjects(db, ctx.env.PACKS, [entry.sha256]);
  return packResponse(ctx, packId, { filesDeleted });
}

/**
 * Withdraws (or restores) a licensed item (FR-PACK-04). When the item is in the published copy, a new index version
 * without it (or with it again) is committed at once; the rest of the pack stays as published.
 */
export async function withdrawEntry(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const packId = packIdParam(ctx);
  const entryId = entryIdParam(ctx);
  const body = object(await readJsonBody(ctx.request, 256), ["withdrawn"]);
  const withdrawn = boolean(body["withdrawn"]);
  const entry = await loadEntry(ctx, packId, entryId);
  if (entry.licensed_holder === null) throw new HttpError(409, "not_licensed");
  if ((entry.withdrawn === 1) === withdrawn) return packResponse(ctx, packId);
  const db = ctx.env.DB;
  const flag = withdrawn ? 1 : 0;
  const statements = [
    db.prepare("UPDATE pack_entries SET withdrawn = ?, updated_at = ? WHERE id = ?").bind(flag, ctx.now, entryId),
    db.prepare("UPDATE published_entries SET withdrawn = ? WHERE id = ?").bind(flag, entryId),
  ];
  const audit = { actor, action: withdrawn ? "pack.entry_withdraw" : "pack.entry_restore", target: `pack:${packId}`, before: { entry: entryId, key: entry.key, withdrawn: !withdrawn }, after: { entry: entryId, key: entry.key, withdrawn } };
  const published = await db.prepare("SELECT 1 FROM published_entries WHERE id = ?").bind(entryId).first();
  let index: Record<string, unknown> | null = null;
  if (published) {
    const result = await commitIndexChange(db, ctx.now, {
      apply: (packs, entries) => ({ packs, entries: entries.map((e) => (e.id === entryId ? { ...e, withdrawn } : e)) }),
      statements,
      audit,
    });
    await clearIndexCache(ctx);
    index = { version: result.version, publishedAt: iso(ctx.now) };
  } else {
    await writeAudited(db, ctx.now, audit, statements);
  }
  return packResponse(ctx, packId, { index });
}

// ---------------------------------------------------------------------------------------------------------------
// The default pack (PLAN_V2_S5B §2.5, S5b-3)

/**
 * Marks one pack as the default pictures for characters, or clears the mark. At most one pack carries it (409
 * `default_exists` for a second). For a published pack the change goes live at once in a new index version, like a
 * withdrawal; for a draft it goes live at its Publish.
 */
export async function setDefaultPack(ctx: Context): Promise<Response> {
  const actor = await requireAdmin(ctx);
  const id = packIdParam(ctx);
  const isDefault = boolean(object(await readJsonBody(ctx.request, 256), ["default"])["default"]);
  const db = ctx.env.DB;
  const row = await db.prepare("SELECT is_default FROM packs WHERE id = ?").bind(id).first<{ is_default: number }>();
  if (!row) throw new HttpError(404, "not_found");
  if ((row.is_default === 1) === isDefault) return packResponse(ctx, id, { index: null });
  if (isDefault && (await db.prepare("SELECT 1 FROM packs WHERE is_default = 1 AND id <> ?").bind(id).first())) throw new HttpError(409, "default_exists");
  const flag = isDefault ? 1 : 0;
  const statements = [
    // Set only while no other pack holds the mark, so two requests at once cannot both set it.
    db.prepare("UPDATE packs SET is_default = ? WHERE id = ? AND (? = 0 OR NOT EXISTS (SELECT 1 FROM packs WHERE is_default = 1 AND id <> ?))").bind(flag, id, flag, id),
    db.prepare("UPDATE published_packs SET is_default = ? WHERE pack_id = ?").bind(flag, id),
  ];
  const audit = { actor, action: "pack.default", target: `pack:${id}`, before: { default: !isDefault }, after: { default: isDefault } };
  const published = await db.prepare("SELECT 1 FROM published_packs WHERE pack_id = ?").bind(id).first();
  let index: Record<string, unknown> | null = null;
  if (published) {
    // A second default in the published set is refused by indexProblem (409 index_limits).
    const result = await commitIndexChange(db, ctx.now, {
      apply: (packs, entries) => ({ packs: packs.map((p) => (p.id === id ? { ...p, default: isDefault } : p)), entries }),
      statements,
      audit,
    });
    await clearIndexCache(ctx);
    index = { version: result.version, publishedAt: iso(ctx.now) };
  } else {
    await writeAudited(db, ctx.now, audit, statements);
  }
  const after = await db.prepare("SELECT is_default FROM packs WHERE id = ?").bind(id).first<{ is_default: number }>();
  if ((after?.is_default === 1) !== isDefault) throw new HttpError(409, "default_exists");
  return packResponse(ctx, id, { index });
}
