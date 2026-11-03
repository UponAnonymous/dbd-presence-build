// FR-ONL-08 / FR-ADM-08 (remote configuration from the Owner Panel): the Discord application list, feature flags,
// banner, minimum version and the emergency switch. Every change raises the config version and is served at once;
// the flags can only disable capture and detection (SAFETY_AND_PRIVACY §2.3, QUALITY_GATES V4.3).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { verifyDocument } from "../src/signing/document.ts";
import { API, Harness, envelopeOf, resetDatabase, trustedKeys } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

async function config(): Promise<{ version: number; payload: Record<string, unknown> }> {
  const response = await handleRequest(new Request(`${API}/v1/config`), env, h.deps);
  const { document } = (await response.json()) as { document: string };
  const result = await verifyDocument(document, "config", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
  expect(result.outcome).toBe("Valid");
  return { version: Number(envelopeOf(document)["version"]), payload: JSON.parse(result.envelope!.payloadJson) as Record<string, unknown> };
}

const DBD = { key: "dbd", applicationId: "1563291325107937340", name: "Dead by Daylight", description: "Unofficial fan-made presence — not affiliated with Behaviour Interactive" };
const SECOND = { key: "dbd-alt", applicationId: "1563291325107930000", name: "Dead by Daylight (alt)", description: "Second application" };

describe("Discord applications", () => {
  it("lists the seeded application and replaces the list, default included", async () => {
    const initial = (await (await h.admin("GET", "/api/apps")).json()) as Record<string, unknown>;
    expect(initial).toMatchObject({ applications: [DBD], defaultApplication: "dbd", configVersion: 0 });
    const saved = await h.admin("PUT", "/api/apps", { applications: [DBD, SECOND], defaultApplication: "dbd-alt" });
    expect(saved.status).toBe(200);
    expect(await saved.json()).toMatchObject({ applications: [DBD, SECOND], defaultApplication: "dbd-alt", configVersion: Math.floor(h.now / 1000) });
    const { payload, version } = await config();
    expect(payload).toMatchObject({ applications: [DBD, SECOND], defaultApplication: "dbd-alt" });
    expect(version).toBe(Math.floor(h.now / 1000));
  });

  it("refuses lists the app would refuse", async () => {
    const bad = [
      { applications: [], defaultApplication: "dbd" },
      { applications: [DBD], defaultApplication: "other" },
      { applications: [DBD, { ...SECOND, key: "dbd" }], defaultApplication: "dbd" },
      { applications: [DBD, { ...SECOND, applicationId: DBD.applicationId }], defaultApplication: "dbd" },
      { applications: [{ ...DBD, key: "DBD" }], defaultApplication: "DBD" },
      { applications: [{ ...DBD, applicationId: "123" }], defaultApplication: "dbd" },
      { applications: [{ ...DBD, name: "" }], defaultApplication: "dbd" },
      { applications: [{ ...DBD, name: "x".repeat(129) }], defaultApplication: "dbd" },
      { applications: [{ ...DBD, description: "a\u0007b" }], defaultApplication: "dbd" },
      { applications: [{ ...DBD, enabled: true }], defaultApplication: "dbd" },
      { applications: Array.from({ length: 11 }, (_, i) => ({ ...DBD, key: `k${i}`, applicationId: `15632913251079373${String(i).padStart(2, "0")}` })), defaultApplication: "k0" },
      { applications: [DBD], defaultApplication: "dbd", extra: 1 },
    ];
    for (const payload of bad) expect((await h.admin("PUT", "/api/apps", payload)).status).toBe(400);
    expect((await config()).payload).toMatchObject({ applications: [DBD] });
  });

  it("records the change in the audit log with keys and IDs only", async () => {
    await h.admin("PUT", "/api/apps", { applications: [DBD, SECOND], defaultApplication: "dbd" });
    const row = await env.DB.prepare("SELECT action, before_json, after_json FROM audit_log").first<Record<string, string>>();
    expect(row!["action"]).toBe("apps.update");
    expect(JSON.parse(row!["after_json"]!)).toEqual({ applications: [{ key: "dbd", applicationId: DBD.applicationId }, { key: "dbd-alt", applicationId: SECOND.applicationId }], defaultApplication: "dbd" });
    // Saving the same list again changes nothing and writes nothing.
    await h.admin("PUT", "/api/apps", { applications: [DBD, SECOND], defaultApplication: "dbd" });
    expect((await env.DB.prepare("SELECT COUNT(*) AS n FROM audit_log").first<{ n: number }>())!.n).toBe(1);
  });
});

describe("config version", () => {
  it("is 0 before any change, and every change raises it and is served at once", async () => {
    const first = await config();
    expect(first.version).toBe(0);
    const versions = [first.version];
    const changes: [string, unknown][] = [
      ["/api/settings", { banner: "Maintenance on Sunday" }],
      ["/api/settings", { minimumVersion: "0.3.0" }],
      ["/api/settings", { disableCapture: true }],
      ["/api/settings", { emergencyStop: true }],
      ["/api/apps", { applications: [DBD, SECOND], defaultApplication: "dbd" }],
      ["/api/settings", { accessMode: "public" }],
    ];
    for (const [path, payload] of changes) {
      const response = await h.admin("PUT", path, payload);
      expect(response.status).toBe(200);
      expect(((await response.json()) as { configVersion: number }).configVersion).toBeGreaterThan(versions.at(-1)!);
      // Several changes within the same second still raise it.
      const served = await config();
      expect(served.version).toBeGreaterThan(versions.at(-1)!);
      versions.push(served.version);
    }
    expect((await config()).payload).toMatchObject({ banner: "Maintenance on Sunday", minimumVersion: "0.3.0", disable: { capture: true, detection: false }, emergencyStop: true, accessMode: "public" });
  });

  it("does not change for the device limit, which the config does not carry, or for a save that changes nothing", async () => {
    await h.admin("PUT", "/api/settings", { banner: "Hello" });
    const before = (await config()).version;
    h.now += 5000;
    await h.admin("PUT", "/api/settings", { deviceLimit: 5 });
    await h.admin("PUT", "/api/settings", { banner: "Hello" });
    expect((await config()).version).toBe(before);
  });

  it("stays above a version a client has seen even if the clock is behind the stored version", async () => {
    await h.admin("PUT", "/api/settings", { banner: "One" });
    const seen = (await config()).version;
    h.now -= 3_600_000;
    await h.admin("PUT", "/api/settings", { banner: "Two" });
    expect((await config()).version).toBe(seen + 1);
  });

  it("is returned with the settings", async () => {
    const response = await h.admin("GET", "/api/settings");
    expect(await response.json()).toMatchObject({ settings: { accessMode: "invite-only", emergencyStop: false }, configVersion: 0 });
  });
});

describe("disable-only (SAFETY §2.3)", () => {
  it("the flags only switch capture and detection off; turning a flag off again is not an enable switch", async () => {
    await h.admin("PUT", "/api/settings", { disableCapture: true, disableDetection: true });
    let payload = (await config()).payload;
    expect(payload["disable"]).toEqual({ capture: true, detection: true });
    await h.admin("PUT", "/api/settings", { disableCapture: false, disableDetection: false });
    payload = (await config()).payload;
    expect(payload["disable"]).toEqual({ capture: false, detection: false });
    // Nothing in the document can switch capture or detection on, whatever the owner saves.
    for (const key of Object.keys(payload)) expect(key).not.toMatch(/enable|opt.?in|allow/i);
    expect(Object.keys(payload).sort()).toEqual(["accessMode", "applications", "banner", "defaultApplication", "disable", "emergencyStop", "minimumVersion", "schemaVersion"]);
  });

  it("the settings API refuses any member that could enable something", async () => {
    for (const payload of [{ enableCapture: true }, { captureEnabled: true }, { detection: true }, { disable: { capture: false } }, { onlineFeatures: true }]) {
      expect((await h.admin("PUT", "/api/settings", payload)).status).toBe(400);
    }
  });

  it("caps the banner length and refuses an invalid minimum version", async () => {
    expect((await h.admin("PUT", "/api/settings", { banner: "x".repeat(281) })).status).toBe(400);
    expect((await h.admin("PUT", "/api/settings", { banner: "x".repeat(280) })).status).toBe(200);
    expect((await h.admin("PUT", "/api/settings", { minimumVersion: "1.0" })).status).toBe(400);
    expect((await h.admin("PUT", "/api/settings", { banner: null })).status).toBe(200);
  });
});
