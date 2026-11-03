// FR-ADM-12 (Characters: added automatically with Undo, add and rename by hand, remove and restore, allow a removed name again, the
// owner's game accounts), FR-ADM-13 (default pack choice and the characters without a default picture), D35, FR-ADM-04 (names as text
// only; images only from this environment's content host), FR-ADM-05 (destructive actions confirmed).

import { describe, expect, it } from "vitest";
import { isPackImageUrl } from "../src/cdn.ts";
import { FakeBackend, HOSTILE, buttonByLabel, buttonByText, ids, mount, openDialog, settle } from "./fake.ts";

const writes = <T extends { method: string }>(requests: T[]): T[] => requests.filter((r) => r.method !== "GET");

async function confirmDialog(): Promise<void> {
  const dialog = openDialog();
  buttonByText(dialog, /Undo|Remove|Mark as my game account|Use as default|Stop using as default/).click();
  await settle();
}

describe("Characters", () => {
  it("shows the signed list, what was added automatically with Undo, names waiting, removed names and the game accounts", async () => {
    const { root } = await mount("characters");
    expect(root.querySelector("h1")?.textContent).toBe("Characters");
    expect(root.textContent).toContain("Signed character list");
    expect(root.textContent).toContain("1793700000");
    expect(root.textContent).toContain(`Added automatically: ${HOSTILE} (killer)`);
    expect(buttonByLabel(root, `Undo ${HOSTILE}`)).toBeTruthy();
    expect(root.textContent).toContain("the warden");
    expect(root.textContent).toContain("2 of 3");
    expect(root.textContent).toContain("mara quell");
    expect(root.querySelector('[data-code="K01"]')!.textContent).toContain("Changes with an app release");
    expect(root.querySelector('[data-code="S55"]')!.textContent).toContain("Removed");
  });

  it("renders hostile names as text only and loads default pictures only from the content host", async () => {
    const { root } = await mount("characters");
    expect(root.querySelectorAll("script, iframe, object, embed").length).toBe(0);
    for (const node of root.querySelectorAll("*")) for (const attr of node.getAttributeNames()) expect(attr.startsWith("on")).toBe(false);
    const images = [...root.querySelectorAll("img")];
    // The default pack (Original) has a live picture for the Trapper; Ghost Face's is licensed but live; the others have none.
    expect(images.length).toBeGreaterThan(0);
    for (const img of images) expect(isPackImageUrl(img.getAttribute("src")!, "production")).toBe(true);
    expect(root.querySelector('[data-code="K45"]')!.textContent).toContain("No picture");
    expect(root.textContent).toContain("Default pictures come from Original.");
  });

  it("Undo asks first, then removes the learned character and lists its name as removed", async () => {
    const { root, backend } = await mount("characters");
    buttonByLabel(root, `Undo ${HOSTILE}`).click();
    await settle();
    expect(openDialog().textContent).toContain("will not be added again on its own");
    await confirmDialog();
    const sent = writes(backend.requests);
    expect(sent.map((r) => [r.method, r.url, r.body])).toEqual([["POST", "/api/roster/characters/K45/withdraw", { withdrawn: true }]]);
    expect(root.querySelector('[data-code="K45"]')!.textContent).toContain("Removed");
    expect(root.textContent).toContain("Nothing yet.");
  });

  it("allows a removed name again and restores a removed character", async () => {
    const { root, backend } = await mount("characters");
    buttonByLabel(root, "Allow mara quell again").click();
    await settle();
    expect(writes(backend.requests).at(-1)).toMatchObject({ method: "DELETE", url: "/api/roster/blocks/mara-quell" });
    buttonByLabel(root, "Restore Mara Quell").click();
    await settle();
    expect(writes(backend.requests).at(-1)).toMatchObject({ method: "POST", url: "/api/roster/characters/S55/withdraw", body: { withdrawn: false } });
    expect(root.querySelector('[data-code="S55"]')!.textContent).toContain("In the list");
  });

  it("clears the names waiting to be added only after asking (I2)", async () => {
    const { root, backend } = await mount("characters");
    expect(root.textContent).toContain("the warden");
    expect(root.textContent).toContain("at least two days");
    buttonByLabel(root, "Clear all names waiting to be added").click();
    await settle();
    expect(openDialog().textContent).toContain("forgotten");
    const dialog = openDialog();
    buttonByText(dialog, /Cancel|Keep/).click();
    await settle();
    expect(writes(backend.requests)).toEqual([]);
    buttonByLabel(root, "Clear all names waiting to be added").click();
    await settle();
    buttonByText(openDialog(), /^Clear$/).click();
    await settle();
    expect(writes(backend.requests).map((r) => [r.method, r.url])).toEqual([["DELETE", "/api/roster/sightings"]]);
    expect(root.textContent).toContain("No unknown names are waiting.");
  });

  it("adds a character by hand with the next free code of its role, and checks the form first", async () => {
    const { root, backend } = await mount("characters");
    const code = root.querySelector<HTMLInputElement>("#add-character-code")!;
    expect(code.value).toBe("K46");
    const role = root.querySelector<HTMLSelectElement>("#add-character-role")!;
    role.value = "survivor";
    role.dispatchEvent(new Event("change"));
    expect(code.value).toBe("S56");
    buttonByText(root, "Add character").click();
    await settle();
    expect(root.querySelector("#add-character-name-error")!.textContent).toBe("Enter the character's name.");
    code.value = "K47";
    root.querySelector<HTMLInputElement>("#add-character-name")!.value = "Nova Reyes";
    buttonByText(root, "Add character").click();
    await settle();
    expect(root.querySelector("#add-character-code-error")!.textContent).toBe("Enter S and two digits.");
    expect(writes(backend.requests)).toEqual([]);
    code.value = "S56";
    buttonByText(root, "Add character").click();
    await settle();
    expect(writes(backend.requests).map((r) => r.body)).toEqual([{ code: "S56", displayName: "Nova Reyes" }]);
    expect(root.querySelector('[data-code="S56"]')!.textContent).toContain("Added by you");
  });

  it("shows the service's reason when a code is taken", async () => {
    const backend = new FakeBackend();
    const { root } = await mount("characters", { backend });
    root.querySelector<HTMLInputElement>("#add-character-code")!.value = "K45";
    root.querySelector<HTMLInputElement>("#add-character-name")!.value = "The Duplicate";
    buttonByText(root, "Add character").click();
    await settle();
    expect(root.textContent).toContain("A character already has this code.");
  });

  it("renames a learned character", async () => {
    const { root, backend } = await mount("characters");
    buttonByLabel(root, `Rename ${HOSTILE}`).click();
    await settle();
    root.querySelector<HTMLInputElement>('[aria-label="New name for K45"]')!.value = "The Lantern - Ada Voss";
    root.querySelector<HTMLInputElement>('[aria-label="New on-screen name for K45"]')!.value = "The Lantern";
    buttonByLabel(root, "Save the name of K45").click();
    await settle();
    expect(writes(backend.requests).at(-1)).toMatchObject({ method: "PUT", url: "/api/roster/characters/K45", body: { displayName: "The Lantern - Ada Voss", screenName: "The Lantern" } });
    expect(root.querySelector('[data-code="K45"]')!.textContent).toContain("The Lantern - Ada Voss (on screen: The Lantern)");
  });

  it("marks the owner's game account after a confirmation, and stops it", async () => {
    const { root, backend } = await mount("characters");
    const select = root.querySelector<HTMLSelectElement>("#roster-reporter")!;
    buttonByText(root, "Mark as my game account").click();
    await settle();
    expect(root.querySelector("#roster-reporter-error")!.textContent).toBe("Choose the account you play with.");
    select.value = ids.user2;
    buttonByText(root, "Mark as my game account").click();
    await settle();
    expect(openDialog().textContent).toContain("No player names and no images are sent.");
    await confirmDialog();
    expect(writes(backend.requests).at(-1)).toMatchObject({ method: "PUT", url: `/api/users/${ids.user2}/roster-reporter`, body: { enabled: true } });
    buttonByLabel(root, "Stop reporting from Rook").click();
    await settle();
    expect(writes(backend.requests).at(-1)).toMatchObject({ body: { enabled: false } });
  });

  it("warns when the list has not been re-signed for 25 days", async () => {
    const backend = new FakeBackend();
    backend.roster.roster.stale = true;
    const { root } = await mount("characters", { backend });
    expect(root.querySelector('[role="status"]')?.textContent ?? root.textContent).toContain("has not been re-signed for 25 days");
  });

  it("shows an error state with Try again when the list cannot be loaded", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/roster", { status: 503, error: "not_configured" });
    const { root } = await mount("characters", { backend });
    expect(root.querySelector(".state-error")?.textContent).toContain("not fully configured");
  });
});

describe("the default pack (FR-ADM-13)", () => {
  it("shows which pack holds the default pictures and moves the mark only after a confirmation", async () => {
    const { root, backend } = await mount("packs");
    expect(root.querySelector('[aria-labelledby="pack-original-title"]')!.textContent).toContain("Default pictures");
    buttonByLabel(root, "Use nightlight-ash as the default pictures".replace("nightlight-ash", HOSTILE)).click();
    await settle();
    await confirmDialog();
    expect(root.textContent).toContain("Another pack already holds the default pictures.");
    buttonByLabel(root, "Stop using Original as the default pictures").click();
    await settle();
    await confirmDialog();
    expect(writes(backend.requests).filter((r) => r.url.endsWith("/default")).map((r) => [r.url, r.body])).toEqual([
      ["/api/packs/nightlight-ash/default", { default: true }],
      ["/api/packs/original/default", { default: false }],
    ]);
    expect(root.querySelector('[aria-labelledby="pack-original-title"]')!.textContent).not.toContain("Default pictures");
  });
});
