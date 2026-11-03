// Uploads (FR-ADM-09, PLAN_V2 §3.5, §7; ADR-0015; owner decision D18): the images users uploaded for their own Discord
// status, newest first, with a thumbnail from the content host (only an address that passes `isUploadImageUrl` is ever
// loaded), the uploader's name as text, size and date; take one down; block or allow new uploads for one user or for
// everyone; the per-person limits (D28: 60 MB and a 300-picture backstop by default); and how much of the storage cap is
// used, with the warning and upload stop. The caps come from the service (GET /api/storage), because they differ per
// environment: 8 GB with the warning at 6 GB in production, 750 MB with the warning at 500 MB in staging.
// Uploads are unlisted, not private: anyone who has the address can open it. A takedown stops the file being served at
// once, but Discord's media proxy may keep showing a cached copy for a while.

import type { SettingsPatch, StorageUsage, UploadItem, UploadList } from "../api.ts";
import { isUploadImageUrl } from "../cdn.ts";
import type { View, ViewContext } from "../context.ts";
import { el, replaceChildren, uploadImage } from "../dom.ts";
import { describeError } from "../errors.ts";
import { count, date, plural, shortId } from "../format.ts";
import { badge, button, card, cell, emptyState, errorState, field, loading, mono, notice, pageHeader, setBusy, stacked, table } from "../ui.ts";
import { formatBytes } from "./packs.ts";

const COLUMNS = ["Image", "Uploaded by", "File", "Uploaded", "Actions"] as const;
/** The most the owner may set per person (service/src/db/settings.ts): 120 MB (MB = 1 048 576 bytes, as in the 2 MB rule) and 1 000 pictures. */
export const UPLOAD_LIMIT_MB_MAX = 120;
export const UPLOAD_LIMIT_FILES_MAX = 1000;
const MB = 1024 * 1024;

/** Storage is counted in decimal units, like the cap (8 GB = 8 000 000 000 bytes). */
export function gigabytes(bytes: number): string {
  return `${(bytes / 1_000_000_000).toFixed(2)} GB`;
}

/** A storage limit in words: whole gigabytes as "8 GB", limits below 1 GB in megabytes ("750 MB"), others to two places. */
export function limitSize(bytes: number): string {
  if (bytes >= 1_000_000_000) return Number.isInteger(bytes / 1_000_000_000) ? `${bytes / 1_000_000_000} GB` : gigabytes(bytes);
  return `${Math.round(bytes / 1_000_000)} MB`;
}

export function storageCard(usage: StorageUsage): HTMLElement {
  const meter = el("meter", {
    className: "storage-meter",
    attrs: { min: 0, max: usage.capBytes, low: usage.warnBytes, high: usage.warnBytes, optimum: 0, value: usage.usedBytes, "aria-label": "Storage used of the cap", id: "storage-meter" },
  });
  const line = `${gigabytes(usage.usedBytes)} of ${gigabytes(usage.capBytes)} used (warning and no new uploads from ${gigabytes(usage.warnBytes)}).`;
  const areas = el(
    "ul",
    { className: "storage-areas" },
    el("li", { text: `Pack files: ${formatBytes(usage.areas.packs.bytes)} in ${plural(usage.areas.packs.objects, "file", "files")}` }),
    el("li", { text: `User uploads: ${formatBytes(usage.areas.uploads.bytes)} in ${plural(usage.areas.uploads.objects, "file", "files")}` }),
    el("li", { text: `Releases: ${formatBytes(usage.areas.releases.bytes)} in ${plural(usage.areas.releases.objects, "file", "files")}` }),
    el("li", { text: `Audit exports: ${formatBytes(usage.areas.audit.bytes)} in ${plural(usage.areas.audit.objects, "file", "files")}` }),
  );
  const state = usage.full
    ? notice("danger", "Storage is full", "The service refuses every new file (uploads, pack images, audit exports) until space is freed. Take down uploads or delete unused pack files.", { live: true })
    : usage.uploadsFull
      ? notice("warning", `New uploads are refused: storage is at ${limitSize(usage.uploadCapBytes)}`, `Users cannot upload until the total is below ${limitSize(usage.uploadCapBytes)}; the space up to ${limitSize(usage.capBytes)} stays for audit exports, pack images and releases. Take down uploads you do not need or purge unused pack files.`, { live: true })
      : usage.warning
        ? notice("warning", `Storage is above ${limitSize(usage.warnBytes)}`, `New files are refused once the total reaches ${limitSize(usage.capBytes)}, to stay inside Cloudflare R2's free 10 GB, which staging and production share. Free space now: take down uploads you do not need or purge unused pack files.`, { live: true })
        : null;
  const pending =
    usage.pendingDeleteBytes > 0
      ? el("p", { className: "muted", text: `${formatBytes(usage.pendingDeleteBytes)} of removed files could not be deleted from storage yet. They are no longer served, still count towards the total, and the service retries daily.` })
      : null;
  return card("Storage", el("p", { className: "muted", text: line }), meter, state, pending, areas);
}

function render(container: HTMLElement, ctx: ViewContext): void {
  let data: UploadList | null = null;
  const message = el("div", { className: "inline-message" });
  const storageSlot = el("div", {});
  const controlsSlot = el("div", {});
  const blockedSlot = el("div", {});
  const listBody = el("div", { className: "card-body" });
  const summary = el("p", { className: "card-summary muted" });
  const refresh = button("Refresh", { icon: "refresh", onClick: () => void load() });

  replaceChildren(
    container,
    pageHeader(
      "Uploads",
      "Images users uploaded for their own Discord status. They are unlisted, not private: anyone who has an image's address can open it. Take down anything that breaks the upload policy.",
      refresh,
    ),
    message,
    storageSlot,
    controlsSlot,
    card("All uploads", summary, listBody),
    blockedSlot,
  );

  function showFailure(prefix: string, error: unknown): void {
    ctx.reportError(error);
    const copy = describeError(error);
    replaceChildren(message, notice("danger", `${prefix}: ${copy.title}`, copy.detail, { live: true }));
  }

  async function load(): Promise<void> {
    replaceChildren(message);
    summary.textContent = "";
    replaceChildren(listBody, loading("Loading uploads…"));
    setBusy(refresh, true);
    try {
      data = await ctx.api.listUploads();
      draw();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(listBody, errorState(error, () => void load(), ctx.reload));
    } finally {
      setBusy(refresh, false);
    }
  }

  function draw(): void {
    if (!data) return;
    replaceChildren(storageSlot, storageCard(data.storage));
    replaceChildren(controlsSlot, controlsCard(data));
    replaceChildren(blockedSlot, blockedCard(data));
    if (data.uploads.length === 0) {
      summary.textContent = "";
      replaceChildren(listBody, emptyState("No uploads", "Images appear here after users upload them from the app."));
      return;
    }
    summary.textContent = data.total > data.uploads.length ? `${count(data.total)} uploads · showing the newest ${count(data.uploads.length)}` : plural(data.total, "upload", "uploads");
    replaceChildren(listBody, table("Uploads, newest first", COLUMNS, data.uploads.map(row), "table-uploads"));
  }

  function thumbnail(item: UploadItem): HTMLElement {
    // Only an upload address on this environment's content host is loaded; anything else is shown as text.
    if (!isUploadImageUrl(item.url, ctx.environment)) return el("span", { className: "muted", text: "Preview not shown (unexpected address)" });
    return el("span", { className: "thumb" }, uploadImage(item.url, ctx.environment, `Upload ${shortId(item.id)}`, 64));
  }

  function row(item: UploadItem): HTMLTableRowElement {
    const blocked = item.user.uploadsBlocked;
    return el(
      "tr",
      { attrs: { "data-upload": item.id } },
      cell("Image", thumbnail(item)),
      cell("Uploaded by", stacked(item.user.displayName, el("span", {}, mono(`ID ${shortId(item.user.id)}`, "subtle"), blocked ? badge("warning", "Uploads blocked") : null))),
      cell("File", stacked(`${item.format === "png" ? "PNG" : "JPEG"} · ${item.width} × ${item.height}`, formatBytes(item.size))),
      cell("Uploaded", date(item.createdAt)),
      cell(
        "Actions",
        el(
          "div",
          { className: "row-actions" },
          button("Take down", { small: true, variant: "danger", icon: "trash", attrs: { "aria-label": `Take down upload ${shortId(item.id)} by ${item.user.displayName}` }, onClick: (e) => void takeDown(item, e.currentTarget as HTMLButtonElement) }),
          button(blocked ? "Allow uploads" : "Block uploads", {
            small: true,
            attrs: { "aria-label": `${blocked ? "Allow" : "Block"} uploads for ${item.user.displayName}` },
            onClick: (e) => void setBlock(item.user.id, item.user.displayName, !blocked, e.currentTarget as HTMLButtonElement),
          }),
        ),
      ),
    );
  }

  function controlsCard(list: UploadList): HTMLElement {
    const slot = el("div", { className: "inline-message" });
    const on = list.settings.uploadsBlocked;
    const toggle = button(on ? "Allow uploads again" : "Block all new uploads", { variant: on ? "primary" : "danger" });
    toggle.addEventListener("click", async () => {
      const ok = await ctx.confirm(
        on
          ? { title: "Allow uploads again?", body: ["Users can upload images again, up to their limit. Users you blocked one by one stay blocked."], confirmLabel: "Allow uploads" }
          : { title: "Block all new uploads?", body: ["Every new upload is refused until you allow them again. Images already uploaded stay online; take them down one by one if needed."], confirmLabel: "Block all uploads", danger: true },
      );
      if (!ok) return;
      setBusy(toggle, true, "Saving…");
      try {
        await ctx.api.saveSettings({ uploadsBlocked: !on });
        data = { ...list, settings: { ...list.settings, uploadsBlocked: !on } };
        draw();
        ctx.announce(on ? "Uploads are allowed again" : "All new uploads are blocked");
      } catch (error) {
        setBusy(toggle, false);
        ctx.reportError(error);
        const copy = describeError(error);
        replaceChildren(slot, notice("danger", `Could not change uploads: ${copy.title}`, copy.detail, { live: true }));
      }
    });

    const numberInput = (max: number, value: string) => {
      const input = el("input", { className: "input input-narrow", attrs: { type: "number", min: 0, max, step: 1, inputmode: "numeric" } });
      input.value = value;
      return input;
    };
    const currentMb = list.settings.uploadLimitBytes / MB;
    const mbInput = numberInput(UPLOAD_LIMIT_MB_MAX, Number.isInteger(currentMb) ? String(currentMb) : currentMb.toFixed(1));
    const filesInput = numberInput(UPLOAD_LIMIT_FILES_MAX, String(list.settings.uploadLimitFiles));
    const mbField = field("upload-limit-mb", "Storage per person (MB)", mbInput, `Whole number from 0 to ${UPLOAD_LIMIT_MB_MAX}. 60 MB holds a whole imported pack. Lowering it removes nothing; people above it cannot save more pictures.`);
    const filesField = field("upload-limit-files", "Pictures per person", filesInput, `Whole number from 0 to ${UPLOAD_LIMIT_FILES_MAX}. A backstop behind the storage limit: 300 is three pictures for each of about 100 characters.`);
    const save = button("Save limits", { variant: "primary", attrs: { disabled: true } });
    const whole = (input: HTMLInputElement, max: number): number | null => (/^\d{1,4}$/.test(input.value.trim()) && Number(input.value) <= max ? Number(input.value) : null);
    const parse = (): { bytes: number; files: number } | null => {
      const mb = whole(mbInput, UPLOAD_LIMIT_MB_MAX);
      const files = whole(filesInput, UPLOAD_LIMIT_FILES_MAX);
      return mb === null || files === null ? null : { bytes: mb * MB, files };
    };
    const changed = (value: { bytes: number; files: number }): SettingsPatch => ({
      ...(value.bytes !== list.settings.uploadLimitBytes ? { uploadLimitBytes: value.bytes } : {}),
      ...(value.files !== list.settings.uploadLimitFiles ? { uploadLimitFiles: value.files } : {}),
    });
    const onInput = () => {
      mbField.error.textContent = whole(mbInput, UPLOAD_LIMIT_MB_MAX) === null ? `Enter a whole number from 0 to ${UPLOAD_LIMIT_MB_MAX}.` : "";
      filesField.error.textContent = whole(filesInput, UPLOAD_LIMIT_FILES_MAX) === null ? `Enter a whole number from 0 to ${UPLOAD_LIMIT_FILES_MAX}.` : "";
      const value = parse();
      save.disabled = value === null || Object.keys(changed(value)).length === 0;
    };
    mbInput.addEventListener("input", onInput);
    filesInput.addEventListener("input", onInput);
    save.addEventListener("click", async () => {
      const value = parse();
      if (value === null) return;
      const patch = changed(value);
      if (Object.keys(patch).length === 0) return;
      setBusy(save, true, "Saving…");
      try {
        await ctx.api.saveSettings(patch);
        data = { ...list, settings: { ...list.settings, uploadLimitBytes: value.bytes, uploadLimitFiles: value.files } };
        draw();
        ctx.announce(`Limits set to ${value.bytes / MB} MB and ${value.files} pictures per person`);
      } catch (error) {
        setBusy(save, false);
        ctx.reportError(error);
        const copy = describeError(error);
        replaceChildren(slot, notice("danger", `Could not save the limits: ${copy.title}`, copy.detail, { live: true }));
      }
    });

    return el(
      "section",
      { className: "card", attrs: { "aria-labelledby": "upload-controls-title" } },
      el("h2", { className: "card-title", attrs: { id: "upload-controls-title" } }, "New uploads ", on ? badge("danger", "Blocked for everyone") : badge("success", "Allowed")),
      el("p", { className: "muted", text: "Users must accept the upload policy in the app before their first upload: their own work or rights held, nothing offensive, and you may remove anything." }),
      slot,
      el("div", { className: "form-actions" }, toggle),
      mbField.root,
      filesField.root,
      el("div", { className: "form-actions" }, save),
    );
  }

  function blockedCard(list: UploadList): HTMLElement {
    if (list.blockedUsers.length === 0) return card("Users blocked from uploading", el("p", { className: "muted", text: "Nobody is blocked one by one." }));
    return card(
      "Users blocked from uploading",
      el(
        "ul",
        { className: "blocked-list" },
        ...list.blockedUsers.map((u) =>
          el(
            "li",
            { className: "blocked-item" },
            stacked(u.displayName, mono(`ID ${shortId(u.id)}`, "subtle")),
            button("Allow uploads", { small: true, attrs: { "aria-label": `Allow uploads for ${u.displayName}` }, onClick: (e) => void setBlock(u.id, u.displayName, false, e.currentTarget as HTMLButtonElement) }),
          ),
        ),
      ),
    );
  }

  async function takeDown(item: UploadItem, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: "Take down this upload?",
      body: [
        `Upload ${shortId(item.id)} by ${item.user.displayName}.`,
        "The file is deleted and its address stops working at once. Discord's media proxy may keep showing a cached copy for a while. This cannot be undone.",
      ],
      confirmLabel: "Take down",
      danger: true,
    });
    if (!ok) return;
    setBusy(trigger, true, "Taking down…");
    try {
      const { storage, fileRemoved } = await ctx.api.takeDownUpload(item.id);
      if (data) data = { ...data, uploads: data.uploads.filter((u) => u.id !== item.id), total: Math.max(0, data.total - 1), storage };
      replaceChildren(message);
      draw();
      if (!fileRemoved) {
        replaceChildren(
          message,
          notice("warning", "Taken down, but the stored file is not deleted yet", "Its address no longer serves the image. Deleting the file from storage failed; the service retries daily.", { live: true }),
        );
      }
      container.querySelector<HTMLElement>("#page-title")?.focus();
      ctx.announce(fileRemoved ? "Upload taken down." : "Upload taken down; the stored file is deleted later.");
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not take the upload down", error);
    }
  }

  async function setBlock(userId: string, name: string, blocked: boolean, trigger: HTMLButtonElement): Promise<void> {
    if (blocked) {
      const ok = await ctx.confirm({
        title: `Block uploads for ${name}?`,
        body: ["Their new uploads are refused. Images they already uploaded stay online; take them down one by one if needed."],
        confirmLabel: "Block uploads",
        danger: true,
      });
      if (!ok) return;
    }
    setBusy(trigger, true, "Saving…");
    try {
      await ctx.api.setUserUploadBlock(userId, blocked);
      if (data) {
        const others = data.blockedUsers.filter((u) => u.id !== userId);
        data = {
          ...data,
          uploads: data.uploads.map((u) => (u.user.id === userId ? { ...u, user: { ...u.user, uploadsBlocked: blocked } } : u)),
          blockedUsers: blocked ? [...others, { id: userId, displayName: name }] : others,
        };
      }
      replaceChildren(message);
      draw();
      ctx.announce(blocked ? `Uploads blocked for ${name}.` : `Uploads allowed for ${name}.`);
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not change the user's uploads", error);
    }
  }

  void load();
}

export const uploadsView: View = { render };
