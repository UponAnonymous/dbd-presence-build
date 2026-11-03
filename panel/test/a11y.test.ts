// FR-ADM-11: axe-core over every section and the confirmation dialog. jsdom has no layout, so colour contrast is
// checked in a real browser by scripts/screenshots.mjs instead; every other axe rule runs here.

import axe from "axe-core";
import { describe, expect, it } from "vitest";
import { JPEG_BYTES, buttonByLabel, buttonByText, chooseFile, imageFile, mount, settle } from "./fake.ts";

async function axeViolations(): Promise<string[]> {
  const result = await axe.run(document, { rules: { "color-contrast": { enabled: false } }, resultTypes: ["violations"] });
  return result.violations.map((v) => `${v.id} (${v.impact ?? "?"}): ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
}

describe("accessibility (axe-core)", () => {
  it.each(["dashboard", "invites", "users", "uploads", "packs", "characters", "apps", "access", "audit", "settings"])("the %s section has no violations", async (section) => {
    await mount(section);
    expect(await axeViolations()).toEqual([]);
  });

  it("the pack editor, with a file chosen and the custom slot open, has no violations", async () => {
    const { root } = await mount("packs");
    buttonByLabel(root, "Edit Original").click();
    await settle();
    chooseFile(root.querySelector<HTMLInputElement>("#add-image-file")!, imageFile("a.jpg", JPEG_BYTES));
    const slot = root.querySelector<HTMLSelectElement>("#add-image-slot")!;
    slot.value = "custom";
    slot.dispatchEvent(new Event("change"));
    root.querySelector<HTMLInputElement>("#edit-pack-third")!.click();
    expect(await axeViolations()).toEqual([]);
  });

  it("the new-pack form and the typed delete confirmation have no violations", async () => {
    const { root } = await mount("packs");
    buttonByText(root, "New pack").click();
    await settle();
    expect(await axeViolations()).toEqual([]);
    buttonByText(root, "Cancel").click();
    await settle();
    buttonByLabel(root, "Unpublish Original").click();
    await settle();
    expect(await axeViolations()).toEqual([]);
  });

  it("the staging banner and an open confirmation dialog have no violations", async () => {
    const { root } = await mount("users", { hostname: "admin-staging.arcavale.com" });
    buttonByLabel(root, "Delete Rook").click();
    await settle();
    expect(await axeViolations()).toEqual([]);
  });
});
