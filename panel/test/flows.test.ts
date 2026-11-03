// FR-ADM-05 (destructive actions need confirmation) and FR-ADM-07 (invites and users), plus FR-ONL-06 (access mode).

import { describe, expect, it } from "vitest";
import { HOSTILE, buttonByLabel, buttonByText, ids, mount, openDialog, settle } from "./fake.ts";

const writes = (requests: { method: string }[]) => requests.filter((r) => r.method !== "GET");

describe("invites", () => {
  it("creates a batch with note and expiry, shows each code once with copy buttons, and hides them on Done", async () => {
    const { root, backend, copied } = await mount("invites");
    const count = root.querySelector<HTMLInputElement>("#invite-count")!;
    count.value = "3";
    count.dispatchEvent(new Event("input"));
    root.querySelector<HTMLSelectElement>("#invite-expiry")!.value = "7";
    root.querySelector<HTMLInputElement>("#invite-note")!.value = "  Clan night  ";
    expect(buttonByText(root, "Create 3 invites")).toBeTruthy();
    root.querySelector("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();

    expect(writes(backend.requests)[0]).toMatchObject({ method: "POST", url: "/api/invites", body: { count: 3, note: "Clan night", expiresInDays: 7 } });
    const codes = [...root.querySelectorAll(".code-value")].map((c) => c.textContent);
    expect(codes).toEqual(["ABCD-EFGH-JKMN-PQR0", "ABCD-EFGH-JKMN-PQR1", "ABCD-EFGH-JKMN-PQR2"]);
    expect(root.textContent).toContain("shown only once");

    buttonByLabel(root, "Copy code ABCD-EFGH-JKMN-PQR0").click();
    await settle();
    expect(copied).toEqual(["ABCD-EFGH-JKMN-PQR0"]);

    // Two codes not copied yet: Done asks first; Cancel keeps them.
    buttonByText(root, "Done").click();
    await settle();
    expect(openDialog().textContent).toContain("You have not copied 2 codes.");
    buttonByText(openDialog(), "Cancel").click();
    await settle();
    expect(root.querySelectorAll(".code-value")).toHaveLength(3);

    buttonByText(root, "Copy all codes").click();
    await settle();
    expect(copied[1]).toBe(codes.join("\n"));
    buttonByText(root, "Done").click();
    await settle();
    expect(document.querySelector("dialog")).toBeNull();
    expect(root.querySelectorAll(".code-value")).toHaveLength(0);
  });

  it("validates the count before sending", async () => {
    const { root, backend } = await mount("invites");
    root.querySelector<HTMLInputElement>("#invite-count")!.value = "101";
    root.querySelector("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    expect(writes(backend.requests)).toHaveLength(0);
    expect(root.querySelector("#invite-count-error")?.textContent).toBe("Enter a whole number from 1 to 100.");
    expect(root.querySelector("#invite-count")?.getAttribute("aria-invalid")).toBe("true");
  });

  it("tells the owner when copying is refused, and keeps the code on screen", async () => {
    const { root } = await mount("invites", { copyWorks: false });
    root.querySelector("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    buttonByLabel(root, "Copy code ABCD-EFGH-JKMN-PQR0").click();
    await settle();
    expect(root.querySelector(".codes-status")?.textContent).toContain("copy it by hand");
    expect(root.querySelector(".code-value")?.textContent).toBe("ABCD-EFGH-JKMN-PQR0");
  });

  it("revokes an unused invite only after confirmation", async () => {
    const { root, backend } = await mount("invites");
    expect(root.textContent).toContain("Unused");
    buttonByLabel(root, `Revoke ${HOSTILE}`).click();
    await settle();
    expect(openDialog().querySelector(".dialog-title")?.textContent).toBe("Revoke this invite?");
    expect(document.activeElement?.textContent).toBe("Cancel");
    buttonByText(openDialog(), "Cancel").click();
    await settle();
    expect(writes(backend.requests)).toHaveLength(0);

    buttonByLabel(root, `Revoke ${HOSTILE}`).click();
    await settle();
    buttonByText(openDialog(), "Revoke invite").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "POST", url: `/api/invites/${ids.invite1}/revoke` })]);
    expect(root.querySelectorAll(".badge-danger")[0]?.textContent).toBe("Revoked");
  });

  it("lists status, redeemer and filters", async () => {
    const { root } = await mount("invites");
    expect(root.textContent).toContain(`Redeemed ${new Date(Date.parse("2026-10-20T12:00:00Z")).getDate()} Oct 2026`);
    buttonByText(root, "Redeemed 1").click();
    await settle();
    expect(root.querySelectorAll("tbody tr")).toHaveLength(1);
    expect(root.querySelector("[aria-pressed='true']")?.textContent).toBe("Redeemed 1");
  });

  it("asks before leaving the section while new codes are on screen", async () => {
    const { root, navigate } = await mount("invites");
    root.querySelector("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
    await settle();
    navigate("users");
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    await settle();
    expect(openDialog().querySelector(".dialog-title")?.textContent).toBe("Leave without copying the codes?");
    buttonByText(openDialog(), "Stay").click();
    await settle();
    expect(window.location.hash).toBe("#/invites");
    expect(root.querySelectorAll(".code-value")).toHaveLength(1);
  });
});

describe("users", () => {
  it("lists status, access, devices, created and last seen", async () => {
    const { root } = await mount("users");
    const rows = root.querySelectorAll("tbody tr");
    expect(rows).toHaveLength(2);
    expect(rows[0]!.textContent).toContain("Active");
    expect(rows[0]!.textContent).toContain("With an invite");
    expect(rows[0]!.textContent).toContain("1 active of 2");
    expect(rows[0]!.textContent).toContain("2 hours ago");
    expect(rows[1]!.textContent).toContain("Waiting for an invite");
    expect(root.textContent).toContain("2 users · 2 active · 0 revoked");
  });

  it("revokes a user after confirmation", async () => {
    const { root, backend } = await mount("users");
    buttonByLabel(root, "Revoke Rook").click();
    await settle();
    expect(openDialog().textContent).toContain("Their sessions end on every device");
    buttonByText(openDialog(), "Revoke user").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "POST", url: `/api/users/${ids.user2}/revoke` })]);
    expect(root.querySelectorAll("tbody tr")[1]!.textContent).toContain("Revoked");
    expect(() => buttonByLabel(root, "Revoke Rook")).toThrow();
  });

  it("revokes one device after confirmation", async () => {
    const { root, backend } = await mount("users");
    buttonByText(root, /1 active of 2/).click();
    await settle();
    expect(root.querySelector(`[data-devices="${ids.user1}"]`)?.getAttribute("aria-expanded")).toBe("true");
    const revoke = [...root.querySelectorAll<HTMLButtonElement>("button")].filter((b) => b.textContent === "Revoke device");
    expect(revoke).toHaveLength(1);
    revoke[0]!.click();
    await settle();
    buttonByText(openDialog(), "Revoke device").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "POST", url: `/api/users/${ids.user1}/devices/${ids.device1}/revoke` })]);
    expect(root.textContent).toContain("0 active of 2");
  });

  it("deletes a user only after the short id is typed", async () => {
    const { root, backend } = await mount("users");
    buttonByLabel(root, "Delete Rook").click();
    await settle();
    const dialog = openDialog();
    const confirm = buttonByText(dialog, "Delete user");
    const input = dialog.querySelector("input")!;
    expect(confirm.disabled).toBe(true);
    input.value = "2222222";
    input.dispatchEvent(new Event("input"));
    expect(confirm.disabled).toBe(true);
    input.value = "22222222";
    input.dispatchEvent(new Event("input"));
    expect(confirm.disabled).toBe(false);
    confirm.click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "DELETE", url: `/api/users/${ids.user2}`, body: { confirm: ids.user2 } })]);
    expect(root.querySelectorAll("tbody tr")).toHaveLength(1);
  });

  it("Escape cancels a confirmation without any request", async () => {
    const { root, backend } = await mount("users");
    const trigger = buttonByLabel(root, "Delete Rook");
    trigger.focus();
    trigger.click();
    await settle();
    openDialog().dispatchEvent(new Event("cancel", { cancelable: true }));
    await settle();
    expect(document.querySelector("dialog")).toBeNull();
    expect(writes(backend.requests)).toHaveLength(0);
    expect(document.activeElement?.getAttribute("aria-label")).toBe("Delete Rook");
  });

  it("exports a user's data as a JSON download", async () => {
    const { root, downloads } = await mount("users");
    buttonByLabel(root, "Export data for Rook").click();
    await settle();
    expect(downloads).toHaveLength(1);
    expect(downloads[0]!.fileName).toBe("dbd-presence-user-22222222-2026-10-24.json");
    expect(downloads[0]!.data).toMatchObject({ schemaVersion: 1, user: { id: ids.user2 } });
  });
});

describe("audit log", () => {
  it("pages through entries with Older and Newer", async () => {
    const { root, backend } = await mount("audit");
    expect(root.querySelectorAll("tbody tr")).toHaveLength(25);
    expect(root.textContent).toContain("Entries #30 to #6");
    buttonByText(root, "Older").click();
    await settle();
    expect(backend.requests.at(-1)!.url).toBe("/api/audit?limit=25&before=6");
    expect(root.querySelectorAll("tbody tr")).toHaveLength(5);
    expect(buttonByText(root, "Older").disabled).toBe(true);
    buttonByText(root, "Newer").click();
    await settle();
    expect(root.textContent).toContain("Entries #30 to #6");
    expect(buttonByText(root, "Newer").disabled).toBe(true);
  });

  it("shows the result of Verify chain, intact or broken", async () => {
    const { root, backend } = await mount("audit");
    buttonByText(root, "Verify chain").click();
    await settle();
    expect(root.querySelector(".verify-result")?.textContent).toContain("Chain intact");
    expect(root.querySelector(".verify-result")?.textContent).toContain("30 entries checked");

    backend.chain = { ok: false, checked: 11, brokenAtSeq: 12, anchored: false, exportedHead: null };
    buttonByText(root, "Verify chain").click();
    await settle();
    const result = root.querySelector(".verify-result")!;
    expect(result.textContent).toContain("Chain broken at entry #12");
    expect(result.querySelector("[role='alert']")).not.toBeNull();
  });

  it("says there is no daily export yet when the service has none", async () => {
    const { root } = await mount("audit");
    buttonByText(root, "Verify chain").click();
    await settle();
    expect(root.querySelector(".verify-result")?.textContent).toContain("No daily export yet");
  });

  it("shows how far the last daily export matches", async () => {
    const { root, backend } = await mount("audit");
    backend.chain = { ok: true, checked: 30, brokenAtSeq: null, anchored: false, exportedHead: { seq: 24, hash: "c".repeat(64), present: true } };
    buttonByText(root, "Verify chain").click();
    await settle();
    const result = root.querySelector(".verify-result")!;
    expect(result.textContent).toContain("Chain intact");
    expect(result.textContent).toContain("Last daily export matches up to entry #24.");
    expect(result.textContent).not.toContain("No daily export yet");
  });

  it("raises the alarm when the newest exported entry is missing even though the links are intact", async () => {
    const { root, backend } = await mount("audit");
    backend.chain = { ok: false, checked: 22, brokenAtSeq: null, anchored: false, exportedHead: { seq: 24, hash: "c".repeat(64), present: false } };
    buttonByText(root, "Verify chain").click();
    await settle();
    const result = root.querySelector(".verify-result")!;
    expect(result.textContent).toContain("does not match the last daily export (entry #24)");
    expect(result.textContent).not.toContain("Chain broken at entry #?");
    expect(result.querySelector("[role='alert']")).not.toBeNull();
  });

  it("shows whether an invite batch had a note, without any note text", async () => {
    const { root } = await mount("audit");
    const rows = [...root.querySelectorAll("tbody tr")].map((r) => r.textContent ?? "");
    expect(rows.some((t) => t.includes("2 invites, with note"))).toBe(true);
    expect(rows.some((t) => t.includes("1 invite, no note"))).toBe(true);
    const second = root.querySelectorAll<HTMLButtonElement>("button[data-entry]")[2]!;
    second.click();
    await settle();
    expect(root.textContent).toContain('"hasNote": true');
  });
});

describe("access mode", () => {
  it("switches to public only after a confirmation that explains the change", async () => {
    const { root, backend } = await mount("access");
    const save = buttonByText(root, "Save access mode");
    expect(save.disabled).toBe(true);
    const publicRadio = root.querySelector<HTMLInputElement>("#mode-public")!;
    publicRadio.checked = true;
    publicRadio.dispatchEvent(new Event("change"));
    expect(save.disabled).toBe(false);
    save.click();
    await settle();
    const dialog = openDialog();
    expect(dialog.querySelector(".dialog-title")?.textContent).toBe("Open access to everyone?");
    expect(dialog.textContent).toContain("does not remove access granted in the meantime");
    buttonByText(dialog, "Switch to public").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "PUT", url: "/api/settings", body: { accessMode: "public" } })]);
    expect(root.textContent).toContain("Access is now public");
    expect(root.querySelector("#mode-public")?.closest("label")?.textContent).toContain("Current");
  });
});
