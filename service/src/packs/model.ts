// The pack index payload (FR-PACK-01, FR-ONL-07): built from the published copy of each pack, exactly in the format
// parsed by src/DbdPresence.Packs/Index/PackIndexParser.cs:
//   { "schemaVersion": 1,
//     "packs":   [{ id, name, version, author, licenceNote, credit, fallback, behaviourArt, default }],
//     "entries": [{ id, pack, kind, key, sha256, sourceSha256, size, width, height, format, licensed }] }
// `licensed` is null or { "holder": "..." }. `default` marks the one pack whose character pictures are the base layer
// (PLAN_V2_S5B §2.5, S5b-3; at most one per channel). `sourceSha256` is null or the SHA-256 of the file the owner
// uploaded before the lossless clean-up, so an app that imports the same creator's file can match it (§2.6). Withdrawn licensed entries are left out (FR-PACK-04): the app then uses
// the fallback pack's entry for that key, else no image (D16). The caps below are the C# parser's, so an index the
// service signs is never refused by the app. Pure code: also used by scripts/make-published-index-fixture.mjs.

export type EntryKind = "role" | "character" | "map" | "app";
export const ENTRY_KINDS: readonly EntryKind[] = ["role", "character", "map", "app"];

export const PACK_ID_PATTERN = /^[a-z0-9-]{1,64}$/;
export const ENTRY_ID_PATTERN = /^e[0-9a-f]{24}$/;
export const KEY_PATTERN = /^(role|character|map|app):[a-z0-9-]{1,64}$/;
export const SHA256_PATTERN = /^[0-9a-f]{64}$/;

/** PackIndexParser caps. */
export const MAX_PACKS = 100;
export const MAX_ENTRIES = 4000;
/** 32 MB per pack (PLAN_V2_S5B §2.5: the larger of the owner's two packs is 21 MB). */
export const MAX_PACK_BYTES = 32 * 1024 * 1024;
export const MAX_TEXT = 200;

export interface PublishedPack {
  id: string;
  name: string;
  author: string;
  licenceNote: string;
  credit: string | null;
  fallback: boolean;
  behaviourArt: boolean;
  /** The default pictures for characters (S5b-3); at most one pack. */
  default: boolean;
  version: number;
}

export interface PublishedEntry {
  id: string;
  pack: string;
  kind: EntryKind;
  key: string;
  sha256: string;
  format: "png" | "jpeg";
  size: number;
  width: number;
  height: number;
  licensedHolder: string | null;
  withdrawn: boolean;
  /** SHA-256 of the file as uploaded, before the lossless clean-up; null when not recorded. */
  sourceSha256: string | null;
}

export interface IndexPayload {
  schemaVersion: 1;
  packs: {
    id: string;
    name: string;
    version: number;
    author: string;
    licenceNote: string;
    credit: string | null;
    fallback: boolean;
    behaviourArt: boolean;
    default: boolean;
  }[];
  entries: {
    id: string;
    pack: string;
    kind: EntryKind;
    key: string;
    sha256: string;
    sourceSha256: string | null;
    size: number;
    width: number;
    height: number;
    format: "png" | "jpeg";
    licensed: { holder: string } | null;
  }[];
}

const byId = <T extends { id: string }>(a: T, b: T): number => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);

/** The payload for a published set: packs by id, entries by pack then key; withdrawn entries left out. */
export function indexPayload(packs: readonly PublishedPack[], entries: readonly PublishedEntry[]): IndexPayload {
  const packIds = new Set(packs.map((p) => p.id));
  return {
    schemaVersion: 1,
    packs: [...packs].sort(byId).map((p) => ({
      id: p.id,
      name: p.name,
      version: p.version,
      author: p.author,
      licenceNote: p.licenceNote,
      credit: p.credit,
      fallback: p.fallback,
      behaviourArt: p.behaviourArt,
      default: p.default,
    })),
    entries: entries
      .filter((e) => !e.withdrawn && packIds.has(e.pack))
      .sort((a, b) => (a.pack !== b.pack ? (a.pack < b.pack ? -1 : 1) : a.key < b.key ? -1 : a.key > b.key ? 1 : 0))
      .map((e) => ({
        id: e.id,
        pack: e.pack,
        kind: e.kind,
        key: e.key,
        sha256: e.sha256,
        sourceSha256: e.sourceSha256,
        size: e.size,
        width: e.width,
        height: e.height,
        format: e.format,
        licensed: e.licensedHolder === null ? null : { holder: e.licensedHolder },
      })),
  };
}

/** The first rule of PackIndexParser.Validate that the payload breaks, or null when it would parse. */
export function indexProblem(payload: IndexPayload): string | null {
  if (payload.packs.length > MAX_PACKS) return "too_many_packs";
  if (payload.entries.length > MAX_ENTRIES) return "too_many_entries";
  const text = (s: string) => s.trim().length > 0 && s.length <= MAX_TEXT;
  if (payload.packs.filter((p) => p.default).length > 1) return "two_defaults";
  const packIds = new Set<string>();
  for (const p of payload.packs) {
    if (!PACK_ID_PATTERN.test(p.id) || packIds.has(p.id)) return "bad_pack";
    packIds.add(p.id);
    if (!text(p.name) || !text(p.author) || !text(p.licenceNote) || (p.credit !== null && !text(p.credit)) || p.version < 1) return "bad_pack";
  }
  const entryIds = new Set<string>();
  const keys = new Set<string>();
  const bytes = new Map<string, number>();
  for (const e of payload.entries) {
    if (!ENTRY_ID_PATTERN.test(e.id) || entryIds.has(e.id) || !packIds.has(e.pack)) return "bad_entry";
    entryIds.add(e.id);
    if (!KEY_PATTERN.test(e.key) || !e.key.startsWith(`${e.kind}:`) || keys.has(`${e.pack}|${e.key}`)) return "bad_entry";
    keys.add(`${e.pack}|${e.key}`);
    if (e.sourceSha256 !== null && !SHA256_PATTERN.test(e.sourceSha256)) return "bad_entry";
    if (!SHA256_PATTERN.test(e.sha256) || e.size < 1 || e.size > 2 * 1024 * 1024) return "bad_entry";
    if (e.width < 1 || e.width > 1024 || e.height < 1 || e.height > 1024) return "bad_entry";
    if (e.licensed !== null && !text(e.licensed.holder)) return "bad_entry";
    bytes.set(e.pack, (bytes.get(e.pack) ?? 0) + e.size);
  }
  for (const total of bytes.values()) if (total > MAX_PACK_BYTES) return "pack_too_large";
  return null;
}

/** The next index (or config) version: always above the previous one, and never below the current time in seconds. */
export function nextVersion(previous: number, now: number): number {
  return Math.max(previous + 1, Math.floor(now / 1000));
}

/** The CDN file name of an object: `<sha256>.png` or `<sha256>.jpg` (PackEntry.CacheFileName in C#). */
export function objectName(sha256: string, format: "png" | "jpeg"): string {
  return `${sha256}.${format === "png" ? "png" : "jpg"}`;
}
