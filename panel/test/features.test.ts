// FR-ADM-08 / FR-ONL-08: Discord applications and Access & features (flags that can only turn things off, the
// emergency switch, the banner with its length cap, the minimum version). Changes are confirmed and show the new
// signed-config version.

import { describe, expect, it } from "vitest";
import { appListProblem } from "../src/views/apps.ts";
import { HOSTILE, buttonByLabel, buttonByText, mount, openDialog, settle } from "./fake.ts";

const writes = <T extends { method: string }>(requests: T[]): T[] => requests.filter((r) => r.method !== "GET");

function type(root: ParentNode, selector: string, value: string): void {
  const input = root.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event("input"));
}

describe("Discord apps", () => {
  it("lists the applications with the default marked and hostile names as text", async () => {
    const { root } = await mount("apps");
    expect(root.querySelector("h1")?.textContent).toBe("Discord apps");
    expect(root.textContent).toContain("Signed configuration version 1793600000");
    expect(root.querySelector<HTMLInputElement>("#app-1-default")!.checked).toBe(true);
    expect(root.querySelector<HTMLInputElement>("#app-2-name")!.value).toBe(HOSTILE);
    expect(root.querySelectorAll("img, script").length).toBe(0);
    expect(buttonByText(root, "Save applications").disabled).toBe(true);
  });

  it("adds an application, makes it the default and saves after a confirmation that lists the change", async () => {
    const { root, backend } = await mount("apps");
    buttonByText(root, "Add application").click();
    await settle();
    expect(document.activeElement?.id).toBe("app-3-key");
    type(root, "#app-3-key", "dbd-event");
    type(root, "#app-3-applicationId", "1563291325107939999");
    type(root, "#app-3-name", "Dead by Daylight (event)");
    const radio = root.querySelector<HTMLInputElement>("#app-3-default")!;
    radio.checked = true;
    radio.dispatchEvent(new Event("change"));
    buttonByText(root, "Save applications").click();
    await settle();
    const dialog = openDialog();
    expect(dialog.textContent).toContain("3 applications will be listed");
    expect(dialog.textContent).toContain("Added: Dead by Daylight (event).");
    expect(dialog.textContent).toContain("New default: Dead by Daylight (event).");
    buttonByText(dialog, "Save applications").click();
    await settle();
    const [put] = writes(backend.requests);
    expect(put).toMatchObject({ method: "PUT", url: "/api/apps" });
    expect(put!.body).toMatchObject({ defaultApplication: "dbd-event" });
    expect((put!.body as { applications: unknown[] }).applications).toHaveLength(3);
    expect(root.textContent).toContain("Applications saved");
    expect(root.textContent).toContain("Signed configuration version 1793600001");
  });

  it("refuses an invalid list before sending and focuses the field", async () => {
    const { root, backend } = await mount("apps");
    type(root, "#app-2-applicationId", "12345");
    buttonByText(root, "Save applications").click();
    await settle();
    expect(root.querySelector("#apps-error")?.textContent).toContain("Application 2: Paste the Application ID");
    expect(document.activeElement?.id).toBe("app-2-applicationId");
    expect(writes(backend.requests)).toHaveLength(0);
  });

  it("cannot remove the default or the last application", async () => {
    const { root } = await mount("apps");
    expect(buttonByLabel(root, "Remove Dead by Daylight").disabled).toBe(true);
    buttonByLabel(root, `Remove ${HOSTILE}`).click();
    await settle();
    expect(root.querySelectorAll(".app-row")).toHaveLength(1);
    expect(buttonByLabel(root, "Remove Dead by Daylight").disabled).toBe(true);
  });

  it("checks the same rules as the app's parser", () => {
    const ok = { key: "dbd", applicationId: "1563291325107937340", name: "N", description: "" };
    expect(appListProblem([ok])).toBeNull();
    expect(appListProblem([])).not.toBeNull();
    expect(appListProblem([{ ...ok, key: "DBD" }])?.field).toBe("key");
    expect(appListProblem([{ ...ok, name: "a\u0001" }])?.field).toBe("name");
    expect(appListProblem([{ ...ok, description: "x".repeat(257) }])?.field).toBe("description");
    expect(appListProblem([ok, { ...ok, key: "other" }])?.field).toBe("applicationId");
  });
});

describe("Access & features", () => {
  it("shows every remote setting and the config version", async () => {
    const { root } = await mount("access");
    expect(root.querySelector("h1")?.textContent).toBe("Access & features");
    for (const title of ["Who can get access", "Emergency switch", "Feature switches", "Announcement banner", "Minimum supported version"]) expect(root.textContent).toContain(title);
    expect(root.textContent).toContain("Signed configuration version 1793600000");
    expect(root.textContent).toContain("These switches can only turn features off");
  });

  it("the emergency switch needs a confirmation and turns online features off", async () => {
    const { root, backend } = await mount("access");
    buttonByText(root, "Turn online features off").click();
    await settle();
    expect(openDialog().textContent).toContain("Discord presence keeps working");
    buttonByText(openDialog(), "Turn online features off").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ method: "PUT", url: "/api/settings", body: { emergencyStop: true } })]);
    expect(root.textContent).toContain("Online features are off for everyone");
    expect(root.querySelector(".card-emergency-on")).not.toBeNull();
    expect(buttonByText(root, "Turn online features back on")).toBeTruthy();
  });

  it("the feature switches send only disable flags, after a confirmation", async () => {
    const { root, backend } = await mount("access");
    const save = buttonByText(root, "Save switches");
    expect(save.disabled).toBe(true);
    root.querySelector<HTMLInputElement>("#flag-capture")!.click();
    expect(save.disabled).toBe(false);
    save.click();
    await settle();
    expect(openDialog().textContent).toContain("Screen capture is turned off in every app.");
    buttonByText(openDialog(), "Save switches").click();
    await settle();
    const body = writes(backend.requests)[0]!.body as Record<string, unknown>;
    expect(body).toEqual({ disableCapture: true, disableDetection: false });
    for (const key of Object.keys(body)) expect(key).toMatch(/^disable/);
  });

  it("the banner has a counter and the 280-character cap, and can be removed", async () => {
    const { root, backend } = await mount("access");
    const area = root.querySelector<HTMLTextAreaElement>("#banner-text")!;
    expect(area.getAttribute("maxlength")).toBe("280");
    type(root, "#banner-text", `  ${HOSTILE}  `);
    expect(root.textContent).toContain(`${HOSTILE.length + 4} of 280 characters`);
    buttonByText(root, "Save banner").click();
    await settle();
    expect(writes(backend.requests)[0]!.body).toEqual({ banner: HOSTILE });
    expect(root.querySelector(".banner-preview")?.textContent).toBe(HOSTILE);
    expect(root.querySelectorAll("img, script").length).toBe(0);
    buttonByText(root, "Remove banner").click();
    await settle();
    buttonByText(openDialog(), "Remove banner").click();
    await settle();
    expect(writes(backend.requests)[1]!.body).toEqual({ banner: null });
  });

  it("the minimum version is checked before it is sent", async () => {
    const { root, backend } = await mount("access");
    type(root, "#minimum-version", "1.0");
    buttonByText(root, "Save minimum version").click();
    await settle();
    expect(root.querySelector("#minimum-version-error")?.textContent).toContain("three numbers");
    type(root, "#minimum-version", "0.4.0");
    buttonByText(root, "Save minimum version").click();
    await settle();
    buttonByText(openDialog(), "Save minimum version").click();
    await settle();
    expect(writes(backend.requests)).toEqual([expect.objectContaining({ body: { minimumVersion: "0.4.0" } })]);
    expect(root.textContent).toContain("Minimum version is now 0.4.0");
  });
});
