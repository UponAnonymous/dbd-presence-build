// A fake admin API for rendering tests. It answers with the service's shapes (service/src/routes/admin.ts), records
// every request with its headers, and can be told to fail a path with a given status.

import { AdminApi, type AuditEntry, type DiscordApp, type Invite, type Overview, type Pack, type PackEntry, type RosterCharacter, type RosterState, type StorageUsage, type UploadItem, type User } from "../src/api.ts";
import { mountShell } from "../src/shell.ts";

export const NOW = Date.parse("2026-10-24T12:00:00Z");
const DAY = 86_400_000;

export const HOSTILE = '<img src=x onerror="alert(1)"><script>alert(2)</script>';

export interface Recorded {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: unknown;
  init: RequestInit;
}

export const ids = {
  user1: "11111111-1111-4111-8111-111111111111",
  user2: "22222222-2222-4222-8222-222222222222",
  device1: "33333333-3333-4333-8333-333333333333",
  device2: "44444444-4444-4444-8444-444444444444",
  invite1: "55555555-5555-4555-8555-555555555555",
  invite2: "66666666-6666-4666-8666-666666666666",
};

const iso = (ms: number): string => new Date(ms).toISOString();

export const CDN = "https://cdn.arcavale.com";
export const shaOf = (n: number): string => n.toString(16).padStart(2, "0").repeat(32);

export function sampleEntry(n: number, key: string, extra: Partial<PackEntry> = {}): PackEntry {
  const format = extra.format ?? "png";
  return {
    id: `e${n.toString(16).padStart(24, "0")}`,
    kind: key.slice(0, key.indexOf(":")) as PackEntry["kind"],
    key,
    sha256: shaOf(n),
    format,
    size: 24_000 + n * 1000,
    width: 1024,
    height: 1024,
    licensedHolder: null,
    withdrawn: false,
    live: true,
    url: `${CDN}/packs/${shaOf(n)}.${format === "png" ? "png" : "jpg"}`,
    createdAt: iso(NOW - 3 * DAY),
    updatedAt: iso(NOW - 3 * DAY),
    ...extra,
  };
}

export function samplePacks(): Pack[] {
  const original: PackEntry[] = [
    sampleEntry(1, "role:killer"),
    sampleEntry(2, "role:survivor"),
    sampleEntry(3, "character:the-trapper", { format: "jpeg", url: `${CDN}/packs/${shaOf(3)}.jpg` }),
    sampleEntry(4, "character:the-ghost-face", { licensedHolder: HOSTILE }),
    sampleEntry(5, "character:the-shape", { licensedHolder: "Example Studio", withdrawn: true, live: false }),
  ];
  return [
    {
      id: "original",
      name: "Original",
      author: "Behaviour Interactive",
      licenceNote: "Dead by Daylight artwork © Behaviour Interactive Inc.",
      credit: null,
      fallback: false,
      behaviourArt: true,
      default: true,
      thirdParty: false,
      permission: null,
      status: "published",
      version: 3,
      changedSincePublish: false,
      createdAt: iso(NOW - 20 * DAY),
      updatedAt: iso(NOW - 2 * DAY),
      publishedAt: iso(NOW - 2 * DAY),
      totalBytes: original.reduce((n, e) => n + e.size, 0),
      entries: original,
    },
    {
      id: "nightlight-ash",
      name: HOSTILE,
      author: "A Creator",
      licenceNote: "Shared with permission",
      credit: null,
      fallback: false,
      behaviourArt: false,
      default: false,
      thirdParty: true,
      permission: null,
      status: "draft",
      version: 0,
      changedSincePublish: false,
      createdAt: iso(NOW - DAY),
      updatedAt: iso(NOW - DAY),
      publishedAt: null,
      totalBytes: 0,
      entries: [],
    },
  ];
}

export function sampleApps(): DiscordApp[] {
  return [
    { key: "dbd", applicationId: "1563291325107937340", name: "Dead by Daylight", description: "Unofficial fan-made presence — not affiliated with Behaviour Interactive" },
    { key: "dbd-alt", applicationId: "1563291325107930001", name: HOSTILE, description: "Second application" },
  ];
}

export function sampleData(): {
  users: User[];
  invites: Invite[];
  audit: AuditEntry[];
  settings: Record<string, unknown>;
  configVersion: number;
  packs: Pack[];
  index: { version: number; publishedAt: string | null; packs: number; entries: number };
  apps: DiscordApp[];
  defaultApp: string;
  userUploads: UploadItem[];
  storage: ReturnType<typeof sampleStorage>;
} {
  return {
    users: [
      {
        id: ids.user1,
        discordId: "310000000000000001",
        displayName: HOSTILE,
        status: "active",
        access: "granted",
        accessVia: "invite",
        createdAt: iso(NOW - 10 * DAY),
        lastSeenAt: iso(NOW - 2 * 3_600_000),
        revokedAt: null,
        devices: [
          { id: ids.device1, thumbprint: "thumbprint-one-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", createdAt: iso(NOW - 9 * DAY), lastSeenAt: iso(NOW - DAY), revokedAt: null },
          { id: ids.device2, thumbprint: "thumbprint-two-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", createdAt: iso(NOW - 8 * DAY), lastSeenAt: iso(NOW - 3 * DAY), revokedAt: iso(NOW - 2 * DAY) },
        ],
        uploads: 2,
        uploadsBlocked: false,
        rosterReporter: true,
      },
      {
        id: ids.user2,
        discordId: "310000000000000002",
        displayName: "Rook",
        status: "active",
        access: "invite_required",
        accessVia: null,
        createdAt: iso(NOW - 2 * DAY),
        lastSeenAt: iso(NOW - DAY),
        revokedAt: null,
        devices: [],
        uploads: 0,
        uploadsBlocked: true,
        rosterReporter: false,
      },
    ],
    invites: [
      { id: ids.invite1, keyId: "k1", note: HOSTILE, status: "active", createdAt: iso(NOW - DAY), createdBy: "owner:owner@example.com", expiresAt: iso(NOW + 29 * DAY), redeemedBy: null, redeemedAt: null, revokedAt: null },
      { id: ids.invite2, keyId: "k1", note: "Friends", status: "redeemed", createdAt: iso(NOW - 5 * DAY), createdBy: "owner:owner@example.com", expiresAt: iso(NOW + 25 * DAY), redeemedBy: ids.user1, redeemedAt: iso(NOW - 4 * DAY), revokedAt: null },
    ],
    audit: Array.from({ length: 30 }, (_, i) => ({
      seq: 30 - i,
      at: iso(NOW - i * 3_600_000),
      actor: i === 0 ? `user:${ids.user1}` : "owner:owner@example.com",
      action: i === 0 ? "invite.redeem" : i === 1 ? "settings.update" : "invite.create",
      target: i === 0 ? `invite:${ids.invite2}` : i === 1 ? "settings" : null,
      before: null,
      // The service records only whether an invite had a note (hasNote), never its text.
      after: i === 1 ? { banner: HOSTILE } : i === 2 ? { ids: ["7777777a-7777-4777-8777-777777777777", "7777777b-7777-4777-8777-777777777777"], hasNote: true, expiresAt: iso(NOW + 30 * DAY) } : i === 3 ? { ids: ["7777777c-7777-4777-8777-777777777777"], hasNote: false, expiresAt: iso(NOW + 30 * DAY) } : null,
      prevHash: "a".repeat(64),
      hash: "b".repeat(64),
    })),
    settings: { accessMode: "invite-only", deviceLimit: 3, disableCapture: false, disableDetection: false, emergencyStop: false, minimumVersion: "0.1.0", banner: null, uploadsBlocked: false, uploadLimitBytes: 62914560, uploadLimitFiles: 300 },
    configVersion: 1793600000,
    packs: samplePacks(),
    index: { version: 1793500000, publishedAt: iso(NOW - 2 * DAY), packs: 1, entries: 4 },
    apps: sampleApps(),
    defaultApp: "dbd",
    userUploads: sampleUploads(),
    storage: sampleStorage(1_234_567_890),
  };
}

export function sampleRoster(): RosterState {
  const seed = (code: string, id: string, name: string): RosterCharacter => ({
    code,
    id,
    role: code.startsWith("K") ? "killer" : "survivor",
    displayName: name,
    screenName: name,
    aliases: [],
    licensedHolder: null,
    source: "seed",
    addedAt: null,
    withdrawn: false,
    withdrawnAt: null,
  });
  return {
    roster: {
      version: 1793700000,
      signedAt: iso(NOW - 2 * DAY),
      expiresAt: iso(NOW + 28 * DAY),
      stale: false,
      characters: [
        seed("K01", "the-trapper", "The Trapper"),
        seed("K05", "the-hag", "The Hag"),
        { ...seed("K06", "the-shape", "The Shape"), licensedHolder: "Example Studio" },
        seed("K16", "the-ghost-face", "Ghost Face"),
        { ...seed("K45", "the-lantern", HOSTILE), source: "auto", addedAt: iso(NOW - DAY) },
        seed("S01", "dwight-fairfield", "Dwight Fairfield"),
        { ...seed("S55", "mara-quell", "Mara Quell"), source: "owner", addedAt: iso(NOW - 5 * DAY), withdrawn: true, withdrawnAt: iso(NOW - 4 * DAY) },
      ],
      nextCodes: { killer: "K46", survivor: "S56" },
    },
    pending: [{ name: "the warden", role: "killer", matches: 2, firstSeenAt: iso(NOW - 3 * DAY), lastSeenAt: iso(NOW - 3_600_000) }],
    blocked: [{ name: "mara quell", blockedAt: iso(NOW - 4 * DAY) }],
    reporters: [{ id: ids.user1, displayName: HOSTILE }],
    limits: { maxCharacters: 400, matchesToAdd: 3, maxReporters: 2, maxAliases: 8 },
  };
}

export const UPLOAD_KEY = (n: number): string => n.toString(16).padStart(2, "0").repeat(16);

export function sampleUploads(): UploadItem[] {
  return [
    { id: "88888888-8888-4888-8888-888888888881", url: `${CDN}/u/${UPLOAD_KEY(1)}.png`, sha256: shaOf(0x61), format: "png", size: 48_213, width: 512, height: 512, createdAt: iso(NOW - DAY), user: { id: ids.user1, displayName: HOSTILE, uploadsBlocked: false } },
    // An address that is not an upload on this environment's content host: never loaded.
    { id: "88888888-8888-4888-8888-888888888882", url: "https://evil.example/u/track.png", sha256: shaOf(0x62), format: "jpeg", size: 120_000, width: 1024, height: 768, createdAt: iso(NOW - 2 * DAY), user: { id: ids.user1, displayName: HOSTILE, uploadsBlocked: false } },
  ];
}

/** The caps the service reports per environment (service/src/storage.ts storageLimits, D18). */
export const STORAGE_LIMITS = {
  production: { capBytes: 8_000_000_000, warnBytes: 6_000_000_000, uploadCapBytes: 6_000_000_000 },
  staging: { capBytes: 750_000_000, warnBytes: 500_000_000, uploadCapBytes: 500_000_000 },
} as const;

export function sampleStorage(
  usedBytes: number,
  environment: keyof typeof STORAGE_LIMITS = "production",
): {
  usedBytes: number;
  capBytes: number;
  warnBytes: number;
  warning: boolean;
  full: boolean;
  uploadCapBytes: number;
  uploadsFull: boolean;
  pendingDeleteBytes: number;
  areas: Record<string, { bytes: number; objects: number }>;
} {
  const { capBytes, warnBytes, uploadCapBytes } = STORAGE_LIMITS[environment];
  return {
    usedBytes,
    capBytes,
    warnBytes,
    warning: usedBytes >= warnBytes,
    full: usedBytes >= capBytes,
    uploadCapBytes,
    uploadsFull: usedBytes >= uploadCapBytes,
    pendingDeleteBytes: 0,
    areas: { packs: { bytes: usedBytes - 168_213, objects: 40 }, uploads: { bytes: 168_213, objects: 2 }, releases: { bytes: 0, objects: 0 }, audit: { bytes: 0, objects: 0 } },
  };
}

/** The Dashboard and Settings data as the service sends it today: every key set up, nothing recorded about backups. */
export function sampleOverview(): Overview {
  return {
    environment: "production",
    hosts: { api: "api.arcavale.com", admin: "dbdadmin.arcavale.com", cdn: "cdn.arcavale.com" },
    users: { total: 14, withAccess: 11, waitingForInvite: 2, revoked: 1, activeThisWeek: 7 },
    invites: { pending: 3 },
    uploads: { total: 42, thisWeek: 5 },
    storage: sampleStorage(1_234_567_890) as StorageUsage,
    database: { sizeBytes: 12_345_678, freeLimitBytes: 500_000_000 },
    keys: {
      service: [
        { name: "licence", configured: true, kid: "licence-2026-10" },
        { name: "config", configured: true, kid: "config-2026-10" },
        { name: "content", configured: true, kid: "content-2026-10" },
        { name: "invite", configured: true, kid: "invite-2" },
        { name: "ban", configured: true, kid: null },
      ],
      offline: { release: ["release-2026-10"], recovery: ["recovery-2026-10"], revoked: [], configured: true },
      keyReplacement: null,
    },
    auditExport: { lastAt: iso(NOW - 3_600_000) },
    backup: { lastExportAt: null, lastExportBytes: null, lastDrillAt: null, lastDrillOk: null },
  };
}

export type Failure = { status: number; error?: string; reason?: string; headers?: Record<string, string> } | "network" | "redirect" | "html";

export class FakeBackend {
  readonly requests: Recorded[] = [];
  readonly data = sampleData();
  /** What GET /api/overview answers; tests change it before mounting. */
  overview: Overview = sampleOverview();
  readonly failures = new Map<string, Failure>();
  chain: { ok: boolean; checked: number; brokenAtSeq: number | null; anchored: boolean; exportedHead: { seq: number; hash: string; present: boolean } | null } | null = null;

  /** Fails every request whose "METHOD path" (path without query) matches. */
  fail(key: string, failure: Failure): void {
    this.failures.set(key, failure);
  }

  private readonly onceFailures: { key: string; failure: Failure; skip: number }[] = [];

  /** Fails one request whose "METHOD path" matches, after letting `skip` matching requests through; queued in order. */
  failOnce(key: string, failure: Failure, skip = 0): void {
    this.onceFailures.push({ key, failure, skip });
  }

  private takeOnce(key: string): Failure | undefined {
    const at = this.onceFailures.findIndex((f) => f.key === key);
    if (at < 0) return undefined;
    const pending = this.onceFailures[at]!;
    if (pending.skip > 0) {
      pending.skip--;
      return undefined;
    }
    this.onceFailures.splice(at, 1);
    return pending.failure;
  }

  readonly fetch = async (input: string, init: RequestInit): Promise<Response> => {
    const headers = Object.fromEntries(Object.entries((init.headers ?? {}) as Record<string, string>));
    const body = typeof init.body === "string" ? (JSON.parse(init.body) as unknown) : init.body instanceof Uint8Array ? init.body : undefined;
    const method = init.method ?? "GET";
    this.requests.push({ method, url: input, headers, body, init });
    const path = input.split("?")[0]!;
    const failure = this.takeOnce(`${method} ${path}`) ?? this.failures.get(`${method} ${path}`);
    if (failure === "network") throw new TypeError("Failed to fetch");
    if (failure === "redirect") return Object.defineProperty(new Response(null, { status: 200 }), "type", { value: "opaqueredirect" });
    if (failure === "html") return new Response("<!doctype html><p>Not the API</p>", { status: 200, headers: { "Content-Type": "text/html" } });
    if (failure) return this.json(failure.status, { ...(failure.error ? { error: failure.error } : {}), ...(failure.reason ? { reason: failure.reason } : {}) }, failure.headers);
    return this.route(method, path, new URL(input, "https://dbdadmin.arcavale.com").searchParams, body as Record<string, unknown> | undefined);
  };

  readonly uploads: FakeUpload[] = [];
  /** Makes takedowns and user deletes report that a stored file could not be deleted yet. */
  fileDeleteFails = false;
  private nextFile = 0x40;

  private findPack(id: string): Pack | undefined {
    return this.data.packs.find((p) => p.id === id);
  }

  private bump(): { version: number; publishedAt: string } {
    this.data.index = { ...this.data.index, version: this.data.index.version + 1, publishedAt: iso(NOW) };
    return { version: this.data.index.version, publishedAt: iso(NOW) };
  }

  private packRoute(method: string, path: string, body: unknown): Response | null {
    const d = this.data;
    const b = (body ?? {}) as Record<string, unknown>;
    let m: RegExpExecArray | null;
    if (method === "GET" && path === "/api/packs") return this.json(200, { packs: d.packs, index: d.index, limits: { maxImageBytes: 2097152, maxPackBytes: 33554432, maxPacks: 100, maxEntries: 4000 } });
    if (method === "POST" && path === "/api/packs") {
      const pack: Pack = { id: b["id"] as string, name: b["name"] as string, author: b["author"] as string, licenceNote: b["licenceNote"] as string, credit: (b["credit"] as string | null) ?? null, fallback: b["fallback"] === true, behaviourArt: b["behaviourArt"] === true, default: false, thirdParty: b["thirdParty"] === true, permission: (b["permission"] as Pack["permission"]) ?? null, status: "draft", version: 0, changedSincePublish: false, createdAt: iso(NOW), updatedAt: iso(NOW), publishedAt: null, totalBytes: 0, entries: [] };
      d.packs.push(pack);
      return this.json(200, { pack });
    }
    if (method === "POST" && path === "/api/pack-files") {
      const bytes = body as Uint8Array;
      this.uploads.push({ bytes, type: "" });
      const n = this.nextFile++;
      const png = bytes[0] === 0x89;
      return this.json(200, { file: { sha256: shaOf(n), sourceSha256: shaOf(n + 1), format: png ? "png" : "jpeg", size: bytes.length, width: 1024, height: 1024, url: `${CDN}/packs/${shaOf(n)}.${png ? "png" : "jpg"}` }, removed: png ? [] : ["JFIF", "Exif"] });
    }
    if (method === "POST" && path === "/api/pack-files/purge") return this.json(200, { purged: { unreferenced: 2, orphaned: 0 } });
    if ((m = /^\/api\/packs\/([a-z0-9-]+)(\/.*)?$/.exec(path))) {
      const pack = this.findPack(m[1]!);
      if (!pack) return this.json(404, { error: "not_found" });
      const rest = m[2] ?? "";
      const touch = () => {
        pack.updatedAt = iso(NOW);
        if (pack.status === "published") pack.changedSincePublish = true;
        pack.totalBytes = pack.entries.reduce((n, e) => n + e.size, 0);
      };
      if (method === "PUT" && rest === "") {
        Object.assign(pack, b);
        touch();
        return this.json(200, { pack });
      }
      if (method === "DELETE" && rest === "") {
        if (b["confirm"] !== pack.id) return this.json(400, { error: "confirmation_mismatch" });
        d.packs = d.packs.filter((p) => p !== pack);
        return this.json(200, { deleted: true, filesDeleted: pack.entries.length });
      }
      if (method === "POST" && rest === "/publish") {
        if (pack.entries.length === 0) return this.json(409, { error: "pack_empty" });
        if (pack.thirdParty && !pack.permission) return this.json(409, { error: "permission_required" });
        pack.status = "published";
        pack.version++;
        pack.changedSincePublish = false;
        pack.publishedAt = iso(NOW);
        for (const e of pack.entries) e.live = !e.withdrawn;
        return this.json(200, { pack, index: this.bump() });
      }
      if (method === "PUT" && rest === "/default") {
        const other = d.packs.find((p) => p.default && p !== pack);
        if (b["default"] === true && other) return this.json(409, { error: "default_exists" });
        pack.default = b["default"] === true;
        return this.json(200, { pack, index: pack.status === "published" ? this.bump() : null });
      }
      if (method === "POST" && rest === "/unpublish") {
        pack.status = "unpublished";
        for (const e of pack.entries) e.live = false;
        return this.json(200, { pack, index: this.bump() });
      }
      if (method === "POST" && rest === "/entries") {
        if (pack.entries.some((e) => e.key === b["key"])) return this.json(409, { error: "key_exists" });
        const sha = b["sha256"] as string;
        const n = parseInt(sha.slice(0, 2), 16);
        pack.entries.push(sampleEntry(n, b["key"] as string, { licensedHolder: (b["licensedHolder"] as string | null) ?? null, live: false }));
        touch();
        return this.json(200, { pack });
      }
      const em = /^\/entries\/(e[0-9a-f]{24})(\/withdraw)?$/.exec(rest);
      const entry = em ? pack.entries.find((e) => e.id === em[1]) : undefined;
      if (em && !entry) return this.json(404, { error: "not_found" });
      if (entry && method === "PUT" && !em![2]) {
        if (typeof b["sha256"] === "string") {
          entry.sha256 = b["sha256"];
          entry.url = `${CDN}/packs/${entry.sha256}.png`;
        }
        if (b["licensedHolder"] !== undefined) entry.licensedHolder = b["licensedHolder"] as string | null;
        touch();
        return this.json(200, { pack });
      }
      if (entry && method === "DELETE" && !em![2]) {
        pack.entries = pack.entries.filter((e) => e !== entry);
        touch();
        return this.json(200, { pack, filesDeleted: 1 });
      }
      if (entry && method === "POST" && em![2]) {
        const wasLive = entry.live || (pack.status === "published" && entry.withdrawn);
        entry.withdrawn = b["withdrawn"] === true;
        if (pack.status === "published") entry.live = !entry.withdrawn;
        return this.json(200, { pack, index: wasLive ? this.bump() : null });
      }
    }
    return null;
  }

  readonly roster = sampleRoster();

  private rosterRoute(method: string, path: string, body: Record<string, unknown> | undefined): Response | null {
    const r = this.roster;
    const b = body ?? {};
    let m: RegExpExecArray | null;
    const answer = () => this.json(200, r as unknown as Record<string, unknown>);
    if (method === "GET" && path === "/api/roster") return answer();
    if (method === "POST" && path === "/api/roster/characters") {
      const code = b["code"] as string;
      if (r.roster.characters.some((c) => c.code === code)) return this.json(409, { error: "code_exists" });
      const name = b["displayName"] as string;
      r.roster.characters.push({ code, id: name.toLowerCase().replaceAll(" ", "-"), role: code.startsWith("K") ? "killer" : "survivor", displayName: name, screenName: (b["screenName"] as string | undefined) ?? name, aliases: [], licensedHolder: null, source: "owner", addedAt: iso(NOW), withdrawn: false, withdrawnAt: null });
      return answer();
    }
    if ((m = /^\/api\/roster\/characters\/([KS][0-9]{2})(\/withdraw)?$/.exec(path))) {
      const character = r.roster.characters.find((c) => c.code === m![1]);
      if (!character) return this.json(404, { error: "not_found" });
      if (character.source === "seed") return this.json(409, { error: "seed_character" });
      if (method === "PUT" && !m[2]) {
        character.displayName = b["displayName"] as string;
        character.screenName = b["screenName"] as string;
        return answer();
      }
      if (method === "POST" && m[2]) {
        character.withdrawn = b["withdrawn"] === true;
        character.withdrawnAt = character.withdrawn ? iso(NOW) : null;
        const folded = character.screenName.toLowerCase();
        if (character.withdrawn) r.blocked.unshift({ name: folded, blockedAt: iso(NOW) });
        else r.blocked = r.blocked.filter((x) => x.name !== folded);
        return answer();
      }
    }
    if (method === "DELETE" && path === "/api/roster/sightings") {
      r.pending = [];
      return answer();
    }
    if (method === "DELETE" && (m = /^\/api\/roster\/blocks\/([a-z0-9-]+)$/.exec(path))) {
      const name = m[1]!.replaceAll("-", " ");
      if (!r.blocked.some((x) => x.name === name)) return this.json(404, { error: "not_found" });
      r.blocked = r.blocked.filter((x) => x.name !== name);
      return answer();
    }
    if (method === "PUT" && (m = /^\/api\/users\/([0-9a-f-]+)\/roster-reporter$/.exec(path))) {
      const user = this.data.users.find((u) => u.id === m![1]);
      if (!user) return this.json(404, { error: "not_found" });
      user.rosterReporter = b["enabled"] === true;
      r.reporters = this.data.users.filter((u) => u.rosterReporter).map((u) => ({ id: u.id, displayName: u.displayName }));
      return this.json(200, { user: { id: user.id, rosterReporter: user.rosterReporter } });
    }
    return null;
  }

  private json(status: number, body: Record<string, unknown>, headers: Record<string, string> = {}): Response {
    return new Response(JSON.stringify({ schemaVersion: 1, ...body }), { status, headers: { "Content-Type": "application/json; charset=utf-8", ...headers } });
  }

  private route(method: string, path: string, query: URLSearchParams, body: Record<string, unknown> | undefined): Response {
    const d = this.data;
    let m: RegExpExecArray | null;
    if (method === "GET" && path === "/api/invites") return this.json(200, { invites: d.invites });
    if (method === "POST" && path === "/api/invites") {
      const count = (body?.["count"] as number) ?? 1;
      const created = Array.from({ length: count }, (_, i) => ({ id: `7777777${i}-7777-4777-8777-777777777777`, code: `ABCD-EFGH-JKMN-PQR${i}`, note: (body?.["note"] as string) ?? null, expiresAt: iso(NOW + 30 * DAY) }));
      return this.json(200, { invites: created });
    }
    if (method === "POST" && (m = /^\/api\/invites\/([0-9a-f-]+)\/revoke$/.exec(path))) {
      const invite = d.invites.find((i) => i.id === m![1])!;
      invite.status = "revoked";
      invite.revokedAt = iso(NOW);
      return this.json(200, { invite });
    }
    if (method === "GET" && path === "/api/users") return this.json(200, { users: d.users });
    if (method === "POST" && (m = /^\/api\/users\/([0-9a-f-]+)\/revoke$/.exec(path))) return this.json(200, { user: { id: m[1], status: "revoked", revokedAt: iso(NOW) } });
    if (method === "POST" && (m = /^\/api\/users\/([0-9a-f-]+)\/devices\/([0-9a-f-]+)\/revoke$/.exec(path))) return this.json(200, { device: { id: m[2], revokedAt: iso(NOW) } });
    if (method === "DELETE" && (m = /^\/api\/users\/([0-9a-f-]+)$/.exec(path))) {
      if (body?.["confirm"] !== m[1]) return this.json(400, { error: "confirmation_mismatch" });
      return this.json(200, { deleted: true, filesRemoved: !this.fileDeleteFails });
    }
    if (method === "GET" && (m = /^\/api\/users\/([0-9a-f-]+)\/export$/.exec(path))) return this.json(200, { exportedAt: iso(NOW), user: d.users.find((u) => u.id === m![1]), sessions: [], invitesRedeemed: [], auditEntries: [] });
    if (method === "GET" && path === "/api/audit") {
      const before = query.get("before") === null ? Infinity : Number(query.get("before"));
      const limit = Number(query.get("limit") ?? 100);
      return this.json(200, { entries: d.audit.filter((e) => e.seq < before).slice(0, limit) });
    }
    if (method === "GET" && path === "/api/audit/verify") return this.json(200, { chain: this.chain ?? { ok: true, checked: d.audit.length, brokenAtSeq: null, anchored: false, exportedHead: null } });
    if (method === "GET" && path === "/api/settings") return this.json(200, { settings: d.settings, configVersion: d.configVersion });
    if (method === "PUT" && path === "/api/settings") {
      d.settings = { ...d.settings, ...body };
      d.configVersion++;
      return this.json(200, { settings: d.settings, configVersion: d.configVersion });
    }
    if (method === "GET" && path === "/api/apps") return this.json(200, { applications: d.apps, defaultApplication: d.defaultApp, configVersion: d.configVersion });
    if (method === "PUT" && path === "/api/apps") {
      d.apps = body?.["applications"] as DiscordApp[];
      d.defaultApp = body?.["defaultApplication"] as string;
      d.configVersion++;
      return this.json(200, { applications: d.apps, defaultApplication: d.defaultApp, configVersion: d.configVersion });
    }
    if (method === "GET" && path === "/api/uploads") {
      const blockedUsers = d.users.filter((u) => u.uploadsBlocked).map((u) => ({ id: u.id, displayName: u.displayName }));
      return this.json(200, { uploads: d.userUploads, total: d.userUploads.length, blockedUsers, settings: { uploadsBlocked: d.settings["uploadsBlocked"], uploadLimitBytes: d.settings["uploadLimitBytes"], uploadLimitFiles: d.settings["uploadLimitFiles"] }, storage: d.storage });
    }
    if (method === "POST" && (m = /^\/api\/uploads\/([0-9a-f-]+)\/takedown$/.exec(path))) {
      const before = d.userUploads.length;
      d.userUploads = d.userUploads.filter((u) => u.id !== m![1]);
      if (d.userUploads.length === before) return this.json(404, { error: "not_found" });
      return this.json(200, { takenDown: true, fileRemoved: !this.fileDeleteFails, storage: d.storage });
    }
    if (method === "POST" && (m = /^\/api\/users\/([0-9a-f-]+)\/uploads$/.exec(path))) {
      const user = d.users.find((u) => u.id === m![1]);
      if (!user) return this.json(404, { error: "not_found" });
      user.uploadsBlocked = body?.["blocked"] === true;
      return this.json(200, { user: { id: user.id, uploadsBlocked: user.uploadsBlocked } });
    }
    if (method === "GET" && path === "/api/storage") return this.json(200, { storage: d.storage });
    if (method === "GET" && path === "/api/overview") return this.json(200, this.overview as unknown as Record<string, unknown>);
    const rosterResult = this.rosterRoute(method, path, body);
    if (rosterResult) return rosterResult;
    const packResult = this.packRoute(method, path, body);
    if (packResult) return packResult;
    return this.json(404, { error: "not_found" });
  }
}

export interface FakeUpload {
  bytes: Uint8Array;
  type: string;
}

/** Lets pending promises and timers of zero length run. */
export async function settle(rounds = 6): Promise<void> {
  for (let i = 0; i < rounds; i++) await new Promise((resolve) => setTimeout(resolve, 0));
}

export interface Mounted {
  backend: FakeBackend;
  root: HTMLElement;
  copied: string[];
  downloads: { fileName: string; data: unknown }[];
  navigate: (id: string) => void;
}

/** A File with the given bytes (jsdom supports arrayBuffer on File). */
export function imageFile(name: string, bytes: Uint8Array): File {
  return new File([bytes as Uint8Array<ArrayBuffer>], name, { type: name.endsWith(".png") ? "image/png" : "image/jpeg" });
}

/** Sets a file input's selection to several files and fires change (a folder or a multi-file choice). */
export function chooseFiles(input: HTMLInputElement, files: File[]): void {
  Object.defineProperty(input, "files", { value: files, configurable: true });
  input.dispatchEvent(new Event("change"));
}

/** The start of a PNG with the given pixel size (signature and IHDR) and a marker byte so each file has its own bytes. */
export function pngOfSize(width: number, height: number, marker = 0): Uint8Array {
  const bytes = new Uint8Array(26);
  bytes.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]);
  new DataView(bytes.buffer).setUint32(16, width);
  new DataView(bytes.buffer).setUint32(20, height);
  bytes[24] = 8;
  bytes[25] = marker;
  return bytes;
}

/** Waits until `condition` holds, letting pending work run in between; fails after `rounds` tries. */
export async function until(condition: () => boolean, rounds = 50): Promise<void> {
  for (let i = 0; i < rounds; i++) {
    if (condition()) return;
    await settle(2);
  }
  throw new Error("condition not reached");
}

/** Sets a file input's selection and fires change, as a browser would after the file dialog. */
export function chooseFile(input: HTMLInputElement, file: File): void {
  Object.defineProperty(input, "files", { value: [file], configurable: true });
  input.dispatchEvent(new Event("change"));
}

export const PNG_BYTES = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]);
export const JPEG_BYTES = new Uint8Array([0xff, 0xd8, 0xff, 0xdb, 0, 4, 0, 0]);

export async function mount(section: string, options: { hostname?: string; backend?: FakeBackend; online?: boolean; copyWorks?: boolean } = {}): Promise<Mounted> {
  document.body.replaceChildren();
  window.history.replaceState(null, "", `/#/${section}`);
  const backend = options.backend ?? new FakeBackend();
  const root = document.createElement("div");
  document.body.appendChild(root);
  const copied: string[] = [];
  const downloads: { fileName: string; data: unknown }[] = [];
  const { navigate } = mountShell(root, {
    win: window,
    api: new AdminApi(backend.fetch, () => options.online ?? true),
    storage: null,
    hostname: options.hostname ?? "dbdadmin.arcavale.com",
    now: () => NOW,
    copy: async (text) => {
      if (options.copyWorks === false) return false;
      copied.push(text);
      return true;
    },
    download: (fileName, data) => downloads.push({ fileName, data }),
  });
  await settle();
  return { backend, root, copied, downloads, navigate };
}

export function buttonByText(root: ParentNode, text: string | RegExp): HTMLButtonElement {
  const match = [...root.querySelectorAll("button")].find((b) => (typeof text === "string" ? b.textContent?.trim() === text : text.test(b.textContent ?? "")));
  if (!match) throw new Error(`no button ${String(text)}`);
  return match;
}

export function buttonByLabel(root: ParentNode, label: string): HTMLButtonElement {
  const match = [...root.querySelectorAll("button")].find((b) => b.getAttribute("aria-label") === label);
  if (!match) throw new Error(`no button labelled ${label}`);
  return match;
}

export function openDialog(): HTMLDialogElement {
  const dialog = document.querySelector("dialog");
  if (!dialog) throw new Error("no dialog open");
  return dialog;
}
