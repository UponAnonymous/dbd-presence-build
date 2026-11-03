// FR-ADM-01 (every section, Dashboard and Settings from V2-S7), FR-ADM-06 (staging banner), FR-ADM-11 (keyboard access) and the honest error
// states: offline, expired Access session, 403, 5xx and an unexpected response.

import { describe, expect, it } from "vitest";
import { environmentFor } from "../src/env.ts";
import { applyTheme, readThemeChoice, saveThemeChoice } from "../src/theme.ts";
import { FakeBackend, buttonByText, mount, settle } from "./fake.ts";

describe("shell", () => {
  it("lists only the sections that exist", async () => {
    const { root } = await mount("invites");
    const labels = [...root.querySelectorAll("nav[aria-label='Sections'] a")].map((a) => a.textContent);
    expect(labels).toEqual(["Dashboard", "Invites", "Users", "Uploads", "Packs", "Characters", "Discord apps", "Access & features", "Releases", "Audit log", "Settings"]);
    expect(root.querySelector("[aria-current='page']")?.textContent).toBe("Invites");
  });

  it("shows the staging banner only on a -staging host", async () => {
    expect(environmentFor("admin-staging.arcavale.com")).toBe("staging");
    expect(environmentFor("dbdadmin.arcavale.com")).toBe("production");
    const staging = await mount("invites", { hostname: "admin-staging.arcavale.com" });
    expect(staging.root.querySelector(".env-banner")?.textContent).toContain("Staging");
    expect(staging.root.querySelector(".env-badge")?.textContent).toBe("Staging");
    const production = await mount("invites", { hostname: "dbdadmin.arcavale.com" });
    expect(production.root.querySelector(".env-banner")).toBeNull();
    expect(production.root.querySelector(".env-badge")?.textContent).toBe("Production");
  });

  it("routes by fragment, moves focus to the page heading and updates the title", async () => {
    const { root, navigate } = await mount("invites");
    navigate("audit");
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    await settle();
    expect(root.querySelector("h1")?.textContent).toBe("Audit log");
    expect(document.activeElement?.id).toBe("page-title");
    expect(document.title).toBe("Audit log · Owner Panel · DBD Presence");
    expect(root.querySelector("[aria-current='page']")?.textContent).toBe("Audit log");
  });

  it("falls back to the Dashboard for an unknown fragment", async () => {
    const { root } = await mount("nowhere");
    expect(root.querySelector("h1")?.textContent).toBe("Dashboard");
    expect(window.location.hash).toBe("#/dashboard");
  });

  it("is operable by keyboard: native buttons and links, a skip control, no positive tabindex", async () => {
    const { root } = await mount("users");
    expect(root.querySelectorAll("[tabindex]:not([tabindex='-1'])")).toHaveLength(0);
    for (const clickable of root.querySelectorAll("[data-devices], .btn")) expect(["BUTTON", "A"]).toContain(clickable.tagName);
    const skip = buttonByText(root, "Skip to content");
    skip.click();
    expect(document.activeElement?.id).toBe("main");
  });

  it("shows a loading state while a list loads", async () => {
    let release: () => void = () => undefined;
    const backend = new FakeBackend();
    const gate = new Promise<void>((resolve) => (release = resolve));
    const slow = Object.assign(Object.create(backend) as FakeBackend, { fetch: async (input: string, init: RequestInit) => (await gate, backend.fetch(input, init)) });
    const { root } = await mount("users", { backend: slow });
    expect(root.querySelector(".state-loading")?.textContent).toBe("Loading users…");
    expect(root.querySelector(".state-loading")?.getAttribute("role")).toBe("status");
    release();
    await settle();
    expect(root.querySelector(".state-loading")).toBeNull();
  });

  it("keeps the theme choice and applies it to the root element", () => {
    const store = new Map<string, string>();
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v), removeItem: (k: string) => void store.delete(k) } as unknown as Storage;
    expect(readThemeChoice(storage)).toBe("system");
    saveThemeChoice(storage, "light");
    expect(readThemeChoice(storage)).toBe("light");
    applyTheme(document.documentElement, "light");
    expect(document.documentElement.dataset["theme"]).toBe("light");
    applyTheme(document.documentElement, "system");
    expect(document.documentElement.hasAttribute("data-theme")).toBe(false);
    const blocked = { getItem: () => { throw new DOMException("blocked", "SecurityError"); } } as unknown as Storage;
    expect(readThemeChoice(blocked)).toBe("system");
  });
});

describe("error states", () => {
  it("an expired Access session (401) shows the reload message in the section and a global banner", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/users", { status: 401, error: "unauthenticated" });
    const { root } = await mount("users", { backend });
    const alert = root.querySelector(".state-error")!;
    expect(alert.getAttribute("role")).toBe("alert");
    expect(alert.textContent).toContain("Your Cloudflare Access session expired");
    expect(alert.textContent).toContain("Reload the page to sign in again");
    expect(buttonByText(alert, "Reload page")).toBeTruthy();
    expect(root.querySelector(".global-banner-danger")?.textContent).toContain("Cloudflare Access session expired");
  });

  it("a redirect to the Access sign-in page counts as an expired session", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/settings", "redirect");
    const { root } = await mount("access", { backend });
    expect(root.querySelector(".state-error")?.textContent).toContain("Your Cloudflare Access session expired");
  });

  it("offline: says so and offers Try again, which recovers", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/audit", "network");
    const { root } = await mount("audit", { backend, online: false });
    expect(root.querySelector(".state-error")?.textContent).toContain("You're offline");
    backend.failures.clear();
    buttonByText(root.querySelector(".state-error")!, "Try again").click();
    await settle();
    expect(root.querySelector(".state-error")).toBeNull();
    expect(root.querySelectorAll("tbody tr").length).toBeGreaterThan(0);
  });

  it("shows the offline banner when the browser goes offline", async () => {
    const { root } = await mount("invites");
    window.dispatchEvent(new Event("offline"));
    expect(root.querySelector(".global-banner-warning")?.textContent).toContain("You're offline");
    window.dispatchEvent(new Event("online"));
    expect(root.querySelector(".global-banner-warning")).toBeNull();
  });

  it("403 for a non-owner email explains the allow-list", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/invites", { status: 403, error: "forbidden" });
    const { root } = await mount("invites", { backend });
    const text = root.querySelector(".state-error")?.textContent ?? "";
    expect(text).toContain("Access refused");
    expect(text).toContain("not on the owner list");
    expect(text).toContain("(forbidden)");
  });

  it("5xx names the status and code and does not claim nothing changed", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/users", { status: 500, error: "internal" });
    const { root } = await mount("users", { backend });
    const text = root.querySelector(".state-error")?.textContent ?? "";
    expect(text).toContain("The service had a problem (error 500)");
    expect(text).toContain("(internal)");
    expect(text).not.toContain("Nothing was changed");
  });

  it("an HTML answer (API not attached) is an unexpected response", async () => {
    const backend = new FakeBackend();
    backend.fail("GET /api/invites", "html");
    const { root } = await mount("invites", { backend });
    expect(root.querySelector(".state-error")?.textContent).toContain("Unexpected response");
  });

  it("a failed action shows an inline alert and leaves the list in place", async () => {
    const backend = new FakeBackend();
    backend.fail("POST /api/users/22222222-2222-4222-8222-222222222222/revoke", { status: 409, error: "already_revoked" });
    const { root } = await mount("users", { backend });
    [...root.querySelectorAll("button")].find((b) => b.getAttribute("aria-label") === "Revoke Rook")!.click();
    await settle();
    buttonByText(document.querySelector("dialog")!, "Revoke user").click();
    await settle();
    const message = root.querySelector(".inline-message [role='alert']");
    expect(message?.textContent).toContain("Could not revoke the user");
    expect(message?.textContent).toContain("This was already revoked.");
    expect(root.querySelectorAll("tbody tr")).toHaveLength(2);
  });
});
