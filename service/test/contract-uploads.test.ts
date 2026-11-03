// PLAN_V2 §5 contracts: the shared fixture tests/fixtures/uploads/contract.json (also parsed by the C# suite,
// tests/DbdPresence.Online.Tests/UploadContractTests.cs) must describe what the Worker really answers: the same
// members with the same JSON types, for GET /v1/me with uploads, POST /v1/uploads, DELETE /v1/uploads/{id} and the
// upload error codes.

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it, inject } from "vitest";
import { handleRequest } from "../src/app.ts";
import { PRODUCTION_STORAGE_LIMITS } from "../src/storage.ts";
import { API, Harness, newDevice, resetDatabase } from "./helpers.ts";
import { app, jpeg, png } from "./images.ts";

const contract = JSON.parse(inject("uploadsContract")) as {
  me: unknown;
  created: { status: number; body: unknown };
  deleted: { status: number; body: unknown };
  errors: { status: number; body: { error: string } }[];
};

/** The JSON shape of a value: member names and types, arrays by their first element. */
function shape(value: unknown): unknown {
  if (value === null) return "null";
  if (Array.isArray(value)) return value.length === 0 ? [] : [shape(value[0])];
  if (typeof value === "object") return Object.fromEntries(Object.entries(value as Record<string, unknown>).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => [k, shape(v)]));
  return typeof value;
}

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

async function signedIn(): Promise<Record<string, string>> {
  await h.admin("PUT", "/api/settings", { accessMode: "public" });
  const { body } = await h.signIn("580000000000000001", await newDevice());
  return { Authorization: `Bearer ${body["accessToken"] as string}` };
}

function post(auth: Record<string, string>, bytes: Uint8Array, type = "image/png"): Promise<Response> {
  const data = new FormData();
  data.append("file", new File([bytes], "image.png", { type }));
  return handleRequest(new Request(`${API}/v1/uploads`, { method: "POST", headers: { ...auth, "CF-Connecting-IP": h.ip }, body: data }), env, h.deps);
}

describe("upload contract fixture", () => {
  it("matches POST /v1/uploads, GET /v1/me and DELETE /v1/uploads/{id}", async () => {
    const auth = await signedIn();
    const created = await post(auth, png());
    expect(created.status).toBe(contract.created.status);
    const createdBody = (await created.json()) as { upload: { id: string } };
    expect(shape(createdBody)).toEqual(shape(contract.created.body));

    const me = await h.call("GET", `${API}/v1/me`, { headers: auth });
    expect(shape(await me.json())).toEqual(shape(contract.me));

    const deleted = await h.call("DELETE", `${API}/v1/uploads/${createdBody.upload.id}`, { headers: auth });
    expect(deleted.status).toBe(contract.deleted.status);
    expect(await deleted.json()).toEqual(contract.deleted.body);
  });

  it("lists every error code the app handles, each with the status the Worker really sends", async () => {
    const auth = await signedIn();
    const seen = new Map<string, { status: number; body: unknown }>();
    const note = async (response: Response) => {
      const body = (await response.json()) as { error: string };
      seen.set(body.error, { status: response.status, body });
    };
    await note(await post(auth, jpeg({ before: [app(0)] }), "image/jpeg"));
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: 0 });
    await note(await post(auth, png()));
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: 62_914_560, uploadsBlocked: true });
    await note(await post(auth, png()));
    await h.admin("PUT", "/api/settings", { uploadsBlocked: false, emergencyStop: true });
    await note(await post(auth, png()));
    await h.admin("PUT", "/api/settings", { emergencyStop: false, accessMode: "invite-only" });
    const { body } = await h.signIn("580000000000000002", await newDevice());
    await note(await post({ Authorization: `Bearer ${body["accessToken"] as string}` }, png()));
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(PRODUCTION_STORAGE_LIMITS.uploadCapBytes).run();
    await note(await post(auth, png()));
    await note(await post(auth, png({ idatBytes: 2_200_000 })));
    await note(await h.call("DELETE", `${API}/v1/uploads/00000000-0000-4000-8000-000000000000`, { headers: auth }));
    // The per-account attempt counter (400 a day) answers 429 with Retry-After.
    await env.DB.prepare("UPDATE rate_limits SET count = 400 WHERE bucket LIKE 'upload-attempt:account:%'").run();
    const limited = await post(auth, png());
    expect(limited.headers.get("Retry-After")).toMatch(/^\d+$/);
    await note(limited);
    for (const expected of contract.errors) {
      const actual = seen.get(expected.body.error);
      expect(actual, expected.body.error).toBeDefined();
      expect(actual!.status, expected.body.error).toBe(expected.status);
      expect(shape(actual!.body), expected.body.error).toEqual(shape(expected.body));
    }
  });
});
