// Local preview server for development and screenshots only (never deployed). Serves dist/ with the headers from
// public/_headers (so the browser enforces the real CSP) and answers /api/* from in-memory sample data shaped like
// service/src/routes/admin.ts. It applies the same request checks as the Worker: the X-DBDP-Admin header on every
// call, and the exact Origin plus `Sec-Fetch-Site: same-origin` on state-changing calls.
//
// Usage: node scripts/mock-server.mjs [port]   (default 4173; binds to 127.0.0.1 only)

import { createHash, randomBytes, randomUUID } from "node:crypto";
import { readFileSync, existsSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, normalize } from "node:path";

const root = join(import.meta.dirname, "..");
const dist = join(root, "dist");
const port = Number(process.argv[2] ?? 4173);

const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml" };

/** Parses the Pages `_headers` file (only the `/*` block is used by the panel). */
function pagesHeaders() {
  const headers = {};
  let inBlock = false;
  for (const line of readFileSync(join(root, "public", "_headers"), "utf8").split("\n")) {
    if (line.startsWith("#") || line.trim() === "") continue;
    if (!line.startsWith(" ")) {
      inBlock = line.trim() === "/*";
      continue;
    }
    if (!inBlock) continue;
    const at = line.indexOf(":");
    headers[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  }
  return headers;
}
const STATIC_HEADERS = pagesHeaders();

// ---------------------------------------------------------------------------------------------------------------
// Sample data, relative to the time the server starts.

const DAY = 86_400_000;
const HOUR = 3_600_000;
const start = Date.now();
const iso = (ms) => (ms === null ? null : new Date(ms).toISOString());
const OWNER = "owner:owner@arcavale.com";

const people = [
  ["Hollow Lantern", -41, -2 * HOUR, 2, 0],
  ["mira.vale", -38, -5 * HOUR, 1, 0],
  ["Tobias Reed", -33, -1 * DAY, 3, 1],
  ["quietfog", -29, -3 * DAY, 1, 0],
  ["Ashgrove", -21, -26 * HOUR, 2, 0],
  ["Nell Okafor", -16, -40 * 60_000, 1, 0],
  ["static_hum", -12, -9 * DAY, 1, 1],
  ["Juniper K", -6, -7 * HOUR, 0, 0],
  ["Rook", -2, -30 * HOUR, 0, 0],
];

const users = people.map(([name, createdDays, seenAgo, deviceCount, revokedDevices], i) => {
  const created = start + createdDays * DAY;
  const devices = Array.from({ length: deviceCount }, (_, d) => ({
    id: randomUUID(),
    thumbprint: randomBytes(32).toString("base64url"),
    created_at: created + d * 3 * DAY,
    last_seen_at: start + seenAgo - d * DAY,
    revoked_at: d < revokedDevices ? start - (4 + d) * DAY : null,
  }));
  const waiting = i === 7 || i === 8;
  return {
    id: randomUUID(),
    discord_id: String(310000000000000000n + BigInt(i * 7919)),
    display_name: name,
    status: i === 6 ? "revoked" : "active",
    access_granted_at: waiting ? null : created + HOUR,
    access_via: waiting ? null : "invite",
    created_at: created,
    last_seen_at: start + seenAgo,
    revoked_at: i === 6 ? start - 4 * DAY : null,
    devices,
  };
});

const notes = ["Discord server mods", "Friends from Thursday lobbies", null, "Creator programme", "Tobias's duo partner", null, "Backup code for Nell", "Community night giveaway", null, "Beta testers wave 2", null, "Spare"];
const invites = notes.map((note, i) => {
  const created = start - (44 - i * 3.5) * DAY;
  const redeemer = i < 7 ? users[i] : null;
  return {
    id: randomUUID(),
    hmac_kid: "k1",
    note,
    created_at: created,
    created_by: OWNER,
    expires_at: i === 8 ? start - 2 * DAY : created + 30 * DAY + (i > 8 ? 30 * DAY : 0),
    redeemed_by: redeemer ? redeemer.id : null,
    redeemed_at: redeemer ? redeemer.created_at + HOUR : null,
    revoked_at: i === 9 ? start - 1 * DAY : null,
  };
});
invites.reverse();

let settings = { accessMode: "invite-only", deviceLimit: 3, disableCapture: false, disableDetection: true, emergencyStop: false, minimumVersion: "0.3.0", banner: "Pack sync is paused on Sunday from 02:00 to 03:00 UK time for maintenance.", uploadsBlocked: false, uploadLimitBytes: 62914560, uploadLimitFiles: 300 };
let configVersion = Math.floor((start - 3 * DAY) / 1000);

// Packs (shapes from service/src/routes/packs.ts). File hashes are derived from the slot so the screenshot script can
// draw a matching sample tile for each address.
const CDN = "https://cdn.arcavale.com";
const tileSha = (pack, key) => createHash("sha256").update(`${pack}|${key}`).digest("hex");
let entrySeq = 1;
function entry(pack, key, { format = "png", size = 38_000, holder = null, withdrawn = false, live = true, width = 1024, height = 1024 } = {}) {
  const sha256 = tileSha(pack, key);
  return { id: `e${(entrySeq++).toString(16).padStart(24, "0")}`, kind: key.slice(0, key.indexOf(":")), key, sha256, format, size, width, height, licensedHolder: holder, withdrawn, live: live && !withdrawn, url: `${CDN}/packs/${sha256}.${format === "png" ? "png" : "jpg"}`, createdAt: iso(start - 12 * DAY), updatedAt: iso(start - 12 * DAY) };
}
function makePack(id, fields, entries) {
  return { id, credit: null, fallback: false, behaviourArt: false, default: false, thirdParty: false, permission: null, status: "draft", version: 0, changedSincePublish: false, createdAt: iso(start - 30 * DAY), updatedAt: iso(start - 2 * DAY), publishedAt: null, ...fields, totalBytes: entries.reduce((n, e) => n + e.size, 0), entries };
}
const packs = [
  makePack(
    "original",
    { name: "Original", author: "Behaviour Interactive", licenceNote: "Dead by Daylight artwork © Behaviour Interactive Inc. Unofficial fan content.", behaviourArt: true, status: "published", version: 4, publishedAt: iso(start - 2 * DAY) },
    [
      entry("original", "role:killer", { size: 31_200 }),
      entry("original", "role:survivor", { size: 29_800 }),
      entry("original", "character:the-trapper", { format: "jpeg", size: 52_400 }),
      entry("original", "character:the-wraith", { format: "jpeg", size: 49_900 }),
      entry("original", "character:the-nurse", { format: "jpeg", size: 54_100 }),
      entry("original", "character:the-huntress", { format: "jpeg", size: 51_300 }),
      entry("original", "character:the-ghost-face", { format: "jpeg", size: 57_700, holder: "Example Studio" }),
      entry("original", "character:the-shape", { format: "jpeg", size: 63_900, holder: "Example Films", withdrawn: true }),
    ],
  ),
  makePack(
    "backup",
    { name: "Backup tiles", author: "DBD Presence", licenceNote: "Line art made for DBD Presence.", fallback: true, status: "published", version: 2, changedSincePublish: true, publishedAt: iso(start - 9 * DAY) },
    [entry("backup", "map:generic", { size: 18_400, height: 576 }), entry("backup", "character:generic-killer", { size: 16_900 }), entry("backup", "character:generic-survivor", { size: 17_200, live: false })],
  ),
  makePack(
    "community-night",
    { name: "Community night", author: "Ash Corvid", licenceNote: "Shared by its creator for DBD Presence.", credit: "Pack by Ash Corvid, used with permission", thirdParty: true, permission: { creator: "Ash Corvid", date: iso(start - 6 * DAY).slice(0, 10), proof: "Email of the 18th, saved offline" } },
    [entry("community-night", "role:killer", { size: 44_000, live: false }), entry("community-night", "role:survivor", { size: 41_500, live: false })],
  ),
];
let index = { version: Math.floor((start - 2 * DAY) / 1000), publishedAt: iso(start - 2 * DAY) };
const indexSummary = () => {
  const live = packs.filter((p) => p.status === "published");
  return { ...index, packs: live.length, entries: live.reduce((n, p) => n + p.entries.filter((e) => e.live).length, 0) };
};
const bumpIndex = () => {
  index = { version: Math.max(index.version + 1, Math.floor(Date.now() / 1000)), publishedAt: iso(Date.now()) };
  return { version: index.version, publishedAt: index.publishedAt };
};
let apps = [
  { key: "dbd", applicationId: "1563291325107937340", name: "Dead by Daylight", description: "Unofficial fan-made presence — not affiliated with Behaviour Interactive" },
  { key: "dbd-event", applicationId: "1563291325107938211", name: "Dead by Daylight (event)", description: "Used during community events" },
];
let defaultApp = "dbd";
const appsBody = () => ({ applications: apps, defaultApplication: defaultApp, configVersion });

const GENESIS = "0".repeat(64);
const audit = [];
function addAudit(at, actor, action, target, before, after) {
  const prev = audit.length ? audit[audit.length - 1].hash : GENESIS;
  const beforeJson = before === undefined ? null : JSON.stringify(before);
  const afterJson = after === undefined ? null : JSON.stringify(after);
  const hash = createHash("sha256").update(JSON.stringify([prev, at, actor, action, target, beforeJson, afterJson])).digest("hex");
  audit.push({ seq: audit.length + 1, at, actor, action, target, before_json: beforeJson, after_json: afterJson, prev_hash: prev, hash });
}
const history = [];
for (const invite of [...invites].reverse()) history.push([invite.created_at, OWNER, "invite.create", null, undefined, { ids: [invite.id], hasNote: invite.note !== null, expiresAt: iso(invite.expires_at) }]);
for (const invite of invites) if (invite.redeemed_by) history.push([invite.redeemed_at, `user:${invite.redeemed_by}`, "invite.redeem", `invite:${invite.id}`]);
for (const user of users) for (const d of user.devices) if (d.revoked_at) history.push([d.revoked_at, OWNER, "device.revoke", `user:${user.id}`, { device: d.id, status: "active" }, { device: d.id, status: "revoked" }]);
history.push([users[6].revoked_at, OWNER, "user.revoke", `user:${users[6].id}`, { status: "active" }, { status: "revoked" }]);
history.push([invites.find((i) => i.revoked_at).revoked_at, OWNER, "invite.revoke", `invite:${invites.find((i) => i.revoked_at).id}`, { status: "active" }, { status: "revoked" }]);
history.push([start - 3 * DAY, OWNER, "user.export", `user:${users[2].id}`]);
history.push([start - 2 * DAY, "system", "session.refresh_token_reused", `user:${users[3].id}`, undefined, { family: randomUUID() }]);
history.push([start - 20 * DAY, OWNER, "settings.update", "settings", { deviceLimit: 2 }, { deviceLimit: 3 }]);
for (let i = 0; i < 46; i++) history.push([start - (60 - i) * DAY, OWNER, "invite.create", null, undefined, { ids: [randomUUID()], hasNote: false, expiresAt: iso(start - (30 - i) * DAY) }]);
history.sort((a, b) => a[0] - b[0]);
for (const h of history) addAudit(...h);

// ---------------------------------------------------------------------------------------------------------------
// Admin API (shapes from service/src/routes/admin.ts)

function inviteStatus(r) {
  if (r.redeemed_at !== null) return "redeemed";
  if (r.revoked_at !== null) return "revoked";
  if (r.expires_at <= Date.now()) return "expired";
  return "active";
}
const inviteView = (r) => ({ id: r.id, keyId: r.hmac_kid, note: r.note, status: inviteStatus(r), createdAt: iso(r.created_at), createdBy: r.created_by, expiresAt: iso(r.expires_at), redeemedBy: r.redeemed_by, redeemedAt: iso(r.redeemed_at), revokedAt: iso(r.revoked_at) });
const deviceView = (d) => ({ id: d.id, thumbprint: d.thumbprint, createdAt: iso(d.created_at), lastSeenAt: iso(d.last_seen_at), revokedAt: iso(d.revoked_at) });
const userView = (u) => ({ id: u.id, discordId: u.discord_id, displayName: u.display_name, status: u.status, access: u.access_granted_at !== null ? "granted" : "invite_required", accessVia: u.access_via, createdAt: iso(u.created_at), lastSeenAt: iso(u.last_seen_at), revokedAt: iso(u.revoked_at), devices: u.devices.map(deviceView), uploads: uploads.filter((x) => x.user_id === u.id).length, uploadsBlocked: u.uploads_blocked === true, rosterReporter: u.roster_reporter === true });

// User uploads (FR-ADM-09): a few per active user. The addresses follow the real /u/ shape; the mock has no content host.
let uploads = users
  .filter((u) => u.status === "active" && u.access_granted_at !== null)
  .slice(0, 5)
  .flatMap((u, i) =>
    Array.from({ length: (i % 3) + 1 }, (_, j) => ({
      id: randomUUID(),
      user_id: u.id,
      key: `${randomBytes(16).toString("hex")}.${j % 2 === 0 ? "png" : "jpg"}`,
      sha256: randomBytes(32).toString("hex"),
      format: j % 2 === 0 ? "png" : "jpeg",
      size: 30_000 + 9_000 * (i + j),
      width: 512,
      height: 512,
      created_at: start - (i * 2 + j) * DAY,
    })),
  );
const uploadView = (x) => {
  const u = users.find((y) => y.id === x.user_id);
  return { id: x.id, url: `https://cdn.arcavale.com/u/${x.key}`, sha256: x.sha256, format: x.format, size: x.size, width: x.width, height: x.height, createdAt: iso(x.created_at), user: { id: u.id, displayName: u.display_name, uploadsBlocked: u.uploads_blocked === true } };
};
const storageView = () => {
  const uploadBytes = uploads.reduce((n, x) => n + x.size, 0);
  const packBytes = 2_150_000_000;
  const used = packBytes + uploadBytes;
  return { usedBytes: used, capBytes: 8_000_000_000, warnBytes: 6_000_000_000, warning: used >= 6_000_000_000, full: used >= 8_000_000_000, uploadCapBytes: 6_000_000_000, uploadsFull: used >= 6_000_000_000, pendingDeleteBytes: 0, areas: { packs: { bytes: packBytes, objects: 412 }, uploads: { bytes: uploadBytes, objects: uploads.length }, releases: { bytes: 0, objects: 0 }, audit: { bytes: 1_200_000, objects: 44 } } };
};
const auditView = (r) => ({ seq: r.seq, at: iso(r.at), actor: r.actor, action: r.action, target: r.target, before: r.before_json && JSON.parse(r.before_json), after: r.after_json && JSON.parse(r.after_json), prevHash: r.prev_hash, hash: r.hash });

class HttpError extends Error {
  constructor(status, code) {
    super(code);
    this.status = status;
    this.code = code;
  }
}

const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const newCode = () => Array.from(randomBytes(16), (b) => ALPHABET[b & 31]).join("").match(/.{4}/g).join("-");

function route(method, path, query, body) {
  let m;
  if (method === "GET" && path === "/api/invites") return { invites: invites.map(inviteView) };
  if (method === "POST" && path === "/api/invites") {
    const count = body.count ?? 1;
    const days = body.expiresInDays ?? 30;
    if (!Number.isInteger(count) || count < 1 || count > 100 || !Number.isInteger(days) || days < 1 || days > 90) throw new HttpError(400, "invalid_request");
    const now = Date.now();
    const created = Array.from({ length: count }, () => ({ id: randomUUID(), code: newCode() }));
    for (const c of created) invites.unshift({ id: c.id, hmac_kid: "k1", note: body.note ?? null, created_at: now, created_by: OWNER, expires_at: now + days * DAY, redeemed_by: null, redeemed_at: null, revoked_at: null });
    addAudit(now, OWNER, "invite.create", null, undefined, { ids: created.map((c) => c.id), hasNote: (body.note ?? null) !== null, expiresAt: iso(now + days * DAY) });
    return { invites: created.map((c) => ({ ...c, note: body.note ?? null, expiresAt: iso(now + days * DAY) })) };
  }
  if (method === "POST" && (m = /^\/api\/invites\/([0-9a-f-]{36})\/revoke$/.exec(path))) {
    const row = invites.find((i) => i.id === m[1]);
    if (!row) throw new HttpError(404, "not_found");
    if (row.redeemed_at !== null || row.revoked_at !== null) throw new HttpError(409, "invite_not_revocable");
    row.revoked_at = Date.now();
    addAudit(row.revoked_at, OWNER, "invite.revoke", `invite:${row.id}`, { status: "active" }, { status: "revoked" });
    return { invite: inviteView(row) };
  }
  if (method === "GET" && path === "/api/users") return { users: users.map(userView) };
  if (method === "GET" && path === "/api/uploads") {
    const sorted = [...uploads].sort((a, b) => b.created_at - a.created_at);
    return { uploads: sorted.map(uploadView), total: uploads.length, blockedUsers: users.filter((u) => u.uploads_blocked === true).map((u) => ({ id: u.id, displayName: u.display_name })), settings: { uploadsBlocked: settings.uploadsBlocked, uploadLimitBytes: settings.uploadLimitBytes, uploadLimitFiles: settings.uploadLimitFiles }, storage: storageView() };
  }
  if (method === "GET" && path === "/api/storage") return { storage: storageView() };
  if (method === "GET" && path === "/api/overview") {
    // Dashboard and Settings (V2-S7, service/src/routes/overview.ts): counts from the sample data; backups not recorded (all null).
    const now = Date.now();
    const active = users.filter((u) => u.status === "active");
    return {
      environment: "production",
      hosts: { api: "api.arcavale.com", admin: "dbdadmin.arcavale.com", cdn: "cdn.arcavale.com" },
      users: { total: users.length, withAccess: active.filter((u) => u.access_granted_at !== null).length, waitingForInvite: active.filter((u) => u.access_granted_at === null).length, revoked: users.length - active.length, activeThisWeek: active.filter((u) => u.last_seen_at >= now - 7 * DAY).length },
      invites: { pending: invites.filter((i) => inviteStatus(i) === "active").length },
      uploads: { total: uploads.length, thisWeek: uploads.filter((x) => x.created_at >= now - 7 * DAY).length },
      storage: storageView(),
      database: { sizeBytes: 3_481_600, freeLimitBytes: 500_000_000 },
      keys: {
        service: [
          { name: "licence", configured: true, kid: "licence-prod-1" },
          { name: "config", configured: true, kid: "config-prod-1" },
          { name: "content", configured: true, kid: "content-prod-1" },
          { name: "invite", configured: true, kid: "k1" },
          { name: "ban", configured: true, kid: null },
        ],
        offline: { release: ["release-prod-1"], recovery: ["recovery-prod-1"], revoked: [], configured: true },
        keyReplacement: null,
      },
      auditExport: { lastAt: iso(now - 5 * HOUR) },
      backup: { lastExportAt: null, lastExportBytes: null, lastDrillAt: null, lastDrillOk: null },
    };
  }
  if (method === "GET" && path === "/api/releases") {
    // Releases (V2-S6): one published stable feed, nothing on beta. The mock never accepts uploads: feeds are signed offline.
    const now = Date.now();
    const published = {
      id: "99999999-9999-4999-8999-999999999991",
      channel: "stable",
      feedVersion: Math.floor((now - 3 * DAY) / 1000),
      kid: "release-prod-1",
      issuedAt: iso(now - 3 * DAY),
      expiresAt: iso(now + 27 * DAY),
      expired: false,
      newest: "0.2.0",
      status: "published",
      uploadedAt: iso(now - 3 * DAY),
      uploadedBy: OWNER,
      publishedAt: iso(now - 3 * DAY),
      packages: [{ version: "0.2.0", fileName: "DbdPresence-0.2.0-stable-full.nupkg", size: 71_234_567, sha256: "c".repeat(64), present: true }],
    };
    return {
      channels: { stable: { published, staged: null }, beta: { published: null, staged: null } },
      keyReplacement: null,
      keys: { release: ["release-prod-1"], recovery: ["recovery-prod-1"], revoked: [], configured: true },
      limits: { maxReleases: 3, maxPackageBytes: 95_000_000 },
      storage: storageView(),
    };
  }
  if (method === "POST" && (m = /^\/api\/uploads\/([0-9a-f-]{36})\/takedown$/.exec(path))) {
    const row = uploads.find((x) => x.id === m[1]);
    if (!row) throw new HttpError(404, "not_found");
    uploads = uploads.filter((x) => x !== row);
    addAudit(Date.now(), OWNER, "upload.takedown", `upload:${row.id}`, { format: row.format, size: row.size, user: row.user_id }, undefined);
    return { takenDown: true, fileRemoved: true, storage: storageView() };
  }
  if (method === "POST" && (m = /^\/api\/users\/([0-9a-f-]{36})\/uploads$/.exec(path))) {
    const u = users.find((x) => x.id === m[1]);
    if (!u) throw new HttpError(404, "not_found");
    if (typeof body.blocked !== "boolean") throw new HttpError(400, "invalid_request");
    if ((u.uploads_blocked === true) !== body.blocked) addAudit(Date.now(), OWNER, body.blocked ? "user.uploads_block" : "user.uploads_allow", `user:${u.id}`, { uploadsBlocked: !body.blocked }, { uploadsBlocked: body.blocked });
    u.uploads_blocked = body.blocked;
    return { user: { id: u.id, uploadsBlocked: body.blocked } };
  }
  if (method === "POST" && (m = /^\/api\/users\/([0-9a-f-]{36})\/revoke$/.exec(path))) {
    const u = users.find((x) => x.id === m[1]);
    if (!u) throw new HttpError(404, "not_found");
    if (u.status === "revoked") throw new HttpError(409, "already_revoked");
    u.status = "revoked";
    u.revoked_at = Date.now();
    addAudit(u.revoked_at, OWNER, "user.revoke", `user:${u.id}`, { status: "active" }, { status: "revoked" });
    return { user: { id: u.id, status: "revoked", revokedAt: iso(u.revoked_at) } };
  }
  if (method === "POST" && (m = /^\/api\/users\/([0-9a-f-]{36})\/devices\/([0-9a-f-]{36})\/revoke$/.exec(path))) {
    const d = users.find((x) => x.id === m[1])?.devices.find((x) => x.id === m[2]);
    if (!d) throw new HttpError(404, "not_found");
    if (d.revoked_at !== null) throw new HttpError(409, "already_revoked");
    d.revoked_at = Date.now();
    addAudit(d.revoked_at, OWNER, "device.revoke", `user:${m[1]}`, { device: d.id, status: "active" }, { device: d.id, status: "revoked" });
    return { device: { id: d.id, revokedAt: iso(d.revoked_at) } };
  }
  if (method === "DELETE" && (m = /^\/api\/users\/([0-9a-f-]{36})$/.exec(path))) {
    if (body.confirm !== m[1]) throw new HttpError(400, "confirmation_mismatch");
    const index = users.findIndex((x) => x.id === m[1]);
    if (index < 0) throw new HttpError(404, "not_found");
    const [u] = users.splice(index, 1);
    addAudit(Date.now(), OWNER, "user.delete", `user:${u.id}`, { status: u.status, access: u.access_granted_at !== null, devices: u.devices.length });
    return { deleted: true, filesRemoved: true };
  }
  if (method === "GET" && (m = /^\/api\/users\/([0-9a-f-]{36})\/export$/.exec(path))) {
    const u = users.find((x) => x.id === m[1]);
    if (!u) throw new HttpError(404, "not_found");
    addAudit(Date.now(), OWNER, "user.export", `user:${u.id}`);
    return { exportedAt: iso(Date.now()), user: userView(u), sessions: [], invitesRedeemed: invites.filter((i) => i.redeemed_by === u.id).map((i) => ({ id: i.id, redeemedAt: iso(i.redeemed_at) })), auditEntries: audit.filter((a) => a.target === `user:${u.id}` || a.actor === `user:${u.id}`).map(auditView) };
  }
  if (method === "GET" && path === "/api/audit") {
    const before = query.get("before") === null ? Number.MAX_SAFE_INTEGER : Number(query.get("before"));
    const limit = Math.min(Math.max(Number(query.get("limit") ?? 100), 1), 200);
    return { entries: audit.filter((a) => a.seq < before).sort((a, b) => b.seq - a.seq).slice(0, limit).map(auditView) };
  }
  if (method === "GET" && path === "/api/audit/verify") {
    // The daily export of the real service ran a few entries ago: its head is the newest entry it holds.
    const exported = audit.length > 3 ? audit[audit.length - 4] : null;
    const exportedHead = exported ? { seq: exported.seq, hash: exported.hash, present: true } : null;
    let prev = null;
    let checked = 0;
    for (const row of audit) {
      if (prev !== null && row.prev_hash !== prev) return { chain: { ok: false, checked, brokenAtSeq: row.seq, anchored: false, exportedHead } };
      const expected = createHash("sha256").update(JSON.stringify([row.prev_hash, row.at, row.actor, row.action, row.target, row.before_json, row.after_json])).digest("hex");
      if (expected !== row.hash) return { chain: { ok: false, checked, brokenAtSeq: row.seq, anchored: false, exportedHead } };
      prev = row.hash;
      checked++;
    }
    return { chain: { ok: true, checked, brokenAtSeq: null, anchored: false, exportedHead } };
  }
  if (method === "GET" && path === "/api/settings") return { settings, configVersion };
  if (method === "PUT" && path === "/api/settings") {
    if (body.accessMode !== undefined && body.accessMode !== "invite-only" && body.accessMode !== "public") throw new HttpError(400, "invalid_request");
    const changed = Object.keys(body).filter((k) => body[k] !== settings[k]);
    if (changed.length > 0) {
      addAudit(Date.now(), OWNER, "settings.update", "settings", Object.fromEntries(changed.map((k) => [k, settings[k]])), Object.fromEntries(changed.map((k) => [k, body[k]])));
      settings = { ...settings, ...body };
      configVersion = Math.max(configVersion + 1, Math.floor(Date.now() / 1000));
    }
    return { settings, configVersion };
  }
  if (method === "GET" && path === "/api/apps") return appsBody();
  if (method === "PUT" && path === "/api/apps") {
    apps = body.applications;
    defaultApp = body.defaultApplication;
    configVersion = Math.max(configVersion + 1, Math.floor(Date.now() / 1000));
    addAudit(Date.now(), OWNER, "apps.update", "config", undefined, { applications: apps.map((a) => ({ key: a.key, applicationId: a.applicationId })), defaultApplication: defaultApp });
    return appsBody();
  }
  const rosterAnswer = rosterRoute(method, path, body);
  if (rosterAnswer) return rosterAnswer;
  if (method === "GET" && path === "/api/packs") return { packs, index: indexSummary(), limits: { maxImageBytes: 2097152, maxPackBytes: 33554432, maxPacks: 100, maxEntries: 4000 } };
  if (method === "POST" && path === "/api/packs") {
    if (packs.some((p) => p.id === body.id)) throw new HttpError(409, "pack_exists");
    const pack = makePack(body.id, { ...body, createdAt: iso(Date.now()), updatedAt: iso(Date.now()) }, []);
    delete pack.id;
    packs.push({ id: body.id, ...pack });
    addAudit(Date.now(), OWNER, "pack.create", `pack:${body.id}`, undefined, { fallback: !!body.fallback, behaviourArt: !!body.behaviourArt });
    return { pack: packs.at(-1) };
  }
  if (method === "POST" && path === "/api/pack-files") {
    const bytes = body;
    const png = bytes[0] === 0x89;
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    return { file: { sha256, sourceSha256: sha256, format: png ? "png" : "jpeg", size: bytes.length, width: 1024, height: 1024, url: `${CDN}/packs/${sha256}.${png ? "png" : "jpg"}` }, removed: png ? [] : ["JFIF"] };
  }
  if (method === "POST" && path === "/api/pack-files/purge") return { purged: { unreferenced: 0, orphaned: 0 } };
  if ((m = /^\/api\/packs\/([a-z0-9-]{1,64})(\/.*)?$/.exec(path))) {
    const pack = packs.find((p) => p.id === m[1]);
    if (!pack) throw new HttpError(404, "not_found");
    const rest = m[2] ?? "";
    const touch = () => {
      pack.updatedAt = iso(Date.now());
      if (pack.status === "published") pack.changedSincePublish = true;
      pack.totalBytes = pack.entries.reduce((n, e) => n + e.size, 0);
    };
    if (method === "PUT" && rest === "") {
      Object.assign(pack, body);
      touch();
      return { pack };
    }
    if (method === "DELETE" && rest === "") {
      if (body.confirm !== pack.id) throw new HttpError(400, "confirmation_mismatch");
      if (pack.status === "published") throw new HttpError(409, "pack_published");
      packs.splice(packs.indexOf(pack), 1);
      return { deleted: true, filesDeleted: pack.entries.length };
    }
    if (method === "POST" && rest === "/publish") {
      if (pack.entries.length === 0) throw new HttpError(409, "pack_empty");
      if (pack.thirdParty && (!pack.permission || !pack.credit)) throw new HttpError(409, "permission_required");
      Object.assign(pack, { status: "published", version: pack.version + 1, changedSincePublish: false, publishedAt: iso(Date.now()) });
      for (const e of pack.entries) e.live = !e.withdrawn;
      return { pack, index: bumpIndex() };
    }
    if (method === "PUT" && rest === "/default") {
      if (body.default === true && packs.some((p) => p.default && p !== pack)) throw new HttpError(409, "default_exists");
      pack.default = body.default === true;
      return { pack, index: pack.status === "published" ? bumpIndex() : null };
    }
    if (method === "POST" && rest === "/unpublish") {
      pack.status = "unpublished";
      for (const e of pack.entries) e.live = false;
      return { pack, index: bumpIndex() };
    }
    if (method === "POST" && rest === "/entries") {
      if (pack.entries.some((e) => e.key === body.key)) throw new HttpError(409, "key_exists");
      const e = entry(pack.id, body.key, { holder: body.licensedHolder ?? null, live: false });
      e.sha256 = body.sha256;
      e.url = `${CDN}/packs/${body.sha256}.png`;
      pack.entries.push(e);
      touch();
      return { pack };
    }
    const em = /^\/entries\/(e[0-9a-f]{24})(\/withdraw)?$/.exec(rest);
    const found = em && pack.entries.find((e) => e.id === em[1]);
    if (em && !found) throw new HttpError(404, "not_found");
    if (found && method === "POST" && em[2]) {
      if (found.licensedHolder === null) throw new HttpError(409, "not_licensed");
      const published = pack.status === "published";
      found.withdrawn = body.withdrawn === true;
      if (published) found.live = !found.withdrawn;
      return { pack, index: published ? bumpIndex() : null };
    }
    if (found && method === "DELETE") {
      pack.entries.splice(pack.entries.indexOf(found), 1);
      touch();
      return { pack, filesDeleted: 1 };
    }
    if (found && method === "PUT") {
      Object.assign(found, body);
      touch();
      return { pack };
    }
  }
  throw new HttpError(404, "not_found");
}

// The character roster (service/src/routes/roster.ts): a few seed characters, one learned from the owner's game, one removed.
const rosterCharacter = (code, id, name, extra = {}) => ({ code, id, role: code.startsWith("K") ? "killer" : "survivor", displayName: name, screenName: name, aliases: [], licensedHolder: null, source: "seed", addedAt: null, withdrawn: false, withdrawnAt: null, ...extra });
const roster = {
  version: Math.floor(start / 1000),
  signedAt: iso(start - 2 * DAY),
  expiresAt: iso(start + 28 * DAY),
  stale: false,
  characters: [
    rosterCharacter("K01", "the-trapper", "The Trapper"),
    rosterCharacter("K02", "the-wraith", "The Wraith"),
    rosterCharacter("K04", "the-nurse", "The Nurse"),
    rosterCharacter("K06", "the-shape", "The Shape", { licensedHolder: "Example Films" }),
    rosterCharacter("K08", "the-huntress", "The Huntress"),
    rosterCharacter("K16", "the-ghost-face", "The Ghost Face", { licensedHolder: "Example Studio" }),
    rosterCharacter("K45", "the-lantern", "The Lantern", { source: "auto", addedAt: iso(start - DAY) }),
    rosterCharacter("S01", "dwight-fairfield", "Dwight Fairfield"),
    rosterCharacter("S55", "mara-quell", "Mara Quell", { source: "owner", addedAt: iso(start - 6 * DAY), withdrawn: true, withdrawnAt: iso(start - 5 * DAY) }),
  ],
};
let rosterPending = [{ name: "the warden", role: "killer", matches: 2, firstSeenAt: iso(start - 3 * DAY), lastSeenAt: iso(start - 3_600_000) }];
let rosterBlocked = [{ name: "mara quell", blockedAt: iso(start - 5 * DAY) }];

function rosterBody() {
  const next = (letter) => `${letter}${String(Math.max(...roster.characters.filter((c) => c.code.startsWith(letter)).map((c) => Number(c.code.slice(1)))) + 1).padStart(2, "0")}`;
  return {
    roster: { ...roster, nextCodes: { killer: next("K"), survivor: next("S") } },
    pending: rosterPending,
    blocked: rosterBlocked,
    reporters: users.filter((u) => u.roster_reporter === true).map((u) => ({ id: u.id, displayName: u.display_name })),
    limits: { maxCharacters: 400, matchesToAdd: 3, maxReporters: 2, maxAliases: 8 },
  };
}

function rosterRoute(method, path, body) {
  let m;
  if (method === "GET" && path === "/api/roster") return rosterBody();
  if (method === "POST" && path === "/api/roster/characters") {
    if (roster.characters.some((c) => c.code === body.code)) throw new HttpError(409, "code_exists");
    roster.characters.push(rosterCharacter(body.code, body.displayName.toLowerCase().replaceAll(" ", "-"), body.displayName, { source: "owner", addedAt: iso(Date.now()), screenName: body.screenName ?? body.displayName }));
    return rosterBody();
  }
  if ((m = /^\/api\/roster\/characters\/([KS][0-9]{2})(\/withdraw)?$/.exec(path))) {
    const c = roster.characters.find((x) => x.code === m[1]);
    if (!c) throw new HttpError(404, "not_found");
    if (c.source === "seed") throw new HttpError(409, "seed_character");
    if (method === "PUT" && !m[2]) Object.assign(c, { displayName: body.displayName, screenName: body.screenName });
    else if (method === "POST" && m[2]) {
      c.withdrawn = body.withdrawn === true;
      c.withdrawnAt = c.withdrawn ? iso(Date.now()) : null;
      if (c.withdrawn) rosterBlocked = [{ name: c.screenName.toLowerCase(), blockedAt: iso(Date.now()) }, ...rosterBlocked];
      else rosterBlocked = rosterBlocked.filter((b) => b.name !== c.screenName.toLowerCase());
    } else throw new HttpError(405, "method_not_allowed");
    return rosterBody();
  }
  if (method === "DELETE" && (m = /^\/api\/roster\/blocks\/([a-z0-9-]+)$/.exec(path))) {
    const name = m[1].replaceAll("-", " ");
    if (!rosterBlocked.some((b) => b.name === name)) throw new HttpError(404, "not_found");
    rosterBlocked = rosterBlocked.filter((b) => b.name !== name);
    return rosterBody();
  }
  if (method === "PUT" && (m = /^\/api\/users\/([0-9a-f-]+)\/roster-reporter$/.exec(path))) {
    const u = users.find((x) => x.id === m[1]);
    if (!u) throw new HttpError(404, "not_found");
    u.roster_reporter = body.enabled === true;
    return { user: { id: u.id, rosterReporter: u.roster_reporter } };
  }
  return null;
}

function sendJson(res, status, body) {
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  res.end(JSON.stringify({ schemaVersion: 1, ...body }));
}

const server = createServer((req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  if (url.pathname.startsWith("/api/")) {
    const chunks = [];
    req.on("data", (chunk) => chunks.push(chunk));
    req.on("end", () => {
      const rawBytes = Buffer.concat(chunks);
      const raw = (req.headers["content-type"] ?? "").startsWith("image/") ? "" : rawBytes.toString("utf8");
      try {
        if (req.headers["x-dbdp-admin"] !== "1") throw new HttpError(403, "admin_header_required");
        if (["POST", "PUT", "DELETE"].includes(req.method)) {
          if (req.headers.origin !== `http://${req.headers.host}`) throw new HttpError(403, "bad_origin");
          if (req.headers["sec-fetch-site"] !== "same-origin") throw new HttpError(403, "bad_fetch_site");
        }
        const body = (req.headers["content-type"] ?? "").startsWith("image/") ? new Uint8Array(rawBytes) : raw ? JSON.parse(raw) : {};
        sendJson(res, 200, route(req.method, url.pathname, url.searchParams, body));
      } catch (error) {
        if (error instanceof HttpError) sendJson(res, error.status, { error: error.code });
        else sendJson(res, 500, { error: "internal" });
      }
    });
    return;
  }
  const relative = url.pathname === "/" ? "index.html" : normalize(url.pathname).replace(/^[/\\]+/, "");
  const file = join(dist, relative);
  if (!file.startsWith(dist) || !existsSync(file) || !statSync(file).isFile() || relative === "_headers") {
    res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8", ...STATIC_HEADERS });
    res.end("Not found");
    return;
  }
  res.writeHead(200, { "Content-Type": TYPES[extname(file)] ?? "application/octet-stream", ...STATIC_HEADERS });
  res.end(readFileSync(file));
});

server.listen(port, "127.0.0.1", () => console.log(`Owner Panel preview with sample data: http://127.0.0.1:${port}/`));
