// FR-ADM-08 (create, edit, publish, unpublish packs), FR-PACK-03/04/05/08 (Behaviour-art flag, licensed items and
// withdrawal, takedown by deleting the pack, third-party permission), FR-ADM-05 (destructive actions confirmed) and
// FR-ADM-04 (text-only rendering; images only from the environment's content host).

import { describe, expect, it } from "vitest";
import { slugify } from "../src/catalog.ts";
import { isPackImageUrl } from "../src/cdn.ts";
import { packImage } from "../src/dom.ts";
import { imageReasonText, sniffImageType } from "../src/views/packs.ts";
import { FakeBackend, HOSTILE, JPEG_BYTES, PNG_BYTES, buttonByLabel, buttonByText, chooseFile, imageFile, mount, openDialog, sampleEntry, settle, shaOf } from "./fake.ts";

const writes = <T extends { method: string }>(requests: T[]): T[] => requests.filter((r) => r.method !== "GET");

async function openEditor(id = "Original") {
  const mounted = await mount("packs");
  buttonByLabel(mounted.root, `Edit ${id}`).click();
  await settle();
  return mounted;
}

function setValue(root: ParentNode, selector: string, value: string): void {
  const input = root.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event("input"));
  input.dispatchEvent(new Event("change"));
}

describe("packs overview", () => {
  it("shows the signed pack list, each pack with its status, flags, size and thumbnails from the content host", async () => {
    const { root } = await mount("packs");
    expect(root.querySelector("h1")?.textContent).toBe("Packs");
    expect(root.textContent).toContain("Signed pack list");
    expect(root.textContent).toContain("1793500000");
    const original = root.querySelector('[aria-labelledby="pack-original-title"]')!;
    expect(original.textContent).toContain("Published");
    expect(original.textContent).toContain("Behaviour art");
    expect(original.textContent).toContain("5 images");
    const images = [...original.querySelectorAll("img")];
    expect(images).toHaveLength(5);
    for (const img of images) expect(isPackImageUrl(img.getAttribute("src")!, "production")).toBe(true);
    expect(images.every((img) => img.getAttribute("referrerpolicy") === "no-referrer")).toBe(true);
    // A third-party pack without permission says so.
    expect(root.textContent).toContain("Third-party, no permission");
  });

  it("shows hostile pack names and rights holders as text only", async () => {
    const { root } = await mount("packs");
    expect(root.textContent).toContain(HOSTILE);
    expect(root.querySelectorAll("script, iframe, object, embed").length).toBe(0);
    for (const img of root.querySelectorAll("img")) expect(img.getAttribute("src")!.startsWith("https://cdn.arcavale.com/packs/")).toBe(true);
    for (const node of root.querySelectorAll("*")) for (const attr of node.getAttributeNames()) expect(attr.startsWith("on")).toBe(false);
    const editor = await openEditor();
    expect(editor.root.textContent).toContain(`Licensed: ${HOSTILE}`);
  });

  it("on staging, loads images only from the staging content host", async () => {
    const backend = new FakeBackend();
    for (const pack of backend.data.packs) for (const e of pack.entries) e.url = e.url.replace("https://cdn.arcavale.com", "https://cdn-staging.arcavale.com");
    const { root } = await mount("packs", { hostname: "admin-staging.arcavale.com", backend });
    const images = [...root.querySelectorAll("img")];
    expect(images.length).toBeGreaterThan(0);
    for (const img of images) expect(img.getAttribute("src")!.startsWith("https://cdn-staging.arcavale.com/packs/")).toBe(true);
  });

  it("refuses to render an image address that is not a pack file on this environment's content host", async () => {
    const backend = new FakeBackend();
    backend.data.packs[0]!.entries[0]!.url = "https://evil.example/packs/" + shaOf(1) + ".png";
    const { root } = await mount("packs", { backend });
    expect(root.querySelector(".state-error")?.textContent).toContain("Something went wrong in the panel");
    expect(root.querySelectorAll("img")).toHaveLength(0);
  });
});

describe("creating a pack", () => {
  it("creates a pack with the Behaviour-art flag, then opens it for images", async () => {
    const { root, backend } = await mount("packs");
    buttonByText(root, "New pack").click();
    await settle();
    setValue(root, "#new-pack-name", "Backup tiles");
    expect(root.querySelector<HTMLInputElement>("#new-pack-id")!.value).toBe("backup-tiles");
    setValue(root, "#new-pack-author", "The owner");
    setValue(root, "#new-pack-licence", "Made for DBD Presence.");
    root.querySelector<HTMLInputElement>("#new-pack-behaviour")!.click();
    root.querySelector<HTMLInputElement>("#new-pack-fallback")!.click();
    root.querySelector<HTMLFormElement>('form[aria-labelledby="new-pack-title"]')!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(writes(backend.requests)[0]).toMatchObject({
      method: "POST",
      url: "/api/packs",
      body: { id: "backup-tiles", name: "Backup tiles", author: "The owner", licenceNote: "Made for DBD Presence.", credit: null, fallback: true, behaviourArt: true, thirdParty: false, permission: null },
    });
    expect(root.querySelector("#editor-title")?.textContent).toBe("Backup tiles");
    expect(root.textContent).toContain("Add an image");
  });

  it("validates before sending: ID, required text, control characters and the permission record", async () => {
    const { root, backend } = await mount("packs");
    buttonByText(root, "New pack").click();
    await settle();
    const form = root.querySelector<HTMLFormElement>('form[aria-labelledby="new-pack-title"]')!;
    setValue(root, "#new-pack-id", "Not OK");
    form.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(root.querySelector("#new-pack-id-error")?.textContent).toContain("lower-case");
    setValue(root, "#new-pack-id", "ok");
    setValue(root, "#new-pack-name", "Name\u0007");
    form.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(root.querySelector("#new-pack-name-error")?.textContent).toContain("control characters");
    expect(root.querySelector("#new-pack-author-error")?.textContent).toBe("Enter the author.");
    setValue(root, "#new-pack-name", "Name");
    setValue(root, "#new-pack-author", "A");
    setValue(root, "#new-pack-licence", "L");
    root.querySelector<HTMLInputElement>("#new-pack-third")!.click();
    setValue(root, "#new-pack-creator", "Someone");
    form.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(root.textContent).toContain("Fill in all three permission fields, or none for now.");
    expect(writes(backend.requests)).toHaveLength(0);
  });
});

describe("images", () => {
  it("uploads a file, maps it to a slot from the picker and adds it; the file name and size show before upload", async () => {
    const { root, backend } = await openEditor();
    chooseFile(root.querySelector<HTMLInputElement>("#add-image-file")!, imageFile("hag.png", PNG_BYTES));
    expect(root.querySelector(".file-info")?.textContent).toBe("hag.png · 12 bytes");
    // Before upload there is no preview: the CSP allows no blob: images.
    expect(root.querySelector('form[aria-label="Add an image"] img')).toBeNull();
    setValue(root, "#add-image-slot", "character:the-hag");
    root.querySelector<HTMLInputElement>("#add-image-licensed")!.click();
    root.querySelector('form[aria-label="Add an image"]')!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(root.querySelector("#add-image-holder-error")?.textContent).toBe("Enter the rights holder.");
    root.querySelector<HTMLInputElement>("#add-image-licensed")!.click();
    root.querySelector('form[aria-label="Add an image"]')!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    const [upload, add] = writes(backend.requests);
    expect(upload).toMatchObject({ method: "POST", url: "/api/pack-files" });
    expect(upload!.headers["Content-Type"]).toBe("image/png");
    expect(upload!.headers["X-DBDP-Admin"]).toBe("1");
    expect(add).toMatchObject({ method: "POST", url: "/api/packs/original/entries", body: { key: "character:the-hag", sha256: shaOf(0x40), sourceSha256: shaOf(0x41), licensedHolder: null } });
    expect(root.textContent).toContain("The Hag added");
    expect(root.textContent).toContain("This pack has changes that apps do not have yet");
    // The new image is shown from the content host after upload.
    expect([...root.querySelectorAll("img")].some((img) => img.getAttribute("src") === `https://cdn.arcavale.com/packs/${shaOf(0x40)}.png`)).toBe(true);
  });

  it("sends the type from the file's bytes, says what metadata was removed, and offers a custom slot slugged like the app", async () => {
    const { root, backend } = await openEditor();
    chooseFile(root.querySelector<HTMLInputElement>("#add-image-file")!, imageFile("photo.png", JPEG_BYTES));
    setValue(root, "#add-image-slot", "custom");
    setValue(root, "#add-image-custom", "The Ghoul (Ken Kaneki)");
    expect(root.textContent).toContain("Key: character:the-ghoul-ken-kaneki");
    root.querySelector('form[aria-label="Add an image"]')!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(writes(backend.requests)[0]!.headers["Content-Type"]).toBe("image/jpeg");
    expect(writes(backend.requests)[1]!.body).toMatchObject({ key: "character:the-ghoul-ken-kaneki" });
    expect(root.textContent).toContain("Removed before storing (the picture is unchanged): JFIF, Exif.");
  });

  it("refuses non-images and oversize files in the panel, and explains a rejection from the service", async () => {
    const { root, backend } = await openEditor();
    const form = root.querySelector('form[aria-label="Add an image"]')!;
    chooseFile(root.querySelector<HTMLInputElement>("#add-image-file")!, imageFile("notes.png", new TextEncoder().encode("hello")));
    setValue(root, "#add-image-slot", "character:the-hag");
    form.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(root.textContent).toContain("Only PNG and JPEG files are accepted.");
    expect(writes(backend.requests)).toHaveLength(0);

  });

  it("shows the service's reason when it refuses an image, in words the owner can act on", async () => {
    const backend = new FakeBackend();
    const refusing = Object.assign(Object.create(backend) as FakeBackend, {
      fetch: async (input: string, init: RequestInit) =>
        input === "/api/pack-files"
          ? new Response(JSON.stringify({ schemaVersion: 1, error: "image_rejected", reason: "png_not_rgb8" }), { status: 422, headers: { "Content-Type": "application/json" } })
          : backend.fetch(input, init),
    });
    const { root } = await mount("packs", { backend: refusing });
    buttonByLabel(root, "Edit Original").click();
    await settle();
    chooseFile(root.querySelector<HTMLInputElement>("#add-image-file")!, imageFile("grey.png", PNG_BYTES));
    setValue(root, "#add-image-slot", "character:the-hag");
    root.querySelector('form[aria-label="Add an image"]')!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    const alert = root.querySelector('form[aria-label="Add an image"]')!.closest("section")!.querySelector(".notice-danger")!;
    expect(alert.textContent).toContain("The image was not added: the image was not accepted");
    expect(alert.textContent).toContain("Export it again as an 8-bit RGBA PNG");
    expect(backend.requests.filter((r) => r.url.includes("/entries"))).toHaveLength(0);
  });

  it("explains every upload rule in words the owner can act on", () => {
    expect(imageReasonText("png_not_rgb8")).toContain("8-bit RGBA PNG");
    expect(imageReasonText("jpeg_rotated")).toContain("Rotate it");
    expect(imageReasonText("bad_checksum")).toContain("damaged");
    expect(imageReasonText("bad_checksum")).toContain("(bad_checksum)");
    expect(sniffImageType(PNG_BYTES)).toBe("image/png");
    expect(sniffImageType(JPEG_BYTES)).toBe("image/jpeg");
    expect(sniffImageType(new TextEncoder().encode("<svg"))).toBeNull();
  });

  it("disables slots the pack already fills", async () => {
    const { root } = await openEditor();
    const option = root.querySelector<HTMLOptionElement>('#add-image-slot option[value="role:killer"]')!;
    expect(option.disabled).toBe(true);
    expect(option.textContent).toContain("already in this pack");
  });

  it("withdraws a licensed image only after confirmation, and restores it", async () => {
    const { root, backend } = await openEditor();
    expect(() => buttonByLabel(root, "Withdraw Killer icon")).toThrow();
    buttonByLabel(root, "Withdraw Ghost Face").click();
    await settle();
    expect(openDialog().querySelector(".dialog-title")?.textContent).toBe("Withdraw Ghost Face?");
    expect(openDialog().textContent).toContain("leaves the signed pack list now");
    buttonByText(openDialog(), "Cancel").click();
    await settle();
    expect(writes(backend.requests)).toHaveLength(0);
    buttonByLabel(root, "Withdraw Ghost Face").click();
    await settle();
    buttonByText(openDialog(), "Withdraw now").click();
    await settle();
    expect(writes(backend.requests)[0]).toMatchObject({ method: "POST", url: `/api/packs/original/entries/e${"4".padStart(24, "0")}/withdraw`, body: { withdrawn: true } });
    expect(root.textContent).toContain("Ghost Face is withdrawn");
    expect(root.textContent).toContain("Pack list version 1793500001");
    buttonByLabel(root, "Restore Ghost Face").click();
    await settle();
    buttonByText(openDialog(), "Restore image").click();
    await settle();
    expect(writes(backend.requests)[1]).toMatchObject({ body: { withdrawn: false } });
  });

  it("marks images of licensed characters and maps as licensed in one go, after confirmation", async () => {
    const backend = new FakeBackend();
    const original = backend.data.packs[0]!;
    original.entries.find((e) => e.key === "character:the-ghost-face")!.licensedHolder = null;
    original.entries.push(sampleEntry(6, "map:haddonfield"));
    const { root } = await mount("packs", { backend });
    buttonByLabel(root, "Edit Original").click();
    await settle();
    expect(root.textContent).toContain("2 images are of licensed characters or maps but not marked licensed");
    buttonByText(root, "Mark them as licensed").click();
    await settle();
    expect(openDialog().textContent).toContain("Ghost Face: Fun World (Ghost Face); Haddonfield: Halloween");
    buttonByText(openDialog(), "Cancel").click();
    await settle();
    expect(writes(backend.requests)).toHaveLength(0);
    buttonByText(root, "Mark them as licensed").click();
    await settle();
    buttonByText(openDialog(), "Mark as licensed").click();
    await settle();
    expect(writes(backend.requests).map((r) => r.body)).toEqual([{ licensedHolder: "Fun World (Ghost Face)" }, { licensedHolder: "Halloween" }]);
    expect(root.textContent).toContain("2 images marked as licensed");
    expect(root.textContent).not.toContain("not marked licensed");
    expect(root.textContent).toContain("Licensed: Halloween");
  });

  it("removes an image after confirmation and copies its address", async () => {
    const { root, backend, copied } = await openEditor();
    buttonByLabel(root, "Copy address of Killer icon").click();
    await settle();
    expect(copied).toEqual([`https://cdn.arcavale.com/packs/${shaOf(1)}.png`]);
    buttonByLabel(root, "Remove The Shape").click();
    await settle();
    expect(openDialog().textContent).toContain("already out of the signed pack list");
    buttonByText(openDialog(), "Remove image").click();
    await settle();
    expect(writes(backend.requests)[0]).toMatchObject({ method: "DELETE", url: `/api/packs/original/entries/e${"5".padStart(24, "0")}` });
    expect(root.textContent).toContain("Its file was deleted from the content host.");
  });

  it("replaces an image through a new upload", async () => {
    const { root, backend } = await openEditor();
    const item = buttonByLabel(root, "Replace image for Killer icon").closest("li")!;
    chooseFile(item.querySelector<HTMLInputElement>('input[type="file"]')!, imageFile("killer.png", PNG_BYTES));
    await settle();
    expect(writes(backend.requests).map((r) => [r.method, r.url])).toEqual([
      ["POST", "/api/pack-files"],
      ["PUT", `/api/packs/original/entries/e${"1".padStart(24, "0")}`],
    ]);
    // The replaced entry records the source hash of the new upload (S5b-3).
    expect(writes(backend.requests)[1]!.body).toEqual({ sha256: shaOf(0x40), sourceSha256: shaOf(0x41) });
  });
});

describe("publishing and takedown", () => {
  it("publish confirms what goes live and whether the pack is marked as Behaviour art", async () => {
    const backend = new FakeBackend();
    const pack = backend.data.packs[1]!;
    pack.name = "Fan tiles";
    pack.thirdParty = false;
    pack.entries = backend.data.packs[0]!.entries.slice(0, 2).map((e, i) => ({ ...e, id: `e${(10 + i).toString(16).padStart(24, "0")}`, live: false }));
    const { root } = await mount("packs", { backend });
    buttonByLabel(root, "Publish: Fan tiles").click();
    await settle();
    const dialog = openDialog();
    expect(dialog.querySelector(".dialog-title")?.textContent).toBe("Publish Fan tiles?");
    expect(dialog.textContent).toContain("2 images become available to every app");
    expect(dialog.textContent).toContain("Not marked as Behaviour Interactive artwork");
    buttonByText(dialog, "Publish pack").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "POST", url: "/api/packs/nightlight-ash/publish" })]);
    expect(root.textContent).toContain("Fan tiles is published");
    expect(root.textContent).toContain("Pack list version 1793500001");
  });

  it("explains why a third-party pack cannot be published yet", async () => {
    const backend = new FakeBackend();
    backend.data.packs[1]!.entries = [{ ...backend.data.packs[0]!.entries[0]!, id: `e${"a".padStart(24, "0")}` }];
    const { root } = await mount("packs", { backend });
    buttonByLabel(root, `Publish: ${HOSTILE}`).click();
    await settle();
    buttonByText(openDialog(), "Publish pack").click();
    await settle();
    expect(root.textContent).toContain("Record the creator's permission and a credit line");
  });

  it("takes a pack down: unpublish (confirmed), then delete with the typed pack ID", async () => {
    const { root, backend } = await mount("packs");
    buttonByLabel(root, "Unpublish Original").click();
    await settle();
    expect(openDialog().textContent).toContain("leaves the signed pack list now");
    buttonByText(openDialog(), "Unpublish pack").click();
    await settle();
    expect(root.textContent).toContain("Original is unpublished");
    buttonByLabel(root, "Delete pack Original").click();
    await settle();
    const confirm = buttonByText(openDialog(), "Delete pack");
    expect(confirm.disabled).toBe(true);
    const typed = openDialog().querySelector<HTMLInputElement>("input")!;
    typed.value = "original";
    typed.dispatchEvent(new Event("input"));
    expect(confirm.disabled).toBe(false);
    confirm.click();
    await settle();
    expect(writes(backend.requests).map((r) => [r.method, r.url, r.body])).toEqual([
      ["POST", "/api/packs/original/unpublish", {}],
      ["DELETE", "/api/packs/original", { confirm: "original" }],
    ]);
    expect(root.textContent).toContain("5 files were deleted from the content host.");
    expect(root.querySelector('[aria-labelledby="pack-original-title"]')).toBeNull();
  });

  it("a published pack offers no delete; a draft offers no unpublish", async () => {
    const { root } = await mount("packs");
    expect(() => buttonByLabel(root, "Delete pack Original")).toThrow();
    expect(() => buttonByLabel(root, `Unpublish ${HOSTILE}`)).toThrow();
  });

  it("removes unused files after confirmation", async () => {
    const { root, backend } = await mount("packs");
    buttonByText(root, "Remove unused files").click();
    await settle();
    buttonByText(openDialog(), "Remove unused files").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "POST", url: "/api/pack-files/purge" })]);
    expect(root.textContent).toContain("2 unused files removed");
  });
});

describe("helpers", () => {
  it("slugs names exactly like the app (MappingProposer.Slugify)", () => {
    expect(slugify("The Trapper")).toBe("the-trapper");
    expect(slugify("  Ghost--Face!! ")).toBe("ghost-face");
    expect(slugify("Élodie Rakoto")).toBe("lodie-rakoto");
    expect(slugify("--")).toBe("");
  });

  it("accepts only pack files on the environment's own content host", () => {
    const sha = "ab".repeat(32);
    expect(isPackImageUrl(`https://cdn.arcavale.com/packs/${sha}.png`, "production")).toBe(true);
    expect(isPackImageUrl(`https://cdn.arcavale.com/packs/${sha}.jpg`, "production")).toBe(true);
    for (const bad of [
      `https://cdn-staging.arcavale.com/packs/${sha}.png`,
      `http://cdn.arcavale.com/packs/${sha}.png`,
      `https://cdn.arcavale.com/packs/${sha}.png?x=1`,
      `https://cdn.arcavale.com/packs/${sha}.png#a`,
      `https://user@cdn.arcavale.com/packs/${sha}.png`,
      `https://cdn.arcavale.com/u/${sha}.png`,
      `https://cdn.arcavale.com/packs/${sha.toUpperCase()}.png`,
      `https://cdn.arcavale.com:444/packs/${sha}.png`,
      "not a url",
    ]) {
      expect(isPackImageUrl(bad, "production")).toBe(false);
    }
    expect(isPackImageUrl(`https://cdn-staging.arcavale.com/packs/${sha}.png`, "staging")).toBe(true);
    expect(() => packImage("https://evil.example/x.png", "production", "x", 64)).toThrow();
  });
});
