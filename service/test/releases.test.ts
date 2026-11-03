// V2-S6 signed updates on the service (FR-ONL-10, FR-ADM-10, NFR-SVC-05, NFR-SVC-11, D18; QUALITY_GATES V6.1, V6.3):
// a feed is accepted only when the owner's offline release key signed it for that channel, it is not expired, not rolled back,
// not older than the published one and its payload follows the app's rules; packages are accepted only with the exact size
// and SHA-256 a staged or published feed lists, under the storage cap, and only the packages the feeds list are kept (the last
// 3 per channel); key replacements are accepted only from the recovery key; and the Worker holds neither private key.

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, inject, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { decodeBase64Url } from "../src/lib/base64url.ts";
import { sha256Hex } from "../src/lib/crypto.ts";
import { parseReleaseFeed, packageFileName } from "../src/releases/feed.ts";
import { sweepReleases } from "../src/routes/releases.ts";
import { importSigningKey, signDocument, type Purpose } from "../src/signing/document.ts";
import { PRODUCTION_STORAGE_LIMITS, storageUsage } from "../src/storage.ts";
import { ADMIN, API, CDN, Harness, resetDatabase, rowCount } from "./helpers.ts";

interface OfflineKey {
  kid: string;
  purposes: string[];
  spki: string;
  pkcs8: string;
}

const [RELEASE_KEY, RECOVERY_KEY] = JSON.parse(inject("offlineSigningKeys")) as [OfflineKey, OfflineKey];
const DAY = 24 * 60 * 60 * 1000;

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

interface Package {
  version: string;
  bytes: Uint8Array;
  sha256: string;
  fileName: string;
}

async function makePackage(version: string, channel = "stable", size = 4096): Promise<Package> {
  const bytes = crypto.getRandomValues(new Uint8Array(size));
  return { version, bytes, sha256: await sha256Hex(bytes), fileName: packageFileName(version, channel) };
}

function payload(packages: Package[], extra: Record<string, unknown> = {}): unknown {
  return {
    schemaVersion: 1,
    packageId: "DbdPresence",
    releases: packages.map((p) => ({ version: p.version, fileName: p.fileName, size: p.bytes.length, sha256: p.sha256 })),
    ...extra,
  };
}

async function sign(purpose: Purpose, channel: string, version: number, body: unknown, key: OfflineKey = purpose === "releases" ? RELEASE_KEY : RECOVERY_KEY, issuedAt = new Date(h.now)): Promise<string> {
  const cryptoKey = await importSigningKey(decodeBase64Url(key.pkcs8)!);
  return signDocument(purpose, channel, version, body, cryptoKey, key.kid, { issuedAt });
}

async function stage(document: string, channel = "stable"): Promise<Response> {
  return h.admin("POST", "/api/releases/feeds", { channel, document });
}

async function putPackage(sha: string, bytes: Uint8Array, length = bytes.length): Promise<Response> {
  const headers = { ...(await h.adminHeaders()), "Content-Type": "application/octet-stream", "Content-Length": String(length), "CF-Connecting-IP": h.ip };
  return handleRequest(new Request(`${ADMIN}/api/releases/packages/${sha}`, { method: "PUT", headers, body: bytes }), env, h.deps);
}

async function publish(id: string): Promise<Response> {
  return h.admin("POST", `/api/releases/feeds/${id}/publish`, {});
}

async function body<T = Record<string, unknown>>(response: Response): Promise<T> {
  return (await response.json()) as T;
}

/** Stages, uploads every package and publishes; returns the feed id. */
async function release(packages: Package[], version: number, channel = "stable"): Promise<string> {
  const staged = await stage(await sign("releases", channel, version, payload(packages)), channel);
  expect(staged.status).toBe(200);
  const id = (await body<{ feed: { id: string } }>(staged)).feed.id;
  for (const p of packages) expect([200]).toContain((await putPackage(p.sha256, p.bytes)).status);
  const published = await publish(id);
  expect(published.status).toBe(200);
  return id;
}

describe("public release feed", () => {
  it("answers 404 no_release before anything is published, then serves the signed text exactly as uploaded", async () => {
    const before = await h.call("GET", `${API}/v1/releases/stable`);
    expect(before.status).toBe(404);
    expect((await body(before))["error"]).toBe("no_release");

    const p = await makePackage("1.1.0");
    const document = await sign("releases", "stable", 10, payload([p]));
    const staged = await stage(document);
    const id = (await body<{ feed: { id: string } }>(staged)).feed.id;
    expect((await putPackage(p.sha256, p.bytes)).status).toBe(200);
    expect((await publish(id)).status).toBe(200);

    const served = await h.call("GET", `${API}/v1/releases/stable`);
    expect(served.status).toBe(200);
    expect(served.headers.get("Cache-Control")).toBe("public, max-age=60");
    expect(await body(served)).toEqual({ schemaVersion: 1, document });
    expect((await h.call("GET", `${API}/v1/releases/beta`)).status).toBe(404);
    expect((await h.call("GET", `${API}/v1/releases/nightly`)).status).toBe(404);
  });

  it("serves the package bytes on the content host only while a feed lists them", async () => {
    const p = await makePackage("1.1.0");
    await release([p], 10);

    const file = await h.call("GET", `${CDN}/releases/${p.sha256}.nupkg`);
    expect(file.status).toBe(200);
    expect(file.headers.get("Content-Type")).toBe("application/octet-stream");
    expect(file.headers.get("Cache-Control")).toContain("immutable");
    expect(new Uint8Array(await file.arrayBuffer())).toEqual(p.bytes);
    expect((await h.call("GET", `${CDN}/releases/${"0".repeat(64)}.nupkg`)).status).toBe(404);
    expect((await h.call("POST", `${CDN}/releases/${p.sha256}.nupkg`)).status).toBe(405);
    expect((await h.call("GET", `${CDN}/releases/${p.sha256}.zip`)).status).toBe(404);
  });
});

describe("feed upload refuses what the app would refuse (V6.1)", () => {
  it("a tampered feed", async () => {
    const document = await sign("releases", "stable", 10, payload([await makePackage("1.1.0")]));
    const outer = JSON.parse(document) as { kid: string; signed: string; sig: string };
    const flipped = outer.signed.slice(0, 20) + (outer.signed[20] === "A" ? "B" : "A") + outer.signed.slice(21);
    const response = await stage(JSON.stringify({ ...outer, signed: flipped }));
    expect(response.status).toBe(422);
    expect(await body(response)).toMatchObject({ error: "feed_refused", reason: "BadSignature" });
    expect(await rowCount("release_feeds")).toBe(0);
  });

  it("an expired feed, a feed for another channel, one signed by the recovery key, and an unsigned payload", async () => {
    const p = await makePackage("1.1.0");
    const expired = await sign("releases", "stable", 10, payload([p]), RELEASE_KEY, new Date(h.now - 31 * DAY));
    expect(await body(await stage(expired))).toMatchObject({ reason: "Expired" });
    const beta = await sign("releases", "beta", 10, payload([await makePackage("1.1.0", "beta")]));
    expect(await body(await stage(beta, "stable"))).toMatchObject({ reason: "WrongChannel" });
    const byRecovery = await sign("releases", "stable", 10, payload([p]), RECOVERY_KEY);
    expect(await body(await stage(byRecovery))).toMatchObject({ reason: "KeyNotAllowedForPurpose" });
    expect(await body(await stage(JSON.stringify(payload([p]))))).toMatchObject({ reason: "Malformed" });
    expect(await rowCount("release_feeds")).toBe(0);
  });

  it("a payload that breaks the rules (package id, more than 3 packages, file name)", async () => {
    const four = await Promise.all(["1.0.0", "1.1.0", "1.2.0", "1.3.0"].map((v) => makePackage(v)));
    expect(await body(await stage(await sign("releases", "stable", 10, payload(four))))).toMatchObject({ reason: "bad_release_list" });
    expect(await body(await stage(await sign("releases", "stable", 10, { ...(payload([four[0]!]) as object), packageId: "Other" })))).toMatchObject({ reason: "wrong_package_id" });
    const misnamed = { ...four[0]!, fileName: "DbdPresence-9.9.9-stable-full.nupkg" };
    expect(await body(await stage(await sign("releases", "stable", 10, payload([misnamed]))))).toMatchObject({ reason: "bad_file_name" });
  });

  it("a downgraded feed: an older feed version, the same version again, or a lower newest release", async () => {
    await release([await makePackage("1.2.0")], 10);
    expect(await body(await stage(await sign("releases", "stable", 9, payload([await makePackage("1.3.0")]))))).toMatchObject({ reason: "RolledBack" });
    const same = await stage(await sign("releases", "stable", 10, payload([await makePackage("1.3.0")])));
    expect(same.status).toBe(409);
    expect((await body(same))["error"]).toBe("feed_version_not_newer");
    const lower = await stage(await sign("releases", "stable", 11, payload([await makePackage("1.1.0")])));
    expect(lower.status).toBe(409);
    expect((await body(lower))["error"]).toBe("release_version_lower");
  });

  it("answers 503 when the offline public keys are not configured", async () => {
    const document = await sign("releases", "stable", 10, payload([await makePackage("1.1.0")]));
    const headers = { ...(await h.adminHeaders()), "Content-Type": "application/json", "CF-Connecting-IP": h.ip };
    const request = () => new Request(`${ADMIN}/api/releases/feeds`, { method: "POST", headers, body: JSON.stringify({ channel: "stable", document }) });
    const response = await handleRequest(request(), { ...env, OFFLINE_PUBLIC_KEYS: "[]" }, h.deps);
    expect(response.status).toBe(503);
    expect((await body(response))["error"]).toBe("release_key_not_configured");
    const broken = await handleRequest(request(), { ...env, OFFLINE_PUBLIC_KEYS: '[{"kid":"x","spki":"y","purposes":["config"]}]' }, h.deps);
    expect(broken.status).toBe(503);
  });
});

describe("publishing checks the staged feed again (LOW, V2-S6 M3)", () => {
  it("refuses a staged feed that has expired by the time it is published, and keeps the published one", async () => {
    const good = await makePackage("1.0.0");
    await release([good], 5);
    const p = await makePackage("1.1.0");
    const id = (await body<{ feed: { id: string } }>(await stage(await sign("releases", "stable", 10, payload([p]))))).feed.id;
    expect((await putPackage(p.sha256, p.bytes)).status).toBe(200);
    h.now += 31 * DAY;
    const refused = await publish(id);
    expect(refused.status).toBe(422);
    expect(await body(refused)).toMatchObject({ error: "feed_refused", reason: "Expired" });
    expect(await rowCount("release_feeds", "status = 'published'")).toBe(1);
    expect(await rowCount("release_feeds", "status = 'staged'")).toBe(1);
  });

  it("refuses a staged feed whose key was revoked after it was staged", async () => {
    const p = await makePackage("1.1.0");
    const id = (await body<{ feed: { id: string } }>(await stage(await sign("releases", "stable", 10, payload([p]))))).feed.id;
    expect((await putPackage(p.sha256, p.bytes)).status).toBe(200);
    const document = await sign("key-replacement", "all", 2, { schemaVersion: 1, revoke: ["release-test-1"], add: [] });
    expect((await h.admin("POST", "/api/keys/replacement", { document })).status).toBe(200);
    const refused = await publish(id);
    expect(refused.status).toBe(422);
    expect(await body(refused)).toMatchObject({ error: "feed_refused", reason: "UnknownKey" });
    expect(await rowCount("release_feeds", "status = 'published'")).toBe(0);
  });
});

describe("the content host serves a package only while its row exists", () => {
  it("answers 404 for a package whose row is gone even though the bytes are still in the bucket", async () => {
    const p = await makePackage("1.1.0");
    await release([p], 10);
    expect((await h.call("GET", `${CDN}/releases/${p.sha256}.nupkg`)).status).toBe(200);
    await env.DB.prepare("DELETE FROM release_packages WHERE sha256 = ?").bind(p.sha256).run();
    expect(await env.RELEASES.head(`${p.sha256}.nupkg`)).not.toBeNull();
    expect((await h.call("GET", `${CDN}/releases/${p.sha256}.nupkg`)).status).toBe(404);
    expect((await h.call("HEAD", `${CDN}/releases/${p.sha256}.nupkg`)).status).toBe(404);
  });
});

describe("packages", () => {
  it("are accepted only with the exact size and SHA-256 a staged feed lists, and publishing needs all of them", async () => {
    const a = await makePackage("1.1.0");
    const b = await makePackage("1.2.0");
    const staged = await stage(await sign("releases", "stable", 10, payload([a, b])));
    const id = (await body<{ feed: { id: string; packages: { present: boolean }[] } }>(staged)).feed.id;

    const notListed = await makePackage("9.0.0");
    expect((await putPackage(notListed.sha256, notListed.bytes)).status).toBe(409);
    expect((await putPackage(a.sha256, a.bytes, a.bytes.length + 1)).status).toBe(422);
    const tampered = a.bytes.slice();
    tampered[0] = tampered[0]! ^ 0xff;
    const refused = await putPackage(a.sha256, tampered);
    expect(refused.status).toBe(422);
    expect((await body(refused))["error"]).toBe("package_not_stored");
    expect(await env.RELEASES.head(`${a.sha256}.nupkg`)).toBeNull();
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.releases.bytes).toBe(0);

    expect((await putPackage(a.sha256, a.bytes)).status).toBe(200);
    const early = await publish(id);
    expect(early.status).toBe(409);
    expect(await body(early)).toMatchObject({ error: "packages_missing", missing: [b.sha256] });
    expect((await putPackage(b.sha256, b.bytes)).status).toBe(200);
    expect(await body(await putPackage(b.sha256, b.bytes))).toMatchObject({ existing: true });
    expect((await publish(id)).status).toBe(200);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.releases).toEqual({ bytes: a.bytes.length + b.bytes.length, objects: 2 });
  });

  it("count towards the storage cap: a package that would pass 8 GB is refused with 507 and nothing is stored", async () => {
    const p = await makePackage("1.1.0");
    await stage(await sign("releases", "stable", 10, payload([p])));
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'audit'").bind(PRODUCTION_STORAGE_LIMITS.capBytes - p.bytes.length + 1).run();

    const refused = await putPackage(p.sha256, p.bytes);

    expect(refused.status).toBe(507);
    expect((await body(refused))["error"]).toBe("storage_full");
    expect(await env.RELEASES.head(`${p.sha256}.nupkg`)).toBeNull();
    expect(await rowCount("release_packages")).toBe(0);
  });

  it("keep only the last 3 per channel: a published feed prunes every package no feed lists any more (D18)", async () => {
    const [v10, v11, v12, v13] = await Promise.all(["1.0.0", "1.1.0", "1.2.0", "1.3.0"].map((v) => makePackage(v)));
    await release([v10!, v11!, v12!], 10);
    expect(await rowCount("release_packages")).toBe(3);

    const staged = await stage(await sign("releases", "stable", 11, payload([v11!, v12!, v13!])));
    const id = (await body<{ feed: { id: string } }>(staged)).feed.id;
    expect((await putPackage(v13!.sha256, v13!.bytes)).status).toBe(200);
    const published = await body<{ pruned: { deleted: number } }>(await publish(id));

    expect(published.pruned.deleted).toBe(1);
    expect(await rowCount("release_packages")).toBe(3);
    expect(await env.RELEASES.head(`${v10!.sha256}.nupkg`)).toBeNull();
    expect((await h.call("GET", `${CDN}/releases/${v10!.sha256}.nupkg`)).status).toBe(404);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.releases.objects).toBe(3);
    expect(await rowCount("release_feeds", "status = 'superseded'")).toBe(1);
  });

  it("of a discarded staged feed are pruned, and the daily sweep gives back an unfinished reservation", async () => {
    const p = await makePackage("1.1.0");
    const staged = await stage(await sign("releases", "stable", 10, payload([p])));
    const id = (await body<{ feed: { id: string } }>(staged)).feed.id;
    expect((await putPackage(p.sha256, p.bytes)).status).toBe(200);
    expect((await h.admin("POST", `/api/releases/feeds/${id}/discard`, {})).status).toBe(200);
    expect(await rowCount("release_packages")).toBe(0);
    expect(await env.RELEASES.head(`${p.sha256}.nupkg`)).toBeNull();

    await env.DB.prepare("INSERT INTO storage_objects (area, key, bytes, created_at) VALUES ('releases', ?, 100, ?)").bind(`${"e".repeat(64)}.nupkg`, h.now - 2 * DAY).run();
    const swept = await sweepReleases(env, h.now);
    expect(swept.reservations).toBe(1);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.releases.bytes).toBe(0);
  });
});

describe("admin list", () => {
  it("shows each channel's published and staged feed with package presence, the key status and the storage", async () => {
    const p = await makePackage("1.1.0");
    await release([p], 10);
    const q = await makePackage("1.2.0");
    await stage(await sign("releases", "stable", 11, payload([q])));

    const list = await body<{
      channels: Record<string, { published: { feedVersion: number; newest: string; packages: { present: boolean }[] } | null; staged: { packages: { present: boolean }[] } | null }>;
      keys: { release: string[]; recovery: string[]; configured: boolean };
      limits: { maxReleases: number };
    }>(await h.admin("GET", "/api/releases"));

    expect(list.channels["stable"]!.published).toMatchObject({ feedVersion: 10, newest: "1.1.0", packages: [{ present: true }] });
    expect(list.channels["stable"]!.staged!.packages).toEqual([expect.objectContaining({ present: false, version: "1.2.0" })]);
    expect(list.channels["beta"]).toEqual({ published: null, staged: null });
    expect(list.keys).toEqual({ release: ["release-test-1"], recovery: ["recovery-test-1"], revoked: [], configured: true });
    expect(list.limits.maxReleases).toBe(3);
  });

  it("needs Cloudflare Access on every release route", async () => {
    for (const [method, path] of [
      ["GET", "/api/releases"],
      ["POST", "/api/releases/feeds"],
      ["PUT", `/api/releases/packages/${"a".repeat(64)}`],
      ["POST", `/api/releases/feeds/${crypto.randomUUID()}/publish`],
      ["POST", "/api/keys/replacement"],
    ] as const) {
      const response = await h.call(method, `${ADMIN}${path}`, { headers: { "X-DBDP-Admin": "1" } });
      expect(response.status).toBe(401);
    }
  });

  it("writes an audit row for each privileged action", async () => {
    await release([await makePackage("1.1.0")], 10);
    const { results } = await env.DB.prepare("SELECT action FROM audit_log ORDER BY seq").all<{ action: string }>();
    expect(results.map((r) => r.action)).toEqual(["release.feed_upload", "release.package_upload", "release.publish"]);
  });
});

describe("key replacement (recovery-key flow)", () => {
  const replacement = { schemaVersion: 1, revoke: ["release-test-1"], add: [{ kid: "release-test-2", spki: RELEASE_KEY.spki, purposes: ["releases"] }] };

  it("is accepted only from the recovery key, newer than the last one, and served at GET /v1/keys", async () => {
    expect((await h.call("GET", `${API}/v1/keys`)).status).toBe(404);
    const document = await sign("key-replacement", "all", 2, replacement);
    const accepted = await h.admin("POST", "/api/keys/replacement", { document });
    expect(accepted.status).toBe(200);
    expect(await body(await h.call("GET", `${API}/v1/keys`))).toEqual({ schemaVersion: 1, document });

    expect(await body(await h.admin("POST", "/api/keys/replacement", { document: await sign("key-replacement", "all", 1, replacement) }))).toMatchObject({ reason: "RolledBack" });
    expect((await h.admin("POST", "/api/keys/replacement", { document: await sign("key-replacement", "all", 2, replacement) })).status).toBe(409);
    const byRelease = await sign("key-replacement", "all", 3, replacement, RELEASE_KEY);
    expect(await body(await h.admin("POST", "/api/keys/replacement", { document: byRelease }))).toMatchObject({ reason: "KeyNotAllowedForPurpose" });
  });

  it("makes the service refuse feeds signed by a revoked release key, and trust the key it adds (L3)", async () => {
    const p = await makePackage("1.1.0");
    const before = await sign("releases", "stable", 10, payload([p]));
    expect((await stage(before)).status).toBe(200);
    const successor = { ...RELEASE_KEY, kid: "release-test-2" };
    const document = await sign("key-replacement", "all", 2, { schemaVersion: 1, revoke: ["release-test-1"], add: [{ kid: "release-test-2", spki: RELEASE_KEY.spki, purposes: ["releases"] }] });
    expect((await h.admin("POST", "/api/keys/replacement", { document })).status).toBe(200);

    expect(await body(await stage(await sign("releases", "stable", 11, payload([p]))))).toMatchObject({ error: "feed_refused", reason: "UnknownKey" });
    expect((await stage(await sign("releases", "stable", 12, payload([p]), successor))).status).toBe(200);
    const list = await body<{ keys: { release: string[]; revoked: string[] } }>(await h.admin("GET", "/api/releases"));
    expect(list.keys).toMatchObject({ release: ["release-test-2"], revoked: ["release-test-1"] });
  });

  it("refuses a replacement that leaves out an earlier revocation (M1)", async () => {
    const first = await sign("key-replacement", "all", 2, { schemaVersion: 1, revoke: ["release-test-1"], add: [] });
    expect((await h.admin("POST", "/api/keys/replacement", { document: first })).status).toBe(200);
    const unrevokes = await sign("key-replacement", "all", 3, { schemaVersion: 1, revoke: ["release-old-9"], add: [] });
    expect(await body(await h.admin("POST", "/api/keys/replacement", { document: unrevokes }))).toMatchObject({ reason: "not_cumulative" });
    const cumulative = await sign("key-replacement", "all", 3, { schemaVersion: 1, revoke: ["release-test-1", "release-old-9"], add: [] });
    expect((await h.admin("POST", "/api/keys/replacement", { document: cumulative })).status).toBe(200);
  });

  it("can never revoke the recovery key or add a recovery key", async () => {
    const revokesRecovery = await sign("key-replacement", "all", 2, { schemaVersion: 1, revoke: ["recovery-test-1"], add: [] });
    expect(await body(await h.admin("POST", "/api/keys/replacement", { document: revokesRecovery }))).toMatchObject({ reason: "revokes_recovery_key" });
    const addsRecovery = await sign("key-replacement", "all", 2, { schemaVersion: 1, revoke: [], add: [{ kid: "recovery-test-2", spki: RECOVERY_KEY.spki, purposes: ["key-replacement"] }] });
    expect(await body(await h.admin("POST", "/api/keys/replacement", { document: addsRecovery }))).toMatchObject({ reason: "bad_add" });
    expect(await rowCount("key_replacements")).toBe(0);
  });
});

describe("key custody (NFR-SVC-05, V6.3): the release and recovery keys are never in the Worker", () => {
  it("no source signs a release feed or a key replacement, and no binding can hold a private offline key", () => {
    const sources = inject("serviceSources");
    expect(sources.length).toBeGreaterThan(10);
    for (const { file, text } of sources) {
      expect(text, file).not.toMatch(/signDocument\(\s*["'](releases|key-replacement)["']/);
      expect(text, file).not.toMatch(/(RELEASE|RECOVERY)_(SIGNING_|PRIVATE_)?KEY\b/);
    }
    for (const config of [inject("wranglerProduction"), inject("wranglerStaging")]) {
      expect(config.secrets.filter((s) => /RELEASE|RECOVERY|OFFLINE/.test(s))).toEqual([]);
    }
  });

  it("wrangler.toml lists exactly the public release and recovery keys of TrustedServiceKeys.json, public halves only", () => {
    const trusted = JSON.parse(inject("trustedServiceKeys")) as Record<string, { kid: string; purposes: string[]; spki?: string }[]>;
    for (const [name, config] of [["production", inject("wranglerProduction")], ["staging", inject("wranglerStaging")]] as const) {
      const listed = JSON.parse(config.vars["OFFLINE_PUBLIC_KEYS"] ?? "null") as Record<string, unknown>[];
      expect(Array.isArray(listed), name).toBe(true);
      for (const entry of listed) expect(Object.keys(entry).sort()).toEqual(["kid", "purposes", "spki"]);
      const expected = trusted[name]!.filter((k) => k.spki && (k.purposes.join() === "releases" || k.purposes.join() === "key-replacement"));
      expect(listed.map((k) => k["kid"]).sort(), name).toEqual(expected.map((k) => k.kid).sort());
    }
  });

  it("parses the cross-stack release feed fixture with the same rules as the app", () => {
    const fixture = inject("crossStackDocuments").find((f) => f.file === "ts-releases.json")!;
    const { expectedPayload } = JSON.parse(fixture.text) as { expectedPayload: unknown };
    const parsed = parseReleaseFeed(JSON.stringify(expectedPayload), "beta");
    expect(parsed.ok && parsed.value.releases.map((r) => r.version)).toEqual(["1.3.0-beta.2", "1.2.0"]);
  });
});
