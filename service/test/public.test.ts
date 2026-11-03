// FR-ONL-08 (signed config, disable-only), FR-ONL-11 (account deletion, pseudonymisation), NFR-SVC-01 (headers,
// validation, no CORS), NFR-SVC-06 (schemaVersion on every response).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { ConfigCache } from "../src/config-cache.ts";
import { DEFAULT_SETTINGS, type Settings } from "../src/db/settings.ts";
import { configPayload } from "../src/routes/config-payload.ts";
import { verifyDocument } from "../src/signing/document.ts";
import { ADMIN, API, Harness, envelopeOf, newDevice, resetDatabase, rowCount, trustedKeys } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

describe("GET /v1/me and DELETE /v1/me", () => {
  it("returns the signed-in account and its devices", async () => {
    const device = await newDevice();
    const { body } = await h.signIn("141111111111111111", device, "Nea");
    const response = await h.call("GET", `${API}/v1/me`, { headers: { Authorization: `Bearer ${body["accessToken"] as string}` } });
    expect(response.status).toBe(200);
    const me = (await response.json()) as { user: Record<string, unknown>; devices: { current: boolean; thumbprint: string }[]; deviceLimit: number };
    expect(me.user).toMatchObject({ displayName: "Nea", status: "active", access: "invite_required" });
    expect(me.user).not.toHaveProperty("discordId");
    expect(me.devices).toEqual([expect.objectContaining({ current: true, thumbprint: (body["device"] as { thumbprint: string }).thumbprint })]);
    expect(me.deviceLimit).toBe(3);
  });

  it("deletes the account, devices and sessions and pseudonymises the audit trail", async () => {
    const invite = (await (await h.admin("POST", "/api/invites", {})).json()) as { invites: { code: string }[] };
    const device = await newDevice();
    const { body } = await h.signIn("151111111111111111", device, "Jake");
    const auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: invite.invites[0]!.code }, headers: auth })).status).toBe(200);
    const userId = (body["user"] as { id: string }).id;

    const response = await h.call("DELETE", `${API}/v1/me`, { headers: auth });
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ schemaVersion: 1, deleted: true, filesRemoved: true });
    for (const table of ["users", "devices", "token_families", "refresh_tokens"]) expect(await rowCount(table)).toBe(0);
    expect((await h.call("GET", `${API}/v1/me`, { headers: auth })).status).toBe(401);
    expect((await h.refresh(body["refreshToken"] as string, device)).status).toBe(401);

    const audit = (await env.DB.prepare("SELECT * FROM audit_log ORDER BY seq").all<Record<string, string>>()).results;
    expect(audit.map((r) => r["action"])).toEqual(["invite.create", "invite.redeem", "account.delete"]);
    const dump = JSON.stringify(audit);
    expect(dump).not.toContain("151111111111111111");
    expect(dump).not.toContain("Jake");
    expect(dump).toContain(`user:${userId}`);
    // The chain still verifies after deletion: pseudonymisation needed no rewrite.
    const chain = (await (await h.admin("GET", "/api/audit/verify")).json()) as { chain: { ok: boolean } };
    expect(chain.chain.ok).toBe(true);
  });

  it("needs a session", async () => {
    expect((await h.call("GET", `${API}/v1/me`)).status).toBe(401);
    expect((await h.call("DELETE", `${API}/v1/me`, { headers: { Authorization: "Bearer at_nottherightlength" } })).status).toBe(401);
  });
});

describe("GET /v1/config", () => {
  it("returns a config document signed for purpose config with a 14-day lifetime", async () => {
    const response = await h.call("GET", `${API}/v1/config`);
    expect(response.status).toBe(200);
    expect(response.headers.get("Cache-Control")).toBe("public, max-age=60");
    const { document } = (await response.json()) as { document: string };
    const result = await verifyDocument(document, "config", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
    expect(result.outcome).toBe("Valid");
    const envelope = envelopeOf(document);
    expect(Date.parse(envelope["expiresAt"] as string) - Date.parse(envelope["issuedAt"] as string)).toBe(14 * 86_400_000);
    expect(JSON.parse(result.envelope!.payloadJson)).toEqual({
      schemaVersion: 1,
      accessMode: "invite-only",
      applications: [
        {
          key: "dbd",
          applicationId: "1563291325107937340",
          name: "Dead by Daylight",
          description: "Unofficial fan-made presence — not affiliated with Behaviour Interactive",
        },
      ],
      defaultApplication: "dbd",
      disable: { capture: false, detection: false },
      emergencyStop: false,
      minimumVersion: "0.1.0",
      banner: null,
    });
  });

  it("reflects the owner's settings", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public", disableDetection: true, emergencyStop: true, minimumVersion: "0.2.0", banner: "Maintenance tonight" });
    const { document } = (await (await h.call("GET", `${API}/v1/config`)).json()) as { document: string };
    const result = await verifyDocument(document, "config", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
    expect(JSON.parse(result.envelope!.payloadJson)).toMatchObject({
      accessMode: "public",
      disable: { capture: false, detection: true },
      emergencyStop: true,
      minimumVersion: "0.2.0",
      banner: "Maintenance tonight",
    });
  });

  /** env with a D1 binding that counts the statements prepared through it. */
  function countingEnv(): { env: typeof env; statements: () => number } {
    let statements = 0;
    const db = new Proxy(env.DB, {
      get(target, property) {
        if (property === "prepare") {
          return (query: string) => {
            statements++;
            return target.prepare(query);
          };
        }
        const value = Reflect.get(target, property, target) as unknown;
        return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
      },
    });
    return { env: { ...env, DB: db }, statements: () => statements };
  }

  async function fetchConfig(e: typeof env, deps = h.deps): Promise<string> {
    const response = await handleRequest(new Request(`${API}/v1/config`), e, deps);
    expect(response.status).toBe(200);
    return ((await response.json()) as { document: string }).document;
  }

  it("serves repeated requests for 60 seconds without reading D1 or signing again", async () => {
    const counted = countingEnv();
    const first = await fetchConfig(counted.env);
    // One read batch: the settings (with the config version) and the application list.
    expect(counted.statements()).toBe(2);
    for (let i = 0; i < 5; i++) {
      h.now += 10_000;
      expect(await fetchConfig(counted.env)).toBe(first);
    }
    expect(counted.statements()).toBe(2);
    h.now += 10_000;
    const next = await fetchConfig(counted.env);
    expect(next).not.toBe(first);
    expect(counted.statements()).toBe(4);
    // Re-signed with a new lifetime but the same version: nothing the document carries has changed.
    expect(envelopeOf(next)["version"]).toBe(envelopeOf(first)["version"]);
    expect(envelopeOf(next)["issuedAt"]).not.toBe(envelopeOf(first)["issuedAt"]);
  });

  it("shares the signed document between isolates through the Cache API", async () => {
    const counted = countingEnv();
    const first = await fetchConfig(counted.env);
    // A second isolate: its own in-memory copy is empty, the data centre's Cache API is shared.
    const otherIsolate = { ...h.deps, configCache: new ConfigCache(() => caches.open(h.cacheName)) };
    h.now += 30_000;
    expect(await fetchConfig(counted.env, otherIsolate)).toBe(first);
    expect(counted.statements()).toBe(2);
  });

  it("serves a settings change at once after the owner saves it", async () => {
    const before = await fetchConfig(env);
    expect((await h.admin("PUT", "/api/settings", { emergencyStop: true })).status).toBe(200);
    const after = await fetchConfig(env);
    expect(after).not.toBe(before);
    const result = await verifyDocument(after, "config", "stable", { keys: await trustedKeys(), now: new Date(h.now) });
    expect(JSON.parse(result.envelope!.payloadJson)).toMatchObject({ emergencyStop: true });
  });

  it("can never enable capture or detection (disable-only, SAFETY §2.3)", () => {
    const flags: (keyof Settings)[] = ["disableCapture", "disableDetection", "emergencyStop"];
    for (let mask = 0; mask < 1 << flags.length; mask++) {
      const settings: Settings = { ...DEFAULT_SETTINGS };
      flags.forEach((f, i) => ((settings as unknown as Record<string, unknown>)[f] = (mask & (1 << i)) !== 0));
      for (const accessMode of ["invite-only", "public"] as const) {
        const text = JSON.stringify(configPayload({ ...settings, accessMode }));
        expect(text).not.toMatch(/enable|opt.?in|allow/i);
        const payload = JSON.parse(text) as { disable: Record<string, unknown> };
        expect(Object.keys(payload.disable).sort()).toEqual(["capture", "detection"]);
        for (const value of Object.values(payload.disable)) expect(typeof value).toBe("boolean");
      }
    }
  });

  it("the settings API has no switch that could enable capture or detection", async () => {
    for (const body of [{ enableCapture: true }, { enableDetection: true }, { captureEnabled: true }, { disableCapture: "no" }]) {
      expect((await h.admin("PUT", "/api/settings", body)).status).toBe(400);
    }
  });
});

describe("hardening on every response", () => {
  const expectHardened = (response: Response) => {
    expect(response.headers.get("Strict-Transport-Security")).toBe("max-age=63072000; includeSubDomains");
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(response.headers.get("X-Frame-Options")).toBe("DENY");
    expect(response.headers.get("Content-Security-Policy")).toContain("frame-ancestors 'none'");
    expect(response.headers.get("Content-Security-Policy")).toContain("default-src 'none'");
    expect(response.headers.get("Referrer-Policy")).toBe("no-referrer");
    expect(response.headers.get("Content-Type")).toBe("application/json; charset=utf-8");
    expect(response.headers.has("Set-Cookie")).toBe(false);
    for (const name of response.headers.keys()) expect(name.startsWith("access-control-")).toBe(false);
  };

  it("applies to successes, errors, 404s and 405s on both hosts", async () => {
    const device = await newDevice();
    const { body } = await h.signIn("161111111111111111", device);
    const auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
    const responses = [
      await h.call("GET", `${API}/v1/config`),
      await h.call("GET", `${API}/v1/packs?channel=stable`),
      await h.call("GET", `${API}/v1/me`, { headers: auth }),
      await h.call("GET", `${API}/v1/me`),
      await h.call("GET", `${API}/v1/nothing`),
      await h.call("GET", `${API}/`),
      await h.call("PUT", `${API}/v1/config`),
      await h.call("OPTIONS", `${API}/v1/auth/discord`, { headers: { Origin: "https://evil.example" } }),
      await h.call("POST", `${API}/v1/auth/discord`, { rawBody: "{", headers: { "Content-Type": "application/json" } }),
      await h.call("GET", `${ADMIN}/api/users`),
      await h.call("GET", `${ADMIN}/`),
      await h.call("GET", "https://elsewhere.example/v1/config"),
    ];
    for (const response of responses) {
      expectHardened(response);
      expect((await response.json()) as Record<string, unknown>).toHaveProperty("schemaVersion", 1);
    }
    expect(responses.map((r) => r.status)).toEqual([200, 200, 200, 401, 404, 404, 405, 405, 400, 401, 404, 404]);
  });

  it("returns only an error code, never a stack or message", async () => {
    const response = await h.call("POST", `${API}/v1/auth/discord`, { rawBody: '{"code":1}', headers: { "Content-Type": "application/json" } });
    expect(await response.text()).toBe('{"schemaVersion":1,"error":"invalid_request"}');
  });

  it("caps request bodies and requires JSON", async () => {
    const big = await h.call("POST", `${API}/v1/auth/discord`, { rawBody: JSON.stringify({ code: "x".repeat(5000) }), headers: { "Content-Type": "application/json" } });
    expect(big.status).toBe(413);
    const form = await h.call("POST", `${API}/v1/auth/discord`, { rawBody: "code=abc", headers: { "Content-Type": "application/x-www-form-urlencoded" } });
    expect(form.status).toBe(415);
    const duplicate = await h.call("POST", `${API}/v1/auth/refresh`, { rawBody: '{"refreshToken":"a","refreshToken":"b"}', headers: { "Content-Type": "application/json" } });
    expect(duplicate.status).toBe(400);
    const deep = await h.call("POST", `${API}/v1/auth/refresh`, { rawBody: '{"a":[[[[[[[1]]]]]]]}', headers: { "Content-Type": "application/json" } });
    expect(deep.status).toBe(400);
  });

  it("refuses plain http", async () => {
    const response = await h.call("GET", "http://api.arcavale.com/v1/config");
    expect(response.status).toBe(400);
  });

  it("answers 503 not_configured when a secret is missing, without echoing anything", async () => {
    const { handleRequest } = await import("../src/app.ts");
    const response = await handleRequest(new Request(`${API}/v1/config`), { ...env, CONFIG_SIGNING_KEY: undefined }, h.deps);
    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "not_configured" });
    const broken = await handleRequest(new Request(`${API}/v1/config`), { ...env, CONFIG_SIGNING_KEY: '{"kid":"x","pkcs8":"AAAA"}' }, h.deps);
    expect(broken.status).toBe(503);
  });
});
