// FR-ADM-01 (Dashboard and Settings sections, V2-S7), NFR-SVC-10 (usage, key status and backup status visible in the panel;
// nothing invented: a value the service has not recorded reads "Not recorded yet"), NFR-SVC-11 (storage against the 8 GB cap,
// warning from 6 GB), FR-ADM-04 (values from the service are text only). Both sections are read-only.

import { describe, expect, it } from "vitest";
import { FakeBackend, HOSTILE, buttonByText, mount, sampleStorage, settle } from "./fake.ts";
import type { StorageUsage } from "../src/api.ts";

const writes = (requests: { method: string }[]) => requests.filter((r) => r.method !== "GET");

function definitions(root: ParentNode): Record<string, string> {
  const out: Record<string, string> = {};
  for (const dt of root.querySelectorAll("dl.kv-facts dt")) out[dt.textContent ?? ""] = dt.nextElementSibling?.textContent ?? "";
  return out;
}

function stats(root: ParentNode): Record<string, string> {
  const out: Record<string, string> = {};
  for (const s of root.querySelectorAll(".stat")) out[s.querySelector(".stat-label")!.textContent!] = s.querySelector(".stat-value")!.textContent!;
  return out;
}

function cardTitled(root: ParentNode, title: string): HTMLElement {
  const match = [...root.querySelectorAll<HTMLElement>("section.card")].find((c) => c.querySelector(".card-title")?.textContent === title);
  if (!match) throw new Error(`no card ${title}`);
  return match;
}

describe("Dashboard", () => {
  it("is the first section and shows people, invites and uploads from the overview, read-only", async () => {
    const { root, backend } = await mount("dashboard");
    expect(root.querySelector("h1")?.textContent).toBe("Dashboard");
    expect(root.querySelector("[aria-current='page']")?.textContent).toBe("Dashboard");
    expect(stats(cardTitled(root, "People"))).toEqual({ "People with access": "11", "Active in the last 7 days": "7", "Waiting for an invite": "2", "Unused invite codes": "3", Revoked: "1" });
    expect(stats(cardTitled(root, "Uploads"))).toEqual({ "Pictures stored": "42", "Uploaded in the last 7 days": "5" });
    expect([...cardTitled(root, "People").querySelectorAll("a")].map((a) => a.getAttribute("href"))).toEqual(["#/users", "#/invites"]);
    expect(backend.requests.map((r) => `${r.method} ${r.url}`)).toEqual(["GET /api/overview"]);
    expect(writes(backend.requests)).toEqual([]);
  });

  it("shows storage against the 8 GB cap with the same card as Uploads, and warns from 6 GB", async () => {
    const { root } = await mount("dashboard");
    const meter = root.querySelector<HTMLMeterElement>("#storage-meter")!;
    expect(meter.getAttribute("max")).toBe("8000000000");
    expect(meter.getAttribute("value")).toBe("1234567890");
    expect(cardTitled(root, "Storage").textContent).toContain("1.23 GB of 8.00 GB used");
    expect(root.querySelector(".notice-warning")).toBeNull();

    const backend = new FakeBackend();
    backend.overview.storage = sampleStorage(6_100_000_000) as StorageUsage;
    const full = await mount("dashboard", { backend });
    expect(cardTitled(full.root, "Storage").textContent).toContain("New uploads are refused: storage is at 6 GB");
  });

  it("shows the database size against the free 500 MB, and says when it was not reported instead of guessing", async () => {
    const { root } = await mount("dashboard");
    const database = cardTitled(root, "Database and daily limits");
    expect(database.textContent).toContain("12.3 MB of 500.0 MB used");
    expect(database.querySelector("#database-meter")?.getAttribute("max")).toBe("500000000");
    expect(database.textContent).toContain("The service cannot see how many requests it answers");

    const backend = new FakeBackend();
    backend.overview.database = { sizeBytes: null, freeLimitBytes: 500_000_000 };
    const unknown = await mount("dashboard", { backend });
    const card = cardTitled(unknown.root, "Database and daily limits");
    expect(card.textContent).toContain("did not report its size");
    expect(card.querySelector("meter")).toBeNull();
    expect(card.textContent).not.toMatch(/\d+(\.\d+)? MB of/);
  });

  it("shows backups as Not recorded yet when the service has recorded none", async () => {
    const { root } = await mount("dashboard");
    const status = cardTitled(root, "Keys and backups");
    expect(definitions(status)).toEqual({ "Last database backup": "Not recorded yet", "Last restore drill": "Not recorded yet" });
    expect(status.textContent).toContain("Every service key is set up");
    expect(status.querySelector("a")?.getAttribute("href")).toBe("#/settings");
  });

  it("names missing keys and a missing release key", async () => {
    const backend = new FakeBackend();
    backend.overview.keys.service = backend.overview.keys.service.map((k) => (k.name === "config" ? { ...k, configured: false, kid: null } : k));
    backend.overview.keys.offline = { release: [], recovery: [], revoked: [], configured: true };
    const { root } = await mount("dashboard", { backend });
    const status = cardTitled(root, "Keys and backups");
    expect(status.textContent).toContain("Missing Configuration signing key");
    expect(status.textContent).toContain("No release key yet");
  });

  it("shows the error state when the overview cannot be loaded, and Try again loads it", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/overview", { status: 503, error: "internal" });
    const { root } = await mount("dashboard", { backend });
    expect(root.querySelector(".state-error")).not.toBeNull();
    backend.failures.clear();
    buttonByText(root, "Try again").click();
    await settle();
    expect(root.querySelector(".state-error")).toBeNull();
    expect(cardTitled(root, "People")).toBeTruthy();
  });

  it("treats a malformed overview as unexpected instead of showing part of it", async () => {
    const backend = new FakeBackend();
    (backend.overview.backup as unknown as Record<string, unknown>)["lastExportBytes"] = "12";
    const { root } = await mount("dashboard", { backend });
    expect(root.querySelector(".state-error")).not.toBeNull();
    expect(root.querySelector(".stat")).toBeNull();
  });

  it("Refresh loads the overview again", async () => {
    const { root, backend } = await mount("dashboard");
    backend.overview.users.activeThisWeek = 9;
    buttonByText(root, "Refresh").click();
    await settle();
    expect(stats(cardTitled(root, "People"))["Active in the last 7 days"]).toBe("9");
    expect(backend.requests.filter((r) => r.url === "/api/overview")).toHaveLength(2);
  });
});

describe("Settings", () => {
  it("shows the environment, every service key with its public id, the offline keys and the server settings, read-only", async () => {
    const { root, backend } = await mount("settings");
    expect(root.querySelector("h1")?.textContent).toBe("Settings");
    expect(definitions(cardTitled(root, "Environment"))).toEqual({
      "This panel": "Production",
      "The service says": "Production",
      "Owner Panel address": "dbdadmin.arcavale.com",
      "App service address": "api.arcavale.com",
      "Pictures and downloads address": "cdn.arcavale.com",
    });
    const keys = cardTitled(root, "Service keys");
    const rows = [...keys.querySelectorAll("tbody tr")].map((tr) => [...tr.querySelectorAll("td")].map((td) => td.textContent));
    expect(rows.map((r) => [r[0], r[2], r[3]])).toEqual([
      ["Licence signing key", "Set up", "licence-2026-10"],
      ["Configuration signing key", "Set up", "config-2026-10"],
      ["Content signing key", "Set up", "content-2026-10"],
      ["Invite key", "Set up", "invite-2"],
      ["Ban key", "Set up", "No id"],
    ]);
    expect(keys.textContent).toContain("Every service key is set up");
    expect(cardTitled(root, "Signing keys").textContent).toContain("release-2026-10");
    const server = definitions(cardTitled(root, "Current server settings"));
    expect(server["Who can get access"]).toBe("Invite-only");
    expect(server["Storage per person"]).toBe("60.0 MB");
    expect(server["New uploads"]).toBe("Allowed");
    expect([...cardTitled(root, "Current server settings").querySelectorAll("a")].map((a) => a.getAttribute("href"))).toEqual(["#/access", "#/uploads"]);
    expect(root.querySelectorAll("input, select:not(#theme-choice), textarea")).toHaveLength(0);
    expect(writes(backend.requests)).toEqual([]);
  });

  it("shows every backup value as Not recorded yet, with a warning, when nothing is recorded", async () => {
    const { root } = await mount("settings");
    const backups = cardTitled(root, "Backups");
    expect(definitions(backups)).toEqual({
      "Last database backup": "Not recorded yet",
      "Size of that backup": "Not recorded yet",
      "Last restore drill": "Not recorded yet",
      "Drill result": "Not recorded yet",
      "Last audit log export": expect.stringMatching(/2026/) as unknown as string,
    });
    expect(backups.querySelector(".notice-warning .notice-title")?.textContent).toBe("No backup is recorded yet");
  });

  it("shows recorded backups and a failed drill when the service reports them", async () => {
    const backend = new FakeBackend();
    backend.overview.backup = { lastExportAt: "2026-10-24T03:00:00Z", lastExportBytes: 2_621_440, lastDrillAt: "2026-10-20T10:00:00Z", lastDrillOk: false };
    backend.overview.auditExport = { lastAt: null };
    const { root } = await mount("settings", { backend });
    const facts = definitions(cardTitled(root, "Backups"));
    expect(facts["Last database backup"]).toContain("24 Oct 2026");
    expect(facts["Size of that backup"]).toBe("2.5 MB");
    expect(facts["Drill result"]).toBe("Failed: the restore did not give back the data");
    expect(facts["Last audit log export"]).toBe("Not done yet");
    expect(cardTitled(root, "Backups").querySelector(".notice-danger .notice-title")?.textContent).toBe("The last restore drill failed");
  });

  it("warns when a key is missing and when the service and the panel disagree about the environment", async () => {
    const backend = new FakeBackend();
    backend.overview.environment = "staging";
    backend.overview.keys.service = backend.overview.keys.service.map((k) => (k.name === "ban" || k.name === "invite" ? { ...k, configured: false, kid: null } : k));
    const { root } = await mount("settings", { backend });
    expect(cardTitled(root, "Environment").querySelector(".notice-danger .notice-title")?.textContent).toBe("This panel and the service disagree about where they run");
    const keys = cardTitled(root, "Service keys");
    expect(keys.querySelector(".notice-danger .notice-title")?.textContent).toBe("2 keys are missing");
    expect(keys.querySelector(".notice-danger .notice-text")?.textContent).toContain("Invite key, Ban key");
    expect(keys.querySelector("tr[data-key='ban']")?.textContent).toContain("Missing");
  });

  it("shows key ids from the service as text only", async () => {
    const backend = new FakeBackend();
    backend.overview.keys.service[0] = { name: "licence", configured: true, kid: HOSTILE };
    const { root } = await mount("settings", { backend });
    expect(cardTitled(root, "Service keys").textContent).toContain(HOSTILE);
    expect(root.querySelectorAll("img, script").length).toBe(0);
  });

  it("shows the error state when either call fails", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/settings", { status: 500 });
    const { root } = await mount("settings", { backend });
    expect(root.querySelector(".state-error")).not.toBeNull();
    expect(root.querySelector(".card")).toBeNull();
  });
});
