// The sample behind tests/fixtures/packs/published-index.json: two published packs (one flagged as Behaviour art and
// as the default pack, one as the fallback pack), PNG and JPEG entries, an entry with its source hash (S5b-3), a
// licensed entry and a withdrawn licensed entry. Shared by
// scripts/make-published-index-fixture.mjs (which writes the fixture) and test/packs.test.ts (which publishes the
// same sample through the admin API and checks the service produces this payload).

import { sha256Hex } from "../src/lib/crypto.ts";
import type { PublishedEntry, PublishedPack } from "../src/packs/model.ts";
import { jpeg, png } from "./images.ts";

export interface SampleFile {
  bytes: Uint8Array;
  sha256: string;
  format: "png" | "jpeg";
  width: number;
  height: number;
}

export interface SampleEntry {
  id: string;
  pack: string;
  key: string;
  file: SampleFile;
  licensedHolder: string | null;
  withdrawn: boolean;
  /** The entry records the upload's source hash (the file was already clean, so it equals the file's own hash). */
  recordSource: boolean;
}

async function file(bytes: Uint8Array, format: "png" | "jpeg", width: number, height: number): Promise<SampleFile> {
  return { bytes, sha256: await sha256Hex(bytes), format, width, height };
}

export const SAMPLE_PACKS: readonly PublishedPack[] = [
  {
    id: "original",
    name: "Original",
    author: "Behaviour Interactive",
    licenceNote: "Unofficial fan content. Dead by Daylight artwork © Behaviour Interactive Inc.",
    credit: null,
    fallback: false,
    behaviourArt: true,
    default: true,
    version: 1,
  },
  {
    id: "backup",
    name: "Backup tiles",
    author: "DBD Presence",
    licenceNote: "Made for DBD Presence.",
    credit: "Line art by the owner",
    fallback: true,
    behaviourArt: false,
    default: false,
    version: 1,
  },
];

export async function sampleEntries(): Promise<SampleEntry[]> {
  const killer = await file(png({ width: 1024, height: 1024, idatBytes: 900 }), "png", 1024, 1024);
  const survivor = await file(png({ width: 1024, height: 1024, idatBytes: 700 }), "png", 1024, 1024);
  const trapper = await file(jpeg({ width: 1024, height: 1024, entropyBytes: 1500 }), "jpeg", 1024, 1024);
  const licensed = await file(jpeg({ width: 1024, height: 1024, entropyBytes: 1200, progressive: true }), "jpeg", 1024, 1024);
  const withdrawn = await file(png({ width: 512, height: 512, idatBytes: 400 }), "png", 512, 512);
  const map = await file(png({ width: 1024, height: 576, idatBytes: 300 }), "png", 1024, 576);
  return [
    { id: "e000000000000000000000001", pack: "original", key: "role:killer", file: killer, licensedHolder: null, withdrawn: false, recordSource: false },
    { id: "e000000000000000000000002", pack: "original", key: "role:survivor", file: survivor, licensedHolder: null, withdrawn: false, recordSource: false },
    { id: "e000000000000000000000003", pack: "original", key: "character:the-trapper", file: trapper, licensedHolder: null, withdrawn: false, recordSource: true },
    { id: "e000000000000000000000004", pack: "original", key: "character:the-shape", file: licensed, licensedHolder: "Example Rights Holder", withdrawn: false, recordSource: false },
    { id: "e000000000000000000000005", pack: "original", key: "character:ghostface", file: withdrawn, licensedHolder: "Another Rights Holder", withdrawn: true, recordSource: false },
    { id: "e000000000000000000000006", pack: "backup", key: "map:generic", file: map, licensedHolder: null, withdrawn: false, recordSource: false },
    { id: "e000000000000000000000007", pack: "backup", key: "role:killer", file: killer, licensedHolder: null, withdrawn: false, recordSource: false },
  ];
}

export function asPublished(entries: readonly SampleEntry[]): PublishedEntry[] {
  return entries.map((e) => ({
    id: e.id,
    pack: e.pack,
    kind: e.key.slice(0, e.key.indexOf(":")) as PublishedEntry["kind"],
    key: e.key,
    sha256: e.file.sha256,
    format: e.file.format,
    size: e.file.bytes.length,
    width: e.file.width,
    height: e.file.height,
    licensedHolder: e.licensedHolder,
    withdrawn: e.withdrawn,
    sourceSha256: e.recordSource ? e.file.sha256 : null,
  }));
}
