// Release feed and key-replacement payloads (V2-S6; FR-ONL-10, PLAN_V2 §6.2, §6.5; ADR-0012). The same rules as the app's
// src/DbdPresence.Packs/Releases/ReleaseFeed.cs and KeyReplacement.cs, so the service refuses at upload whatever the app
// would refuse. The service never signs either document: the owner signs them offline with the release or recovery key.

import { decodeBase64Url, encodeBase64Url } from "../lib/base64url.ts";
import { JsonSyntaxError, parseStrict, toPlain } from "../lib/json.ts";

export const RELEASE_PACKAGE_ID = "DbdPresence";
export const RELEASE_CHANNELS = ["stable", "beta"] as const;
export type ReleaseChannel = (typeof RELEASE_CHANNELS)[number];
/** At most 3 packages per feed (owner decision D18: keep only the last 3 packages). */
export const MAX_RELEASES = 3;
/** Below the 100 MB request body limit of the Workers free plan. */
export const MAX_PACKAGE_BYTES = 95_000_000;
export const MAX_NOTES_LENGTH = 2000;
export const KEY_REPLACEMENT_CHANNEL = "all";
const MAX_KEY_ENTRIES = 16;
const SHA256 = /^[0-9a-f]{64}$/;
const KID = /^[A-Za-z0-9._-]{1,128}$/;

export interface ReleasePackage {
  version: string;
  fileName: string;
  size: number;
  sha256: string;
  notes: string | null;
}

export interface ReleaseFeed {
  channel: string;
  releases: ReleasePackage[];
}

export type ParseResult<T> = { ok: true; value: T } | { ok: false; reason: string };

const fail = <T>(reason: string): ParseResult<T> => ({ ok: false, reason });

// ---------------------------------------------------------------------------------------------------------------
// Versions: major.minor.patch with an optional lower-case pre-release label; SemVer 2.0 precedence.

interface Version {
  core: [number, number, number];
  label: string[];
}

function part(text: string): number | null {
  if (!/^(0|[1-9]\d{0,8})$/.test(text)) return null;
  return Number(text);
}

export function parseVersion(text: unknown): Version | null {
  if (typeof text !== "string" || text.length === 0 || text.length > 64) return null;
  const dash = text.indexOf("-");
  const core = (dash < 0 ? text : text.slice(0, dash)).split(".");
  if (core.length !== 3) return null;
  const numbers = core.map(part);
  if (numbers.some((n) => n === null)) return null;
  const label = dash < 0 ? [] : text.slice(dash + 1).split(".");
  for (const id of label) {
    if (!/^[0-9a-z-]+$/.test(id) || (/^\d+$/.test(id) && id.length > 1 && id.startsWith("0"))) return null;
  }
  return { core: numbers as [number, number, number], label };
}

function compareIdentifier(a: string, b: string): number {
  const an = /^\d+$/.test(a);
  const bn = /^\d+$/.test(b);
  if (an && bn) return a.length !== b.length ? Math.sign(a.length - b.length) : a < b ? -1 : a > b ? 1 : 0;
  if (an !== bn) return an ? -1 : 1;
  return a < b ? -1 : a > b ? 1 : 0;
}

/** SemVer 2.0 precedence of two valid versions. */
export function compareVersions(left: string, right: string): number {
  const a = parseVersion(left)!;
  const b = parseVersion(right)!;
  for (let i = 0; i < 3; i++) if (a.core[i] !== b.core[i]) return Math.sign(a.core[i]! - b.core[i]!);
  if (a.label.length === 0 || b.label.length === 0) return a.label.length === b.label.length ? 0 : a.label.length === 0 ? 1 : -1;
  for (let i = 0; i < Math.min(a.label.length, b.label.length); i++) {
    const c = compareIdentifier(a.label[i]!, b.label[i]!);
    if (c !== 0) return c;
  }
  return Math.sign(a.label.length - b.label.length);
}

export function packageFileName(version: string, channel: string): string {
  return `${RELEASE_PACKAGE_ID}-${version}-${channel}-full.nupkg`;
}

function plainObject(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

function onlyMembers(record: Record<string, unknown>, allowed: readonly string[]): boolean {
  return Object.keys(record).every((k) => allowed.includes(k));
}

function parsePayload(payloadJson: string): unknown {
  try {
    return toPlain(parseStrict(payloadJson, 8));
  } catch (error) {
    if (error instanceof JsonSyntaxError) return undefined;
    throw error;
  }
}

/** Reads a release feed payload (newest first), or the reason it is refused (the same reason codes as the C# parser). */
export function parseReleaseFeed(payloadJson: string, channel: string): ParseResult<ReleaseFeed> {
  const root = plainObject(parsePayload(payloadJson));
  if (!root) return fail("malformed");
  if (!onlyMembers(root, ["schemaVersion", "packageId", "releases"])) return fail("unexpected_members");
  if (root["schemaVersion"] !== 1) return fail("unsupported_schema");
  if (root["packageId"] !== RELEASE_PACKAGE_ID) return fail("wrong_package_id");
  const list = root["releases"];
  if (!Array.isArray(list) || list.length > MAX_RELEASES) return fail("bad_release_list");
  const releases: ReleasePackage[] = [];
  for (const item of list) {
    const entry = plainObject(item);
    if (!entry || !onlyMembers(entry, ["version", "fileName", "size", "sha256", "notes"])) return fail("bad_release");
    const version = entry["version"];
    if (typeof version !== "string" || !parseVersion(version)) return fail("bad_version");
    if (entry["fileName"] !== packageFileName(version, channel)) return fail("bad_file_name");
    const size = entry["size"];
    if (typeof size !== "number" || !Number.isInteger(size) || size < 1 || size > MAX_PACKAGE_BYTES) return fail("bad_size");
    const sha = entry["sha256"];
    if (typeof sha !== "string" || !SHA256.test(sha)) return fail("bad_sha256");
    const notes = entry["notes"];
    if (notes !== undefined && (typeof notes !== "string" || notes.length > MAX_NOTES_LENGTH)) return fail("bad_notes");
    if (releases.some((r) => compareVersions(r.version, version) === 0)) return fail("duplicate_version");
    if (releases.some((r) => r.sha256 === sha)) return fail("duplicate_package");
    releases.push({ version, fileName: packageFileName(version, channel), size, sha256: sha, notes: typeof notes === "string" ? notes : null });
  }
  releases.sort((a, b) => compareVersions(b.version, a.version));
  return { ok: true, value: { channel, releases } };
}

// ---------------------------------------------------------------------------------------------------------------
// Key replacements

export interface AddedKey {
  kid: string;
  spki: string;
  purposes: string[];
}

export interface KeyReplacement {
  revoke: string[];
  add: AddedKey[];
}

const ALLOWED_PURPOSE_SETS: readonly string[][] = [["licence"], ["config"], ["pack-index", "roster"], ["releases"]];

function samePurposes(a: string[], b: readonly string[]): boolean {
  return a.length === b.length && b.every((p) => a.includes(p));
}

/** Reads a key-replacement payload, or the reason it is refused (the same reason codes as the C# parser). */
export function parseKeyReplacement(payloadJson: string): ParseResult<KeyReplacement> {
  const root = plainObject(parsePayload(payloadJson));
  if (!root) return fail("malformed");
  if (!onlyMembers(root, ["schemaVersion", "revoke", "add"])) return fail("unexpected_members");
  if (root["schemaVersion"] !== 1) return fail("unsupported_schema");
  const revokeList = root["revoke"];
  const addList = root["add"];
  if (!Array.isArray(revokeList) || revokeList.length > MAX_KEY_ENTRIES || !Array.isArray(addList) || addList.length > MAX_KEY_ENTRIES) return fail("bad_lists");
  const revoke: string[] = [];
  for (const kid of revokeList) {
    if (typeof kid !== "string" || !KID.test(kid) || revoke.includes(kid)) return fail("bad_revoke");
    revoke.push(kid);
  }
  const add: AddedKey[] = [];
  for (const item of addList) {
    const entry = plainObject(item);
    if (!entry || !onlyMembers(entry, ["kid", "spki", "purposes"])) return fail("bad_add");
    const { kid, spki, purposes } = entry as { kid: unknown; spki: unknown; purposes: unknown };
    if (typeof kid !== "string" || !KID.test(kid) || add.some((k) => k.kid === kid) || revoke.includes(kid)) return fail("bad_add");
    if (!Array.isArray(purposes) || purposes.some((p) => typeof p !== "string") || new Set(purposes).size !== purposes.length) return fail("bad_add");
    if (!ALLOWED_PURPOSE_SETS.some((set) => samePurposes(purposes as string[], set))) return fail("bad_add");
    const bytes = typeof spki === "string" ? decodeBase64Url(spki) : null;
    if (!bytes || bytes.length === 0 || bytes.length > 512 || encodeBase64Url(bytes) !== spki) return fail("bad_add");
    add.push({ kid, spki: spki as string, purposes: purposes as string[] });
  }
  if (revoke.length === 0 && add.length === 0) return fail("empty");
  return { ok: true, value: { revoke, add } };
}
