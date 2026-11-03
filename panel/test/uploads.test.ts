// FR-ADM-09 (uploads: view, take down, block per user and globally), D18 (storage use with the 6 GB warning), FR-ADM-04
// (uploaders' names as text only; thumbnails only from this environment's content host, /u/ paths).

import { describe, expect, it } from "vitest";
import { isUploadImageUrl } from "../src/cdn.ts";
import { HOSTILE, UPLOAD_KEY, FakeBackend, buttonByLabel, buttonByText, ids, mount, openDialog, sampleStorage, settle } from "./fake.ts";

const writes = (requests: { method: string }[]) => requests.filter((r) => r.method !== "GET");

describe("upload addresses", () => {
  it("accept only /u/<32 hex>.png|jpg on the environment's own content host", () => {
    const key = "ab".repeat(16);
    expect(isUploadImageUrl(`https://cdn.arcavale.com/u/${key}.png`, "production")).toBe(true);
    expect(isUploadImageUrl(`https://cdn.arcavale.com/u/${key}.jpg`, "production")).toBe(true);
    expect(isUploadImageUrl(`https://cdn-staging.arcavale.com/u/${key}.png`, "staging")).toBe(true);
    for (const url of [
      `https://cdn-staging.arcavale.com/u/${key}.png`,
      `https://cdn.arcavale.com/u/${key}.png?x=1`,
      `https://cdn.arcavale.com/u/${key}.png#a`,
      `https://cdn.arcavale.com/u/${key.toUpperCase()}.png`,
      `https://cdn.arcavale.com/u/${key}.gif`,
      `https://cdn.arcavale.com/packs/${key}.png`,
      `https://user:pw@cdn.arcavale.com/u/${key}.png`,
      `http://cdn.arcavale.com/u/${key}.png`,
      "https://evil.example/u/track.png",
      "not a url",
    ]) {
      expect(isUploadImageUrl(url, "production"), url).toBe(false);
    }
  });
});

describe("Uploads section", () => {
  it("lists uploads with the uploader's name as text, and loads a thumbnail only for an upload address", async () => {
    const { root } = await mount("uploads");
    expect(root.querySelector("h1")?.textContent).toBe("Uploads");
    expect(root.textContent).toContain("unlisted, not private");
    expect(root.textContent).toContain(HOSTILE);
    const images = [...root.querySelectorAll("img")];
    expect(images.map((i) => i.getAttribute("src"))).toEqual([`https://cdn.arcavale.com/u/${UPLOAD_KEY(1)}.png`]);
    expect(images[0]!.getAttribute("referrerpolicy")).toBe("no-referrer");
    expect(root.textContent).toContain("Preview not shown (unexpected address)");
    // Nothing from the hostile name became markup.
    expect(root.querySelectorAll("script, iframe, object, embed").length).toBe(0);
    for (const node of root.querySelectorAll("*")) for (const attr of node.getAttributeNames()) expect(attr.startsWith("on")).toBe(false);
  });

  it("shows storage use against the 8 GB cap, warns from 6 GB and says when it is full", async () => {
    const backend = new FakeBackend();
    let { root } = await mount("uploads", { backend });
    expect(root.textContent).toContain("1.23 GB of 8.00 GB used");
    expect(root.querySelector("meter")?.getAttribute("value")).toBe("1234567890");
    expect(root.textContent).not.toContain("storage is at 6 GB");

    backend.data.storage = sampleStorage(6_100_000_000);
    ({ root } = await mount("uploads", { backend }));
    // From 6 GB new uploads are refused (L3), which is the warning the owner sees.
    expect(root.textContent).toContain("New uploads are refused: storage is at 6 GB");

    backend.data.storage = sampleStorage(8_000_000_000);
    ({ root } = await mount("uploads", { backend }));
    expect(root.textContent).toContain("Storage is full");
  });

  it("uses the caps the service reports: staging's 750 MB cap with the warning and upload stop at 500 MB", async () => {
    const backend = new FakeBackend();
    backend.data.storage = sampleStorage(120_000_000, "staging");
    let { root } = await mount("uploads", { backend });
    expect(root.textContent).toContain("0.12 GB of 0.75 GB used (warning and no new uploads from 0.50 GB).");
    expect(root.querySelector("meter")?.getAttribute("max")).toBe("750000000");
    expect(root.querySelector("meter")?.getAttribute("high")).toBe("500000000");
    expect(root.querySelector(".notice-warning")).toBeNull();

    backend.data.storage = sampleStorage(500_000_000, "staging");
    ({ root } = await mount("uploads", { backend }));
    expect(root.textContent).toContain("New uploads are refused: storage is at 500 MB");
    expect(root.textContent).toContain("the space up to 750 MB stays for audit exports");
    expect(root.textContent).not.toMatch(/\b[68] GB\b/);

    // The warning alone (no upload stop), as a service might report it, names the reported numbers too.
    backend.data.storage = { ...sampleStorage(510_000_000, "staging"), uploadsFull: false };
    ({ root } = await mount("uploads", { backend }));
    expect(root.textContent).toContain("Storage is above 500 MB");
    expect(root.textContent).toContain("New files are refused once the total reaches 750 MB");

    backend.data.storage = sampleStorage(750_000_000, "staging");
    ({ root } = await mount("uploads", { backend }));
    expect(root.textContent).toContain("Storage is full");
  });

  it("names production's limits exactly as before: the warning from 6 GB and the cap at 8 GB", async () => {
    const backend = new FakeBackend();
    backend.data.storage = { ...sampleStorage(6_100_000_000), uploadsFull: false };
    const { root } = await mount("uploads", { backend });
    expect(root.textContent).toContain("Storage is above 6 GB");
    expect(root.textContent).toContain("New files are refused once the total reaches 8 GB");
  });

  it("takes an upload down only after confirmation, and says Discord may show a cached copy", async () => {
    const { root, backend } = await mount("uploads");
    buttonByLabel(root, `Take down upload 88888888 by ${HOSTILE}`).click();
    await settle();
    expect(openDialog().textContent).toContain("Discord's media proxy may keep showing a cached copy");
    buttonByText(openDialog(), "Cancel").click();
    await settle();
    expect(writes(backend.requests)).toHaveLength(0);

    buttonByLabel(root, `Take down upload 88888888 by ${HOSTILE}`).click();
    await settle();
    buttonByText(openDialog(), "Take down").click();
    await settle();
    expect(writes(backend.requests)[0]).toMatchObject({ method: "POST", url: "/api/uploads/88888888-8888-4888-8888-888888888881/takedown", body: {} });
    expect(root.querySelectorAll("img")).toHaveLength(0);
    expect(root.querySelectorAll("tr[data-upload]")).toHaveLength(1);
  });

  it("blocks and allows one user's uploads, and lists blocked users", async () => {
    const { root, backend } = await mount("uploads");
    // Rook is blocked in the sample data and has no uploads: listed in the blocked users card.
    expect(root.textContent).toContain("Users blocked from uploading");
    buttonByLabel(root, "Allow uploads for Rook").click();
    await settle();
    expect(writes(backend.requests)[0]).toMatchObject({ method: "POST", url: `/api/users/${ids.user2}/uploads`, body: { blocked: false } });
    expect(root.textContent).toContain("Nobody is blocked one by one.");

    buttonByLabel(root, `Block uploads for ${HOSTILE}`).click();
    await settle();
    buttonByText(openDialog(), "Block uploads").click();
    await settle();
    expect(writes(backend.requests)[1]).toMatchObject({ url: `/api/users/${ids.user1}/uploads`, body: { blocked: true } });
    expect(root.textContent).toContain("Uploads blocked");
  });

  it("blocks all new uploads after confirmation and saves the per-person limits (D28)", async () => {
    const { root, backend } = await mount("uploads");
    buttonByText(root, "Block all new uploads").click();
    await settle();
    buttonByText(openDialog(), "Block all uploads").click();
    await settle();
    expect(writes(backend.requests)[0]).toMatchObject({ method: "PUT", url: "/api/settings", body: { uploadsBlocked: true } });
    expect(root.textContent).toContain("Blocked for everyone");

    const mb = root.querySelector<HTMLInputElement>("#upload-limit-mb")!;
    const files = root.querySelector<HTMLInputElement>("#upload-limit-files")!;
    expect(mb.value).toBe("60");
    expect(files.value).toBe("300");
    expect(buttonByText(root, "Save limits").disabled).toBe(true);
    mb.value = "121";
    mb.dispatchEvent(new Event("input"));
    expect(buttonByText(root, "Save limits").disabled).toBe(true);
    expect(root.querySelector("#upload-limit-mb-error")?.textContent).toBe("Enter a whole number from 0 to 120.");
    mb.value = "40";
    mb.dispatchEvent(new Event("input"));
    expect(root.querySelector("#upload-limit-mb-error")?.textContent).toBe("");
    files.value = "1001";
    files.dispatchEvent(new Event("input"));
    expect(buttonByText(root, "Save limits").disabled).toBe(true);
    expect(root.querySelector("#upload-limit-files-error")?.textContent).toBe("Enter a whole number from 0 to 1000.");
    files.value = "300";
    files.dispatchEvent(new Event("input"));
    buttonByText(root, "Save limits").click();
    await settle();
    // Only what changed is sent, in bytes (MB = 1 048 576 bytes).
    expect(writes(backend.requests)[1]).toMatchObject({ method: "PUT", url: "/api/settings", body: { uploadLimitBytes: 40 * 1024 * 1024 } });
    expect(JSON.stringify(writes(backend.requests)[1])).not.toContain("uploadLimitFiles");
    expect(root.querySelector<HTMLInputElement>("#upload-limit-mb")!.value).toBe("40");
  });

  it("shows an error state when the list cannot be loaded, and a message when a takedown fails", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/uploads", { status: 500, error: "internal" });
    const { root } = await mount("uploads", { backend });
    expect(root.querySelector("[role=alert]")).not.toBeNull();
    backend.failures.clear();
    backend.fail("POST /api/uploads/88888888-8888-4888-8888-888888888881/takedown", { status: 404, error: "not_found" });
    buttonByText(root, "Try again").click();
    await settle();
    buttonByLabel(root, `Take down upload 88888888 by ${HOSTILE}`).click();
    await settle();
    buttonByText(openDialog(), "Take down").click();
    await settle();
    expect(root.textContent).toContain("Could not take the upload down");
  });
});

describe("files that could not be deleted yet (security review M2)", () => {
  it("a takedown says so when the stored file is not deleted yet", async () => {
    const backend = new FakeBackend();
    backend.fileDeleteFails = true;
    const { root } = await mount("uploads", { backend });
    buttonByLabel(root, `Take down upload 88888888 by ${HOSTILE}`).click();
    await settle();
    buttonByText(openDialog(), "Take down").click();
    await settle();
    expect(root.textContent).toContain("Taken down, but the stored file is not deleted yet");
    expect(root.textContent).toContain("no longer serves the image");
  });

  it("deleting a user says so when an uploaded file is not deleted yet", async () => {
    const backend = new FakeBackend();
    backend.fileDeleteFails = true;
    const { root } = await mount("users", { backend });
    buttonByLabel(root, "Delete Rook").click();
    await settle();
    const dialog = openDialog();
    const typed = dialog.querySelector<HTMLInputElement>("input")!;
    typed.value = "22222222";
    typed.dispatchEvent(new Event("input"));
    buttonByText(dialog, "Delete user").click();
    await settle();
    expect(root.textContent).toContain("some uploaded files are not deleted yet");
  });

  it("the storage card shows bytes waiting to be deleted, and that uploads stop at 6 GB", async () => {
    const backend = new FakeBackend();
    backend.data.storage = { ...sampleStorage(6_000_000_000), pendingDeleteBytes: 2048 };
    const { root } = await mount("uploads", { backend });
    expect(root.textContent).toContain("New uploads are refused: storage is at 6 GB");
    expect(root.textContent).toContain("could not be deleted from storage yet");
  });
});

describe("Users section upload controls", () => {
  it("shows each user's upload count and blocks uploads from the row", async () => {
    const { root, backend } = await mount("users");
    expect(root.textContent).toContain("2 uploads");
    expect(root.textContent).toContain("uploads blocked");
    buttonByLabel(root, `Block uploads for ${HOSTILE}`).click();
    await settle();
    buttonByText(openDialog(), "Block uploads").click();
    await settle();
    expect(writes(backend.requests)[0]).toMatchObject({ method: "POST", url: `/api/users/${ids.user1}/uploads`, body: { blocked: true } });
    expect(buttonByLabel(root, `Allow uploads for ${HOSTILE}`)).toBeTruthy();
  });
});
