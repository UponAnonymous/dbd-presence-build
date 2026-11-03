// FR-ADM-10 (Releases: upload feeds and packages signed offline, choose the channel, publish; the panel never holds the release
// key; an unsigned or tampered feed is refused), NFR-SVC-05, D18 (only the packages the feeds list are kept), PLAN_V2 §6.2
// (key replacement), FR-ADM-04 (everything a file says is shown as text only).

import { describe, expect, it } from "vitest";
import type { ReleaseFeedView } from "../src/api.ts";
import { previewEnvelope } from "../src/views/releases.ts";
import { FakeBackend, HOSTILE, NOW, buttonByLabel, buttonByText, chooseFile, mount, openDialog, sampleStorage, settle } from "./fake.ts";

const DAY = 86_400_000;
const iso = (ms: number): string => new Date(ms).toISOString();
const FEED_ID = "99999999-9999-4999-8999-999999999991";
const STAGED_ID = "99999999-9999-4999-8999-999999999992";

function toBase64Url(text: string): string {
  return btoa(String.fromCharCode(...new TextEncoder().encode(text))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** A document shaped like a signed one (the signature is not checked by the panel; the service checks it). */
function signedText(envelope: Record<string, unknown>): string {
  return JSON.stringify({ kid: envelope["kid"], signed: toBase64Url(JSON.stringify(envelope)), sig: "A".repeat(86) });
}

const feedEnvelope = (channel: string, purpose = "releases") => ({
  kid: "release-prod-1",
  purpose,
  channel,
  version: 1793900000,
  issuedAt: iso(NOW),
  expiresAt: iso(NOW + 30 * DAY),
  payload: { schemaVersion: 1, packageId: "DbdPresence", releases: [{ version: "0.3.0", fileName: "DbdPresence-0.3.0-stable-full.nupkg", size: 5, sha256: "a".repeat(64) }] },
});

async function sha256Hex(bytes: Uint8Array<ArrayBuffer>): Promise<string> {
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** The fake service plus the release routes (service/src/routes/releases.ts shapes). */
class ReleasesBackend extends FakeBackend {
  published: ReleaseFeedView | null = feed(FEED_ID, "published", "0.2.0", [{ version: "0.2.0", present: true, sha: "b".repeat(64), size: 70_000_000 }], NOW + 3 * DAY);
  staged: ReleaseFeedView | null = null;
  keysConfigured = true;
  replacement: { version: number; kid: string; uploadedAt: string } | null = null;
  readonly calls: { method: string; path: string; body: unknown }[] = [];
  refuseNextStage: { error: string; reason: string } | null = null;

  private reply(status: number, body: Record<string, unknown>): Response {
    return new Response(JSON.stringify({ schemaVersion: 1, ...body }), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
  }

  constructor() {
    super();
    const base = this.fetch;
    // The release routes are answered here; everything else goes to the shared fake.
    Object.defineProperty(this, "fetch", { value: (input: string, init: RequestInit) => this.handle(input, init, base) });
  }

  private async handle(input: string, init: RequestInit, base: (input: string, init: RequestInit) => Promise<Response>): Promise<Response> {
    const path = input.split("?")[0]!;
    const method = init.method ?? "GET";
    if (!path.startsWith("/api/releases") && path !== "/api/keys/replacement") return base(input, init);
    const body = typeof init.body === "string" ? (JSON.parse(init.body) as Record<string, unknown>) : init.body;
    this.calls.push({ method, path, body });
    this.requests.push({ method, url: input, headers: Object.fromEntries(Object.entries((init.headers ?? {}) as Record<string, string>)), body, init });
    if (method === "GET" && path === "/api/releases") {
      return this.reply(200, {
        channels: { stable: { published: this.published, staged: this.staged }, beta: { published: null, staged: null } },
        keyReplacement: this.replacement,
        keys: this.keysConfigured
          ? { release: ["release-prod-2"], recovery: ["recovery-prod-1"], revoked: ["release-prod-1"], configured: true }
          : { release: [], recovery: [], revoked: [], configured: true },
        limits: { maxReleases: 3, maxPackageBytes: 95_000_000 },
        storage: sampleStorage(1_234_567_890),
      });
    }
    if (method === "POST" && path === "/api/releases/feeds") {
      if (this.refuseNextStage) return this.reply(422, this.refuseNextStage);
      this.staged = feed(STAGED_ID, "staged", "0.3.0", [{ version: "0.3.0", present: false, sha: STAGED_SHA, size: STAGED_BYTES.length }], NOW + 30 * DAY);
      return this.reply(200, { feed: this.staged });
    }
    let m: RegExpExecArray | null;
    if (method === "PUT" && (m = /^\/api\/releases\/packages\/([0-9a-f]{64})$/.exec(path))) {
      const pkg = this.staged?.packages.find((p) => p.sha256 === m![1]);
      if (!pkg) return this.reply(409, { error: "package_not_in_feed" });
      pkg.present = true;
      return this.reply(200, { package: { sha256: pkg.sha256, size: pkg.size }, existing: false });
    }
    if (method === "POST" && (m = /^\/api\/releases\/feeds\/([0-9a-f-]+)\/publish$/.exec(path))) {
      if (!this.staged || this.staged.id !== m[1]) return this.reply(409, { error: "feed_not_staged" });
      this.published = { ...this.staged, status: "published", publishedAt: iso(NOW) };
      this.staged = null;
      return this.reply(200, { feed: this.published, pruned: { deleted: 1, pending: 0 } });
    }
    if (method === "POST" && (m = /^\/api\/releases\/feeds\/([0-9a-f-]+)\/discard$/.exec(path))) {
      this.staged = null;
      return this.reply(200, { discarded: true, pruned: { deleted: 0, pending: 0 } });
    }
    if (method === "POST" && path === "/api/keys/replacement") {
      this.replacement = { version: 2, kid: "recovery-prod-1", uploadedAt: iso(NOW) };
      return this.reply(200, { keyReplacement: { version: 2, kid: "recovery-prod-1", revoke: ["release-prod-1"], add: ["release-prod-2"] } });
    }
    return this.reply(404, { error: "not_found" });
  }
}

const STAGED_BYTES = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
let STAGED_SHA = "";

function feed(id: string, status: ReleaseFeedView["status"], newest: string, packages: { version: string; present: boolean; sha: string; size: number }[], expiresAt: number): ReleaseFeedView {
  return {
    id,
    channel: "stable",
    feedVersion: status === "published" ? 1793800000 : 1793900000,
    kid: "release-prod-1",
    issuedAt: iso(expiresAt - 30 * DAY),
    expiresAt: iso(expiresAt),
    expired: expiresAt <= NOW,
    newest,
    status,
    uploadedAt: iso(NOW - DAY),
    uploadedBy: "owner:owner@example.com",
    publishedAt: status === "published" ? iso(NOW - DAY) : null,
    packages: packages.map((p) => ({ version: p.version, fileName: `DbdPresence-${p.version}-stable-full.nupkg`, size: p.size, sha256: p.sha, present: p.present })),
  };
}

function file(name: string, content: string | Uint8Array<ArrayBuffer>): File {
  return new File([content], name, { type: "application/octet-stream" });
}

async function stagedBackend(): Promise<ReleasesBackend> {
  STAGED_SHA = await sha256Hex(STAGED_BYTES);
  return new ReleasesBackend();
}

describe("Releases section", () => {
  it("shows the published feed, its expiry warning, the key names and says the panel never holds the release key", async () => {
    const { root } = await mount("releases", { backend: await stagedBackend() });

    expect(root.querySelector("h1")?.textContent).toBe("Releases");
    expect(root.textContent).toContain("This panel never holds the release key");
    expect(root.textContent).toContain("Version 0.2.0 · feed 1793800000");
    expect(root.textContent).toContain("Expires soon");
    expect(root.textContent).toContain("Release key: release-prod-2");
    expect(root.textContent).toContain("recovery-prod-1");
    expect(root.textContent).toContain("Revoked by the last key replacement (refused): release-prod-1");
    expect(root.textContent).toContain("Nothing is published on this channel");
    expect(root.textContent).toContain("never installs a lower version");
    expect(root.textContent).toContain("Releases: 0 bytes in 0 files");
  });

  it("warns when no release key is configured", async () => {
    const backend = await stagedBackend();
    backend.keysConfigured = false;
    const { root } = await mount("releases", { backend });

    expect(root.textContent).toContain("No release key yet");
  });

  it("previews a chosen feed as text, stages it, uploads the matching package and publishes", async () => {
    const backend = await stagedBackend();
    const { root } = await mount("releases", { backend });
    const input = root.querySelector<HTMLInputElement>("#release-feed-file")!;
    const hostile = { ...feedEnvelope("stable"), kid: HOSTILE };

    chooseFile(input, file("releases-stable.json", signedText(hostile)));
    await settle();
    expect(root.textContent).toContain(`signed by ${HOSTILE}`);
    expect(root.querySelectorAll("img, script").length).toBe(0);
    buttonByText(root, "Stage this feed").click();
    await settle();

    expect(backend.calls.find((c) => c.path === "/api/releases/feeds")?.body).toEqual({ channel: "stable", document: signedText(hostile) });
    expect(root.textContent).toContain("Staged: version 0.3.0");
    expect(buttonByLabel(root, "Publish the staged Stable feed").disabled).toBe(true);

    chooseFile(root.querySelector<HTMLInputElement>('input[aria-label="Package file for version 0.3.0"]')!, file("DbdPresence-0.3.0-stable-full.nupkg", STAGED_BYTES));
    await settle(12);
    const put = backend.requests.find((r) => r.method === "PUT")!;
    expect(put.url).toBe(`/api/releases/packages/${STAGED_SHA}`);
    expect(put.headers["Content-Type"]).toBe("application/octet-stream");

    buttonByLabel(root, "Publish the staged Stable feed").click();
    await settle();
    buttonByText(openDialog(), "Publish").click();
    await settle();
    expect(backend.calls.some((c) => c.path === `/api/releases/feeds/${STAGED_ID}/publish`)).toBe(true);
    expect(root.textContent).toContain("Version 0.3.0 · feed 1793900000");
  });

  it("refuses a package file whose size or fingerprint differs from the signed feed, before any upload", async () => {
    const backend = await stagedBackend();
    const { root } = await mount("releases", { backend });
    chooseFile(root.querySelector<HTMLInputElement>("#release-feed-file")!, file("releases-stable.json", signedText(feedEnvelope("stable"))));
    await settle();
    buttonByText(root, "Stage this feed").click();
    await settle();

    const picker = () => root.querySelector<HTMLInputElement>('input[aria-label="Package file for version 0.3.0"]')!;
    chooseFile(picker(), file("short.nupkg", new Uint8Array([1, 2, 3])));
    await settle(12);
    expect(root.textContent).toContain("This is not the package the feed lists");
    chooseFile(picker(), file("other.nupkg", new Uint8Array([9, 9, 9, 9, 9, 9, 9, 9])));
    await settle(12);
    expect(root.textContent).toContain("Its SHA-256 differs from the signed feed's");
    expect(backend.requests.filter((r) => r.method === "PUT")).toEqual([]);
  });

  it("does not offer to stage a file that is not a release feed, or one for another channel", async () => {
    const { root, backend } = await mount("releases", { backend: await stagedBackend() });
    const input = root.querySelector<HTMLInputElement>("#release-feed-file")!;

    chooseFile(input, file("notes.json", '{"hello":"world"}'));
    await settle();
    expect(root.textContent).toContain("This is not a signed release feed");
    expect(buttonByText(root, "Stage this feed").disabled).toBe(true);

    chooseFile(input, file("kr.json", signedText(feedEnvelope("all", "key-replacement"))));
    await settle();
    expect(root.textContent).toContain("This is not a signed release feed");

    chooseFile(input, file("beta.json", signedText(feedEnvelope("beta"))));
    await settle();
    expect(root.textContent).toContain("This feed was signed for the beta channel");
    expect(buttonByText(root, "Stage this feed").disabled).toBe(true);
    expect(backend.requests.filter((r) => r.method !== "GET")).toEqual([]);
  });

  it("shows the service's refusal of a tampered or unsigned feed, with its reason", async () => {
    const backend = await stagedBackend();
    backend.refuseNextStage = { error: "feed_refused", reason: "BadSignature" };
    const { root } = await mount("releases", { backend });

    chooseFile(root.querySelector<HTMLInputElement>("#release-feed-file")!, file("releases-stable.json", signedText(feedEnvelope("stable"))));
    await settle();
    buttonByText(root, "Stage this feed").click();
    await settle();

    expect(root.textContent).toContain("The feed was not staged");
    expect(root.textContent).toContain("Reason: BadSignature.");
    expect(root.textContent).toContain("Nothing was changed");
  });

  it("uploads a key replacement only after a typed confirmation that names the revoked and added keys", async () => {
    const backend = await stagedBackend();
    const { root } = await mount("releases", { backend });
    const envelope = { kid: "recovery-prod-1", purpose: "key-replacement", channel: "all", version: 2, issuedAt: iso(NOW), expiresAt: iso(NOW + 3650 * DAY), payload: { schemaVersion: 1, revoke: ["release-prod-1"], add: [{ kid: "release-prod-2", spki: "x", purposes: ["releases"] }] } };

    chooseFile(root.querySelector<HTMLInputElement>("#key-replacement-file")!, file("kr.json", signedText(envelope)));
    await settle();
    const dialog = openDialog();
    expect(dialog.textContent).toContain("Revoked: release-prod-1. Trusted from now on: release-prod-2.");
    const confirm = buttonByText(dialog, "Replace keys");
    expect(confirm.disabled).toBe(true);
    const typed = dialog.querySelector<HTMLInputElement>("input")!;
    typed.value = "REPLACE";
    typed.dispatchEvent(new Event("input"));
    confirm.click();
    await settle();

    expect(backend.calls.some((c) => c.path === "/api/keys/replacement")).toBe(true);
    expect(root.textContent).toContain("Current replacement: version 2, signed by recovery-prod-1");
  });
});

describe("feed preview", () => {
  it("reads the envelope of a signed document and nothing else", () => {
    expect(previewEnvelope(signedText(feedEnvelope("stable")))).toMatchObject({ purpose: "releases", channel: "stable", version: 1793900000 });
    expect(previewEnvelope("not json")).toBeNull();
    expect(previewEnvelope('{"signed":"%%%"}')).toBeNull();
    expect(previewEnvelope(JSON.stringify({ signed: toBase64Url('{"purpose":1}') }))).toBeNull();
  });
});
