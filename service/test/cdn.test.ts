// NFR-SVC-02 / PLAN_V2 §1 host hardening and QUALITY_GATES V4.5 (the serving-headers test for /packs/): the content
// host serves only /packs/<64 hex>.(png|jpg) from R2 with a fixed image type, nosniff, a sandbox CSP, no cookie and an
// immutable cache lifetime; 404, 405 and 500 carry the same hardening with a text/plain body.

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { CDN, Harness, resetDatabase } from "./helpers.ts";
import { jpeg, png } from "./images.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

const SHA = "ab".repeat(32);

function expectHardened(response: Response): void {
  expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
  expect(response.headers.get("Content-Security-Policy")).toBe("default-src 'none'; sandbox");
  expect(response.headers.get("Strict-Transport-Security")).toBe("max-age=63072000; includeSubDomains");
  expect(response.headers.get("Referrer-Policy")).toBe("no-referrer");
  expect(response.headers.has("Set-Cookie")).toBe(false);
  for (const name of response.headers.keys()) expect(name.startsWith("access-control-")).toBe(false);
}

async function get(path: string, init: RequestInit = {}, e = env): Promise<Response> {
  return handleRequest(new Request(`${CDN}${path}`, init), e, h.deps);
}

describe("content host", () => {
  it("serves a stored PNG and JPEG with a fixed type and an immutable lifetime", async () => {
    const pngBytes = png();
    const jpgBytes = jpeg();
    // Stored with a misleading type: the response type comes from the file name, never from the object.
    await env.PACKS.put(`${SHA}.png`, pngBytes, { httpMetadata: { contentType: "text/html" } });
    await env.PACKS.put(`${"cd".repeat(32)}.jpg`, jpgBytes);
    const a = await get(`/packs/${SHA}.png`);
    expect(a.status).toBe(200);
    expectHardened(a);
    expect(a.headers.get("Content-Type")).toBe("image/png");
    expect(a.headers.get("Cache-Control")).toBe("public, max-age=31536000, immutable");
    expect(a.headers.get("Content-Length")).toBe(String(pngBytes.length));
    expect(a.headers.get("ETag")).toBe(`"${SHA}"`);
    expect(new Uint8Array(await a.arrayBuffer())).toEqual(pngBytes);
    const b = await get(`/packs/${"cd".repeat(32)}.jpg`);
    expect(b.headers.get("Content-Type")).toBe("image/jpeg");
    expect(new Uint8Array(await b.arrayBuffer())).toEqual(jpgBytes);
  });

  it("answers HEAD with the headers and no body", async () => {
    await env.PACKS.put(`${SHA}.png`, png());
    const response = await get(`/packs/${SHA}.png`, { method: "HEAD" });
    expect(response.status).toBe(200);
    expectHardened(response);
    expect(response.headers.get("Content-Type")).toBe("image/png");
    expect(await response.text()).toBe("");
  });

  it.each([
    "/",
    "/packs/",
    `/packs/${SHA}`,
    `/packs/${SHA}.PNG`,
    `/packs/${SHA}.jpeg`,
    `/packs/${SHA}.gif`,
    `/packs/${SHA}.png/`,
    `/packs/${SHA.toUpperCase()}.png`,
    `/packs/${SHA.slice(1)}.png`,
    `/packs/${SHA}a.png`,
    `/packs/%2e%2e/${SHA}.png`,
    `/u/${SHA}.png`,
    `/app/setup.exe`,
    "/v1/config",
    "/api/packs",
    "/favicon.ico",
  ])("serves nothing at %s (404, hardened, text/plain)", async (path) => {
    // Even when an object with a matching key exists.
    await env.PACKS.put(`${SHA}.png`, png());
    const response = await get(path);
    expect(response.status).toBe(404);
    expectHardened(response);
    expect(response.headers.get("Content-Type")).toBe("text/plain; charset=utf-8");
    expect(await response.text()).toBe("Not found\n");
  });

  it("answers 404 for a missing file, cached briefly", async () => {
    const missing = await get(`/packs/${SHA}.png`);
    expect(missing.status).toBe(404);
    expectHardened(missing);
    expect(missing.headers.get("Content-Type")).toBe("text/plain; charset=utf-8");
    expect(missing.headers.get("Cache-Control")).toBe("public, max-age=60");
  });

  it.each(["POST", "PUT", "DELETE", "OPTIONS", "PATCH"])("refuses %s with 405, hardened", async (method) => {
    await env.PACKS.put(`${SHA}.png`, png());
    const response = await get(`/packs/${SHA}.png`, { method, headers: { Origin: "https://evil.example" } });
    expect(response.status).toBe(405);
    expectHardened(response);
    expect(response.headers.get("Content-Type")).toBe("text/plain; charset=utf-8");
    expect(response.headers.get("Allow")).toBe("GET, HEAD");
    expect(response.headers.get("Cache-Control")).toBe("no-store");
    expect(await env.PACKS.head(`${SHA}.png`)).not.toBeNull();
  });

  it("answers 500 with the same hardening when storage fails, and no detail", async () => {
    const broken = {
      ...env,
      PACKS: {
        get: () => Promise.reject(new Error("storage down: secret detail")),
        head: () => Promise.reject(new Error("storage down: secret detail")),
      } as unknown as R2Bucket,
    };
    const response = await get(`/packs/${SHA}.png`, {}, broken);
    expect(response.status).toBe(500);
    expectHardened(response);
    expect(response.headers.get("Content-Type")).toBe("text/plain; charset=utf-8");
    expect(response.headers.get("Cache-Control")).toBe("no-store");
    expect(await response.text()).toBe("Internal error\n");
  });

  it("refuses plain http with 404", async () => {
    await env.PACKS.put(`${SHA}.png`, png());
    const response = await handleRequest(new Request(`http://cdn.arcavale.com/packs/${SHA}.png`), env, h.deps);
    expect(response.status).toBe(404);
    expectHardened(response);
  });

  it("serves the file the admin API stored, at the address it returned", async () => {
    const bytes = png({ width: 300, height: 300 });
    const upload = await h.upload(bytes, "image/png");
    const { file } = (await upload.json()) as { file: { url: string } };
    const response = await handleRequest(new Request(file.url), env, h.deps);
    expect(response.status).toBe(200);
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(bytes);
  });
});
