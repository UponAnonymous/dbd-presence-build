// "Add pictures from a folder" in the pack editor (FR-ADM-08, FR-PACK-03/04; PLAN_V2_S5B §2.5, D31). The owner chooses a
// folder or several files; each file is matched to a slot by its name (src/matching.ts) and checked like a single upload
// (src/image-check.ts). A review lists what matched, what needs a slot, clashes, files that cannot be used and the slots
// still without a picture. Nothing is sent until the owner presses Upload; then the files go one at a time through the
// same calls as "Add an image" (POST /api/pack-files, then add or replace the entry), waiting and trying again when the
// service answers 429. Nothing is published: the owner still presses Publish.
//
// Pictures come only from files the owner chooses (D31, D35); nothing is fetched. Before upload the review shows each
// file's name, size and pixel size only (ADR-0014: no blob: or data: images); after upload it shows the stored picture
// from this environment's content host, like the rest of the editor.

import type { AdminApi, Pack, PackFields } from "../api.ts";
import { ApiError } from "../api.ts";
import type { Slot, SlotCatalog } from "../catalog.ts";
import type { ViewContext } from "../context.ts";
import { el, packImage, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { count, formatBytes, plural } from "../format.ts";
import { type CheckedImage, MAX_FILE, TARGET_FILE, checkImage, imageReasonText } from "../image-check.ts";
import { convertPngForApp } from "../png-convert.ts";
import { FileMatcher, type FileMatch } from "../matching.ts";
import { button, cell, field, notice, setBusy, table, type Tone } from "../ui.ts";

/** The most files one review takes (the app's import takes 400 pictures, PLAN_V2_S5B §2.7). */
export const MAX_BATCH = 400;
/** Tries after a 429 before a file is marked as failed. */
export const MAX_RETRIES = 4;
/** Waits after a 429 that names no Retry-After, in milliseconds. */
export const BACKOFF_MS: readonly number[] = [2_000, 4_000, 8_000, 16_000];
/** The longest wait the panel accepts from Retry-After. */
export const MAX_WAIT_MS = 60_000;
const HOLDER_MAX = 120;
const CONTROL = /[\p{Cc}\p{Cf}]/u;

// ---------------------------------------------------------------------------------------------------------------
// The upload run (pure apart from the API calls; tested on its own)

export interface UploadJob {
  id: number;
  file: File;
  key: string;
  licensedHolder: string | null;
  /** Replace the pack's current picture for the key; without it an existing key is refused. */
  replace: boolean;
}

export type JobOutcome = { ok: true; replaced: boolean; url: string } | { ok: false; error: unknown };

export interface RunDeps {
  api: Pick<AdminApi, "uploadFile" | "addEntry" | "updateEntry">;
  /** The pack as it is when the run starts. */
  pack: Pack;
  sleep: (ms: number) => Promise<void>;
  shouldStop: () => boolean;
  onStart: (id: number) => void;
  /** Called before each wait after a 429. */
  onWait: (id: number, ms: number) => void;
  /** `pack` is the pack the service returned after a successful file, else null. */
  onDone: (id: number, outcome: JobOutcome, pack: Pack | null) => void;
}

export interface RunResult {
  /** The pack as the service last returned it. */
  pack: Pack;
  /** Why the run ended early: the owner pressed Stop, the session ended, the connection dropped, or a panel error. */
  stopped: "user" | "session" | "offline" | "error" | null;
}

/** Unexpected (non-service) errors in a row after which the run stops instead of failing file after file. */
export const MAX_UNEXPECTED_IN_A_ROW = 3;

/** Runs one call, waiting and trying again on 429 (Retry-After when given, else 2, 4, 8 and 16 seconds). */
export async function withRetry<T>(call: () => Promise<T>, sleep: (ms: number) => Promise<void>, onWait: (ms: number) => void): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    try {
      return await call();
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 429 || attempt >= MAX_RETRIES) throw error;
      const wait = error.retryAfterSeconds !== null ? Math.min(error.retryAfterSeconds * 1000, MAX_WAIT_MS) : BACKOFF_MS[attempt]!;
      onWait(wait);
      await sleep(wait);
    }
  }
}

/** Uploads the files one after another and adds each to the pack (or replaces the slot's picture). Never publishes. */
export async function runUploads(jobs: readonly UploadJob[], deps: RunDeps): Promise<RunResult> {
  let pack = deps.pack;
  // An error that is not the service's answer (for example the browser could not read a file whose name Windows garbled
  // when it unzipped it, owner report 2026-11-02) fails that file only; the run goes on, and stops only after this many in a row.
  let unexpectedInARow = 0;
  for (const job of jobs) {
    if (deps.shouldStop()) return { pack, stopped: "user" };
    deps.onStart(job.id);
    const retry = <T>(call: () => Promise<T>) => withRetry(call, deps.sleep, (ms) => deps.onWait(job.id, ms));
    try {
      let bytes = new Uint8Array(await job.file.arrayBuffer());
      // A palette, greyscale, 16-bit or colour-key PNG is converted to 8-bit RGBA here, so the owner never has to re-export it.
      const conversion = await convertPngForApp(bytes);
      if (conversion.kind === "failed") throw new ApiError("client", null, "image_rejected", conversion.reason);
      if (conversion.kind === "converted") bytes = conversion.bytes;
      const checked = checkImage(bytes);
      if (!checked.ok) throw new ApiError("client", null, "image_rejected", checked.reason);
      const uploaded = await retry(() => deps.api.uploadFile(bytes, checked.image.type));
      const existing = pack.entries.find((e) => e.key === job.key);
      if (existing && !job.replace) throw new ApiError("client", 409, "key_exists");
      const packId = pack.id;
      if (existing) {
        const patch: { sha256: string; sourceSha256: string; licensedHolder?: string | null } = { sha256: uploaded.file.sha256, sourceSha256: uploaded.file.sourceSha256 };
        if (existing.licensedHolder !== job.licensedHolder) patch.licensedHolder = job.licensedHolder;
        pack = await retry(() => deps.api.updateEntry(packId, existing.id, patch));
      } else {
        pack = await retry(() => deps.api.addEntry(packId, { key: job.key, sha256: uploaded.file.sha256, sourceSha256: uploaded.file.sourceSha256, licensedHolder: job.licensedHolder }));
      }
      const entry = pack.entries.find((e) => e.key === job.key);
      deps.onDone(job.id, { ok: true, replaced: existing !== undefined, url: entry?.url ?? uploaded.file.url }, pack);
      unexpectedInARow = 0;
    } catch (error) {
      deps.onDone(job.id, { ok: false, error }, null);
      if (!(error instanceof ApiError)) {
        unexpectedInARow++;
        if (unexpectedInARow >= MAX_UNEXPECTED_IN_A_ROW) return { pack, stopped: "error" };
        continue;
      }
      unexpectedInARow = 0;
      if (error.kind === "session" || error.kind === "offline") return { pack, stopped: error.kind };
    }
  }
  return { pack, stopped: null };
}

// ---------------------------------------------------------------------------------------------------------------
// The review

type RowResult = { state: "uploading"; waitMs: number | null } | { state: "done"; replaced: boolean; url: string } | { state: "failed"; title: string; detail: string };

export interface BulkRow {
  id: number;
  file: File;
  /** The path inside the chosen folder, or the file name. */
  path: string;
  check: { ok: true; image: CheckedImage } | { ok: false; reason: string };
  /** The first file of this review with the same bytes. */
  duplicateOf: string | null;
  match: FileMatch;
  /** The file that matched this row's slot first, when this one matched it too. */
  sameAs: string | null;
  /** The chosen slot; "" skips the file. */
  key: string;
  replace: boolean;
  licensed: boolean;
  holder: string;
  result: RowResult | null;
}

export interface BulkState {
  packId: string;
  rows: BulkRow[];
  /** The answer to "Is any of these pictures Behaviour Interactive artwork?", when the pack is not marked yet. */
  behaviour: "yes" | "no" | null;
  phase: "review" | "running" | "finished";
  stop: boolean;
  /** The current or last run: files sent, files not added, files in the run. */
  progress: { finished: number; failed: number; total: number };
  summary: { tone: Tone; title: string; text: string } | null;
  /** Refreshes the card currently on screen, if any. */
  view: (() => void) | null;
}

function relativePath(file: File): string {
  const path = (file as File & { webkitRelativePath?: string }).webkitRelativePath;
  return path && path.length > 0 ? path : file.name;
}

async function digest(bytes: Uint8Array<ArrayBuffer>): Promise<string | null> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) return null;
  const hash = new Uint8Array(await subtle.digest("SHA-256", bytes));
  return [...hash].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Reads, checks and matches the chosen files. Each file's bytes are read once here and again when it is uploaded. */
export async function prepareRows(files: readonly File[], catalog: SlotCatalog, pack: Pack): Promise<BulkRow[]> {
  const matcher = new FileMatcher(catalog);
  const firstByHash = new Map<string, string>();
  const firstByKey = new Map<string, string>();
  const rows: BulkRow[] = [];
  for (const [id, file] of files.entries()) {
    const path = relativePath(file);
    let check: BulkRow["check"];
    let hash: string | null = null;
    if (file.size === 0) check = { ok: false, reason: "empty" };
    else if (file.size > MAX_FILE) check = { ok: false, reason: "too_large" };
    else {
      const bytes = new Uint8Array(await file.arrayBuffer());
      check = checkImage(bytes);
      if (check.ok) hash = await digest(bytes);
    }
    const duplicateOf = hash === null ? null : (firstByHash.get(hash) ?? null);
    if (hash !== null && duplicateOf === null) firstByHash.set(hash, path);
    const match: FileMatch = check.ok ? matcher.match(file.name) : { status: "unmatched" };
    let key = "";
    let sameAs: string | null = null;
    if (check.ok && duplicateOf === null && match.status === "matched") {
      sameAs = firstByKey.get(match.key) ?? null;
      if (sameAs === null) {
        key = match.key;
        firstByKey.set(key, path);
      }
    }
    const row: BulkRow = { id, file, path, check, duplicateOf, match, sameAs, key, replace: false, licensed: false, holder: "", result: null };
    applyLicenceDefault(row, catalog, pack);
    rows.push(row);
  }
  return rows;
}

/** Licensed defaults for the chosen slot: the pack's current picture, else the roster's licensed holder. */
function applyLicenceDefault(row: BulkRow, catalog: SlotCatalog, pack: Pack): void {
  const existing = pack.entries.find((e) => e.key === row.key);
  const holder = existing ? existing.licensedHolder : (catalog.find(row.key)?.licensedHolder ?? null);
  row.licensed = holder !== null;
  row.holder = holder ?? "";
}

type Issue = "refused" | "done" | "skip" | "clash" | "exists" | "holder" | "ready";

function holderProblem(row: BulkRow): string | null {
  if (!row.licensed) return null;
  const value = row.holder.trim();
  if (value === "") return "Enter the rights holder, or untick Licensed.";
  if (value.length > HOLDER_MAX) return `Shorten the rights holder to ${HOLDER_MAX} characters or fewer.`;
  if (CONTROL.test(value)) return "Remove the invisible control characters from the rights holder.";
  return null;
}

function issueOf(row: BulkRow, state: BulkState, pack: Pack): Issue {
  if (!row.check.ok) return "refused";
  if (row.result?.state === "done") return "done";
  if (row.key === "") return "skip";
  if (state.rows.some((r) => r !== row && r.key === row.key && r.check.ok && r.result?.state !== "done")) return "clash";
  if (pack.entries.some((e) => e.key === row.key) && !row.replace) return "exists";
  if (holderProblem(row) !== null) return "holder";
  return "ready";
}

const BASIS: Readonly<Record<string, string>> = { code: "Matched by code", name: "Matched by name", similar: "Matched by a similar name", partial: "Matched by part of the name" };

function statusText(row: BulkRow, issue: Issue, pack: Pack, catalog: SlotCatalog): { tone: Tone; text: string } {
  const r = row.result;
  if (r?.state === "done") return { tone: "success", text: r.replaced ? "Replaced. Not published yet." : "Added. Not published yet." };
  if (r?.state === "uploading") return { tone: "info", text: r.waitMs !== null ? `The service asked the panel to slow down. Trying again in ${count(Math.round(r.waitMs / 1000))} s…` : "Uploading…" };
  const failed = r?.state === "failed" ? `Not added: ${r.title}. ${r.detail} ` : "";
  const tone: Tone = r?.state === "failed" ? "danger" : "neutral";
  switch (issue) {
    case "refused":
      return { tone: "danger", text: imageReasonText(row.check.ok ? null : row.check.reason) };
    case "skip": {
      if (row.duplicateOf !== null) return { tone: "neutral", text: `Same picture as ${row.duplicateOf}. Skipped.` };
      if (row.sameAs !== null && row.match.status === "matched") return { tone: "warning", text: `${row.sameAs} matched ${catalog.label(row.match.key)} first. Choose another slot, or leave this file skipped.` };
      if (row.match.status === "conflict") return { tone: "warning", text: `The code says ${catalog.label(row.match.key)} but the name says ${catalog.label(row.match.other)}. Choose the slot.` };
      if (row.match.status === "ambiguous") return { tone: "warning", text: `The name fits more than one slot (${row.match.keys.map((k) => catalog.label(k)).join(", ")}). Choose the slot.` };
      if (row.match.status === "unmatched") return { tone: "warning", text: "No slot matched the name. Choose the slot, or leave the file skipped." };
      return { tone: "neutral", text: "Skipped." };
    }
    case "clash":
      return { tone: "warning", text: `${failed}Another file is also set to ${catalog.label(row.key)}. Choose another slot for one of them, or skip one.` };
    case "exists":
      return { tone: "warning", text: `${failed}This pack already has a picture for ${catalog.label(row.key)}. Tick Replace to use this file instead.` };
    case "holder":
      return { tone: "warning", text: `${failed}${holderProblem(row)}` };
    case "done":
    case "ready": {
      const how = row.match.status === "matched" && row.match.key === row.key ? BASIS[row.match.basis]! : "Slot chosen by you";
      const replaces = pack.entries.some((e) => e.key === row.key) ? " Replaces the current picture." : "";
      return { tone, text: `${failed}${failed ? "Press Upload to try it again. " : ""}${how}.${replaces}` };
    }
  }
  return { tone: "neutral", text: "" };
}

export interface BulkHost {
  ctx: ViewContext;
  catalog: SlotCatalog;
  maxPackBytes: number;
  /** The pack as the editor has it now. */
  pack: () => Pack;
  /** Keeps the pack the service returned. */
  packUpdated: (pack: Pack) => void;
  /** Redraws the editor; the review survives in the state. */
  redraw: () => void;
  state: () => BulkState | null;
  setState: (state: BulkState | null) => void;
  /** Waits; a test can make it immediate. */
  sleep: (ms: number) => Promise<void>;
}

export const NAMING_HINT =
  "Name each file after the character or slot, for example “Ace Visconti.png”, “ace_visconti.jpg”, “The Trapper.png” or “Trapper.png”. " +
  "Capital letters, spaces, punctuation and accents do not matter, and a killer can be named with or without “The”. " +
  "Files named like the game's own (“K27_TheOnryo_Portrait.png”) match by their code. Role icons and tiles match their slot name, such as “Killer icon.png”. " +
  "You check every match before anything is uploaded.";

/** The "Add pictures from a folder" card. */
export function bulkCard(host: BulkHost, pack: Pack): HTMLElement {
  const { catalog } = host;
  const folderInput = el("input", { className: "input input-file", attrs: { type: "file", multiple: true, webkitdirectory: true } });
  const filesInput = el("input", { className: "input input-file", attrs: { type: "file", multiple: true, accept: "image/png,image/jpeg" } });
  const folderField = field("bulk-folder", "Choose a folder", folderInput, "Every file in the folder and its subfolders is read.");
  const filesField = field("bulk-files", "Or choose files", filesInput, "Select many files at once with Ctrl or Shift.");
  const reading = el("p", { className: "field-hint bulk-reading", attrs: { role: "status" } });
  const review = el("div", { className: "stack" });

  const chosen = async (input: HTMLInputElement, errorSlot: HTMLElement): Promise<void> => {
    const files = [...(input.files ?? [])];
    input.value = "";
    errorSlot.textContent = "";
    if (files.length === 0) return;
    if (host.state()?.phase === "running") {
      errorSlot.textContent = "Wait until the upload has finished, or press Stop.";
      return;
    }
    if (files.length > MAX_BATCH) {
      errorSlot.textContent = `That is ${count(files.length)} files. Choose at most ${count(MAX_BATCH)} at a time, for example one folder for killers and one for survivors.`;
      return;
    }
    reading.textContent = `Reading ${plural(files.length, "file", "files")}…`;
    let rows: BulkRow[];
    try {
      rows = await prepareRows(files, catalog, host.pack());
    } catch (error) {
      // A file that went away or cannot be read after it was chosen: say so; nothing was sent.
      reading.textContent = "";
      errorSlot.textContent = `A file could not be read (${error instanceof Error ? error.name : "unknown error"}). Check the folder and choose it again.`;
      return;
    }
    reading.textContent = "";
    host.setState({ packId: pack.id, rows, behaviour: null, phase: "review", stop: false, progress: { finished: 0, failed: 0, total: 0 }, summary: null, view: null });
    drawReview();
    review.querySelector<HTMLElement>("#bulk-review-title")?.focus();
  };
  folderInput.addEventListener("change", () => void chosen(folderInput, folderField.error));
  filesInput.addEventListener("change", () => void chosen(filesInput, filesField.error));

  function drawReview(): void {
    const state = host.state();
    if (!state || state.packId !== pack.id) {
      replaceChildren(review);
      return;
    }
    replaceChildren(review, ...reviewParts(host, state));
  }
  drawReview();

  return el(
    "section",
    { className: "card", attrs: { "aria-labelledby": "bulk-title" } },
    el("h2", { className: "card-title", text: "Add pictures from a folder", attrs: { id: "bulk-title" } }),
    el("p", { className: "muted", text: NAMING_HINT }),
    el("div", { className: "form-two" }, folderField.root, filesField.root),
    reading,
    review,
  );
}

interface RowView {
  row: BulkRow;
  select: HTMLSelectElement;
  replaceRoot: HTMLElement;
  replace: HTMLInputElement;
  licensed: HTMLInputElement;
  holder: HTMLInputElement;
  holderRoot: HTMLElement;
  status: HTMLElement;
  picture: HTMLElement;
}

function slotSelect(catalog: SlotCatalog, pack: Pack, row: BulkRow, label: string): HTMLSelectElement {
  const inPack = new Set(pack.entries.map((e) => e.key));
  const option = (s: Slot) => el("option", { text: inPack.has(s.key) ? `${s.label} (in this pack)` : s.label, attrs: { value: s.key } });
  const select = el(
    "select",
    { className: "input select", attrs: { "aria-label": label } },
    el("option", { text: "Skip this file", attrs: { value: "" } }),
    ...catalog.groups.filter((g) => g.slots.length > 0).map((g) => el("optgroup", { attrs: { label: g.label } }, ...g.slots.map(option))),
  );
  // A key no group lists (a withdrawn character the pack still has) stays selectable for this row.
  if (row.key !== "" && !catalog.groups.some((g) => g.slots.some((s) => s.key === row.key))) select.appendChild(el("option", { text: catalog.label(row.key), attrs: { value: row.key } }));
  select.value = row.key;
  return select;
}

function fileFacts(row: BulkRow): string {
  if (!row.check.ok) return formatBytes(row.file.size);
  const { type, width, height } = row.check.image;
  const size = width !== null && height !== null ? ` · ${width} × ${height}` : "";
  return `${type === "image/png" ? "PNG" : "JPEG"}${size} · ${formatBytes(row.file.size)}${row.file.size > TARGET_FILE ? " · over the 60 KB target" : ""}`;
}

function reviewParts(host: BulkHost, state: BulkState): HTMLElement[] {
  const { ctx, catalog } = host;
  const pack = host.pack();
  const usable = state.rows.filter((r) => r.check.ok);
  const refused = state.rows.filter((r) => !r.check.ok);
  const views: RowView[] = [];

  const summary = el("p", { className: "muted" });
  const missingBox = el("div", {});
  const sizeNote = el("div", {});
  const uploadButton = button("Upload", { variant: "primary", icon: "upload", onClick: () => void start(false) });
  const retryButton = button("Retry the failed files", { onClick: () => void start(true) });
  const stopButton = button("Stop after this picture", { variant: "danger", onClick: () => ((state.stop = true), setBusy(stopButton, true, "Stopping…")) });
  const clearButton = button("Clear the list", { onClick: () => (host.setState(null), host.redraw(), ctx.doc.querySelector<HTMLElement>("#bulk-title")?.focus()) });
  const actionError = el("p", { className: "field-error", attrs: { id: "bulk-upload-error", "aria-live": "polite" } });
  const progress = el("progress", { className: "bulk-progress", attrs: { max: 1, value: 0, "aria-label": "Upload progress" } });
  const progressText = el("p", { className: "field-hint", attrs: { role: "status" } });
  const progressBox = el("div", { className: "stack bulk-progress-box" }, progress, progressText);

  // The Behaviour Interactive question, asked once for the whole set (FR-PACK-03, D9).
  let behaviourBox: HTMLElement;
  const behaviourError = el("p", { className: "field-error", attrs: { id: "bulk-behaviour-error", "aria-live": "polite" } });
  const radios: HTMLInputElement[] = [];
  if (pack.behaviourArt) {
    behaviourBox = el("p", { className: "muted", text: "This pack is marked as containing Behaviour Interactive artwork, so apps show “Unofficial · © Behaviour Interactive” with these pictures too." });
  } else {
    const radio = (value: "yes" | "no", label: string) => {
      const input = el("input", { className: "checkbox", attrs: { type: "radio", name: "bulk-behaviour", id: `bulk-behaviour-${value}`, value, checked: state.behaviour === value, "aria-describedby": "bulk-behaviour-hint bulk-behaviour-error" } });
      input.addEventListener("change", () => {
        state.behaviour = value;
        behaviourError.textContent = "";
      });
      radios.push(input);
      return el("div", { className: "check-field" }, input, el("label", { className: "field-label", text: label, attrs: { for: input.id } }));
    };
    behaviourBox = el(
      "fieldset",
      { className: "subfields" },
      el("legend", { className: "subfields-legend", text: "Is any of these pictures Behaviour Interactive artwork?" }),
      el("p", { className: "field-hint", attrs: { id: "bulk-behaviour-hint" }, text: "For example pictures drawn from the game's own portraits. If so, the pack is marked before the upload, and apps show “Unofficial · © Behaviour Interactive” with its pictures (ADR-0013)." }),
      radio("yes", "Yes: mark this pack as containing Behaviour Interactive artwork"),
      radio("no", "No: none of them is Behaviour Interactive artwork"),
      behaviourError,
    );
  }

  const rowsTable = usable.length === 0 ? el("p", { className: "muted", text: "None of the chosen files can be used." }) : table("Pictures to upload", ["File", "Slot", "Licensed third-party content", "Status"], usable.map((row) => rowElement(row)), "table-bulk");

  function rowElement(row: BulkRow): HTMLTableRowElement {
    const select = slotSelect(catalog, pack, row, `Slot for ${row.path}`);
    const replace = el("input", { className: "checkbox", attrs: { type: "checkbox", id: `bulk-replace-${row.id}`, checked: row.replace } });
    const replaceRoot = el("div", { className: "check-field" }, replace, el("label", { className: "field-label", text: "Replace", attrs: { for: replace.id } }));
    const licensed = el("input", { className: "checkbox", attrs: { type: "checkbox", id: `bulk-licensed-${row.id}`, checked: row.licensed, "aria-label": `Licensed third-party content: ${row.path}` } });
    const holder = el("input", { className: "input", attrs: { type: "text", maxlength: HOLDER_MAX, autocomplete: "off", value: row.holder, "aria-label": `Rights holder for ${row.path}` } });
    const holderRoot = el("div", {}, holder);
    const status = el("p", { className: "entry-status bulk-status" });
    const picture = el("div", { className: "bulk-picture" });
    const view: RowView = { row, select, replaceRoot, replace, licensed, holder, holderRoot, status, picture };
    views.push(view);
    select.addEventListener("change", () => {
      row.key = select.value;
      row.replace = false;
      replace.checked = false;
      applyLicenceDefault(row, catalog, host.pack());
      licensed.checked = row.licensed;
      holder.value = row.holder;
      refresh();
    });
    replace.addEventListener("change", () => ((row.replace = replace.checked), refresh()));
    licensed.addEventListener("change", () => ((row.licensed = licensed.checked), refresh()));
    holder.addEventListener("input", () => ((row.holder = holder.value), refresh()));
    return el(
      "tr",
      { attrs: { "data-row": row.id } },
      cell("File", el("div", { className: "stacked" }, picture, el("span", { className: "stacked-primary", text: row.path }), el("span", { className: "stacked-secondary", text: fileFacts(row) }))),
      cell("Slot", el("div", { className: "stack bulk-slot" }, select, replaceRoot)),
      cell("Licensed third-party content", el("div", { className: "stack bulk-slot" }, licensed, holderRoot)),
      cell("Status", status),
    );
  }

  function readyRows(onlyFailed: boolean): BulkRow[] {
    const current = host.pack();
    return state.rows.filter((r) => issueOf(r, state, current) === "ready" && (!onlyFailed || r.result?.state === "failed"));
  }

  function refresh(): void {
    const current = host.pack();
    const nowRunning = state.phase === "running";
    for (const v of views) {
      const issue = issueOf(v.row, state, current);
      const done = v.row.result?.state === "done";
      const status = statusText(v.row, issue, current, catalog);
      v.status.textContent = status.text;
      v.status.className = `entry-status bulk-status bulk-status-${status.tone}`;
      v.replaceRoot.hidden = v.row.key === "" || done || !current.entries.some((e) => e.key === v.row.key);
      v.holderRoot.hidden = !v.row.licensed;
      for (const control of [v.select, v.replace, v.licensed, v.holder]) control.disabled = nowRunning || done;
      const result = v.row.result;
      if (result?.state === "done" && v.picture.childElementCount === 0) v.picture.appendChild(el("div", { className: "thumb" }, packImage(result.url, ctx.environment, catalog.label(v.row.key), 48)));
    }
    const counts = { ready: 0, done: 0, skip: 0, attention: 0 };
    for (const row of usable) {
      const issue = issueOf(row, state, current);
      if (issue === "ready") counts.ready++;
      else if (issue === "done") counts.done++;
      else if (issue === "skip" && (row.duplicateOf !== null || row.match.status === "matched")) counts.skip++;
      else counts.attention++;
    }
    summary.textContent = [
      `${plural(state.rows.length, "file", "files")}:`,
      `${count(counts.ready)} ready to upload`,
      counts.done ? `${count(counts.done)} added` : null,
      counts.attention ? `${count(counts.attention)} need your choice` : null,
      counts.skip ? `${count(counts.skip)} skipped` : null,
      refused.length ? `${count(refused.length)} cannot be used` : null,
    ]
      .filter(Boolean)
      .join(" · ");
    const ready = readyRows(false).length;
    const failed = readyRows(true).length;
    uploadButton.querySelector(".btn-label")!.textContent = ready === 1 ? "Upload 1 picture" : `Upload ${count(ready)} pictures`;
    uploadButton.hidden = nowRunning;
    retryButton.hidden = nowRunning || failed === 0 || failed === ready;
    retryButton.querySelector(".btn-label")!.textContent = failed === 1 ? "Retry the 1 that failed" : `Retry the ${count(failed)} that failed`;
    stopButton.hidden = !nowRunning;
    clearButton.hidden = nowRunning;
    for (const r of radios) r.disabled = nowRunning;
    progressBox.hidden = state.phase === "review";
    const { finished, failed: notAdded, total } = state.progress;
    progress.max = Math.max(total, 1);
    progress.value = finished;
    progressText.textContent = `Uploaded ${count(finished - notAdded)} of ${count(total)}${notAdded ? ` · ${count(notAdded)} not added` : ""}`;
    drawMissing(current);
    drawSize(current);
  }

  function drawMissing(current: Pack): void {
    const filled = new Set([...current.entries.map((e) => e.key), ...state.rows.filter((r) => r.key !== "" && r.check.ok).map((r) => r.key)]);
    const groups = catalog.groups.map((g) => ({ label: g.label, missing: g.slots.filter((s) => !filled.has(s.key)) })).filter((g) => g.missing.length > 0);
    const total = groups.reduce((n, g) => n + g.missing.length, 0);
    if (total === 0) {
      replaceChildren(missingBox, el("p", { className: "muted", text: "Every slot will have a picture." }));
      return;
    }
    replaceChildren(
      missingBox,
      el(
        "details",
        { className: "bulk-missing" },
        el("summary", { text: `Slots still without a picture after this upload: ${count(total)}` }),
        ...groups.map((g) => el("p", { className: "muted" }, el("strong", { text: `${g.label} (${count(g.missing.length)}): ` }), g.missing.map((s) => s.label).join(", "))),
      ),
    );
  }

  function drawSize(current: Pack): void {
    const adding = state.rows.filter((r) => issueOf(r, state, current) === "ready").reduce((n, r) => n + r.file.size, 0);
    replaceChildren(
      sizeNote,
      current.totalBytes + adding > host.maxPackBytes
        ? notice("warning", "These files may not all fit in the pack", `The pack holds ${formatBytes(current.totalBytes)} and these files add up to ${formatBytes(adding)}; a pack can hold ${formatBytes(host.maxPackBytes)}. Files past the limit are refused. Export them smaller (aim for under 60 KB each).`)
        : null,
    );
  }

  async function start(onlyFailed: boolean): Promise<void> {
    actionError.textContent = "";
    if (state.phase === "running") return;
    const current = host.pack();
    if (!current.behaviourArt && state.behaviour === null) {
      behaviourError.textContent = "Answer this before uploading.";
      radios[0]?.focus();
      return;
    }
    const rows = readyRows(onlyFailed);
    if (rows.length === 0) {
      actionError.textContent = "Nothing to upload yet. Choose a slot for at least one file.";
      return;
    }
    state.phase = "running";
    state.stop = false;
    state.summary = null;
    state.progress = { finished: 0, failed: 0, total: rows.length };
    ctx.setLeaveGuard(true);
    setBusy(stopButton, false);
    refresh();
    let result: RunResult;
    try {
      let startPack = current;
      if (!current.behaviourArt && state.behaviour === "yes") {
        startPack = await ctx.api.updatePack(current.id, fieldsOf(current, true));
        host.packUpdated(startPack);
      }
      const byId = new Map(rows.map((r) => [r.id, r]));
      result = await runUploads(
        rows.map((r) => ({ id: r.id, file: r.file, key: r.key, licensedHolder: r.licensed ? r.holder.trim() : null, replace: r.replace })),
        {
          api: ctx.api,
          pack: startPack,
          sleep: host.sleep,
          shouldStop: () => state.stop,
          onStart: (id) => {
            byId.get(id)!.result = { state: "uploading", waitMs: null };
            state.view?.();
          },
          onWait: (id, ms) => {
            byId.get(id)!.result = { state: "uploading", waitMs: ms };
            state.view?.();
          },
          onDone: (id, outcome, updated) => {
            const row = byId.get(id)!;
            state.progress.finished++;
            if (outcome.ok) row.result = { state: "done", replaced: outcome.replaced, url: outcome.url };
            else {
              state.progress.failed++;
              ctx.reportError(outcome.error);
              row.result = { state: "failed", ...failureCopy(outcome.error) };
            }
            // The editor keeps the newest pack, so a redraw while the run goes on shows what is stored.
            if (updated) host.packUpdated(updated);
            state.view?.();
          },
        },
      );
    } catch (error) {
      // Only the Behaviour flag can fail here (runUploads reports each file itself): nothing was uploaded.
      ctx.reportError(error);
      state.phase = "review";
      ctx.setLeaveGuard(false);
      const copy = failureCopy(error);
      state.summary = { tone: "danger", title: `Nothing was uploaded: the pack could not be marked as Behaviour Interactive artwork. ${copy.title}`, text: copy.detail };
      host.redraw();
      return;
    }
    for (const r of rows) if (r.result?.state === "uploading") r.result = null;
    state.phase = "finished";
    ctx.setLeaveGuard(false);
    const added = rows.filter((r) => r.result?.state === "done").length;
    const notAdded = rows.filter((r) => r.result?.state === "failed").length;
    const left = rows.length - added - notAdded;
    const publishHint = "Nothing is published yet. Check the images above, then press Publish (or Publish changes) when the pack looks right.";
    const ended =
      result.stopped === "user"
        ? `Stopped. ${plural(left, "file was", "files were")} not sent; they are still in the list.`
        : result.stopped === "session"
          ? "Your session ended, so the upload stopped. Sign in again, then press Upload to send the rest."
          : result.stopped === "offline"
            ? "The connection dropped, so the upload stopped. When you are back online, press Upload to send the rest."
            : result.stopped === "error"
              ? "The panel hit an error, so the upload stopped. Reload the page and try again."
              : null;
    state.summary = {
      tone: notAdded > 0 || result.stopped !== null ? "warning" : "success",
      title: `${plural(added, "picture", "pictures")} added${notAdded ? `, ${count(notAdded)} not added` : ""}`,
      text: [ended, notAdded ? "Each file that was not added says why in the list; fix it and press Retry, or Upload again." : null, publishHint].filter(Boolean).join(" "),
    };
    host.packUpdated(result.pack);
    ctx.announce(`${plural(added, "picture", "pictures")} added. Nothing is published yet.`);
    host.redraw();
    ctx.doc.querySelector<HTMLElement>("#bulk-review-title")?.focus();
  }

  state.view = refresh;
  const head = el("h3", { className: "card-subtitle", text: "Check the list before uploading", attrs: { id: "bulk-review-title", tabindex: -1 } });
  const parts: HTMLElement[] = [
    head,
    state.summary ? notice(state.summary.tone, state.summary.title, state.summary.text, { live: true }) : el("div", {}),
    summary,
    rowsTable,
  ];
  if (refused.length > 0) {
    parts.push(
      el("h3", { className: "card-subtitle", text: `Files that cannot be used (${count(refused.length)})` }),
      el("ul", { className: "bulk-refused" }, ...refused.map((r) => el("li", {}, el("strong", { text: `${r.path}: ` }), imageReasonText(r.check.ok ? null : r.check.reason)))),
    );
  }
  parts.push(missingBox, sizeNote, behaviourBox, progressBox, el("div", { className: "form-actions" }, clearButton, retryButton, stopButton, uploadButton), actionError);
  refresh();
  return parts;
}

function fieldsOf(pack: Pack, behaviourArt: boolean): PackFields {
  return { name: pack.name, author: pack.author, licenceNote: pack.licenceNote, credit: pack.credit, fallback: pack.fallback, behaviourArt, thirdParty: pack.thirdParty, permission: pack.permission };
}

function failureCopy(error: unknown): { title: string; detail: string } {
  if (error instanceof ApiError && error.code === "image_rejected") return { title: "the image was not accepted", detail: imageReasonText(error.reason) };
  if (!(error instanceof ApiError)) {
    const what = error instanceof Error && error.name ? ` (${error.name})` : "";
    return {
      title: "this file could not be read",
      detail: `The browser could not read the file${what}. This often happens when a name with letters such as ō or É was garbled while unzipping. Rename the file (for example "The Onryo.png"), choose it again and press Upload.`,
    };
  }
  const copy = describeError(error);
  return { title: copy.title, detail: copy.detail };
}
