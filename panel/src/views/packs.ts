// Packs (FR-ADM-08, FR-PACK-01/03/04/05/08; ADR-0013): create a pack, upload images, map each to a slot, mark licensed
// items, flag the fallback pack and Behaviour artwork, publish and unpublish, withdraw a licensed item, delete a pack
// and its files. The owner edits a draft; Publish copies it into the signed pack index that apps sync.
//
// Images are shown only from the content host of this environment (src/cdn.ts) after the service stored them. Before
// upload the panel shows the file's name and size only: the CSP allows no blob: or data: images (ADR-0014).
//
// The slot list is the app's (src/catalog.ts): killers and survivors come from the signed character roster, so every
// character the app knows has a slot. "Add pictures from a folder" (src/views/bulk-upload.ts) fills many slots at once.

import type { EntryKind, Pack, PackEntry, PackFields, PackList, UploadedFile } from "../api.ts";
import { ApiError } from "../api.ts";
import { KEY_PATTERN, KIND_LABELS, SlotCatalog, slugify } from "../catalog.ts";
import { MAX_FILE, TARGET_FILE, imageReasonText, sniffImageType } from "../image-check.ts";
import { convertPngForApp, convertedText } from "../png-convert.ts";
import { type BulkState, bulkCard } from "./bulk-upload.ts";
import type { View, ViewContext } from "../context.ts";
import { el, icon, packImage, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { count, date, dateTime, formatBytes, plural } from "../format.ts";
import { badge, button, card, emptyState, errorState, field, loading, mono, notice, pageHeader, setBusy, type Tone } from "../ui.ts";

const LIMITS = { name: 80, author: 120, licenceNote: 200, credit: 200, holder: 120, creator: 120, proof: 500 } as const;
const PACK_ID = /^[a-z0-9-]{1,64}$/;
const CONTROL = /[\p{Cc}\p{Cf}]/u;

export { formatBytes } from "../format.ts";
export { imageReasonText, sniffImageType } from "../image-check.ts";

const STATUS: Record<Pack["status"], { label: string; tone: Tone }> = {
  draft: { label: "Draft", tone: "neutral" },
  published: { label: "Published", tone: "success" },
  unpublished: { label: "Unpublished", tone: "warning" },
};

function packBadges(pack: Pack): HTMLElement {
  return el(
    "div",
    { className: "badge-row" },
    badge(STATUS[pack.status].tone, STATUS[pack.status].label),
    pack.status === "published" && pack.changedSincePublish ? badge("warning", "Changes not published") : null,
    pack.fallback ? badge("info", "Fallback pack") : null,
    pack.default ? badge("info", "Default pictures") : null,
    pack.behaviourArt ? badge("info", "Behaviour art") : null,
    pack.thirdParty ? badge(pack.permission ? "neutral" : "danger", pack.permission ? "Third-party, permission recorded" : "Third-party, no permission") : null,
  );
}

function entryBadges(entry: PackEntry, catalog: SlotCatalog): HTMLElement {
  // A character key no roster character has (for example an old spelling) is never shown by the app.
  const unknownCharacter = catalog.rosterLoaded && entry.key.startsWith("character:") && !catalog.isKnown(entry.key);
  return el(
    "div",
    { className: "badge-row" },
    unknownCharacter ? badge("warning", "No character has this slot") : null,
    entry.withdrawn ? badge("danger", "Withdrawn") : entry.live ? badge("success", "Live") : badge("neutral", "Not published yet"),
    entry.licensedHolder !== null ? badge("warning", `Licensed: ${entry.licensedHolder}`) : null,
    entry.size > TARGET_FILE ? badge("warning", "Over 60 KB") : null,
  );
}

/** The address as shown: host, folder and the start of the hash. Copy address copies it in full. */
function shortAddress(entry: PackEntry): string {
  const url = new URL(entry.url);
  return `${url.host}/packs/${entry.sha256.slice(0, 12)}….${entry.format === "png" ? "png" : "jpg"}`;
}

function thumbnail(ctx: ViewContext, entry: PackEntry, label: string, size: number): HTMLElement {
  return el("div", { className: `thumb${entry.withdrawn ? " thumb-withdrawn" : ""}` }, packImage(entry.url, ctx.environment, label, size));
}

/** Reads a chosen file and uploads it; throws an ApiError("client") with a reason the owner can act on. */
async function uploadChosen(ctx: ViewContext, file: File): Promise<{ file: UploadedFile; removed: string[]; converted: string | null }> {
  if (file.size > MAX_FILE) throw new ApiError("client", null, "image_rejected", "too_large");
  if (file.size === 0) throw new ApiError("client", null, "image_rejected", "empty");
  let bytes = new Uint8Array(await file.arrayBuffer());
  const type = sniffImageType(bytes);
  if (type === null) throw new ApiError("client", null, "image_rejected", "unknown_format");
  // A palette, greyscale, 16-bit or colour-key PNG is converted to 8-bit RGBA here, so the owner never has to re-export it.
  const conversion = await convertPngForApp(bytes);
  if (conversion.kind === "failed") throw new ApiError("client", null, "image_rejected", conversion.reason);
  if (conversion.kind === "converted") bytes = conversion.bytes;
  const uploaded = await ctx.api.uploadFile(bytes, type);
  return { ...uploaded, converted: conversion.kind === "converted" ? convertedText(conversion.from) : null };
}

function failureNotice(prefix: string, error: unknown): HTMLElement {
  if (error instanceof ApiError && error.code === "image_rejected") return notice("danger", `${prefix}: the image was not accepted`, imageReasonText(error.reason), { live: true });
  const copy = describeError(error);
  return notice("danger", `${prefix}: ${copy.title}`, copy.detail, { live: true });
}

function removedText(removed: readonly string[]): string | null {
  return removed.length === 0 ? null : `Removed before storing (the picture is unchanged): ${removed.join(", ")}.`;
}

// ---------------------------------------------------------------------------------------------------------------
// Form helpers

function textInput(value: string, max: number, attrs: Record<string, string | number | boolean> = {}): HTMLInputElement {
  return el("input", { className: "input", attrs: { type: "text", maxlength: max, autocomplete: "off", value, ...attrs } });
}

function checkbox(id: string, label: string, hint: string, checked: boolean): { root: HTMLElement; input: HTMLInputElement } {
  const input = el("input", { className: "checkbox", attrs: { type: "checkbox", id, checked, "aria-describedby": `${id}-hint` } });
  const root = el("div", { className: "check-field" }, input, el("div", { className: "check-text" }, el("label", { className: "field-label", text: label, attrs: { for: id } }), el("p", { className: "field-hint", text: hint, attrs: { id: `${id}-hint` } })));
  return { root, input };
}

function checkText(input: HTMLInputElement, error: HTMLElement, max: number, required: boolean, label: string): string | null | undefined {
  const value = input.value.trim();
  error.textContent = "";
  input.removeAttribute("aria-invalid");
  let message: string | null = null;
  if (value === "" && required) message = `Enter ${label}.`;
  else if (value.length > max) message = `Shorten it to ${max} characters or fewer.`;
  else if (CONTROL.test(value)) message = "Remove the invisible control characters.";
  if (message) {
    error.textContent = message;
    input.setAttribute("aria-invalid", "true");
    return undefined;
  }
  return value === "" ? null : value;
}

interface DetailsForm {
  root: HTMLElement;
  read: () => PackFields | null;
  focusFirstError: () => void;
}

const today = (now: number) => new Date(now).toISOString().slice(0, 10);

/** The pack fields form, shared by "New pack" and the editor. */
function detailsForm(ctx: ViewContext, prefix: string, pack: Pack | null): DetailsForm {
  const name = field(`${prefix}-name`, "Name", textInput(pack?.name ?? "", LIMITS.name), "Shown on the app's Packs page.");
  const author = field(`${prefix}-author`, "Author", textInput(pack?.author ?? "", LIMITS.author), "Who made the artwork, for example Behaviour Interactive.");
  const licence = field(`${prefix}-licence`, "Licence note", textInput(pack?.licenceNote ?? "", LIMITS.licenceNote), "Shown with the pack, for example the copyright line.");
  const credit = field(`${prefix}-credit`, "Credit (optional)", textInput(pack?.credit ?? "", LIMITS.credit), "An extra credit line in the app. Required for a third-party pack.");
  const fallback = checkbox(`${prefix}-fallback`, "Fallback pack", "Apps use this pack's image for a slot only when the chosen pack has none. Only one pack can be the fallback.", pack?.fallback ?? false);
  const behaviour = checkbox(
    `${prefix}-behaviour`,
    "Contains Behaviour Interactive artwork",
    "Apps then show “Unofficial · © Behaviour Interactive” with these images. Leaving it off for Behaviour art breaks condition 2 of the fan-content terms (ADR-0013).",
    pack?.behaviourArt ?? false,
  );
  const third = checkbox(`${prefix}-third`, "Made by someone else (for example a NightLight creator)", "Publishing then needs the creator's written permission recorded below, and a credit.", pack?.thirdParty ?? false);
  const creator = field(`${prefix}-creator`, "Creator", textInput(pack?.permission?.creator ?? "", LIMITS.creator), "As the creator wants to be credited.");
  const dateInput = el("input", { className: "input input-narrow", attrs: { type: "date", max: today(ctx.now()), value: pack?.permission?.date ?? "" } });
  const granted = field(`${prefix}-granted`, "Permission given on", dateInput);
  const proof = field(`${prefix}-proof`, "Where the proof is kept", el("textarea", { className: "input textarea", attrs: { rows: 2, maxlength: LIMITS.proof } }), "For example “Email of 20 October 2026, saved offline”. Only you see this; it is never published.");
  (proof.root.querySelector("textarea") as HTMLTextAreaElement).value = pack?.permission?.proof ?? "";
  const permissionBlock = el("fieldset", { className: "subfields" }, el("legend", { className: "subfields-legend", text: "Creator's permission" }), creator.root, granted.root, proof.root);
  const syncPermission = () => {
    permissionBlock.hidden = !third.input.checked;
  };
  third.input.addEventListener("change", syncPermission);
  syncPermission();

  const root = el(
    "div",
    { className: "form-two" },
    name.root,
    author.root,
    el("div", { className: "field-span" }, licence.root),
    el("div", { className: "field-span" }, credit.root),
    el("div", { className: "field-span checks" }, behaviour.root, fallback.root, third.root),
    el("div", { className: "field-span" }, permissionBlock),
  );

  const inputs: [HTMLElement, HTMLElement][] = [];
  const read = (): PackFields | null => {
    inputs.length = 0;
    const get = (f: { root: HTMLElement; error: HTMLElement }, max: number, required: boolean, label: string) => {
      const input = f.root.querySelector<HTMLInputElement>("input, textarea")!;
      const value = checkText(input, f.error, max, required, label);
      if (value === undefined) inputs.push([input, f.error]);
      return value;
    };
    const values = {
      name: get(name, LIMITS.name, true, "a name"),
      author: get(author, LIMITS.author, true, "the author"),
      licenceNote: get(licence, LIMITS.licenceNote, true, "a licence note"),
      credit: get(credit, LIMITS.credit, false, "a credit"),
    };
    let permission: PackFields["permission"] = null;
    if (third.input.checked) {
      const c = get(creator, LIMITS.creator, false, "the creator");
      const p = get(proof, LIMITS.proof, false, "where the proof is kept");
      granted.error.textContent = "";
      const d = dateInput.value;
      if (d !== "" && d > today(ctx.now())) {
        granted.error.textContent = "The date cannot be in the future.";
        inputs.push([dateInput, granted.error]);
      }
      const filled = [c, p, d === "" ? null : d].filter((v) => v !== null && v !== undefined).length;
      if (filled > 0 && filled < 3 && inputs.length === 0) {
        const missing = c === null ? creator : d === "" ? granted : proof;
        missing.error.textContent = "Fill in all three permission fields, or none for now.";
        inputs.push([missing.root.querySelector<HTMLElement>("input, textarea")!, missing.error]);
      }
      if (filled === 3 && typeof c === "string" && typeof p === "string") permission = { creator: c, date: d, proof: p };
    }
    if (inputs.length > 0 || values.name == null || values.author == null || values.licenceNote == null || values.credit === undefined) return null;
    return { name: values.name, author: values.author, licenceNote: values.licenceNote, credit: values.credit, fallback: fallback.input.checked, behaviourArt: behaviour.input.checked, thirdParty: third.input.checked, permission };
  };
  return { root, read, focusFirstError: () => inputs[0]?.[0].focus() };
}

// ---------------------------------------------------------------------------------------------------------------
// The slot picker

interface SlotPicker {
  root: HTMLElement;
  /** The chosen key, or null with an error shown. */
  read: () => string | null;
  reset: () => void;
}

function slotPicker(prefix: string, catalog: SlotCatalog, taken: ReadonlySet<string>, onChange: (key: string) => void): SlotPicker {
  const select = el(
    "select",
    { className: "input select" },
    el("option", { text: "Choose a slot…", attrs: { value: "" } }),
    ...catalog.groups.filter((g) => g.slots.length > 0).map((g) => el("optgroup", { attrs: { label: g.label } }, ...g.slots.map((s) => el("option", { text: taken.has(s.key) ? `${s.label} (already in this pack)` : s.label, attrs: { value: s.key, disabled: taken.has(s.key) } })))),
    el("optgroup", { attrs: { label: "Other" } }, el("option", { text: "Another character or map…", attrs: { value: "custom" } })),
  );
  const slot = field(`${prefix}-slot`, "Slot", select, "What the image stands for in the app.");
  const kind = el("select", { className: "input select" }, ...(["character", "map", "role", "app"] as EntryKind[]).map((k) => el("option", { text: KIND_LABELS[k], attrs: { value: k } })));
  const kindField = field(`${prefix}-kind`, "Kind", kind);
  const name = textInput("", 64, { placeholder: "For example The Ghoul" });
  const nameField = field(`${prefix}-custom`, "Name as players type it", name);
  const preview = el("p", { className: "field-hint", attrs: { "aria-live": "polite" } });
  const custom = el("div", { className: "form-two custom-slot" }, kindField.root, el("div", {}, nameField.root, preview));
  const update = () => {
    custom.hidden = select.value !== "custom";
    const slug = slugify(name.value);
    preview.textContent = slug ? `Key: ${kind.value}:${slug}` : "The key is made from the name the way the app makes it.";
  };
  select.addEventListener("change", () => {
    update();
    onChange(select.value);
  });
  name.addEventListener("input", update);
  kind.addEventListener("change", update);
  update();

  const read = (): string | null => {
    slot.error.textContent = "";
    nameField.error.textContent = "";
    if (select.value === "") {
      slot.error.textContent = "Choose what the image stands for.";
      select.focus();
      return null;
    }
    if (select.value !== "custom") return select.value;
    const key = `${kind.value}:${slugify(name.value)}`;
    if (!KEY_PATTERN.test(key)) {
      nameField.error.textContent = "Enter a name with letters or digits (at most 64 in the key).";
      name.focus();
      return null;
    }
    if (taken.has(key)) {
      nameField.error.textContent = "This pack already has an image for that slot. Replace that image instead.";
      name.focus();
      return null;
    }
    return key;
  };
  return {
    root: el("div", { className: "slot-picker" }, slot.root, custom),
    read,
    reset: () => {
      select.value = "";
      name.value = "";
      update();
    },
  };
}

// ---------------------------------------------------------------------------------------------------------------
// The view

function render(container: HTMLElement, ctx: ViewContext): void {
  let data: PackList | null = null;
  let editing: string | null = null;
  let creating = false;
  /** The slots, with the roster's killers and survivors once it has loaded. */
  let catalog = new SlotCatalog(null);
  let rosterError: unknown = null;
  /** The "Add pictures from a folder" review, kept across redraws of the editor. */
  let bulk: BulkState | null = null;
  const message = el("div", { className: "inline-message" });
  const body = el("div", { className: "packs-body" });
  const refresh = button("Refresh", { icon: "refresh", onClick: () => void load() });
  const newPack = button("New pack", {
    variant: "primary",
    icon: "plus",
    onClick: () => {
      creating = true;
      editing = null;
      draw();
      container.querySelector<HTMLElement>("#new-pack-title")?.focus();
    },
  });

  replaceChildren(container, pageHeader("Packs", "Image packs that every app syncs from the content host. Edit a pack as a draft, then publish it; withdraw a licensed image on its own.", refresh, newPack), message, body);

  const say = (node: HTMLElement | null) => replaceChildren(message, node);

  async function load(): Promise<void> {
    say(null);
    replaceChildren(body, loading("Loading packs…"));
    setBusy(refresh, true);
    try {
      const roster = ctx.api.getRoster().then(
        (state) => ({ ok: true as const, characters: state.roster.characters }),
        (error: unknown) => ({ ok: false as const, error }),
      );
      data = await ctx.api.listPacks();
      const answer = await roster;
      if (answer.ok) {
        catalog = new SlotCatalog(answer.characters);
        rosterError = null;
      } else {
        // The packs still work; the editor says the killers and survivors are missing and offers Try again.
        ctx.reportError(answer.error);
        catalog = new SlotCatalog(null);
        rosterError = answer.error;
      }
      draw();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(body, errorState(error, () => void load(), ctx.reload));
    } finally {
      setBusy(refresh, false);
    }
  }

  function replacePack(updated: Pack): void {
    if (!data) return;
    data = { ...data, packs: data.packs.some((p) => p.id === updated.id) ? data.packs.map((p) => (p.id === updated.id ? updated : p)) : [...data.packs, updated] };
  }

  function setIndex(index: { version: number; publishedAt: string } | null): void {
    if (!data || !index) return;
    const live = data.packs.filter((p) => p.status === "published");
    data = { ...data, index: { version: index.version, publishedAt: index.publishedAt, packs: live.length, entries: live.reduce((n, p) => n + p.entries.filter((e) => e.live).length, 0) } };
  }

  function draw(): void {
    if (!data) return;
    newPack.hidden = editing !== null || creating;
    const pack = editing === null ? null : data.packs.find((p) => p.id === editing) ?? null;
    if (pack) replaceChildren(body, editor(pack));
    else if (creating) replaceChildren(body, createCard());
    else replaceChildren(body, overview(data));
  }

  // --- Overview ------------------------------------------------------------------------------------------------

  function overview(list: PackList): HTMLElement {
    const index = list.index;
    const summary = card(
      "Signed pack list",
      el(
        "dl",
        { className: "kv kv-inline" },
        el("dt", { text: "Version" }),
        el("dd", {}, mono(String(index.version))),
        el("dt", { text: "Published" }),
        el("dd", { text: index.publishedAt ? dateTime(index.publishedAt) : "Nothing published yet" }),
        el("dt", { text: "In it" }),
        el("dd", { text: `${plural(index.packs, "pack", "packs")}, ${plural(index.entries, "image", "images")}` }),
      ),
      el("p", { className: "fine-print", text: "Apps check for a new version when they start and every 6 hours. A withdrawn or unpublished image disappears at their next check." }),
    );
    if (list.packs.length === 0) {
      return el(
        "div",
        { className: "stack" },
        summary,
        card(null, emptyState("No packs yet", "Create a pack, add its images, then publish it. Nothing reaches the apps until you publish.", button("New pack", { variant: "primary", icon: "plus", onClick: () => ((creating = true), draw()) }))),
      );
    }
    const purge = button("Remove unused files", { icon: "trash", onClick: (e) => void purgeFiles(e.currentTarget as HTMLButtonElement) });
    return el(
      "div",
      { className: "stack" },
      summary,
      el("ul", { className: "pack-list", attrs: { "aria-label": "Packs" } }, ...list.packs.map((p) => el("li", {}, packCard(p)))),
      card(
        "Storage",
        el("p", { className: "muted", text: "Files are deleted when no pack uses them. This also removes uploads that were never added to a pack (after an hour) and any stray file left by an interrupted upload." }),
        el("div", { className: "form-actions" }, purge),
      ),
    );
  }

  function packCard(pack: Pack): HTMLElement {
    const shown = pack.entries.slice(0, 8);
    const rest = pack.entries.length - shown.length;
    const titleId = `pack-${pack.id}-title`;
    const actions = el(
      "div",
      { className: "row-actions" },
      button("Edit", { small: true, attrs: { "aria-label": `Edit ${pack.name}` }, onClick: () => ((editing = pack.id), (creating = false), draw(), container.querySelector<HTMLElement>("#editor-title")?.focus()) }),
      publishButton(pack, true),
      pack.status === "published" ? button("Unpublish", { small: true, variant: "danger", attrs: { "aria-label": `Unpublish ${pack.name}` }, onClick: (e) => void unpublish(pack, e.currentTarget as HTMLButtonElement) }) : null,
      pack.status !== "published" ? button("Delete pack", { small: true, variant: "danger", attrs: { "aria-label": `Delete pack ${pack.name}` }, onClick: (e) => void deletePack(pack, e.currentTarget as HTMLButtonElement) }) : null,
      button(pack.default ? "Stop using as default" : "Use as default pictures", {
        small: true,
        attrs: { "aria-label": pack.default ? `Stop using ${pack.name} as the default pictures` : `Use ${pack.name} as the default pictures` },
        onClick: (e) => void setDefault(pack, !pack.default, e.currentTarget as HTMLButtonElement),
      }),
    );
    return el(
      "article",
      { className: "card pack-card", attrs: { "aria-labelledby": titleId } },
      el(
        "div",
        { className: "pack-card-head" },
        el("div", { className: "pack-card-title" }, el("h2", { className: "card-title", text: pack.name, attrs: { id: titleId } }), mono(pack.id, "subtle")),
        packBadges(pack),
      ),
      el(
        "p",
        { className: "muted pack-meta" },
        `${plural(pack.entries.length, "image", "images")} · ${formatBytes(pack.totalBytes)} of ${formatBytes(data?.limits.maxPackBytes ?? 0)} · `,
        pack.status === "draft" ? "Never published" : pack.publishedAt ? `Version ${pack.version}, published ${date(pack.publishedAt)}` : `Version ${pack.version}`,
      ),
      shown.length > 0
        ? el("ul", { className: "thumb-strip", attrs: { "aria-label": `Images in ${pack.name}` } }, ...shown.map((e) => el("li", {}, thumbnail(ctx, e, catalog.label(e.key), 64))), rest > 0 ? el("li", { className: "thumb-more", text: `+${count(rest)}` }) : null)
        : el("p", { className: "muted", text: "No images yet." }),
      actions,
    );
  }

  function publishButton(pack: Pack, small: boolean): HTMLButtonElement | null {
    if (pack.status === "published" && !pack.changedSincePublish) return null;
    const label = pack.status === "published" ? "Publish changes" : "Publish";
    return button(label, { small, variant: "primary", attrs: { "aria-label": `${label}: ${pack.name}` }, onClick: (e) => void publish(pack, e.currentTarget as HTMLButtonElement) });
  }

  // --- Actions -----------------------------------------------------------------------------------------------

  async function publish(pack: Pack, trigger: HTMLButtonElement): Promise<void> {
    const images = pack.entries.filter((e) => !e.withdrawn).length;
    const body = [
      `${plural(images, "image", "images")} become available to every app at its next check (when it starts, or within 6 hours). The pack list is signed again with a higher version.`,
      pack.behaviourArt
        ? "Marked as Behaviour Interactive artwork: apps show the Behaviour notice with these images."
        : "Not marked as Behaviour Interactive artwork: apps show no Behaviour notice for it. If any image is Behaviour artwork, cancel and tick that box first.",
    ];
    if (pack.fallback) body.push("This is the fallback pack: apps use its images wherever the chosen pack has none.");
    if (pack.entries.some((e) => e.withdrawn)) body.push(`${plural(pack.entries.filter((e) => e.withdrawn).length, "withdrawn image stays", "withdrawn images stay")} out.`);
    const ok = await ctx.confirm({ title: `Publish ${pack.name}?`, body, confirmLabel: "Publish pack" });
    if (!ok) return;
    setBusy(trigger, true, "Publishing…");
    try {
      const change = await ctx.api.publishPack(pack.id);
      replacePack(change.pack);
      setIndex(change.index);
      draw();
      say(notice("success", `${change.pack.name} is published`, change.index ? `Pack list version ${change.index.version}.` : null, { live: true }));
      ctx.announce(`${change.pack.name} published.`);
    } catch (error) {
      setBusy(trigger, false);
      ctx.reportError(error);
      say(failureNotice(`${pack.name} was not published`, error));
    }
  }

  async function unpublish(pack: Pack, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: `Unpublish ${pack.name}?`,
      body: [
        "The pack leaves the signed pack list now. Apps stop showing its images at their next check and use the fallback pack's images, or none.",
        "Its files stay on the content host until you delete the pack. Copies already on players' PCs and in Discord's cache can remain for a while.",
      ],
      confirmLabel: "Unpublish pack",
      danger: true,
    });
    if (!ok) return;
    setBusy(trigger, true, "Unpublishing…");
    try {
      const change = await ctx.api.unpublishPack(pack.id);
      replacePack(change.pack);
      setIndex(change.index);
      draw();
      say(notice("success", `${change.pack.name} is unpublished`, "To delete its files as well, delete the pack.", { live: true }));
      ctx.announce(`${change.pack.name} unpublished.`);
    } catch (error) {
      setBusy(trigger, false);
      ctx.reportError(error);
      say(failureNotice(`${pack.name} was not unpublished`, error));
    }
  }

  async function setDefault(pack: Pack, isDefault: boolean, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm(
      isDefault
        ? {
            title: `Use ${pack.name} as the default pictures?`,
            body: [
              "Its character pictures become what every app shows for a character when the player has not chosen a picture of their own. Only one pack can be the default.",
              pack.status === "published" ? "The pack list is signed again now, and apps pick it up at their next check." : "It takes effect when you publish the pack.",
            ],
            confirmLabel: "Use as default",
          }
        : {
            title: `Stop using ${pack.name} as the default pictures?`,
            body: ["Characters the player has not chosen a picture for then show the fallback pack's picture, or none."],
            confirmLabel: "Stop using as default",
            danger: true,
          },
    );
    if (!ok) return;
    setBusy(trigger, true, "Saving…");
    try {
      const change = await ctx.api.setDefaultPack(pack.id, isDefault);
      replacePack(change.pack);
      setIndex(change.index);
      draw();
      say(notice("success", isDefault ? `${change.pack.name} holds the default pictures` : `${change.pack.name} no longer holds the default pictures`, change.index ? `Pack list version ${change.index.version}.` : null, { live: true }));
      ctx.announce(isDefault ? `${change.pack.name} is the default pack.` : `${change.pack.name} is no longer the default pack.`);
    } catch (error) {
      setBusy(trigger, false);
      ctx.reportError(error);
      say(failureNotice(`${pack.name} was not changed`, error));
    }
  }

  async function deletePack(pack: Pack, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: `Delete ${pack.name}?`,
      body: [`The pack and its ${plural(pack.entries.length, "image", "images")} are deleted. Files that no other pack uses are deleted from the content host at once. This cannot be undone.`],
      confirmLabel: "Delete pack",
      danger: true,
      typed: { expected: pack.id, label: "To confirm, type the pack ID" },
    });
    if (!ok) return;
    setBusy(trigger, true, "Deleting…");
    try {
      const filesDeleted = await ctx.api.deletePack(pack.id);
      if (data) data = { ...data, packs: data.packs.filter((p) => p.id !== pack.id) };
      editing = null;
      draw();
      say(notice("success", `${pack.name} is deleted`, `${plural(filesDeleted, "file was", "files were")} deleted from the content host.`, { live: true }));
      ctx.announce(`${pack.name} deleted.`);
    } catch (error) {
      setBusy(trigger, false);
      ctx.reportError(error);
      say(failureNotice(`${pack.name} was not deleted`, error));
    }
  }

  async function purgeFiles(trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({ title: "Remove unused files?", body: ["Files that no pack uses are deleted from the content host. Files any pack uses, published or not, are kept."], confirmLabel: "Remove unused files" });
    if (!ok) return;
    setBusy(trigger, true, "Removing…");
    try {
      const result = await ctx.api.purgeFiles();
      setBusy(trigger, false);
      const total = result.unreferenced + result.orphaned;
      say(notice("success", total === 0 ? "No unused files" : `${plural(total, "unused file", "unused files")} removed`, null, { live: true }));
    } catch (error) {
      setBusy(trigger, false);
      ctx.reportError(error);
      say(failureNotice("Unused files were not removed", error));
    }
  }

  // --- New pack ------------------------------------------------------------------------------------------------

  function createCard(): HTMLElement {
    const idInput = textInput("", 64, { spellcheck: "false", autocapitalize: "off", placeholder: "for example original" });
    const idField = field("new-pack-id", "Pack ID", idInput, "Lower-case letters, digits and hyphens. It cannot be changed later.");
    const details = detailsForm(ctx, "new-pack", null);
    const nameInput = details.root.querySelector<HTMLInputElement>("#new-pack-name")!;
    let idEdited = false;
    idInput.addEventListener("input", () => (idEdited = true));
    nameInput.addEventListener("input", () => {
      if (!idEdited) idInput.value = slugify(nameInput.value).slice(0, 64);
    });
    const submit = button("Create pack", { variant: "primary", type: "submit" });
    const cancel = button("Cancel", { onClick: () => ((creating = false), draw()) });
    const formMessage = el("div", { className: "inline-message" });
    const form = el("form", { className: "stack", attrs: { novalidate: true, "aria-labelledby": "new-pack-title" } }, el("div", { className: "form-two" }, idField.root), details.root, el("div", { className: "form-actions" }, cancel, submit));
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      void create();
    });
    async function create(): Promise<void> {
      replaceChildren(formMessage);
      idField.error.textContent = "";
      idInput.removeAttribute("aria-invalid");
      const id = idInput.value.trim();
      const fields = details.read();
      if (!PACK_ID.test(id)) {
        idField.error.textContent = "Use 1 to 64 lower-case letters, digits and hyphens.";
        idInput.setAttribute("aria-invalid", "true");
        idInput.focus();
        return;
      }
      if (!fields) {
        details.focusFirstError();
        return;
      }
      setBusy(submit, true, "Creating…");
      try {
        const pack = await ctx.api.createPack(id, fields);
        replacePack(pack);
        creating = false;
        editing = pack.id;
        draw();
        say(notice("success", `${pack.name} is created`, "Add its images below, then publish it.", { live: true }));
        container.querySelector<HTMLElement>("#editor-title")?.focus();
      } catch (error) {
        setBusy(submit, false);
        ctx.reportError(error);
        replaceChildren(formMessage, failureNotice("The pack was not created", error));
      }
    }
    return el(
      "section",
      { className: "card", attrs: { "aria-labelledby": "new-pack-title" } },
      el("h2", { className: "card-title", text: "New pack", attrs: { id: "new-pack-title", tabindex: -1 } }),
      formMessage,
      form,
    );
  }

  // --- Editor ------------------------------------------------------------------------------------------------

  function editor(pack: Pack): HTMLElement {
    const back = button("All packs", { variant: "ghost", icon: "back", onClick: () => ((editing = null), draw(), container.querySelector<HTMLElement>("#page-title")?.focus()) });
    const head = el(
      "section",
      { className: "card editor-head", attrs: { "aria-labelledby": "editor-title" } },
      el(
        "div",
        { className: "pack-card-head" },
        el("div", { className: "pack-card-title" }, el("h2", { className: "card-title", text: pack.name, attrs: { id: "editor-title", tabindex: -1 } }), mono(pack.id, "subtle")),
        packBadges(pack),
      ),
      el("p", { className: "muted pack-meta", text: `${plural(pack.entries.length, "image", "images")} · ${formatBytes(pack.totalBytes)} of ${formatBytes(data?.limits.maxPackBytes ?? 0)}${pack.status === "draft" ? " · Never published" : ` · Version ${pack.version}`}` }),
      pack.status === "published" && pack.changedSincePublish ? notice("warning", "This pack has changes that apps do not have yet", "Publish the changes to sign a new pack list with them.") : null,
      el(
        "div",
        { className: "row-actions" },
        publishButton(pack, false),
        pack.status === "published" ? button("Unpublish", { variant: "danger", onClick: (e) => void unpublish(pack, e.currentTarget as HTMLButtonElement) }) : null,
        pack.status !== "published" ? button("Delete pack", { variant: "danger", onClick: (e) => void deletePack(pack, e.currentTarget as HTMLButtonElement) }) : null,
      ),
    );
    if (bulk !== null && bulk.packId !== pack.id && bulk.phase !== "running") bulk = null;
    return el("div", { className: "stack" }, el("div", {}, back), head, rosterNotice(), imagesCard(pack), folderCard(pack), addImageCard(pack), detailsCard(pack));
  }

  function rosterNotice(): HTMLElement | null {
    if (rosterError === null) return null;
    const copy = describeError(rosterError);
    const retry = button("Try again", { icon: "refresh", onClick: (e) => void reloadRoster(e.currentTarget as HTMLButtonElement) });
    return notice("warning", "The character list could not be loaded", `Killers and survivors are missing from the slot lists until it loads. ${copy.title}. ${copy.detail}`, { children: [el("div", { className: "row-actions" }, retry)] });
  }

  async function reloadRoster(trigger: HTMLButtonElement): Promise<void> {
    setBusy(trigger, true, "Loading…");
    try {
      catalog = new SlotCatalog((await ctx.api.getRoster()).roster.characters);
      rosterError = null;
    } catch (error) {
      ctx.reportError(error);
      rosterError = error;
    }
    draw();
  }

  function folderCard(pack: Pack): HTMLElement {
    return bulkCard(
      {
        ctx,
        catalog,
        maxPackBytes: data?.limits.maxPackBytes ?? 0,
        pack: () => data?.packs.find((p) => p.id === pack.id) ?? pack,
        packUpdated: replacePack,
        redraw: draw,
        state: () => bulk,
        setState: (state) => (bulk = state),
        sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
      },
      pack,
    );
  }

  function detailsCard(pack: Pack): HTMLElement {
    const details = detailsForm(ctx, "edit-pack", pack);
    const save = button("Save details", { variant: "primary", type: "submit" });
    const formMessage = el("div", { className: "inline-message" });
    const form = el("form", { className: "stack", attrs: { novalidate: true, "aria-label": "Pack details" } }, details.root, el("div", { className: "form-actions" }, save));
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      void saveDetails();
    });
    async function saveDetails(): Promise<void> {
      replaceChildren(formMessage);
      const fields = details.read();
      if (!fields) {
        details.focusFirstError();
        return;
      }
      setBusy(save, true, "Saving…");
      try {
        const updated = await ctx.api.updatePack(pack.id, fields);
        replacePack(updated);
        draw();
        say(notice("success", "Details saved", updated.status === "published" ? "Publish the changes to send them to the apps." : null, { live: true }));
      } catch (error) {
        setBusy(save, false);
        ctx.reportError(error);
        replaceChildren(formMessage, failureNotice("The details were not saved", error));
      }
    }
    return card("Details", formMessage, form);
  }

  function addImageCard(pack: Pack): HTMLElement {
    const fileInput = el("input", { className: "input input-file", attrs: { type: "file", accept: "image/png,image/jpeg" } });
    const fileField = field("add-image-file", "Image file", fileInput, "PNG (8-bit RGB or RGBA) or JPEG, up to 1024 × 1024 pixels and 2 MB; aim for under 60 KB. Metadata is removed when it is stored.");
    const fileInfo = el("p", { className: "file-info", attrs: { "aria-live": "polite" } });
    fileInput.addEventListener("change", () => {
      const file = fileInput.files?.[0];
      fileField.error.textContent = "";
      fileInfo.textContent = file ? `${file.name} · ${formatBytes(file.size)}${file.size > TARGET_FILE ? " · over the 60 KB target" : ""}` : "";
      if (file && file.size > MAX_FILE) fileField.error.textContent = "This file is over 2 MB and will not be accepted.";
    });
    const licensed = checkbox("add-image-licensed", "Licensed third-party content", "For characters and maps owned by someone other than Behaviour (for example a film studio). Licensed images can be withdrawn on their own.", false);
    const holder = field("add-image-holder", "Rights holder", textInput("", LIMITS.holder), "Shown in the app's credits.");
    holder.root.hidden = true;
    // A character the roster marks as licensed starts with its rights holder filled in; the owner can change it.
    const picker = slotPicker("add-image", catalog, new Set(pack.entries.map((e) => e.key)), (key) => {
      const known = catalog.find(key)?.licensedHolder ?? null;
      licensed.input.checked = known !== null;
      holder.root.querySelector<HTMLInputElement>("input")!.value = known ?? "";
      holder.root.hidden = !licensed.input.checked;
    });
    licensed.input.addEventListener("change", () => (holder.root.hidden = !licensed.input.checked));
    const submit = button("Upload and add", { variant: "primary", type: "submit", icon: "upload" });
    const formMessage = el("div", { className: "inline-message" });
    const form = el(
      "form",
      { className: "stack", attrs: { novalidate: true, "aria-label": "Add an image" } },
      el("div", { className: "form-two" }, el("div", {}, fileField.root, fileInfo), picker.root),
      el("div", { className: "checks" }, licensed.root),
      holder.root,
      el("div", { className: "form-actions" }, submit),
    );
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      void add();
    });
    async function add(): Promise<void> {
      replaceChildren(formMessage);
      fileField.error.textContent = "";
      const file = fileInput.files?.[0];
      if (!file) {
        fileField.error.textContent = "Choose a PNG or JPEG file.";
        fileInput.focus();
        return;
      }
      const key = picker.read();
      if (key === null) return;
      let holderValue: string | null = null;
      if (licensed.input.checked) {
        const input = holder.root.querySelector<HTMLInputElement>("input")!;
        const value = checkText(input, holder.error, LIMITS.holder, true, "the rights holder");
        if (!value) {
          input.focus();
          return;
        }
        holderValue = value;
      }
      setBusy(submit, true, "Uploading…");
      try {
        const uploaded = await uploadChosen(ctx, file);
        const updated = await ctx.api.addEntry(pack.id, { key, sha256: uploaded.file.sha256, sourceSha256: uploaded.file.sourceSha256, licensedHolder: holderValue });
        replacePack(updated);
        draw();
        say(notice("success", `${catalog.label(key)} added`, [uploaded.converted, removedText(uploaded.removed), updated.status === "published" ? "Publish the changes to send it to the apps." : null].filter(Boolean).join(" ") || null, { live: true }));
        ctx.announce(`${catalog.label(key)} added.`);
      } catch (error) {
        setBusy(submit, false);
        ctx.reportError(error);
        replaceChildren(formMessage, failureNotice("The image was not added", error));
      }
    }
    return card("Add an image", formMessage, form);
  }

  function imagesCard(pack: Pack): HTMLElement {
    if (pack.entries.length === 0) return card("Images", emptyState("No images yet", "Add the first image below. Choose the slot it fills, such as the killer icon or a character portrait."));
    const listMessage = el("div", { className: "inline-message" });
    const items = pack.entries.map((entry) => entryItem(pack, entry, listMessage));
    return card(`Images (${count(pack.entries.length)})`, unmarkedNotice(pack, listMessage), listMessage, el("ul", { className: "entry-list" }, ...items));
  }

  /** Images whose slot is a licensed character or map (catalog.ts, licensed.ts) but that are not marked licensed. */
  function unmarkedLicensed(pack: Pack): { entry: PackEntry; holder: string }[] {
    return pack.entries.flatMap((entry) => {
      const holder = entry.licensedHolder === null ? catalog.find(entry.key)?.licensedHolder : null;
      return holder ? [{ entry, holder }] : [];
    });
  }

  function unmarkedNotice(pack: Pack, listMessage: HTMLElement): HTMLElement | null {
    const unmarked = unmarkedLicensed(pack);
    if (unmarked.length === 0) return null;
    const mark = button("Mark them as licensed", { onClick: (e) => void markLicensed(pack, listMessage, e.currentTarget as HTMLButtonElement) });
    return notice(
      "warning",
      `${plural(unmarked.length, "image is", "images are")} of licensed characters or maps but not marked licensed`,
      "Marked images name their rights holder and can be withdrawn on their own if the holder asks.",
      { children: [el("div", { className: "row-actions" }, mark)] },
    );
  }

  async function markLicensed(pack: Pack, listMessage: HTMLElement, trigger: HTMLButtonElement): Promise<void> {
    const unmarked = unmarkedLicensed(pack);
    const ok = await ctx.confirm({
      title: `Mark ${plural(unmarked.length, "image", "images")} as licensed?`,
      body: [unmarked.map(({ entry, holder }) => `${catalog.label(entry.key)}: ${holder}`).join("; "), "Each is marked with the name shown. Nothing reaches the apps until you publish."],
      confirmLabel: "Mark as licensed",
    });
    if (!ok) return;
    replaceChildren(listMessage);
    setBusy(trigger, true, "Marking…");
    let current = pack;
    let marked = 0;
    try {
      for (const { entry, holder } of unmarked) {
        current = await ctx.api.updateEntry(current.id, entry.id, { licensedHolder: holder });
        marked++;
      }
      replacePack(current);
      draw();
      say(notice("success", `${plural(marked, "image", "images")} marked as licensed`, current.status === "published" ? "Publish the changes to send them to the apps." : null, { live: true }));
    } catch (error) {
      ctx.reportError(error);
      if (marked > 0) replacePack(current);
      draw();
      say(failureNotice(`${plural(marked, "image was", "images were")} marked before an error; the rest were not`, error));
    }
  }

  function entryItem(pack: Pack, entry: PackEntry, listMessage: HTMLElement): HTMLElement {
    const label = catalog.label(entry.key);
    const status = el("p", { className: "entry-status", attrs: { role: "status" } });
    const copyAddress = button("Copy address", {
      small: true,
      variant: "ghost",
      icon: "copy",
      attrs: { "aria-label": `Copy address of ${label}` },
      onClick: async () => {
        status.textContent = (await ctx.copy(entry.url)) ? "Address copied." : "The browser did not allow copying. Select the address and copy it by hand.";
      },
    });
    const replaceInput = el("input", { className: "visually-hidden", attrs: { type: "file", accept: "image/png,image/jpeg", tabindex: -1, "aria-hidden": "true" } });
    const replace = button("Replace image", { small: true, attrs: { "aria-label": `Replace image for ${label}` }, onClick: () => replaceInput.click() });
    replaceInput.addEventListener("change", () => {
      const file = replaceInput.files?.[0];
      if (file) void replaceImage(file, replace);
    });
    const actions = el(
      "div",
      { className: "row-actions" },
      replace,
      replaceInput,
      entry.licensedHolder !== null
        ? entry.withdrawn
          ? button("Restore", { small: true, attrs: { "aria-label": `Restore ${label}` }, onClick: (e) => void setWithdrawn(false, e.currentTarget as HTMLButtonElement) })
          : button("Withdraw", { small: true, variant: "danger", attrs: { "aria-label": `Withdraw ${label}` }, onClick: (e) => void setWithdrawn(true, e.currentTarget as HTMLButtonElement) })
        : null,
      button("Remove", { small: true, variant: "danger", attrs: { "aria-label": `Remove ${label}` }, onClick: (e) => void remove(e.currentTarget as HTMLButtonElement) }),
    );

    async function replaceImage(file: File, trigger: HTMLButtonElement): Promise<void> {
      replaceChildren(listMessage);
      setBusy(trigger, true, "Uploading…");
      try {
        const uploaded = await uploadChosen(ctx, file);
        const updated = await ctx.api.updateEntry(pack.id, entry.id, { sha256: uploaded.file.sha256, sourceSha256: uploaded.file.sourceSha256 });
        replacePack(updated);
        draw();
        say(notice("success", `Image for ${label} replaced`, [uploaded.converted, removedText(uploaded.removed), updated.status === "published" ? "Publish the changes to send it to the apps." : null].filter(Boolean).join(" ") || null, { live: true }));
      } catch (error) {
        setBusy(trigger, false);
        ctx.reportError(error);
        replaceChildren(listMessage, failureNotice(`The image for ${label} was not replaced`, error));
      }
    }

    async function setWithdrawn(withdrawn: boolean, trigger: HTMLButtonElement): Promise<void> {
      const ok = await ctx.confirm(
        withdrawn
          ? {
              title: `Withdraw ${label}?`,
              body: [
                entry.live
                  ? "It leaves the signed pack list now; the rest of the pack stays as published. Apps show the fallback pack's image for this slot at their next check, or none."
                  : "It is not published, and it will stay out when the pack is published.",
                "The file stays on the content host so you can restore it. To delete it for good, remove the image afterwards.",
              ],
              confirmLabel: "Withdraw now",
              danger: true,
            }
          : { title: `Restore ${label}?`, body: [entry.live || pack.status === "published" ? "It goes back into the signed pack list now, and apps show it again at their next check." : "It will be included when the pack is published."], confirmLabel: "Restore image" },
      );
      if (!ok) return;
      setBusy(trigger, true, withdrawn ? "Withdrawing…" : "Restoring…");
      try {
        const change = await ctx.api.withdrawEntry(pack.id, entry.id, withdrawn);
        replacePack(change.pack);
        setIndex(change.index);
        draw();
        say(notice("success", withdrawn ? `${label} is withdrawn` : `${label} is restored`, change.index ? `Pack list version ${change.index.version}.` : null, { live: true }));
        ctx.announce(withdrawn ? `${label} withdrawn.` : `${label} restored.`);
      } catch (error) {
        setBusy(trigger, false);
        ctx.reportError(error);
        replaceChildren(listMessage, failureNotice(withdrawn ? `${label} was not withdrawn` : `${label} was not restored`, error));
      }
    }

    async function remove(trigger: HTMLButtonElement): Promise<void> {
      const body =
        entry.withdrawn
          ? ["It is already out of the signed pack list. Removing it deletes it from the published pack too, and its file is deleted unless another pack uses it. This cannot be undone."]
          : entry.live
            ? ["It is removed from the draft. Apps keep showing it until you publish the pack's changes; its file is deleted then, unless another pack uses it."]
            : ["It is removed from the draft, and its file is deleted unless another pack uses it."];
      const ok = await ctx.confirm({ title: `Remove ${label}?`, body, confirmLabel: "Remove image", danger: true });
      if (!ok) return;
      setBusy(trigger, true, "Removing…");
      try {
        const result = await ctx.api.removeEntry(pack.id, entry.id);
        replacePack(result.pack);
        draw();
        say(notice("success", `${label} removed`, result.filesDeleted > 0 ? "Its file was deleted from the content host." : null, { live: true }));
        ctx.announce(`${label} removed.`);
      } catch (error) {
        setBusy(trigger, false);
        ctx.reportError(error);
        replaceChildren(listMessage, failureNotice(`${label} was not removed`, error));
      }
    }

    return el(
      "li",
      { className: "entry-item" },
      thumbnail(ctx, entry, label, 96),
      el(
        "div",
        { className: "entry-body" },
        el("div", { className: "entry-title" }, el("span", { className: "entry-label", text: label }), catalog.isKnown(entry.key) ? mono(entry.key, "subtle") : null),
        entryBadges(entry, catalog),
        el("p", { className: "muted entry-meta", text: `${entry.format === "png" ? "PNG" : "JPEG"} · ${entry.width} × ${entry.height} · ${formatBytes(entry.size)}` }),
        el("div", { className: "entry-address" }, icon("image"), mono(shortAddress(entry), "subtle"), copyAddress),
        status,
      ),
      actions,
    );
  }

  void load();
}

export const packsView: View = { render };
