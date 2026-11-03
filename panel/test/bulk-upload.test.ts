// FR-ADM-08, FR-PACK-03/04 (Behaviour flag asked once, licensed items), FR-ADM-04 (text only; images only from the
// content host), FR-ADM-11 (axe): "Add pictures from a folder" in the pack editor. The review before anything is sent,
// the upload through the same calls as a single image, waiting on 429, partial failure with retry, and no publish.

import axe from "axe-core";
import { describe, expect, it } from "vitest";
import { ApiError, type Pack } from "../src/api.ts";
import { BACKOFF_MS, MAX_RETRIES, MAX_UNEXPECTED_IN_A_ROW, runUploads, withRetry } from "../src/views/bulk-upload.ts";
import { FakeBackend, HOSTILE, buttonByLabel, buttonByText, chooseFiles, mount, pngOfSize, samplePacks, settle, shaOf, until } from "./fake.ts";

const writes = <T extends { method: string; url: string }>(requests: T[]): T[] => requests.filter((r) => r.method !== "GET");

function png(name: string, marker: number, size = 512, path?: string): File {
  const file = new File([pngOfSize(size, size, marker) as Uint8Array<ArrayBuffer>], name, { type: "image/png" });
  if (path) Object.defineProperty(file, "webkitRelativePath", { value: path });
  return file;
}

async function openEditor(backend = new FakeBackend(), id = "Original") {
  const mounted = await mount("packs", { backend });
  buttonByLabel(mounted.root, `Edit ${id}`).click();
  await settle();
  return mounted;
}

async function choose(root: HTMLElement, files: File[], input = "#bulk-files"): Promise<void> {
  chooseFiles(root.querySelector<HTMLInputElement>(input)!, files);
  await until(() => root.querySelector("#bulk-review-title") !== null);
}

const rowFor = (root: HTMLElement, path: string) => [...root.querySelectorAll<HTMLTableRowElement>(".table-bulk tbody tr")].find((tr) => tr.textContent?.includes(path))!;
const statusOf = (root: HTMLElement, path: string) => rowFor(root, path).querySelector(".bulk-status")!.textContent ?? "";
const slotOf = (root: HTMLElement, path: string) => rowFor(root, path).querySelector("select")!;

function pick(select: HTMLSelectElement, value: string): void {
  select.value = value;
  select.dispatchEvent(new Event("change"));
}

function answerBehaviour(root: HTMLElement, answer: "yes" | "no"): void {
  const radio = root.querySelector<HTMLInputElement>(`#bulk-behaviour-${answer}`)!;
  radio.checked = true;
  radio.dispatchEvent(new Event("change"));
}

async function axeViolations(): Promise<string[]> {
  const result = await axe.run(document, { rules: { "color-contrast": { enabled: false } }, resultTypes: ["violations"] });
  return result.violations.map((v) => `${v.id} (${v.impact ?? "?"}): ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

describe("Add pictures from a folder: the review", () => {
  it("offers a folder picker and a multi-file picker, and explains how to name the files", async () => {
    const { root } = await openEditor();
    const folder = root.querySelector<HTMLInputElement>("#bulk-folder")!;
    expect(folder.type).toBe("file");
    expect(folder.hasAttribute("webkitdirectory")).toBe(true);
    expect(folder.multiple).toBe(true);
    expect(root.querySelector<HTMLInputElement>("#bulk-files")!.multiple).toBe(true);
    expect(root.textContent).toContain("“Ace Visconti.png”, “ace_visconti.jpg”, “The Trapper.png” or “Trapper.png”");
    expect(root.textContent).toContain("with or without “The”");
  });

  it("shows matched, unmatched, clashing, duplicate, existing and refused files, and the slots still without a picture, before sending anything", async () => {
    const { root, backend } = await openEditor();
    const files = [
      png("The Hag.png", 1, 512, "Portraits/The Hag.png"),
      png("dwight_fairfield.png", 2, 512, "Portraits/dwight_fairfield.png"),
      png("Hag.png", 3, 512, "Portraits/Hag.png"),
      png("holiday.png", 4, 512, "Portraits/holiday.png"),
      png("copy of dwight.png", 2, 512, "Portraits/copy of dwight.png"),
      png("The Trapper.png", 5, 512, "Portraits/The Trapper.png"),
      png("huge.png", 6, 2048, "Portraits/huge.png"),
      new File([new TextEncoder().encode("notes")], "notes.txt", { type: "text/plain" }),
    ];
    await choose(root, files, "#bulk-folder");
    expect(writes(backend.requests)).toEqual([]);
    // Matched, with the file's facts (no preview before upload: ADR-0014).
    expect(slotOf(root, "Portraits/The Hag.png").value).toBe("character:the-hag");
    expect(statusOf(root, "Portraits/The Hag.png")).toContain("Matched by name");
    expect(rowFor(root, "Portraits/The Hag.png").textContent).toContain("PNG · 512 × 512");
    expect(root.querySelector(".table-bulk img")).toBeNull();
    expect(slotOf(root, "Portraits/dwight_fairfield.png").value).toBe("character:dwight-fairfield");
    // A second file for the same slot waits for the owner.
    expect(slotOf(root, "Portraits/Hag.png").value).toBe("");
    expect(statusOf(root, "Portraits/Hag.png")).toContain("Portraits/The Hag.png matched The Hag first");
    // Unmatched: the owner picks the slot from the list.
    expect(slotOf(root, "Portraits/holiday.png").value).toBe("");
    expect(statusOf(root, "Portraits/holiday.png")).toContain("No slot matched the name");
    // The same bytes twice.
    expect(statusOf(root, "Portraits/copy of dwight.png")).toContain("Same picture as Portraits/dwight_fairfield.png");
    // The pack already has the Trapper: only with Replace.
    expect(statusOf(root, "Portraits/The Trapper.png")).toContain("already has a picture for The Trapper");
    // Refused, with the same words as single upload.
    expect(root.textContent).toContain("Files that cannot be used (2)");
    expect(root.textContent).toContain("Portraits/huge.png: Images can be at most 1024 × 1024 pixels.");
    expect(root.textContent).toContain("notes.txt: Only PNG and JPEG files are accepted.");
    // Slots still without a picture: what neither the pack nor this list fills.
    const missing = root.querySelector(".bulk-missing")!.textContent!;
    expect(missing).toContain("Slots still without a picture after this upload");
    expect(missing).toContain("Generic killer portrait");
    expect(missing).not.toContain("The Hag");
    expect(buttonByText(root, "Upload 2 pictures")).toBeTruthy();
  });

  it("updates the list as the owner chooses: a clash blocks both files until one moves, Replace allows an existing slot", async () => {
    const { root } = await openEditor();
    await choose(root, [png("The Hag.png", 1), png("holiday.png", 2), png("The Trapper.png", 3)]);
    pick(slotOf(root, "holiday.png"), "character:the-hag");
    expect(statusOf(root, "holiday.png")).toContain("Another file is also set to The Hag");
    expect(statusOf(root, "The Hag.png")).toContain("Another file is also set to The Hag");
    expect(buttonByText(root, "Upload 0 pictures")).toBeTruthy();
    pick(slotOf(root, "holiday.png"), "character:dwight-fairfield");
    expect(statusOf(root, "holiday.png")).toContain("Slot chosen by you");
    const replace = rowFor(root, "The Trapper.png").querySelector<HTMLInputElement>('input[id^="bulk-replace-"]')!;
    replace.checked = true;
    replace.dispatchEvent(new Event("change"));
    expect(statusOf(root, "The Trapper.png")).toContain("Replaces the current picture");
    expect(buttonByText(root, "Upload 3 pictures")).toBeTruthy();
  });

  it("ticks Licensed with the roster's rights holder for a licensed character, and asks for a holder when ticked by hand", async () => {
    const backend = new FakeBackend();
    backend.data.packs[0]!.entries = backend.data.packs[0]!.entries.filter((e) => e.key !== "character:the-shape");
    const { root } = await openEditor(backend);
    await choose(root, [png("Michael Myers.png", 1), png("Dwight Fairfield.png", 2)]);
    const shape = rowFor(root, "Michael Myers.png");
    expect(slotOf(root, "Michael Myers.png").value).toBe("character:the-shape");
    expect(shape.querySelector<HTMLInputElement>('input[type="checkbox"][id^="bulk-licensed-"]')!.checked).toBe(true);
    expect(shape.querySelector<HTMLInputElement>('input[type="text"]')!.value).toBe("Example Studio");
    const dwight = rowFor(root, "Dwight Fairfield.png").querySelector<HTMLInputElement>('input[id^="bulk-licensed-"]')!;
    expect(dwight.checked).toBe(false);
    dwight.checked = true;
    dwight.dispatchEvent(new Event("change"));
    expect(statusOf(root, "Dwight Fairfield.png")).toContain("Enter the rights holder");
    expect(buttonByText(root, "Upload 1 picture")).toBeTruthy();
  });

  it("refuses a selection of more than 400 files", async () => {
    const { root } = await openEditor();
    const many = Array.from({ length: 401 }, (_, i) => new File([new Uint8Array(1)], `${i}.png`));
    chooseFiles(root.querySelector<HTMLInputElement>("#bulk-files")!, many);
    await settle();
    expect(root.querySelector("#bulk-files-error")!.textContent).toContain("Choose at most 400 at a time");
    expect(root.querySelector("#bulk-review-title")).toBeNull();
  });

  it("has no axe violations with the review open", async () => {
    const { root } = await openEditor(new FakeBackend(), HOSTILE);
    await choose(root, [png("The Hag.png", 1), png("holiday.png", 2), new File([new Uint8Array(0)], "empty.png")]);
    expect(await axeViolations()).toEqual([]);
  });
});

describe("Add pictures from a folder: uploading", () => {
  it("uploads one by one through the single-upload calls, shows each result and the stored picture, and never publishes", async () => {
    const { root, backend } = await openEditor();
    await choose(root, [png("The Hag.png", 1), png("Dwight Fairfield.png", 2), png("notes.png", 3)]);
    buttonByText(root, "Upload 2 pictures").click();
    await until(() => root.textContent!.includes("2 pictures added"));
    const sent = writes(backend.requests).map((r) => `${r.method} ${r.url}`);
    expect(sent).toEqual(["POST /api/pack-files", "POST /api/packs/original/entries", "POST /api/pack-files", "POST /api/packs/original/entries"]);
    expect(sent.some((s) => s.includes("/publish"))).toBe(false);
    expect(writes(backend.requests)[0]!.headers["Content-Type"]).toBe("image/png");
    expect(writes(backend.requests)[1]!.body).toEqual({ key: "character:the-hag", sha256: shaOf(0x40), sourceSha256: shaOf(0x41), licensedHolder: null });
    expect(statusOf(root, "The Hag.png")).toContain("Added. Not published yet.");
    expect(root.textContent).toContain("Nothing is published yet.");
    // The stored picture is shown from the content host only.
    const thumbs = [...rowFor(root, "The Hag.png").querySelectorAll("img")];
    expect(thumbs).toHaveLength(1);
    expect(thumbs[0]!.getAttribute("src")!.startsWith("https://cdn.arcavale.com/packs/")).toBe(true);
    // The pack has the new images and still needs publishing.
    expect(root.textContent).toContain("Images (7)");
    expect(root.textContent).toContain("This pack has changes that apps do not have yet");
    expect(backend.data.packs[0]!.changedSincePublish).toBe(true);
    expect(backend.data.packs[0]!.entries.slice(-2).map((e) => [e.key, e.live])).toEqual([["character:the-hag", false], ["character:dwight-fairfield", false]]);
  });

  it("asks once whether the set is Behaviour Interactive artwork, and marks the pack before uploading when it is", async () => {
    const { root, backend } = await openEditor(new FakeBackend(), HOSTILE);
    await choose(root, [png("The Hag.png", 1)]);
    buttonByText(root, "Upload 1 picture").click();
    await settle();
    expect(root.querySelector("#bulk-behaviour-error")!.textContent).toBe("Answer this before uploading.");
    expect(writes(backend.requests)).toEqual([]);
    answerBehaviour(root, "yes");
    buttonByText(root, "Upload 1 picture").click();
    await until(() => root.textContent!.includes("1 picture added"));
    const sent = writes(backend.requests);
    expect(sent.map((r) => `${r.method} ${r.url}`)).toEqual(["PUT /api/packs/nightlight-ash", "POST /api/pack-files", "POST /api/packs/nightlight-ash/entries"]);
    expect(sent[0]!.body).toMatchObject({ behaviourArt: true, thirdParty: true });
    expect(backend.data.packs[1]!.status).toBe("draft");
  });

  it("with No leaves the pack's flag alone", async () => {
    const { root, backend } = await openEditor(new FakeBackend(), HOSTILE);
    await choose(root, [png("The Hag.png", 1)]);
    answerBehaviour(root, "no");
    buttonByText(root, "Upload 1 picture").click();
    await until(() => root.textContent!.includes("1 picture added"));
    expect(writes(backend.requests).map((r) => `${r.method} ${r.url}`)).toEqual(["POST /api/pack-files", "POST /api/packs/nightlight-ash/entries"]);
  });

  it("keeps going after a refused file, says why, and retries only the ones that failed", async () => {
    const backend = new FakeBackend();
    backend.failOnce("POST /api/pack-files", { status: 422, error: "image_rejected", reason: "png_not_rgb8" }, 1);
    const { root } = await openEditor(backend);
    await choose(root, [png("The Hag.png", 1), png("Dwight Fairfield.png", 2), png("Ghost Face.png", 3)]);
    pick(slotOf(root, "Ghost Face.png"), "character:the-lantern");
    buttonByText(root, "Upload 3 pictures").click();
    await until(() => root.textContent!.includes("2 pictures added, 1 not added"));
    expect(statusOf(root, "The Hag.png")).toContain("Added");
    expect(statusOf(root, "Dwight Fairfield.png")).toContain("Not added: the image was not accepted. The app shows only 8-bit RGB or RGBA PNG files.");
    expect(statusOf(root, "Ghost Face.png")).toContain("Added");
    const before = writes(backend.requests).length;
    buttonByText(root, "Upload 1 picture").click();
    await until(() => statusOf(root, "Dwight Fairfield.png").includes("Added"));
    expect(writes(backend.requests).slice(before).map((r) => `${r.method} ${r.url}`)).toEqual(["POST /api/pack-files", "POST /api/packs/original/entries"]);
    expect(writes(backend.requests).some((r) => r.url.includes("/publish"))).toBe(false);
  });

  it("replaces a slot's picture when Replace is ticked, through the entry update", async () => {
    const { root, backend } = await openEditor();
    await choose(root, [png("The Trapper.png", 1)]);
    const replace = rowFor(root, "The Trapper.png").querySelector<HTMLInputElement>('input[id^="bulk-replace-"]')!;
    replace.checked = true;
    replace.dispatchEvent(new Event("change"));
    buttonByText(root, "Upload 1 picture").click();
    await until(() => root.textContent!.includes("1 picture added"));
    const sent = writes(backend.requests);
    expect(sent.map((r) => `${r.method} ${r.url}`)).toEqual(["POST /api/pack-files", "PUT /api/packs/original/entries/e000000000000000000000003"]);
    expect(sent[1]!.body).toEqual({ sha256: shaOf(0x40), sourceSha256: shaOf(0x41) });
    expect(statusOf(root, "The Trapper.png")).toContain("Replaced");
  });

  it("stops when the session ends and keeps the rest in the list", async () => {
    const backend = new FakeBackend();
    backend.failOnce("POST /api/pack-files", { status: 401 }, 1);
    const { root } = await openEditor(backend);
    await choose(root, [png("The Hag.png", 1), png("Dwight Fairfield.png", 2), png("Ghost Face.png", 3)]);
    pick(slotOf(root, "Ghost Face.png"), "character:the-lantern");
    buttonByText(root, "Upload 3 pictures").click();
    await until(() => root.textContent!.includes("Your session ended"));
    expect(writes(backend.requests).filter((r) => r.url === "/api/pack-files")).toHaveLength(2);
    expect(statusOf(root, "Ghost Face.png")).toContain("Slot chosen by you");
  });
});

describe("the upload run", () => {
  const pack = (): Pack => samplePacks()[1]!;
  const file = () => new File([pngOfSize(64, 64) as Uint8Array<ArrayBuffer>], "a.png");
  const uploaded = { file: { sha256: shaOf(0x40), sourceSha256: shaOf(0x41), format: "png" as const, size: 26, width: 64, height: 64, url: `https://cdn.arcavale.com/packs/${shaOf(0x40)}.png` }, removed: [] };

  it("waits as long as Retry-After says, then tries again", async () => {
    const waits: number[] = [];
    let calls = 0;
    const value = await withRetry(
      async () => {
        calls++;
        if (calls < 3) throw new ApiError("client", 429, "rate_limited", null, calls === 1 ? 7 : null);
        return "ok";
      },
      async (ms) => void waits.push(ms),
      () => undefined,
    );
    expect(value).toBe("ok");
    expect(waits).toEqual([7_000, BACKOFF_MS[1]]);
  });

  it("gives up after the last retry and passes other errors straight on", async () => {
    let calls = 0;
    const always429 = withRetry(
      async () => {
        calls++;
        throw new ApiError("client", 429, "rate_limited");
      },
      async () => undefined,
      () => undefined,
    );
    await expect(always429).rejects.toMatchObject({ status: 429 });
    expect(calls).toBe(MAX_RETRIES + 1);
    calls = 0;
    await expect(withRetry(async () => (calls++, Promise.reject(new ApiError("server", 500, null))), async () => undefined, () => undefined)).rejects.toMatchObject({ status: 500 });
    expect(calls).toBe(1);
  });

  it("uploads each file, adds it with its licensed holder, and reports 429 waits, without publishing", async () => {
    const calls: string[] = [];
    const waits: number[] = [];
    let first = true;
    const p = pack();
    const api = {
      uploadFile: async () => {
        calls.push("upload");
        if (first) {
          first = false;
          throw new ApiError("client", 429, "rate_limited", null, 2);
        }
        return uploaded;
      },
      addEntry: async (_id: string, input: { key: string; licensedHolder: string | null }) => {
        calls.push(`add ${input.key} ${input.licensedHolder}`);
        return { ...p, entries: [...p.entries, { ...samplePacks()[0]!.entries[0]!, key: input.key }] };
      },
      updateEntry: async () => {
        throw new Error("not expected");
      },
    };
    const outcomes: unknown[] = [];
    const result = await runUploads([{ id: 1, file: file(), key: "character:the-shape", licensedHolder: "Example Studio", replace: false }], {
      api,
      pack: p,
      sleep: async () => undefined,
      shouldStop: () => false,
      onStart: () => undefined,
      onWait: (_id, ms) => void waits.push(ms),
      onDone: (_id, outcome) => void outcomes.push(outcome),
    });
    expect(calls).toEqual(["upload", "upload", "add character:the-shape Example Studio"]);
    expect(waits).toEqual([2_000]);
    expect(outcomes).toEqual([{ ok: true, replaced: false, url: samplePacks()[0]!.entries[0]!.url }]);
    expect(result.stopped).toBeNull();
  });

  it("fails only the file the browser cannot read, goes on with the next, and stops after three such files in a row", async () => {
    const unreadable = (name: string) => {
      const f = new File([new Uint8Array(4)], name);
      Object.defineProperty(f, "arrayBuffer", { value: () => Promise.reject(new DOMException("could not read", "NotReadableError")) });
      return f;
    };
    const added: string[] = [];
    const api = {
      uploadFile: async () => uploaded,
      addEntry: async (_id: string, input: { key: string }) => (added.push(input.key), pack()),
      updateEntry: async () => pack(),
    };
    const outcomes: { id: number; ok: boolean }[] = [];
    const deps = (jobs: number) => ({ api, pack: pack(), sleep: async () => undefined, shouldStop: () => false, onStart: () => undefined, onWait: () => undefined, onDone: (id: number, outcome: { ok: boolean }) => void outcomes.push({ id, ok: outcome.ok }), jobs });
    const first = await runUploads(
      [
        { id: 1, file: unreadable("The Onry\u253c\u00ec.png"), key: "character:the-onryo", licensedHolder: null, replace: false },
        { id: 2, file: file(), key: "character:the-pig", licensedHolder: null, replace: false },
      ],
      deps(2),
    );
    expect(outcomes).toEqual([{ id: 1, ok: false }, { id: 2, ok: true }]);
    expect(added).toEqual(["character:the-pig"]);
    expect(first.stopped).toBeNull();

    outcomes.length = 0;
    const second = await runUploads(
      [1, 2, 3, 4].map((id) => ({ id, file: unreadable(`f${id}.png`), key: `character:k${id}`, licensedHolder: null, replace: false })),
      deps(4),
    );
    expect(outcomes.map((o) => o.id)).toEqual([1, 2, 3]);
    expect(second.stopped).toBe("error");
    expect(MAX_UNEXPECTED_IN_A_ROW).toBe(3);
  });

  it("stops before the next file when asked", async () => {
    let stop = false;
    const started: number[] = [];
    const api = {
      uploadFile: async () => uploaded,
      addEntry: async () => pack(),
      updateEntry: async () => pack(),
    };
    const result = await runUploads(
      [1, 2, 3].map((id) => ({ id, file: file(), key: `character:k${id}`, licensedHolder: null, replace: false })),
      { api, pack: pack(), sleep: async () => undefined, shouldStop: () => stop, onStart: (id) => (started.push(id), (stop = true)), onWait: () => undefined, onDone: () => undefined },
    );
    expect(started).toEqual([1]);
    expect(result.stopped).toBe("user");
  });
});
