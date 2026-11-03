// Client for the admin API on the panel's own origin (service/src/routes/admin.ts; ADR-0014, FR-ADM-03).
// Every call: same-origin fetch, `credentials: 'same-origin'`, the `X-DBDP-Admin: 1` header, no redirects followed.
// The browser adds `Origin` and `Sec-Fetch-Site: same-origin` to state-changing calls itself. Responses are checked
// against the shapes the service returns before anything is rendered.

export const ADMIN_HEADER = "X-DBDP-Admin";

export type InviteStatus = "active" | "redeemed" | "expired" | "revoked";

export interface Invite {
  id: string;
  keyId: string;
  note: string | null;
  status: InviteStatus;
  createdAt: string;
  createdBy: string;
  expiresAt: string;
  redeemedBy: string | null;
  redeemedAt: string | null;
  revokedAt: string | null;
}

export interface CreatedInvite {
  id: string;
  code: string;
  note: string | null;
  expiresAt: string;
}

export interface Device {
  id: string;
  thumbprint: string;
  createdAt: string;
  lastSeenAt: string;
  revokedAt: string | null;
}

export interface User {
  id: string;
  discordId: string;
  displayName: string;
  status: "active" | "revoked";
  access: "granted" | "invite_required";
  accessVia: string | null;
  createdAt: string;
  lastSeenAt: string;
  revokedAt: string | null;
  devices: Device[];
  /** How many images the user has stored. */
  uploads: number;
  /** New uploads refused for this user (FR-ADM-09). */
  uploadsBlocked: boolean;
  /** The owner marked this as their own game account: its app reports unknown character names (D35). */
  rosterReporter: boolean;
}

export interface AuditEntry {
  seq: number;
  at: string;
  actor: string;
  action: string;
  target: string | null;
  before: unknown;
  after: unknown;
  prevHash: string;
  hash: string;
}

export interface ChainReport {
  ok: boolean;
  checked: number;
  brokenAtSeq: number | null;
  anchored: boolean;
  /** The newest entry of the last daily export and whether the log still holds it unchanged; null before the first export. */
  exportedHead: ExportedHead | null;
}

export interface ExportedHead {
  seq: number;
  hash: string;
  present: boolean;
}

export type AccessMode = "invite-only" | "public";

/** The server settings (service/src/db/settings.ts). Capture and detection switches can only turn things off. */
export interface Settings {
  accessMode: AccessMode;
  deviceLimit: number;
  disableCapture: boolean;
  disableDetection: boolean;
  emergencyStop: boolean;
  minimumVersion: string;
  banner: string | null;
  /** Every new upload refused (FR-ADM-09). Not carried by the signed config. */
  uploadsBlocked: boolean;
  /** Most bytes of pictures one person may store (D28; 60 MB = 62 914 560 by default). Not carried by the signed config. */
  uploadLimitBytes: number;
  /** Most pictures one person may store, a backstop behind the byte cap (300 by default). Not carried by the signed config. */
  uploadLimitFiles: number;
}

export type SettingsPatch = Partial<Omit<Settings, "deviceLimit">>;

export type StorageArea = "packs" | "uploads" | "releases" | "audit";

/** The service's running total of stored bytes (D18): writes past `capBytes` are refused; the panel warns from `warnBytes`. */
export interface StorageUsage {
  usedBytes: number;
  capBytes: number;
  warnBytes: number;
  warning: boolean;
  full: boolean;
  /** New uploads are refused from here (6 GB in production, 500 MB in staging), keeping the rest for audit exports, pack files and releases. */
  uploadCapBytes: number;
  uploadsFull: boolean;
  /** Bytes of removed files whose deletion from storage failed and is retried daily (still counted). */
  pendingDeleteBytes: number;
  areas: Record<StorageArea, { bytes: number; objects: number }>;
}

/** One user upload as the owner sees it (service/src/routes/uploads.ts). */
export interface UploadItem {
  id: string;
  /** The unlisted address on the content host (checked before it is ever loaded, src/cdn.ts). */
  url: string;
  sha256: string;
  format: "png" | "jpeg";
  size: number;
  width: number;
  height: number;
  createdAt: string;
  user: { id: string; displayName: string; uploadsBlocked: boolean };
}

export interface UploadList {
  uploads: UploadItem[];
  /** All uploads stored; the list holds the newest 500. */
  total: number;
  blockedUsers: { id: string; displayName: string }[];
  settings: { uploadsBlocked: boolean; uploadLimitBytes: number; uploadLimitFiles: number };
  storage: StorageUsage;
}

export interface SettingsState {
  settings: Settings;
  /** The version of the signed config that carries these settings; raised by every change. */
  configVersion: number;
}

export interface DiscordApp {
  key: string;
  applicationId: string;
  name: string;
  description: string;
}

export interface AppList {
  applications: DiscordApp[];
  defaultApplication: string;
  configVersion: number;
}

export type EntryKind = "role" | "character" | "map" | "app";
export type PackStatus = "draft" | "published" | "unpublished";

export interface PackEntry {
  id: string;
  kind: EntryKind;
  key: string;
  sha256: string;
  format: "png" | "jpeg";
  size: number;
  width: number;
  height: number;
  licensedHolder: string | null;
  withdrawn: boolean;
  /** In the signed index now. */
  live: boolean;
  /** The file's address on the content host (checked before it is ever loaded, src/cdn.ts). */
  url: string;
  createdAt: string;
  updatedAt: string;
}

export interface PackPermission {
  creator: string;
  date: string;
  proof: string;
}

export interface Pack {
  id: string;
  name: string;
  author: string;
  licenceNote: string;
  credit: string | null;
  fallback: boolean;
  behaviourArt: boolean;
  /** The default pictures for characters (S5b-3); at most one pack. */
  default: boolean;
  thirdParty: boolean;
  permission: PackPermission | null;
  status: PackStatus;
  version: number;
  changedSincePublish: boolean;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
  totalBytes: number;
  entries: PackEntry[];
}

export interface PackIndexSummary {
  version: number;
  publishedAt: string | null;
  packs: number;
  entries: number;
}

export interface PackLimits {
  maxImageBytes: number;
  maxPackBytes: number;
  maxPacks: number;
  maxEntries: number;
}

export interface PackList {
  packs: Pack[];
  index: PackIndexSummary;
  limits: PackLimits;
}

export interface PackFields {
  name: string;
  author: string;
  licenceNote: string;
  credit: string | null;
  fallback: boolean;
  behaviourArt: boolean;
  thirdParty: boolean;
  permission: PackPermission | null;
}

export interface UploadedFile {
  sha256: string;
  /** SHA-256 of the file as chosen, before the service's clean-up; recorded on the entry (S5b-3). */
  sourceSha256: string;
  format: "png" | "jpeg";
  size: number;
  width: number;
  height: number;
  url: string;
}

export interface PackChange {
  pack: Pack;
  /** Set when the change committed a new signed-index version. */
  index: { version: number; publishedAt: string } | null;
}

export type RosterRole = "killer" | "survivor";
export type RosterSource = "seed" | "owner" | "auto";

export interface RosterAlias {
  name: string;
  seenOnScreen: string;
}

/** One character of the roster (service/src/routes/roster.ts). */
export interface RosterCharacter {
  code: string;
  id: string;
  role: RosterRole;
  displayName: string;
  screenName: string;
  aliases: RosterAlias[];
  licensedHolder: string | null;
  source: RosterSource;
  addedAt: string | null;
  withdrawn: boolean;
  withdrawnAt: string | null;
}

/** A name the owner's game saw that is not a character yet (D35). */
export interface RosterSighting {
  name: string;
  role: RosterRole;
  matches: number;
  firstSeenAt: string;
  lastSeenAt: string;
}

export type ReleaseChannel = "stable" | "beta";

/** One package a release feed lists (service/src/routes/releases.ts). */
export interface ReleasePackageView {
  version: string;
  fileName: string;
  size: number;
  sha256: string;
  /** Whether the service already holds the bytes. */
  present: boolean;
}

/** A signed release feed as the service keeps it: the owner signed it offline; the panel only uploads and publishes it. */
export interface ReleaseFeedView {
  id: string;
  channel: ReleaseChannel;
  feedVersion: number;
  kid: string;
  issuedAt: string;
  expiresAt: string;
  expired: boolean;
  newest: string | null;
  status: "staged" | "published" | "superseded" | "discarded";
  uploadedAt: string;
  uploadedBy: string;
  publishedAt: string | null;
  packages: ReleasePackageView[];
}

export interface ReleaseState {
  channels: Record<ReleaseChannel, { published: ReleaseFeedView | null; staged: ReleaseFeedView | null }>;
  keyReplacement: { version: number; kid: string; uploadedAt: string } | null;
  /** The public offline keys the service checks uploads against (never a private key). */
  keys: { release: string[]; recovery: string[]; revoked: string[]; configured: boolean };
  limits: { maxReleases: number; maxPackageBytes: number };
  storage: StorageUsage;
}

export interface RosterState {
  roster: {
    version: number;
    signedAt: string;
    expiresAt: string;
    /** Not re-signed for 25 days: the daily job is failing. */
    stale: boolean;
    characters: RosterCharacter[];
    nextCodes: { killer: string | null; survivor: string | null };
  };
  pending: RosterSighting[];
  blocked: { name: string; blockedAt: string }[];
  reporters: { id: string; displayName: string }[];
  limits: { maxCharacters: number; matchesToAdd: number; maxReporters: number; maxAliases: number };
}

export interface NewCharacter {
  code: string;
  displayName: string;
  screenName: string | null;
  id: string | null;
}

export type ServiceKeyName = "licence" | "config" | "content" | "invite" | "ban";

/** One key secret of the service: whether it is present and usable, and its public id (never key material). */
export interface ServiceKey {
  name: ServiceKeyName;
  configured: boolean;
  kid: string | null;
}

/** What the service has recorded about backups (service/src/backup.ts); null means not recorded. */
export interface BackupStatus {
  lastExportAt: string | null;
  lastExportBytes: number | null;
  lastDrillAt: string | null;
  lastDrillOk: boolean | null;
}

/** The Dashboard and Settings data (service/src/routes/overview.ts): only what the service itself holds. */
export interface Overview {
  /** The service's own name for its deployment ("production" or "staging"). */
  environment: string;
  hosts: { api: string; admin: string; cdn: string };
  users: { total: number; withAccess: number; waitingForInvite: number; revoked: number; activeThisWeek: number };
  invites: { pending: number };
  uploads: { total: number; thisWeek: number };
  storage: StorageUsage;
  /** `sizeBytes` is null when the database did not report its size. */
  database: { sizeBytes: number | null; freeLimitBytes: number };
  keys: {
    service: ServiceKey[];
    offline: ReleaseState["keys"];
    keyReplacement: { version: number; kid: string; uploadedAt: string | null } | null;
  };
  auditExport: { lastAt: string | null };
  backup: BackupStatus;
}

export type ApiErrorKind = "offline" | "session" | "forbidden" | "client" | "server" | "unexpected";

/**
 * A failed call, classified for the error states. `code` is the service's error code when it sent one; `reason` is the
 * rule an uploaded image broke (`image_rejected`).
 */
export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number | null;
  readonly code: string | null;
  readonly reason: string | null;
  /** For 429: the seconds the service (or the edge) asked to wait in `Retry-After`; null when it did not say. */
  readonly retryAfterSeconds: number | null;

  constructor(kind: ApiErrorKind, status: number | null, code: string | null, reason: string | null = null, retryAfterSeconds: number | null = null) {
    super(code ?? kind);
    this.name = "ApiError";
    this.kind = kind;
    this.status = status;
    this.code = code;
    this.reason = reason;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

export type FetchLike = (input: string, init: RequestInit) => Promise<Response>;

// ---------------------------------------------------------------------------------------------------------------
// Shape checks. A response that does not match is reported as "unexpected", never rendered half-understood.

type Json = Record<string, unknown>;

function fail(): never {
  throw new ApiError("unexpected", null, "bad_response");
}

function obj(value: unknown): Json {
  if (typeof value !== "object" || value === null || Array.isArray(value)) fail();
  return value as Json;
}

function arr(value: unknown): unknown[] {
  if (!Array.isArray(value)) fail();
  return value;
}

function str(value: unknown): string {
  if (typeof value !== "string") fail();
  return value;
}

function strOrNull(value: unknown): string | null {
  return value === null ? null : str(value);
}

function int(value: unknown): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value)) fail();
  return value;
}

function bool(value: unknown): boolean {
  if (typeof value !== "boolean") fail();
  return value;
}

function oneOf<T extends string>(value: unknown, options: readonly T[]): T {
  if (typeof value !== "string" || !(options as readonly string[]).includes(value)) fail();
  return value as T;
}

function invite(value: unknown): Invite {
  const o = obj(value);
  return {
    id: str(o["id"]),
    keyId: str(o["keyId"]),
    note: strOrNull(o["note"]),
    status: oneOf(o["status"], ["active", "redeemed", "expired", "revoked"] as const),
    createdAt: str(o["createdAt"]),
    createdBy: str(o["createdBy"]),
    expiresAt: str(o["expiresAt"]),
    redeemedBy: strOrNull(o["redeemedBy"]),
    redeemedAt: strOrNull(o["redeemedAt"]),
    revokedAt: strOrNull(o["revokedAt"]),
  };
}

function createdInvite(value: unknown): CreatedInvite {
  const o = obj(value);
  return { id: str(o["id"]), code: str(o["code"]), note: strOrNull(o["note"]), expiresAt: str(o["expiresAt"]) };
}

function device(value: unknown): Device {
  const o = obj(value);
  return { id: str(o["id"]), thumbprint: str(o["thumbprint"]), createdAt: str(o["createdAt"]), lastSeenAt: str(o["lastSeenAt"]), revokedAt: strOrNull(o["revokedAt"]) };
}

function user(value: unknown): User {
  const o = obj(value);
  return {
    id: str(o["id"]),
    discordId: str(o["discordId"]),
    displayName: str(o["displayName"]),
    status: oneOf(o["status"], ["active", "revoked"] as const),
    access: oneOf(o["access"], ["granted", "invite_required"] as const),
    accessVia: strOrNull(o["accessVia"]),
    createdAt: str(o["createdAt"]),
    lastSeenAt: str(o["lastSeenAt"]),
    revokedAt: strOrNull(o["revokedAt"]),
    devices: arr(o["devices"]).map(device),
    uploads: int(o["uploads"]),
    uploadsBlocked: bool(o["uploadsBlocked"]),
    rosterReporter: bool(o["rosterReporter"]),
  };
}

function auditEntry(value: unknown): AuditEntry {
  const o = obj(value);
  return {
    seq: int(o["seq"]),
    at: str(o["at"]),
    actor: str(o["actor"]),
    action: str(o["action"]),
    target: strOrNull(o["target"]),
    before: o["before"] ?? null,
    after: o["after"] ?? null,
    prevHash: str(o["prevHash"]),
    hash: str(o["hash"]),
  };
}

function chainReport(value: unknown): ChainReport {
  const o = obj(value);
  return {
    ok: bool(o["ok"]),
    checked: int(o["checked"]),
    brokenAtSeq: o["brokenAtSeq"] === null ? null : int(o["brokenAtSeq"]),
    anchored: bool(o["anchored"]),
    exportedHead: exportedHead(o["exportedHead"]),
  };
}

function exportedHead(value: unknown): ExportedHead | null {
  if (value === null) return null;
  const o = obj(value);
  return { seq: int(o["seq"]), hash: str(o["hash"]), present: bool(o["present"]) };
}

function settings(value: unknown): Settings {
  const o = obj(value);
  return {
    accessMode: oneOf(o["accessMode"], ["invite-only", "public"] as const),
    deviceLimit: int(o["deviceLimit"]),
    disableCapture: bool(o["disableCapture"]),
    disableDetection: bool(o["disableDetection"]),
    emergencyStop: bool(o["emergencyStop"]),
    minimumVersion: str(o["minimumVersion"]),
    banner: strOrNull(o["banner"]),
    uploadsBlocked: bool(o["uploadsBlocked"]),
    uploadLimitBytes: int(o["uploadLimitBytes"]),
    uploadLimitFiles: int(o["uploadLimitFiles"]),
  };
}

const AREAS = ["packs", "uploads", "releases", "audit"] as const;
const RELEASE_CHANNELS = ["stable", "beta"] as const;

function releaseFeed(value: unknown): ReleaseFeedView {
  const o = obj(value);
  return {
    id: str(o["id"]),
    channel: oneOf(o["channel"], RELEASE_CHANNELS),
    feedVersion: int(o["feedVersion"]),
    kid: str(o["kid"]),
    issuedAt: str(o["issuedAt"]),
    expiresAt: str(o["expiresAt"]),
    expired: bool(o["expired"]),
    newest: strOrNull(o["newest"]),
    status: oneOf(o["status"], ["staged", "published", "superseded", "discarded"] as const),
    uploadedAt: str(o["uploadedAt"]),
    uploadedBy: str(o["uploadedBy"]),
    publishedAt: strOrNull(o["publishedAt"]),
    packages: arr(o["packages"]).map((p) => {
      const pkg = obj(p);
      return { version: str(pkg["version"]), fileName: str(pkg["fileName"]), size: int(pkg["size"]), sha256: sha(pkg["sha256"]), present: bool(pkg["present"]) };
    }),
  };
}

function releaseState(value: Json): ReleaseState {
  const channels = obj(value["channels"]);
  const channel = (name: ReleaseChannel) => {
    const c = obj(channels[name]);
    return { published: c["published"] === null ? null : releaseFeed(c["published"]), staged: c["staged"] === null ? null : releaseFeed(c["staged"]) };
  };
  const replacement = value["keyReplacement"] === null ? null : obj(value["keyReplacement"]);
  const l = obj(value["limits"]);
  return {
    channels: { stable: channel("stable"), beta: channel("beta") },
    keyReplacement: replacement === null ? null : { version: int(replacement["version"]), kid: str(replacement["kid"]), uploadedAt: str(replacement["uploadedAt"]) },
    keys: offlineKeys(value["keys"]),
    limits: { maxReleases: int(l["maxReleases"]), maxPackageBytes: int(l["maxPackageBytes"]) },
    storage: storage(value["storage"]),
  };
}

function storage(value: unknown): StorageUsage {
  const o = obj(value);
  const areas = obj(o["areas"]);
  const area = (name: StorageArea) => {
    const a = obj(areas[name]);
    return { bytes: int(a["bytes"]), objects: int(a["objects"]) };
  };
  return {
    usedBytes: int(o["usedBytes"]),
    capBytes: int(o["capBytes"]),
    warnBytes: int(o["warnBytes"]),
    warning: bool(o["warning"]),
    full: bool(o["full"]),
    uploadCapBytes: int(o["uploadCapBytes"]),
    uploadsFull: bool(o["uploadsFull"]),
    pendingDeleteBytes: int(o["pendingDeleteBytes"]),
    areas: Object.fromEntries(AREAS.map((a) => [a, area(a)])) as Record<StorageArea, { bytes: number; objects: number }>,
  };
}

function intOrNull(value: unknown): number | null {
  return value === null ? null : int(value);
}

function boolOrNull(value: unknown): boolean | null {
  return value === null ? null : bool(value);
}

function offlineKeys(value: unknown): ReleaseState["keys"] {
  const keys = obj(value);
  return { release: arr(keys["release"]).map(str), recovery: arr(keys["recovery"]).map(str), revoked: arr(keys["revoked"]).map(str), configured: bool(keys["configured"]) };
}

const SERVICE_KEYS = ["licence", "config", "content", "invite", "ban"] as const;

function overview(value: Json): Overview {
  const hosts = obj(value["hosts"]);
  const u = obj(value["users"]);
  const up = obj(value["uploads"]);
  const db = obj(value["database"]);
  const keys = obj(value["keys"]);
  const replacement = keys["keyReplacement"] === null ? null : obj(keys["keyReplacement"]);
  const b = obj(value["backup"]);
  return {
    environment: str(value["environment"]),
    hosts: { api: str(hosts["api"]), admin: str(hosts["admin"]), cdn: str(hosts["cdn"]) },
    users: { total: int(u["total"]), withAccess: int(u["withAccess"]), waitingForInvite: int(u["waitingForInvite"]), revoked: int(u["revoked"]), activeThisWeek: int(u["activeThisWeek"]) },
    invites: { pending: int(obj(value["invites"])["pending"]) },
    uploads: { total: int(up["total"]), thisWeek: int(up["thisWeek"]) },
    storage: storage(value["storage"]),
    database: { sizeBytes: intOrNull(db["sizeBytes"]), freeLimitBytes: int(db["freeLimitBytes"]) },
    keys: {
      service: arr(keys["service"]).map((k) => {
        const o = obj(k);
        return { name: oneOf(o["name"], SERVICE_KEYS), configured: bool(o["configured"]), kid: strOrNull(o["kid"]) };
      }),
      offline: offlineKeys(keys["offline"]),
      keyReplacement: replacement === null ? null : { version: int(replacement["version"]), kid: str(replacement["kid"]), uploadedAt: strOrNull(replacement["uploadedAt"]) },
    },
    auditExport: { lastAt: strOrNull(obj(value["auditExport"])["lastAt"]) },
    backup: { lastExportAt: strOrNull(b["lastExportAt"]), lastExportBytes: intOrNull(b["lastExportBytes"]), lastDrillAt: strOrNull(b["lastDrillAt"]), lastDrillOk: boolOrNull(b["lastDrillOk"]) },
  };
}

function uploadItem(value: unknown): UploadItem {
  const o = obj(value);
  const u = obj(o["user"]);
  return {
    id: str(o["id"]),
    url: str(o["url"]),
    sha256: sha(o["sha256"]),
    format: oneOf(o["format"], ["png", "jpeg"] as const),
    size: int(o["size"]),
    width: int(o["width"]),
    height: int(o["height"]),
    createdAt: str(o["createdAt"]),
    user: { id: str(u["id"]), displayName: str(u["displayName"]), uploadsBlocked: bool(u["uploadsBlocked"]) },
  };
}

function uploadList(value: Json): UploadList {
  const s = obj(value["settings"]);
  return {
    uploads: arr(value["uploads"]).map(uploadItem),
    total: int(value["total"]),
    blockedUsers: arr(value["blockedUsers"]).map((b) => {
      const o = obj(b);
      return { id: str(o["id"]), displayName: str(o["displayName"]) };
    }),
    settings: { uploadsBlocked: bool(s["uploadsBlocked"]), uploadLimitBytes: int(s["uploadLimitBytes"]), uploadLimitFiles: int(s["uploadLimitFiles"]) },
    storage: storage(value["storage"]),
  };
}

function settingsState(value: Json): SettingsState {
  return { settings: settings(value["settings"]), configVersion: int(value["configVersion"]) };
}

function discordApp(value: unknown): DiscordApp {
  const o = obj(value);
  return { key: str(o["key"]), applicationId: str(o["applicationId"]), name: str(o["name"]), description: str(o["description"]) };
}

function appList(value: Json): AppList {
  return { applications: arr(value["applications"]).map(discordApp), defaultApplication: str(value["defaultApplication"]), configVersion: int(value["configVersion"]) };
}

const SHA256 = /^[0-9a-f]{64}$/;

function sha(value: unknown): string {
  const s = str(value);
  if (!SHA256.test(s)) fail();
  return s;
}

function packEntry(value: unknown): PackEntry {
  const o = obj(value);
  return {
    id: str(o["id"]),
    kind: oneOf(o["kind"], ["role", "character", "map", "app"] as const),
    key: str(o["key"]),
    sha256: sha(o["sha256"]),
    format: oneOf(o["format"], ["png", "jpeg"] as const),
    size: int(o["size"]),
    width: int(o["width"]),
    height: int(o["height"]),
    licensedHolder: strOrNull(o["licensedHolder"]),
    withdrawn: bool(o["withdrawn"]),
    live: bool(o["live"]),
    url: str(o["url"]),
    createdAt: str(o["createdAt"]),
    updatedAt: str(o["updatedAt"]),
  };
}

function permission(value: unknown): PackPermission | null {
  if (value === null) return null;
  const o = obj(value);
  return { creator: str(o["creator"]), date: str(o["date"]), proof: str(o["proof"]) };
}

function pack(value: unknown): Pack {
  const o = obj(value);
  return {
    id: str(o["id"]),
    name: str(o["name"]),
    author: str(o["author"]),
    licenceNote: str(o["licenceNote"]),
    credit: strOrNull(o["credit"]),
    fallback: bool(o["fallback"]),
    behaviourArt: bool(o["behaviourArt"]),
    default: bool(o["default"]),
    thirdParty: bool(o["thirdParty"]),
    permission: permission(o["permission"]),
    status: oneOf(o["status"], ["draft", "published", "unpublished"] as const),
    version: int(o["version"]),
    changedSincePublish: bool(o["changedSincePublish"]),
    createdAt: str(o["createdAt"]),
    updatedAt: str(o["updatedAt"]),
    publishedAt: strOrNull(o["publishedAt"]),
    totalBytes: int(o["totalBytes"]),
    entries: arr(o["entries"]).map(packEntry),
  };
}

function indexSummary(value: unknown): PackIndexSummary {
  const o = obj(value);
  return { version: int(o["version"]), publishedAt: strOrNull(o["publishedAt"]), packs: int(o["packs"]), entries: int(o["entries"]) };
}

function limits(value: unknown): PackLimits {
  const o = obj(value);
  return { maxImageBytes: int(o["maxImageBytes"]), maxPackBytes: int(o["maxPackBytes"]), maxPacks: int(o["maxPacks"]), maxEntries: int(o["maxEntries"]) };
}

function packChange(value: Json): PackChange {
  const index = value["index"] === undefined || value["index"] === null ? null : obj(value["index"]);
  return { pack: pack(value["pack"]), index: index === null ? null : { version: int(index["version"]), publishedAt: str(index["publishedAt"]) } };
}

function uploadedFile(value: unknown): UploadedFile {
  const o = obj(value);
  return { sha256: sha(o["sha256"]), sourceSha256: sha(o["sourceSha256"]), format: oneOf(o["format"], ["png", "jpeg"] as const), size: int(o["size"]), width: int(o["width"]), height: int(o["height"]), url: str(o["url"]) };
}

const ROLES = ["killer", "survivor"] as const;

function rosterCharacter(value: unknown): RosterCharacter {
  const o = obj(value);
  return {
    code: str(o["code"]),
    id: str(o["id"]),
    role: oneOf(o["role"], ROLES),
    displayName: str(o["displayName"]),
    screenName: str(o["screenName"]),
    aliases: arr(o["aliases"]).map((a) => {
      const alias = obj(a);
      return { name: str(alias["name"]), seenOnScreen: str(alias["seenOnScreen"]) };
    }),
    licensedHolder: strOrNull(o["licensedHolder"]),
    source: oneOf(o["source"], ["seed", "owner", "auto"] as const),
    addedAt: strOrNull(o["addedAt"]),
    withdrawn: bool(o["withdrawn"]),
    withdrawnAt: strOrNull(o["withdrawnAt"]),
  };
}

function rosterState(value: Json): RosterState {
  const r = obj(value["roster"]);
  const next = obj(r["nextCodes"]);
  const l = obj(value["limits"]);
  return {
    roster: {
      version: int(r["version"]),
      signedAt: str(r["signedAt"]),
      expiresAt: str(r["expiresAt"]),
      stale: bool(r["stale"]),
      characters: arr(r["characters"]).map(rosterCharacter),
      nextCodes: { killer: strOrNull(next["killer"]), survivor: strOrNull(next["survivor"]) },
    },
    pending: arr(value["pending"]).map((p) => {
      const o = obj(p);
      return { name: str(o["name"]), role: oneOf(o["role"], ROLES), matches: int(o["matches"]), firstSeenAt: str(o["firstSeenAt"]), lastSeenAt: str(o["lastSeenAt"]) };
    }),
    blocked: arr(value["blocked"]).map((b) => {
      const o = obj(b);
      return { name: str(o["name"]), blockedAt: str(o["blockedAt"]) };
    }),
    reporters: arr(value["reporters"]).map((u) => {
      const o = obj(u);
      return { id: str(o["id"]), displayName: str(o["displayName"]) };
    }),
    limits: { maxCharacters: int(l["maxCharacters"]), matchesToAdd: int(l["matchesToAdd"]), maxReporters: int(l["maxReporters"]), maxAliases: int(l["maxAliases"]) },
  };
}

const CODE = /^[KS][0-9]{2}$/;
const FOLDED = /^[a-z0-9]+( [a-z0-9]+)*$/;

function code(value: string): string {
  if (!CODE.test(value)) throw new ApiError("client", null, "invalid_id");
  return value;
}

// ---------------------------------------------------------------------------------------------------------------

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function id(value: string): string {
  if (!UUID.test(value)) throw new ApiError("client", null, "invalid_id");
  return value;
}

const PACK_ID = /^[a-z0-9-]{1,64}$/;
const ENTRY_ID = /^e[0-9a-f]{24}$/;

function packId(value: string): string {
  if (!PACK_ID.test(value)) throw new ApiError("client", null, "invalid_id");
  return value;
}

function entryId(value: string): string {
  if (!ENTRY_ID.test(value)) throw new ApiError("client", null, "invalid_id");
  return value;
}

export interface CreateInvitesInput {
  count: number;
  note: string | null;
  expiresInDays: number;
}

export class AdminApi {
  private readonly fetchImpl: FetchLike;
  private readonly isOnline: () => boolean;

  constructor(fetchImpl: FetchLike, isOnline: () => boolean = () => true) {
    this.fetchImpl = fetchImpl;
    this.isOnline = isOnline;
  }

  /**
   * Sends one admin call and returns the parsed JSON body, or throws a classified `ApiError`. `body` is sent as JSON;
   * `raw` sends bytes with their own type (image uploads).
   */
  async request(method: "GET" | "POST" | "PUT" | "DELETE", path: string, body?: Json, raw?: { bytes: Uint8Array<ArrayBuffer>; type: string }): Promise<Json> {
    if (!path.startsWith("/api/")) throw new Error(`not an admin API path: ${path}`);
    const headers: Record<string, string> = { [ADMIN_HEADER]: "1", Accept: "application/json" };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    else if (raw !== undefined) headers["Content-Type"] = raw.type;
    const init: RequestInit = {
      method,
      headers,
      credentials: "same-origin",
      mode: "same-origin",
      redirect: "manual",
      cache: "no-store",
      referrerPolicy: "no-referrer",
    };
    if (body !== undefined) init.body = JSON.stringify(body);
    else if (raw !== undefined) init.body = raw.bytes;

    let response: Response;
    try {
      response = await this.fetchImpl(path, init);
    } catch (error) {
      if (error instanceof TypeError) throw new ApiError("offline", null, this.isOnline() ? "network" : "no_connection");
      throw error;
    }

    // Cloudflare Access answers an expired session with a redirect to its sign-in page; `redirect: "manual"` turns
    // that into an opaque response instead of following it to another origin.
    if (response.type === "opaqueredirect" || (response.status >= 300 && response.status < 400)) throw new ApiError("session", null, "access_redirect");

    const type = response.headers.get("Content-Type") ?? "";
    let parsed: Json | null = null;
    if (/^application\/json\b/i.test(type)) {
      try {
        parsed = obj(await response.json());
      } catch (error) {
        if (error instanceof SyntaxError || error instanceof ApiError) parsed = null;
        else throw error;
      }
    }
    const code = parsed !== null && typeof parsed["error"] === "string" ? parsed["error"] : null;
    const reason = parsed !== null && typeof parsed["reason"] === "string" ? parsed["reason"] : null;

    if (response.status === 401) throw new ApiError("session", 401, code);
    if (response.status === 403) throw new ApiError("forbidden", 403, code);
    if (response.status >= 500) throw new ApiError("server", response.status, code);
    if (response.status === 429) {
      const retryAfter = /^\d{1,5}$/.exec(response.headers.get("Retry-After")?.trim() ?? "");
      throw new ApiError("client", 429, code, reason, retryAfter ? Number(retryAfter[0]) : null);
    }
    if (response.status >= 400) throw new ApiError("client", response.status, code, reason);
    if (parsed === null || parsed["schemaVersion"] !== 1) throw new ApiError("unexpected", response.status, null);
    return parsed;
  }

  /** The Dashboard and Settings overview: counts, storage, database size, key status and backup status (read-only). */
  async getOverview(): Promise<Overview> {
    return overview(await this.request("GET", "/api/overview"));
  }

  async listInvites(): Promise<Invite[]> {
    return arr((await this.request("GET", "/api/invites"))["invites"]).map(invite);
  }

  async createInvites(input: CreateInvitesInput): Promise<CreatedInvite[]> {
    const body: Json = { count: input.count, expiresInDays: input.expiresInDays };
    if (input.note !== null) body["note"] = input.note;
    return arr((await this.request("POST", "/api/invites", body))["invites"]).map(createdInvite);
  }

  async revokeInvite(inviteId: string): Promise<Invite> {
    return invite((await this.request("POST", `/api/invites/${id(inviteId)}/revoke`, {}))["invite"]);
  }

  async listUsers(): Promise<User[]> {
    return arr((await this.request("GET", "/api/users"))["users"]).map(user);
  }

  /** Returns when the user was revoked. */
  async revokeUser(userId: string): Promise<string> {
    return str(obj((await this.request("POST", `/api/users/${id(userId)}/revoke`, {}))["user"])["revokedAt"]);
  }

  /** Returns when the device was revoked. */
  async revokeDevice(userId: string, deviceId: string): Promise<string> {
    return str(obj((await this.request("POST", `/api/users/${id(userId)}/devices/${id(deviceId)}/revoke`, {}))["device"])["revokedAt"]);
  }

  /**
   * Deletion repeats the user id in the body; the service refuses a mismatch (`confirmation_mismatch`). Returns false when an
   * uploaded file could not be deleted from storage yet (its address already answers 404; the service retries daily).
   */
  async deleteUser(userId: string): Promise<boolean> {
    const result = await this.request("DELETE", `/api/users/${id(userId)}`, { confirm: id(userId) });
    if (result["deleted"] !== true) fail();
    return bool(result["filesRemoved"]);
  }

  /** Everything the service holds about one user. The export itself is recorded in the audit log. */
  async exportUser(userId: string): Promise<Json> {
    return this.request("GET", `/api/users/${id(userId)}/export`);
  }

  async listAudit(before: number | null, limit: number): Promise<AuditEntry[]> {
    const query = new URLSearchParams({ limit: String(limit) });
    if (before !== null) query.set("before", String(before));
    return arr((await this.request("GET", `/api/audit?${query.toString()}`))["entries"]).map(auditEntry);
  }

  async verifyAudit(): Promise<ChainReport> {
    return chainReport((await this.request("GET", "/api/audit/verify"))["chain"]);
  }

  async getSettings(): Promise<Settings> {
    return settings((await this.request("GET", "/api/settings"))["settings"]);
  }

  async setAccessMode(mode: AccessMode): Promise<Settings> {
    return settings((await this.request("PUT", "/api/settings", { accessMode: mode }))["settings"]);
  }

  async getSettingsState(): Promise<SettingsState> {
    return settingsState(await this.request("GET", "/api/settings"));
  }

  async saveSettings(patch: SettingsPatch): Promise<SettingsState> {
    return settingsState(await this.request("PUT", "/api/settings", { ...patch }));
  }

  // --- Discord applications ---------------------------------------------------------------------------------

  async getApps(): Promise<AppList> {
    return appList(await this.request("GET", "/api/apps"));
  }

  async saveApps(applications: DiscordApp[], defaultApplication: string): Promise<AppList> {
    return appList(await this.request("PUT", "/api/apps", { applications: applications.map((a) => ({ ...a })), defaultApplication }));
  }

  // --- Packs ------------------------------------------------------------------------------------------------

  async listPacks(): Promise<PackList> {
    const result = await this.request("GET", "/api/packs");
    return { packs: arr(result["packs"]).map(pack), index: indexSummary(result["index"]), limits: limits(result["limits"]) };
  }

  async createPack(packIdValue: string, fields: PackFields): Promise<Pack> {
    return pack((await this.request("POST", "/api/packs", { id: packId(packIdValue), ...fields }))["pack"]);
  }

  async updatePack(packIdValue: string, fields: Partial<PackFields>): Promise<Pack> {
    return pack((await this.request("PUT", `/api/packs/${packId(packIdValue)}`, { ...fields }))["pack"]);
  }

  /** Deletion repeats the pack id in the body; returns how many files were deleted. */
  async deletePack(packIdValue: string): Promise<number> {
    const result = await this.request("DELETE", `/api/packs/${packId(packIdValue)}`, { confirm: packId(packIdValue) });
    if (result["deleted"] !== true) fail();
    return int(result["filesDeleted"]);
  }

  async publishPack(packIdValue: string): Promise<PackChange> {
    return packChange(await this.request("POST", `/api/packs/${packId(packIdValue)}/publish`, {}));
  }

  async unpublishPack(packIdValue: string): Promise<PackChange> {
    return packChange(await this.request("POST", `/api/packs/${packId(packIdValue)}/unpublish`, {}));
  }

  /** Uploads one PNG or JPEG; the service removes metadata and names what it removed. */
  async uploadFile(bytes: Uint8Array<ArrayBuffer>, type: "image/png" | "image/jpeg"): Promise<{ file: UploadedFile; removed: string[] }> {
    const result = await this.request("POST", "/api/pack-files", undefined, { bytes, type });
    return { file: uploadedFile(result["file"]), removed: arr(result["removed"]).map(str) };
  }

  async purgeFiles(): Promise<{ unreferenced: number; orphaned: number }> {
    const purged = obj((await this.request("POST", "/api/pack-files/purge", {}))["purged"]);
    return { unreferenced: int(purged["unreferenced"]), orphaned: int(purged["orphaned"]) };
  }

  async addEntry(packIdValue: string, input: { key: string; sha256: string; licensedHolder: string | null; sourceSha256?: string }): Promise<Pack> {
    return pack((await this.request("POST", `/api/packs/${packId(packIdValue)}/entries`, { ...input }))["pack"]);
  }

  async updateEntry(packIdValue: string, entryIdValue: string, patch: Partial<{ key: string; sha256: string; licensedHolder: string | null; sourceSha256: string | null }>): Promise<Pack> {
    return pack((await this.request("PUT", `/api/packs/${packId(packIdValue)}/entries/${entryId(entryIdValue)}`, { ...patch }))["pack"]);
  }

  /** Returns the pack and how many files were deleted with the entry. */
  async removeEntry(packIdValue: string, entryIdValue: string): Promise<{ pack: Pack; filesDeleted: number }> {
    const result = await this.request("DELETE", `/api/packs/${packId(packIdValue)}/entries/${entryId(entryIdValue)}`);
    return { pack: pack(result["pack"]), filesDeleted: int(result["filesDeleted"]) };
  }

  // --- Uploads (FR-ADM-09) and storage (D18) ---------------------------------------------------------------

  async listUploads(): Promise<UploadList> {
    return uploadList(await this.request("GET", "/api/uploads"));
  }

  /**
   * Removes one upload: its address stops working at once. `fileRemoved` is false when the stored file could not be deleted
   * yet (the service retries daily). Returns the storage use afterwards.
   */
  async takeDownUpload(uploadId: string): Promise<{ storage: StorageUsage; fileRemoved: boolean }> {
    const result = await this.request("POST", `/api/uploads/${id(uploadId)}/takedown`, {});
    if (result["takenDown"] !== true) fail();
    return { storage: storage(result["storage"]), fileRemoved: bool(result["fileRemoved"]) };
  }

  /** Blocks (or allows again) new uploads for one user. */
  async setUserUploadBlock(userId: string, blocked: boolean): Promise<boolean> {
    return bool(obj((await this.request("POST", `/api/users/${id(userId)}/uploads`, { blocked }))["user"])["uploadsBlocked"]);
  }

  // --- Releases (FR-ADM-10, V2-S6): feeds and packages signed offline; the panel never holds the release key ------------------

  async getReleases(): Promise<ReleaseState> {
    return releaseState(await this.request("GET", "/api/releases"));
  }

  /** Stages a signed release feed for a channel (the service verifies it as the app would). */
  async stageReleaseFeed(channel: ReleaseChannel, document: string): Promise<ReleaseFeedView> {
    return releaseFeed((await this.request("POST", "/api/releases/feeds", { channel, document }))["feed"]);
  }

  /** Uploads the bytes of one package a staged feed lists; `existing` when the service already had them. */
  async uploadReleasePackage(sha256: string, bytes: Uint8Array<ArrayBuffer>): Promise<{ existing: boolean }> {
    if (!SHA256.test(sha256)) throw new ApiError("client", null, "invalid_id");
    return { existing: bool((await this.request("PUT", `/api/releases/packages/${sha256}`, undefined, { bytes, type: "application/octet-stream" }))["existing"]) };
  }

  /** Publishes a staged feed; returns it and how many old packages were deleted (only the last 3 are kept). */
  async publishReleaseFeed(feedId: string): Promise<{ feed: ReleaseFeedView; pruned: number }> {
    const result = await this.request("POST", `/api/releases/feeds/${id(feedId)}/publish`, {});
    return { feed: releaseFeed(result["feed"]), pruned: int(obj(result["pruned"])["deleted"]) };
  }

  async discardReleaseFeed(feedId: string): Promise<void> {
    if ((await this.request("POST", `/api/releases/feeds/${id(feedId)}/discard`, {}))["discarded"] !== true) fail();
  }

  /** Uploads a key replacement signed offline by the recovery key. */
  async uploadKeyReplacement(document: string): Promise<{ version: number; revoke: string[]; add: string[] }> {
    const r = obj((await this.request("POST", "/api/keys/replacement", { document }))["keyReplacement"]);
    return { version: int(r["version"]), revoke: arr(r["revoke"]).map(str), add: arr(r["add"]).map(str) };
  }

  async getStorage(): Promise<StorageUsage> {
    return storage((await this.request("GET", "/api/storage"))["storage"]);
  }

  async withdrawEntry(packIdValue: string, entryIdValue: string, withdrawn: boolean): Promise<PackChange> {
    return packChange(await this.request("POST", `/api/packs/${packId(packIdValue)}/entries/${entryId(entryIdValue)}/withdraw`, { withdrawn }));
  }

  /** Marks one pack as the default pictures for characters, or clears the mark (S5b-3). */
  async setDefaultPack(packIdValue: string, isDefault: boolean): Promise<PackChange> {
    return packChange(await this.request("PUT", `/api/packs/${packId(packIdValue)}/default`, { default: isDefault }));
  }

  // --- Characters (PLAN_V2_S5B §2.8, D35) ------------------------------------------------------------------

  async getRoster(): Promise<RosterState> {
    return rosterState(await this.request("GET", "/api/roster"));
  }

  async addRosterCharacter(input: NewCharacter): Promise<RosterState> {
    const body: Json = { code: code(input.code), displayName: input.displayName };
    if (input.screenName !== null) body["screenName"] = input.screenName;
    if (input.id !== null) body["id"] = input.id;
    return rosterState(await this.request("POST", "/api/roster/characters", body));
  }

  async renameRosterCharacter(codeValue: string, names: { displayName: string; screenName: string }): Promise<RosterState> {
    return rosterState(await this.request("PUT", `/api/roster/characters/${code(codeValue)}`, { ...names }));
  }

  /** Removes (Undo for a learned character, which also blocks its name) or restores a character. */
  async withdrawRosterCharacter(codeValue: string, withdrawn: boolean): Promise<RosterState> {
    return rosterState(await this.request("POST", `/api/roster/characters/${code(codeValue)}/withdraw`, { withdrawn }));
  }

  /** Lets a removed name be learned from the game again. */
  async allowRosterName(name: string): Promise<RosterState> {
    if (!FOLDED.test(name) || name.length > 40) throw new ApiError("client", null, "invalid_id");
    return rosterState(await this.request("DELETE", `/api/roster/blocks/${name.replaceAll(" ", "-")}`));
  }

  /** Forgets every name that was seen in the game but not added yet (round-2 review I2). */
  async clearRosterSightings(): Promise<RosterState> {
    return rosterState(await this.request("DELETE", "/api/roster/sightings"));
  }

  /** Marks (or unmarks) an app account as the owner's own game account (D35). */
  async setRosterReporter(userId: string, enabled: boolean): Promise<boolean> {
    return bool(obj((await this.request("PUT", `/api/users/${id(userId)}/roster-reporter`, { enabled }))["user"])["rosterReporter"]);
  }
}
