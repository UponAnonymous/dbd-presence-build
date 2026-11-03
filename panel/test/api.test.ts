// FR-ADM-03 (custom header, same-origin, no CORS) and the error classification behind the error states.

import { describe, expect, it } from "vitest";
import { AdminApi, ApiError } from "../src/api.ts";
import { describeError } from "../src/errors.ts";
import { FakeBackend, ids } from "./fake.ts";

function api(backend = new FakeBackend(), online = true) {
  return { backend, api: new AdminApi(backend.fetch, () => online) };
}

describe("admin API client: request protection", () => {
  it("sends the custom header, same-origin credentials and mode, and follows no redirect on every call", async () => {
    const { backend, api: client } = api();
    await client.listInvites();
    await client.listUsers();
    await client.listAudit(null, 25);
    await client.verifyAudit();
    await client.getSettings();
    await client.createInvites({ count: 2, note: "n", expiresInDays: 7 });
    await client.revokeInvite(ids.invite1);
    await client.revokeUser(ids.user1);
    await client.revokeDevice(ids.user1, ids.device1);
    await client.deleteUser(ids.user1);
    await client.exportUser(ids.user1);
    await client.setAccessMode("public");
    await client.getSettingsState();
    await client.saveSettings({ emergencyStop: true });
    await client.getApps();
    await client.saveApps([{ key: "dbd", applicationId: "1563291325107937340", name: "N", description: "" }], "dbd");
    await client.listPacks();
    await client.updatePack("original", { name: "N" });
    await client.publishPack("original");
    await client.uploadFile(new Uint8Array([0x89, 0x50, 0x4e, 0x47]), "image/png");
    await client.withdrawEntry("original", `e${"4".padStart(24, "0")}`, true);
    await client.purgeFiles();
    expect(backend.requests).toHaveLength(22);
    for (const request of backend.requests) {
      expect(request.headers["X-DBDP-Admin"]).toBe("1");
      expect(request.init.credentials).toBe("same-origin");
      expect(request.init.mode).toBe("same-origin");
      expect(request.init.redirect).toBe("manual");
      expect(request.init.cache).toBe("no-store");
      expect(request.url.startsWith("/api/")).toBe(true);
    }
  });

  it("uses the service's methods, paths and bodies", async () => {
    const { backend, api: client } = api();
    await client.createInvites({ count: 3, note: null, expiresInDays: 14 });
    await client.deleteUser(ids.user2);
    await client.setAccessMode("public");
    await client.listAudit(120, 25);
    const [create, del, put, audit] = backend.requests;
    expect([create!.method, create!.url, create!.body]).toEqual(["POST", "/api/invites", { count: 3, expiresInDays: 14 }]);
    expect(create!.headers["Content-Type"]).toBe("application/json");
    expect([del!.method, del!.url, del!.body]).toEqual(["DELETE", `/api/users/${ids.user2}`, { confirm: ids.user2 }]);
    expect([put!.method, put!.url, put!.body]).toEqual(["PUT", "/api/settings", { accessMode: "public" }]);
    expect(audit!.url).toBe("/api/audit?limit=25&before=120");
  });

  it("refuses ids that are not UUIDs before sending anything", async () => {
    const { backend, api: client } = api();
    await expect(client.revokeInvite("../settings")).rejects.toMatchObject({ kind: "client", code: "invalid_id" });
    await expect(client.publishPack("../settings")).rejects.toMatchObject({ kind: "client", code: "invalid_id" });
    await expect(client.removeEntry("original", "../../x")).rejects.toMatchObject({ kind: "client", code: "invalid_id" });
    expect(backend.requests).toHaveLength(0);
  });
});

describe("admin API client: error classification", () => {
  const cases: [string, Parameters<FakeBackend["fail"]>[1], Partial<ApiError>][] = [
    ["a network failure", "network", { kind: "offline", code: "network" }],
    ["a redirect (expired Access session)", "redirect", { kind: "session" }],
    ["401", { status: 401, error: "unauthenticated" }, { kind: "session", status: 401, code: "unauthenticated" }],
    ["403 for a non-owner", { status: 403, error: "forbidden" }, { kind: "forbidden", status: 403, code: "forbidden" }],
    ["409", { status: 409, error: "invite_not_revocable" }, { kind: "client", status: 409, code: "invite_not_revocable" }],
    ["500", { status: 500, error: "internal" }, { kind: "server", status: 500, code: "internal" }],
    ["503", { status: 503, error: "not_configured" }, { kind: "server", status: 503, code: "not_configured" }],
    ["an HTML page instead of JSON", "html", { kind: "unexpected" }],
  ];
  it.each(cases)("classifies %s", async (_label, failure, expected) => {
    const { backend, api: client } = api();
    backend.fail("GET /api/users", failure);
    const error = await client.listUsers().catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject(expected);
  });

  it("reports no connection when the browser is offline", async () => {
    const { backend, api: client } = api(new FakeBackend(), false);
    backend.fail("GET /api/users", "network");
    await expect(client.listUsers()).rejects.toMatchObject({ kind: "offline", code: "no_connection" });
  });

  it("rejects a response whose shape does not match", async () => {
    const backend = new FakeBackend();
    (backend.data.users[0] as unknown as Record<string, unknown>)["status"] = "superuser";
    await expect(new AdminApi(backend.fetch).listUsers()).rejects.toMatchObject({ kind: "unexpected" });
  });
});

describe("admin API client: audit verification", () => {
  it("reads the exported head of the chain report, present or absent", async () => {
    const { backend, api: client } = api();
    expect((await client.verifyAudit()).exportedHead).toBeNull();

    backend.chain = { ok: true, checked: 30, brokenAtSeq: null, anchored: true, exportedHead: { seq: 24, hash: "d".repeat(64), present: true } };
    expect(await client.verifyAudit()).toEqual({ ok: true, checked: 30, brokenAtSeq: null, anchored: true, exportedHead: { seq: 24, hash: "d".repeat(64), present: true } });
  });

  it("refuses a chain report whose exported head is malformed", async () => {
    const { backend, api: client } = api();
    backend.chain = { ok: true, checked: 1, brokenAtSeq: null, anchored: false, exportedHead: { seq: "24", hash: "d", present: true } as never };
    await expect(client.verifyAudit()).rejects.toMatchObject({ kind: "unexpected" });
  });

  it("explains the user_changed conflict without claiming anything was deleted", () => {
    const copy = describeError(new ApiError("client", 409, "user_changed"));
    expect(copy.detail).toContain("nothing was deleted");
    expect(copy.detail).toContain("(user_changed)");
    expect(copy.action).toBe("retry");
  });
});
