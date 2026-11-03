// FR-ADM-08 (create, edit, publish and unpublish packs; Publish signs the next index version), FR-ONL-07 (the signed
// pack index: purpose pack-index, channel stable, increasing version, 30-day lifetime, 60 s cache, public), FR-PACK-01
// (the C# PackIndex format), FR-PACK-04 (withdrawn licensed items are left out), FR-PACK-05 (takedown: files deleted
// when nothing refers to them), FR-PACK-08 (third-party packs need a permission record), FR-ADM-05 (audit, no free
// text), FR-ADM-02/03 (admin auth on every new route).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, inject, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { indexPayload, objectName } from "../src/packs/model.ts";
import { verifyDocument } from "../src/signing/document.ts";
import { ADMIN, API, Harness, envelopeOf, resetDatabase, rowCount, trustedKeys } from "./helpers.ts";
import { chunk, concat, jpeg, png } from "./images.ts";
import { sha256Hex } from "../src/lib/crypto.ts";
import { SAMPLE_PACKS, asPublished, sampleEntries } from "./published-index-sample.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

type Json = Record<string, unknown>;
interface EntryView {
  id: string;
  key: string;
  sha256: string;
  withdrawn: boolean;
  live: boolean;
  licensedHolder: string | null;
  url: string;
}
interface PackView {
  id: string;
  status: string;
  version: number;
  changedSincePublish: boolean;
  entries: EntryView[];
}

async function body<T = Json>(response: Response): Promise<T> {
  return (await response.json()) as T;
}

async function createPack(id: string, extra: Json = {}): Promise<PackView> {
  const response = await h.admin("POST", "/api/packs", { id, name: `Pack ${id}`, author: "The owner", licenceNote: "Made for DBD Presence.", ...extra });
  expect(response.status).toBe(200);
  return (await body<{ pack: PackView }>(response)).pack;
}

async function uploadFile(bytes: Uint8Array, type = "image/png"): Promise<{ sha256: string; sourceSha256: string; url: string; format: string; width: number; height: number; size: number }> {
  const response = await h.upload(bytes, type);
  expect(response.status).toBe(200);
  return (await body<{ file: { sha256: string; sourceSha256: string; url: string; format: string; width: number; height: number; size: number } }>(response)).file;
}

async function addEntry(pack: string, key: string, bytes: Uint8Array, licensedHolder: string | null = null, recordSource = false): Promise<PackView> {
  const file = await uploadFile(bytes, bytes[0] === 0x89 ? "image/png" : "image/jpeg");
  const extra = recordSource ? { sourceSha256: file.sourceSha256 } : {};
  const response = await h.admin("POST", `/api/packs/${pack}/entries`, { key, sha256: file.sha256, licensedHolder, ...extra });
  expect(response.status).toBe(200);
  return (await body<{ pack: PackView }>(response)).pack;
}

async function fetchIndex(deps = h.deps): Promise<{ response: Response; document: string }> {
  const response = await handleRequest(new Request(`${API}/v1/packs?channel=stable`), env, deps);
  const text = await response.clone().text();
  return { response, document: (JSON.parse(text) as { document: string }).document };
}

async function verifiedPayload(document: string): Promise<{ packs: Json[]; entries: Json[] }> {
  const result = await verifyDocument(document, "pack-index", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
  expect(result.outcome).toBe("Valid");
  return JSON.parse(result.envelope!.payloadJson) as { packs: Json[]; entries: Json[] };
}

describe("GET /v1/packs", () => {
  it("is public and serves an empty signed index (version 0) before anything is published", async () => {
    const { response, document } = await fetchIndex();
    expect(response.status).toBe(200);
    expect(response.headers.get("Cache-Control")).toBe("public, max-age=300");
    expect(response.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
    expect(response.headers.has("Set-Cookie")).toBe(false);
    expect(await verifiedPayload(document)).toEqual({ schemaVersion: 1, packs: [], entries: [] });
    expect(envelopeOf(document)).toMatchObject({ purpose: "pack-index", channel: "stable", version: 0 });
  });

  it("signs with a 30-day lifetime and refuses another channel", async () => {
    const { document } = await fetchIndex();
    const envelope = envelopeOf(document);
    expect(Date.parse(envelope["expiresAt"] as string) - Date.parse(envelope["issuedAt"] as string)).toBe(30 * 86_400_000);
    expect((await h.call("GET", `${API}/v1/packs?channel=beta`)).status).toBe(400);
    expect((await h.call("GET", `${API}/v1/packs`)).status).toBe(200);
    expect((await h.call("POST", `${API}/v1/packs`)).status).toBe(405);
  });

  it("is cached for 60 seconds: no D1 read or signature per request, then re-signed with the same version", async () => {
    await createPack("p1");
    await addEntry("p1", "role:killer", png());
    expect((await h.admin("POST", "/api/packs/p1/publish", {})).status).toBe(200);
    let statements = 0;
    const db = new Proxy(env.DB, {
      get(target, property) {
        if (property === "prepare") return (q: string) => (statements++, target.prepare(q));
        const value = Reflect.get(target, property, target) as unknown;
        return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
      },
    });
    const counted = { ...env, DB: db };
    const first = ((await (await handleRequest(new Request(`${API}/v1/packs`), counted, h.deps)).json()) as { document: string }).document;
    const reads = statements;
    expect(reads).toBe(1);
    h.now += 30_000;
    const again = ((await (await handleRequest(new Request(`${API}/v1/packs`), counted, h.deps)).json()) as { document: string }).document;
    expect(again).toBe(first);
    expect(statements).toBe(reads);
    h.now += 31_000;
    const later = ((await (await handleRequest(new Request(`${API}/v1/packs`), counted, h.deps)).json()) as { document: string }).document;
    expect(later).not.toBe(first);
    expect(envelopeOf(later)["version"]).toBe(envelopeOf(first)["version"]);
  });

  it("answers 503 not_configured without the content signing key", async () => {
    const response = await handleRequest(new Request(`${API}/v1/packs`), { ...env, CONTENT_SIGNING_KEY: undefined }, h.deps);
    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "not_configured" });
  });
});

describe("publishing", () => {
  it("publish signs an index that verifies with the shared verifier and matches the C# format (the committed fixture)", async () => {
    const sample = await sampleEntries();
    for (const pack of SAMPLE_PACKS) {
      await createPack(pack.id, { name: pack.name, author: pack.author, licenceNote: pack.licenceNote, credit: pack.credit, fallback: pack.fallback, behaviourArt: pack.behaviourArt });
      if (pack.default) expect((await h.admin("PUT", `/api/packs/${pack.id}/default`, { default: true })).status).toBe(200);
    }
    const ids = new Map<string, string>();
    for (const entry of sample) {
      const view = await addEntry(entry.pack, entry.key, entry.file.bytes, entry.licensedHolder, entry.recordSource);
      ids.set(view.entries.find((e) => e.key === entry.key)!.id, entry.id);
    }
    const withdrawn = sample.find((e) => e.withdrawn)!;
    const withdrawnId = [...ids].find(([, fixtureId]) => fixtureId === withdrawn.id)![0];
    expect((await h.admin("POST", `/api/packs/${withdrawn.pack}/entries/${withdrawnId}/withdraw`, { withdrawn: true })).status).toBe(200);
    for (const pack of SAMPLE_PACKS) expect((await h.admin("POST", `/api/packs/${pack.id}/publish`, {})).status).toBe(200);

    const payload = await verifiedPayload((await fetchIndex()).document);
    // The service's entry ids are random; map them to the sample's to compare with the fixture.
    const normalised = { ...payload, entries: payload.entries.map((e): Json => ({ ...e, id: ids.get(e["id"] as string) })) };
    const fixture = JSON.parse(inject("publishedIndexFixture")) as { expectedPayload: unknown; documentText: string; now: string; withdrawnEntryIds: string[] };
    expect(normalised).toEqual(fixture.expectedPayload);
    expect(normalised).toEqual(indexPayload(SAMPLE_PACKS, asPublished(sample)));
    // The withdrawn licensed item is absent; the other licensed item names its holder.
    expect(payload.entries.map((e) => ids.get(e["id"] as string))).not.toContain(withdrawn.id);
    expect(payload.entries.find((e) => e["key"] === "character:the-shape")).toMatchObject({ licensed: { holder: "Example Rights Holder" } });
    expect(payload.packs.find((p) => p["id"] === "backup")).toMatchObject({ fallback: true, behaviourArt: false });
    expect(payload.packs.find((p) => p["id"] === "original")).toMatchObject({ fallback: false, behaviourArt: true, default: true });
    expect(payload.entries.find((e) => e["key"] === "character:the-trapper")).toMatchObject({ sourceSha256: sample.find((e) => e.recordSource)!.file.sha256 });

    // The committed fixture itself is a valid TypeScript-signed document.
    const result = await verifyDocument(fixture.documentText, "pack-index", "stable", { keys: await trustedKeys(), now: new Date(fixture.now) });
    expect(result.outcome).toBe("Valid");
    expect(JSON.parse(result.envelope!.payloadJson)).toEqual(fixture.expectedPayload);
    expect(fixture.withdrawnEntryIds).toEqual([withdrawn.id]);
  });

  it("every publish, unpublish and withdrawal raises the version; drafts are not live until Publish", async () => {
    await createPack("p1");
    await addEntry("p1", "role:killer", png());
    const versions: number[] = [];
    const current = async () => Number(envelopeOf((await fetchIndex()).document)["version"]);
    const step = async (method: string, path: string, payload: Json = {}) => {
      expect((await h.admin(method, path, payload)).status).toBe(200);
      versions.push(await current());
      h.now += 1000;
    };
    await step("POST", "/api/packs/p1/publish");
    expect(versions[0]).toBe(Math.floor((h.now - 1000) / 1000));

    // A draft edit is not live, and marks the pack as changed.
    let pack = await addEntry("p1", "character:the-trapper", jpeg(), "Some Holder");
    expect(pack.changedSincePublish).toBe(true);
    expect((await verifiedPayload((await fetchIndex()).document)).entries).toHaveLength(1);
    expect(pack.entries.find((e) => e.key === "character:the-trapper")!.live).toBe(false);

    await step("POST", "/api/packs/p1/publish");
    pack = (await body<{ packs: PackView[] }>(await h.admin("GET", "/api/packs"))).packs[0]!;
    expect(pack.version).toBe(2);
    expect(pack.changedSincePublish).toBe(false);
    expect((await verifiedPayload((await fetchIndex()).document)).entries).toHaveLength(2);
    const licensed = pack.entries.find((e) => e.key === "character:the-trapper")!;
    await step("POST", `/api/packs/p1/entries/${licensed.id}/withdraw`, { withdrawn: true });
    expect((await verifiedPayload((await fetchIndex()).document)).entries.map((e) => e["key"])).toEqual(["role:killer"]);
    await step("POST", `/api/packs/p1/entries/${licensed.id}/withdraw`, { withdrawn: false });
    await step("POST", "/api/packs/p1/unpublish");
    expect(await verifiedPayload((await fetchIndex()).document)).toEqual({ schemaVersion: 1, packs: [], entries: [] });
    for (let i = 1; i < versions.length; i++) expect(versions[i]!).toBeGreaterThan(versions[i - 1]!);
  });

  it("a newer index is accepted by the client's rollback rule and an older one is not", async () => {
    await createPack("p1");
    await addEntry("p1", "role:killer", png());
    await h.admin("POST", "/api/packs/p1/publish", {});
    const older = (await fetchIndex()).document;
    h.now += 5000;
    await h.admin("POST", "/api/packs/p1/unpublish", {});
    const newer = (await fetchIndex()).document;
    const keys = await trustedKeys();
    const stored = Number(envelopeOf(newer)["version"]);
    const floor = { keys, now: new Date(h.now), highestVersion: () => BigInt(stored) };
    expect((await verifyDocument(newer, "pack-index", "stable", floor)).outcome).toBe("Valid");
    expect((await verifyDocument(older, "pack-index", "stable", floor)).outcome).toBe("RolledBack");
  });

  it("refuses to publish an empty pack, a third-party pack without permission and credit, and a second fallback pack", async () => {
    await createPack("empty");
    expect((await h.admin("POST", "/api/packs/empty/publish", {})).status).toBe(409);
    await createPack("nl", { thirdParty: true });
    await addEntry("nl", "role:killer", png());
    const refused = await h.admin("POST", "/api/packs/nl/publish", {});
    expect(refused.status).toBe(409);
    expect(await refused.json()).toMatchObject({ error: "permission_required" });
    expect((await h.admin("PUT", "/api/packs/nl", { permission: { creator: "A Creator", date: "2026-10-20", proof: "Email of 20 October, kept offline" }, credit: "Pack by A Creator, with permission" })).status).toBe(200);
    expect((await h.admin("POST", "/api/packs/nl/publish", {})).status).toBe(200);
    // A permission date in the future, or not a real date, is refused.
    expect((await h.admin("PUT", "/api/packs/nl", { permission: { creator: "A", date: "2026-12-01", proof: "x" } })).status).toBe(400);
    expect((await h.admin("PUT", "/api/packs/nl", { permission: { creator: "A", date: "2026-02-30", proof: "x" } })).status).toBe(400);

    await createPack("fb1", { fallback: true });
    const second = await h.admin("POST", "/api/packs", { id: "fb2", name: "B", author: "A", licenceNote: "N", fallback: true });
    expect(second.status).toBe(409);
    expect(await second.json()).toMatchObject({ error: "fallback_exists" });
    // The permission record never reaches the signed index.
    const text = JSON.stringify(await verifiedPayload((await fetchIndex()).document));
    expect(text).not.toContain("A Creator\"");
    expect(text).not.toContain("Email of 20 October");
  });

  it("validates input: ids, keys, text lengths, control characters and unknown members", async () => {
    const bad = [
      { id: "Bad Id", name: "n", author: "a", licenceNote: "l" },
      { id: "ok", name: "", author: "a", licenceNote: "l" },
      { id: "ok", name: "x".repeat(81), author: "a", licenceNote: "l" },
      { id: "ok", name: "n\u0000", author: "a", licenceNote: "l" },
      { id: "ok", name: "n", author: "a", licenceNote: "l", enable: true },
      { id: "ok", name: "n", author: "a" },
    ];
    for (const payload of bad) expect((await h.admin("POST", "/api/packs", payload)).status).toBe(400);
    await createPack("ok");
    expect((await h.admin("POST", "/api/packs", { id: "ok", name: "n", author: "a", licenceNote: "l" })).status).toBe(409);
    const file = await uploadFile(png());
    for (const key of ["killer", "role:", "role:Killer", "weapon:x", "role:a b"]) {
      expect((await h.admin("POST", "/api/packs/ok/entries", { key, sha256: file.sha256 })).status).toBe(400);
    }
    expect((await h.admin("POST", "/api/packs/ok/entries", { key: "role:killer", sha256: "0".repeat(64) })).status).toBe(409);
    expect((await h.admin("POST", "/api/packs/ok/entries", { key: "role:killer", sha256: file.sha256 })).status).toBe(200);
    expect((await h.admin("POST", "/api/packs/ok/entries", { key: "role:killer", sha256: file.sha256 })).status).toBe(409);
    expect((await h.admin("POST", "/api/packs/missing/entries", { key: "role:killer", sha256: file.sha256 })).status).toBe(404);
  });

  it("only a licensed entry can be withdrawn, and a withdrawn entry stays licensed until restored", async () => {
    await createPack("p1");
    const pack = await addEntry("p1", "role:killer", png());
    const entry = pack.entries[0]!;
    const notLicensed = await h.admin("POST", `/api/packs/p1/entries/${entry.id}/withdraw`, { withdrawn: true });
    expect(notLicensed.status).toBe(409);
    expect(await notLicensed.json()).toMatchObject({ error: "not_licensed" });
    await h.admin("PUT", `/api/packs/p1/entries/${entry.id}`, { licensedHolder: "Holder" });
    expect((await h.admin("POST", `/api/packs/p1/entries/${entry.id}/withdraw`, { withdrawn: true })).status).toBe(200);
    const clear = await h.admin("PUT", `/api/packs/p1/entries/${entry.id}`, { licensedHolder: null });
    expect(clear.status).toBe(409);
    expect(await clear.json()).toMatchObject({ error: "entry_withdrawn" });
  });
});

describe("files and takedown", () => {
  it("stores an accepted image under its SHA-256 and returns its CDN address; the same bytes are one file", async () => {
    const bytes = png({ width: 800, height: 600 });
    const file = await uploadFile(bytes);
    expect(file).toMatchObject({ format: "png", width: 800, height: 600, size: bytes.length });
    expect(file.url).toBe(`https://cdn.arcavale.com/packs/${file.sha256}.png`);
    const stored = await env.PACKS.get(`${file.sha256}.png`);
    expect(new Uint8Array(await stored!.arrayBuffer())).toEqual(bytes);
    await uploadFile(bytes);
    expect(await rowCount("pack_objects")).toBe(1);
    const jpg = await uploadFile(jpeg(), "image/jpeg");
    expect(jpg.url).toMatch(/\.jpg$/);
  });

  it("removes metadata before storing, and stores and names the cleaned file", async () => {
    const clean = png();
    const dirty = concat(clean.slice(0, 33), chunk("tEXt", new TextEncoder().encode("Comment\0hello")), chunk("pHYs", new Uint8Array(9)), clean.slice(33));
    const response = await h.upload(dirty, "image/png");
    expect(response.status).toBe(200);
    const result = (await response.json()) as { file: { sha256: string; size: number }; removed: string[] };
    expect(result.removed).toEqual(["tEXt", "pHYs"]);
    expect(result.file.size).toBe(clean.length);
    expect(result.file.sha256).toBe(await sha256Hex(clean));
    expect(new Uint8Array(await (await env.PACKS.get(`${result.file.sha256}.png`))!.arrayBuffer())).toEqual(clean);
    const withExif = Uint8Array.from([...jpeg().slice(0, 2), 0xff, 0xe1, 0x00, 0x08, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, ...jpeg().slice(2)]);
    const jpg = (await (await h.upload(withExif, "image/jpeg")).json()) as { removed: string[]; file: { size: number } };
    expect(jpg.removed).toEqual(["Exif"]);
    expect(jpg.file.size).toBe(jpeg().length);
  });

  it("refuses files the app could not use, with the rule's name, and stores nothing", async () => {
    const cases: [Uint8Array, string, string][] = [
      [png({ colour: 3, middle: [chunk("PLTE", new Uint8Array(30)), chunk("IDAT", new Uint8Array(20))] }), "image/png", "png_not_rgb8"],
      [png({ depth: 16 }), "image/png", "png_not_rgb8"],
      [png({ middle: [chunk("acTL", new Uint8Array(8)), chunk("IDAT", new Uint8Array(20))] }), "image/png", "animation_not_allowed"],
      [png({ width: 2048, height: 10 }), "image/png", "dimensions_out_of_range"],
      [Uint8Array.from([...png(), 0]), "image/png", "trailing_data"],
      [png(), "image/jpeg", "type_mismatch"],
      [new TextEncoder().encode("<svg/>"), "image/png", "unknown_format"],
    ];
    for (const [bytes, type, reason] of cases) {
      const response = await h.upload(bytes, type);
      expect(response.status).toBe(422);
      expect(await response.json()).toEqual({ schemaVersion: 1, error: "image_rejected", reason });
    }
    expect((await h.upload(png(), "image/svg+xml")).status).toBe(415);
    expect((await h.upload(new Uint8Array(2 * 1024 * 1024 + 1), "image/png")).status).toBe(413);
    expect(await rowCount("pack_objects")).toBe(0);
    expect((await env.PACKS.list()).objects).toHaveLength(0);
  });

  it("deletes a file when nothing refers to it any more (reference counting), and keeps shared files", async () => {
    await createPack("a");
    await createPack("b");
    const shared = png({ idatBytes: 333 });
    const own = png({ idatBytes: 444 });
    let a = await addEntry("a", "role:killer", shared);
    await addEntry("b", "role:killer", shared);
    a = await addEntry("a", "role:survivor", own);
    const sharedSha = a.entries.find((e) => e.key === "role:killer")!.sha256;
    const ownSha = a.entries.find((e) => e.key === "role:survivor")!.sha256;

    // Removing a's entries: the own file goes, the shared one stays (b still uses it).
    for (const e of a.entries) expect((await h.admin("DELETE", `/api/packs/a/entries/${e.id}`)).status).toBe(200);
    expect(await env.PACKS.head(objectName(ownSha, "png"))).toBeNull();
    expect(await env.PACKS.head(objectName(sharedSha, "png"))).not.toBeNull();

    // A published copy keeps its files alive until it is unpublished and the draft is deleted (the takedown).
    await h.admin("POST", "/api/packs/b/publish", {});
    const b = (await body<{ packs: PackView[] }>(await h.admin("GET", "/api/packs"))).packs.find((p) => p.id === "b")!;
    expect((await h.admin("DELETE", `/api/packs/b/entries/${b.entries[0]!.id}`)).status).toBe(200);
    expect(await env.PACKS.head(objectName(sharedSha, "png"))).not.toBeNull();
    const published = await h.admin("DELETE", "/api/packs/b", { confirm: "b" });
    expect(published.status).toBe(409);
    expect(await published.json()).toMatchObject({ error: "pack_published" });
    expect((await h.admin("POST", "/api/packs/b/unpublish", {})).status).toBe(200);
    expect(await env.PACKS.head(objectName(sharedSha, "png"))).toBeNull();
    expect((await h.admin("DELETE", "/api/packs/b", { confirm: "wrong" })).status).toBe(400);
    expect((await h.admin("DELETE", "/api/packs/b", { confirm: "b" })).status).toBe(200);
    expect(await rowCount("packs", "id = 'b'")).toBe(0);
  });

  it("removing a withdrawn licensed entry takes its file down at once, without republishing the pack", async () => {
    await createPack("p1");
    await addEntry("p1", "role:killer", png());
    let pack = await addEntry("p1", "character:ghostface", png({ idatBytes: 99 }), "Holder");
    await h.admin("POST", "/api/packs/p1/publish", {});
    const licensed = pack.entries.find((e) => e.key === "character:ghostface")!;
    await h.admin("POST", `/api/packs/p1/entries/${licensed.id}/withdraw`, { withdrawn: true });
    const removed = await h.admin("DELETE", `/api/packs/p1/entries/${licensed.id}`);
    expect(await removed.json()).toMatchObject({ filesDeleted: 1 });
    expect(await env.PACKS.head(objectName(licensed.sha256, "png"))).toBeNull();
    pack = (await body<{ packs: PackView[] }>(await h.admin("GET", "/api/packs"))).packs[0]!;
    expect(pack.status).toBe("published");
    expect((await verifiedPayload((await fetchIndex()).document)).entries.map((e) => e["key"])).toEqual(["role:killer"]);
  });

  it("replacing an entry's image releases the old file", async () => {
    await createPack("p1");
    const pack = await addEntry("p1", "role:killer", png({ idatBytes: 10 }));
    const replacement = await uploadFile(png({ idatBytes: 20 }));
    expect((await h.admin("PUT", `/api/packs/p1/entries/${pack.entries[0]!.id}`, { sha256: replacement.sha256 })).status).toBe(200);
    expect(await env.PACKS.head(objectName(pack.entries[0]!.sha256, "png"))).toBeNull();
    expect(await env.PACKS.head(objectName(replacement.sha256, "png"))).not.toBeNull();
  });

  it("the owner's purge removes unused uploads after an hour and R2 objects with no record", async () => {
    const unused = await uploadFile(png({ idatBytes: 55 }));
    await env.PACKS.put(`${"a".repeat(64)}.png`, new Uint8Array([1]));
    const early = await h.admin("POST", "/api/pack-files/purge", {});
    expect(await early.json()).toMatchObject({ purged: { unreferenced: 0 } });
    expect(await env.PACKS.head(objectName(unused.sha256, "png"))).not.toBeNull();
    h.now += 61 * 60_000;
    const later = await h.admin("POST", "/api/pack-files/purge", {});
    expect(await later.json()).toMatchObject({ purged: { unreferenced: 1 } });
    expect(await env.PACKS.head(objectName(unused.sha256, "png"))).toBeNull();
    expect(await env.PACKS.head(`${"a".repeat(64)}.png`)).toBeNull();
  });
});

describe("audit", () => {
  it("records every pack action with ids, keys and flags but no free text", async () => {
    await createPack("p1", { name: "Secret Name", author: "Jane Doe", licenceNote: "Private note", credit: "Credit Person", thirdParty: true, permission: { creator: "Creator Person", date: "2026-10-01", proof: "Proof text" } });
    let pack = await addEntry("p1", "role:killer", png(), "Holder Company");
    await h.admin("PUT", "/api/packs/p1", { name: "Another Secret" });
    await h.admin("POST", "/api/packs/p1/publish", {});
    await h.admin("POST", `/api/packs/p1/entries/${pack.entries[0]!.id}/withdraw`, { withdrawn: true });
    await h.admin("POST", "/api/packs/p1/unpublish", {});
    pack = (await body<{ packs: PackView[] }>(await h.admin("GET", "/api/packs"))).packs[0]!;
    await h.admin("DELETE", `/api/packs/p1/entries/${pack.entries[0]!.id}`);
    await h.admin("DELETE", "/api/packs/p1", { confirm: "p1" });
    await h.admin("POST", "/api/pack-files/purge", {});
    const rows = (await env.DB.prepare("SELECT action, target, before_json, after_json FROM audit_log ORDER BY seq").all<Record<string, string>>()).results;
    expect(rows.map((r) => r["action"])).toEqual([
      "pack.create",
      "pack.file_upload",
      "pack.entry_add",
      "pack.update",
      "pack.publish",
      "pack.entry_withdraw",
      "pack.unpublish",
      "pack.entry_remove",
      "pack.delete",
      "pack.purge",
    ]);
    const dump = JSON.stringify(rows);
    for (const secret of ["Secret Name", "Another Secret", "Jane Doe", "Private note", "Credit Person", "Creator Person", "Proof text", "Holder Company"]) expect(dump).not.toContain(secret);
    expect(dump).toContain("role:killer");
    expect(JSON.parse(rows.find((r) => r["action"] === "pack.update")!["after_json"]!)).toMatchObject({ fields: ["name"] });
    const chain = await body<{ chain: { ok: boolean } }>(await h.admin("GET", "/api/audit/verify"));
    expect(chain.chain.ok).toBe(true);
  });
});

describe("admin authentication on the pack routes", () => {
  const routes: [string, string, unknown][] = [
    ["GET", "/api/packs", undefined],
    ["POST", "/api/packs", { id: "x", name: "n", author: "a", licenceNote: "l" }],
    ["PUT", "/api/packs/x", { name: "n" }],
    ["DELETE", "/api/packs/x", { confirm: "x" }],
    ["POST", "/api/packs/x/publish", {}],
    ["POST", "/api/packs/x/unpublish", {}],
    ["POST", "/api/packs/x/entries", { key: "role:killer", sha256: "0".repeat(64) }],
    ["PUT", `/api/packs/x/entries/e${"0".repeat(24)}`, {}],
    ["DELETE", `/api/packs/x/entries/e${"0".repeat(24)}`, undefined],
    ["POST", `/api/packs/x/entries/e${"0".repeat(24)}/withdraw`, { withdrawn: true }],
    ["POST", "/api/pack-files/purge", {}],
    ["GET", "/api/apps", undefined],
    ["PUT", "/api/apps", { applications: [], defaultApplication: "x" }],
  ];

  it.each(routes)("%s %s needs a valid Access JWT (401), an owner (403) and the admin header (403)", async (method, path, payload) => {
    expect((await h.admin(method, path, payload, { "Cf-Access-Jwt-Assertion": null })).status).toBe(401);
    expect((await h.admin(method, path, payload, { "Cf-Access-Jwt-Assertion": await h.accessJwt({ email: "someone@example.com" }) })).status).toBe(403);
    expect((await h.admin(method, path, payload, { "X-DBDP-Admin": null })).status).toBe(403);
    if (method !== "GET") {
      expect((await h.admin(method, path, payload, { Origin: "https://evil.example" })).status).toBe(403);
      expect((await h.admin(method, path, payload, { "Sec-Fetch-Site": "cross-site" })).status).toBe(403);
    }
  });

  it("the upload route needs the same protections", async () => {
    expect((await h.upload(png(), "image/png", { "Cf-Access-Jwt-Assertion": null })).status).toBe(401);
    expect((await h.upload(png(), "image/png", { "X-DBDP-Admin": null })).status).toBe(403);
    expect((await h.upload(png(), "image/png", { Origin: "https://evil.example" })).status).toBe(403);
    expect((await h.upload(png(), "image/png", { "Sec-Fetch-Site": "same-site" })).status).toBe(403);
    expect(await rowCount("pack_objects")).toBe(0);
  });

  it("is not reachable on the public API host", async () => {
    expect((await h.call("GET", `${API}/api/packs`)).status).toBe(404);
    expect((await h.call("GET", `${ADMIN}/v1/packs`)).status).toBe(404);
  });
});
