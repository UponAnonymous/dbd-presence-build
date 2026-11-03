// V2-S5 uploads (FR-ONL-09, FR-ONL-11, FR-ADM-09, NFR-SVC-02, D18; QUALITY_GATES V5.1–V5.4):
// POST /v1/uploads and DELETE /v1/uploads/{id}, the unlisted /u/ path on the content host and its serving headers, the
// strict parser through the route (fuzz), limits and blocks, the owner's takedown and per-user block, the storage cap,
// and account deletion removing uploads (rows, ledger, files) with the audit pseudonymised.

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { runAuditExport } from "../src/audit-export.ts";
import { loadBanKey } from "../src/bans.ts";
import { sweepUploads, SWEEP_GRACE_MS } from "../src/db/uploads.ts";
import { MAX_IMAGE_BYTES } from "../src/images/strict.ts";
import { sha256Hex } from "../src/lib/crypto.ts";
import { ACCOUNT_LIMITS, D1_DAILY_WRITE_LIMIT, SENSITIVE_WRITE_BUDGET, accountRouteDailyWrites } from "../src/ratelimit.ts";
import { INACTIVE_ACCOUNT_MS, runRetention } from "../src/retention.ts";
import { PRODUCTION_STORAGE_LIMITS, STAGING_STORAGE_LIMITS, reserveStorage, reserveUpload, storageLimits, storageUsage } from "../src/storage.ts";
import { API, CDN, Harness, interceptBatches, newDevice, resetDatabase, rowCount } from "./helpers.ts";
import { deleteUser } from "../src/routes/me.ts";
import { releaseObjects } from "../src/db/packs.ts";
import { CPU_BUDGET_MS, bestMedian } from "./cpu.ts";
import { DHT, DQT, EOI, PNG_SIG, SOF0, SOI, app, chunk, com, concat, concatList, denseJpeg, entropy, filler, ihdr, jpeg, png, sof, sos } from "./images.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

interface Account {
  userId: string;
  accessToken: string;
  auth: Record<string, string>;
}

let discordSeq = 0;
async function account(options: { access?: boolean } = {}): Promise<Account> {
  if (options.access !== false) await h.admin("PUT", "/api/settings", { accessMode: "public" });
  else await h.admin("PUT", "/api/settings", { accessMode: "invite-only" });
  const { status, body } = await h.signIn(`5900000000000${String(++discordSeq).padStart(5, "0")}`, await newDevice());
  expect(status).toBe(200);
  const accessToken = body["accessToken"] as string;
  return { userId: (body["user"] as { id: string }).id, accessToken, auth: { Authorization: `Bearer ${accessToken}` } };
}

function form(parts: { name: string; bytes: Uint8Array; type: string }[]): FormData {
  const data = new FormData();
  for (const p of parts) data.append(p.name, new File([p.bytes], "image", { type: p.type }));
  return data;
}

async function upload(who: Account, bytes: Uint8Array, type = "image/png", body?: BodyInit, headers: Record<string, string> = {}): Promise<Response> {
  const request = new Request(`${API}/v1/uploads`, { method: "POST", headers: { ...who.auth, "CF-Connecting-IP": h.ip, ...headers }, body: body ?? form([{ name: "file", bytes, type }]) });
  return handleRequest(request, env, h.deps);
}

interface UploadBody {
  id: string;
  url: string;
  sha256: string;
  format: string;
  size: number;
  width: number;
  height: number;
  createdAt: string;
}

async function uploaded(response: Response): Promise<UploadBody> {
  expect([200, 201]).toContain(response.status);
  return ((await response.json()) as { upload: UploadBody }).upload;
}

function cdnGet(url: string, init: RequestInit = {}): Promise<Response> {
  return handleRequest(new Request(url, init), env, h.deps);
}

interface MeUploads {
  items: UploadBody[];
  limit: number;
  limitBytes: number;
  limitFiles: number;
  usedBytes: number;
  usedFiles: number;
  blocked: string | null;
}

async function me(who: Account): Promise<{ uploads: MeUploads }> {
  const response = await h.call("GET", `${API}/v1/me`, { headers: who.auth });
  expect(response.status).toBe(200);
  return (await response.json()) as { uploads: MeUploads };
}

const UPLOAD_URL = /^https:\/\/cdn\.arcavale\.com\/u\/[0-9a-f]{32}\.(png|jpg)$/;

describe("POST /v1/uploads", () => {
  it("stores a strictly parsed PNG and JPEG under an unlisted random path with no user id, and lists them in GET /v1/me", async () => {
    const who = await account();
    const pngBytes = png({ width: 640, height: 480 });
    const a = await upload(who, pngBytes);
    expect(a.status).toBe(201);
    const first = await uploaded(a);
    h.now += 1;
    expect(first.url).toMatch(UPLOAD_URL);
    expect(first.url.endsWith(".png")).toBe(true);
    expect(first.url).not.toContain(who.userId);
    expect(first).toMatchObject({ format: "png", size: pngBytes.length, width: 640, height: 480, sha256: await sha256Hex(pngBytes) });
    const second = await uploaded(await upload(who, jpeg(), "image/jpeg"));
    expect(second.url.endsWith(".jpg")).toBe(true);
    expect(second.url).not.toBe(first.url);

    const listed = await me(who);
    expect(listed.uploads.items.map((u) => u.id)).toEqual([first.id, second.id]);
    // The quota (D28, FR-ONL-15): 60 MB and 300 pictures per person by default; `limit` is the file cap for older apps.
    expect(listed.uploads).toMatchObject({ limit: 300, limitBytes: 62_914_560, limitFiles: 300, usedBytes: first.size + second.size, usedFiles: 2 });
    expect(listed.uploads.blocked).toBeNull();
    // The stored bytes are exactly the uploaded ones.
    const served = await cdnGet(first.url);
    expect(new Uint8Array(await served.arrayBuffer())).toEqual(pngBytes);
    expect(await rowCount("audit_log", "action = 'upload.create' AND actor = ?", `user:${who.userId}`)).toBe(2);
  });

  it("returns the existing upload for the same bytes from the same user, and a separate file for another user", async () => {
    const alice = await account();
    const bob = await account();
    const bytes = png();
    const first = await uploaded(await upload(alice, bytes));
    const again = await upload(alice, bytes);
    expect(again.status).toBe(200);
    expect((await uploaded(again)).id).toBe(first.id);
    const bobs = await uploaded(await upload(bob, bytes));
    expect(bobs.url).not.toBe(first.url);
    expect(await rowCount("uploads")).toBe(2);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.uploads).toEqual({ bytes: 2 * bytes.length, objects: 2 });
  });

  it("refuses files that fail the strict parser and stores nothing (V5.1: allow-lists, CRC, trailing data, dimensions, size)", async () => {
    const who = await account();
    const idat = chunk("IDAT", filler(64, 7));
    const rejected: [string, Uint8Array, string, string][] = [
      ["text chunk", png({ middle: [chunk("tEXt", new TextEncoder().encode("Comment\0hi")), idat] }), "image/png", "metadata_not_allowed"],
      ["colour profile", png({ middle: [chunk("iCCP", filler(20)), idat] }), "image/png", "metadata_not_allowed"],
      ["EXIF chunk", png({ middle: [chunk("eXIf", filler(20)), idat] }), "image/png", "metadata_not_allowed"],
      ["animation", png({ middle: [chunk("acTL", filler(8)), idat] }), "image/png", "animation_not_allowed"],
      ["private chunk", png({ middle: [chunk("prVt", filler(8)), idat] }), "image/png", "chunk_not_allowed"],
      ["bad CRC", concat(PNG_SIG, ihdr(64, 64), chunk("IDAT", filler(64), true), chunk("IEND")), "image/png", "bad_checksum"],
      ["data after IEND", concat(png(), new Uint8Array([0x3c, 0x73, 0x63, 0x72, 0x69, 0x70, 0x74, 0x3e])), "image/png", "trailing_data"],
      ["too wide", png({ width: 1025, height: 10 }), "image/png", "dimensions_out_of_range"],
      ["JPEG with APP0 (JFIF)", jpeg({ before: [app(0)] }), "image/jpeg", "metadata_not_allowed"],
      ["JPEG with APP1 (EXIF)", jpeg({ before: [app(1, new TextEncoder().encode("Exif\0\0"))] }), "image/jpeg", "metadata_not_allowed"],
      ["JPEG comment", jpeg({ before: [com("hello")] }), "image/jpeg", "metadata_not_allowed"],
      ["data after EOI", concat(jpeg(), new Uint8Array([1, 2, 3])), "image/jpeg", "trailing_data"],
      ["too tall JPEG", jpeg({ width: 10, height: 2000 }), "image/jpeg", "dimensions_out_of_range"],
      ["GIF", new TextEncoder().encode("GIF89a\x01\x00\x01\x00\x00\x00\x00;"), "image/png", "unknown_format"],
      ["SVG", new TextEncoder().encode("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), "image/png", "unknown_format"],
      ["JPEG declared as PNG", jpeg(), "image/png", "type_mismatch"],
    ];
    for (const [name, bytes, type, reason] of rejected) {
      h.now += 1;
      const response = await upload(who, bytes, type);
      expect(response.status, name).toBe(422);
      const body = (await response.json()) as { error: string; reason: string };
      expect(body.error, name).toBe("image_rejected");
      expect(body.reason, name).toBe(reason);
      // Refused files count as failures on the account; a new day starts a new counter.
      await env.DB.prepare("DELETE FROM rate_limits").run();
    }
    expect(await rowCount("uploads")).toBe(0);
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.uploads.bytes).toBe(0);
  });

  it("refuses a file over 2 MB before parsing it (413) and accepts one right at the cap", async () => {
    const who = await account();
    const atCap = png({ width: 1024, height: 1024, idatBytes: MAX_IMAGE_BYTES - 57 });
    expect(atCap.length).toBe(MAX_IMAGE_BYTES);
    expect((await upload(who, atCap)).status).toBe(201);
    const over = png({ width: 1024, height: 1024, idatBytes: MAX_IMAGE_BYTES + 20_000 });
    expect((await upload(who, over)).status).toBe(413);
  });

  it("accepts only one multipart part named file, of an image type", async () => {
    const who = await account();
    const bytes = png();
    const cases: [string, BodyInit | undefined, Record<string, string>, number][] = [
      // A second part ends up as trailing data after the image's end and is refused by the image check.
      ["two parts", form([{ name: "file", bytes, type: "image/png" }, { name: "file", bytes, type: "image/png" }]), {}, 422],
      ["wrong name", form([{ name: "image", bytes, type: "image/png" }]), {}, 400],
      ["text field", (() => { const f = new FormData(); f.append("file", "hello"); return f; })(), {}, 400],
      ["preamble", `x\r\n--b\r\nContent-Disposition: form-data; name="file"\r\nContent-Type: image/png\r\n\r\nabc\r\n--b--`, { "Content-Type": "multipart/form-data; boundary=b" }, 400],
      ["unknown part header", `--b\r\nContent-Disposition: form-data; name="file"\r\nContent-Type: image/png\r\nX-Other: 1\r\n\r\nabc\r\n--b--`, { "Content-Type": "multipart/form-data; boundary=b" }, 400],
      ["no closing delimiter", `--b\r\nContent-Disposition: form-data; name="file"\r\nContent-Type: image/png\r\n\r\nabc\r\n--b`, { "Content-Type": "multipart/form-data; boundary=b" }, 400],
      ["epilogue", `--b\r\nContent-Disposition: form-data; name="file"\r\nContent-Type: image/png\r\n\r\nabc\r\n--b--\r\nmore`, { "Content-Type": "multipart/form-data; boundary=b" }, 400],
      ["wrong Content-Length", `--b\r\nContent-Disposition: form-data; name="file"\r\nContent-Type: image/png\r\nContent-Length: 4\r\n\r\nabc\r\n--b--`, { "Content-Type": "multipart/form-data; boundary=b" }, 400],
      ["webp part", form([{ name: "file", bytes, type: "image/webp" }]), {}, 415],
      ["raw PNG body", bytes, { "Content-Type": "image/png" }, 415],
      ["JSON body", JSON.stringify({ file: "x" }), { "Content-Type": "application/json" }, 415],
      ["broken multipart", "--x\r\nContent-Disposition: form-data; name=\"file\"\r\n\r\nno end", { "Content-Type": "multipart/form-data; boundary=x" }, 400],
    ];
    for (const [name, body, headers, status] of cases) {
      const response = await upload(who, bytes, "image/png", body, headers);
      expect(response.status, name).toBe(status);
    }
    // Hand-built in the shape the app sends (unquoted name, filename*, CRLF after the closing delimiter) is accepted.
    const head = new TextEncoder().encode(`--abc123\r\nContent-Type: image/png\r\nContent-Disposition: form-data; name=file; filename=image.png; filename*=utf-8''image.png\r\n\r\n`);
    const tail = new TextEncoder().encode("\r\n--abc123--\r\n");
    const ok = await upload(who, bytes, "image/png", concat(head, bytes, tail), { "Content-Type": "multipart/form-data; boundary=\"abc123\"" });
    expect(ok.status).toBe(201);
    expect(await rowCount("uploads")).toBe(1);
  });

  it("needs a session (401) and access (403 invite_required); a revoked account gets its signed revocation", async () => {
    const anonymous = { userId: "", accessToken: "", auth: {} };
    expect((await upload(anonymous, png())).status).toBe(401);
    const waiting = await account({ access: false });
    const refused = await upload(waiting, png());
    expect(refused.status).toBe(403);
    expect(((await refused.json()) as { error: string }).error).toBe("invite_required");
    const who = await account();
    await env.DB.prepare("UPDATE users SET status = 'revoked' WHERE id = ?").bind(who.userId).run();
    const revoked = await upload(who, png());
    expect(revoked.status).toBe(403);
    expect(((await revoked.json()) as { error: string; revocation?: string }).revocation).toEqual(expect.any(String));
    expect(await rowCount("uploads")).toBe(0);
  });

  it("enforces the per-person file backstop (300 by default, owner-adjustable) with 409 upload_limit naming files", async () => {
    const who = await account();
    await h.admin("PUT", "/api/settings", { uploadLimitFiles: 3 });
    for (let i = 0; i < 3; i++) expect((await upload(who, png({ idatBytes: 64 + i }))).status).toBe(201);
    const over = await upload(who, png({ idatBytes: 99 }));
    expect(over.status).toBe(409);
    expect(await over.json()).toMatchObject({ error: "upload_limit", limit: "files" });
    // Re-uploading something already stored is not a new upload.
    expect((await upload(who, png({ idatBytes: 64 }))).status).toBe(200);
    expect((await me(who)).uploads).toMatchObject({ limit: 3, limitFiles: 3, usedFiles: 3 });
    expect(await rowCount("uploads")).toBe(3);
    expect((await env.UPLOADS.list()).objects).toHaveLength(3);
  });

  it("enforces the per-person byte cap (60 MB by default): the upload that would pass it gets 409 naming bytes, one that fits exactly is stored", async () => {
    const who = await account();
    const other = await account();
    const a = png({ idatBytes: 4000 });
    const b = png({ idatBytes: 5000 });
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: a.length + b.length - 1 });
    expect((await upload(who, a)).status).toBe(201);
    const over = await upload(who, b);
    expect(over.status).toBe(409);
    expect(await over.json()).toMatchObject({ error: "upload_limit", limit: "bytes" });
    // Nothing of the refused file is stored or counted, and another person has a quota of their own.
    expect((await env.UPLOADS.list()).objects).toHaveLength(1);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(1);
    expect((await upload(other, b)).status).toBe(201);
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: a.length + b.length });
    expect((await upload(who, b)).status).toBe(201);
    expect((await me(who)).uploads).toMatchObject({ usedBytes: a.length + b.length, usedFiles: 2, limitBytes: a.length + b.length });
  });

  it("gives the bytes back when an upload is deleted or taken down, even while its stored file's delete is still pending", async () => {
    const who = await account();
    const a = png({ idatBytes: 3000 });
    const b = png({ idatBytes: 3100 });
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: a.length + b.length - 1 });
    const first = await uploaded(await upload(who, a));
    expect((await upload(who, b)).status).toBe(409);
    // A failed R2 delete leaves a counted tombstone in the service-wide total, but it no longer counts for the person.
    const failing = failingDeletes(env.UPLOADS);
    const deleted = await handleRequest(new Request(`${API}/v1/uploads/${first.id}`, { method: "DELETE", headers: who.auth }), { ...env, UPLOADS: failing }, h.deps);
    expect(await deleted.json()).toMatchObject({ deleted: true, fileRemoved: false });
    expect(await rowCount("storage_objects", "area = 'uploads' AND released = 1 AND owner IS NULL")).toBe(1);
    expect((await me(who)).uploads).toMatchObject({ usedBytes: 0, usedFiles: 0 });
    const second = await uploaded(await upload(who, b));
    expect((await h.admin("POST", `/api/uploads/${second.id}/takedown`, {})).status).toBe(200);
    expect((await upload(who, a)).status).toBe(201);
  });

  it("never lets parallel uploads of one person pass the byte cap or the file backstop together", async () => {
    const who = await account();
    const files = Array.from({ length: 8 }, (_, i) => png({ idatBytes: 2000 + i }));
    const size = Math.max(...files.map((f) => f.length));
    // Room for exactly three of the eight under the byte cap.
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: 3 * size + 2 });
    const statuses = (await Promise.all(files.map((f) => upload(who, f)))).map((r) => r.status);
    expect(statuses.filter((s) => s === 201).length).toBe(3);
    expect(statuses.every((s) => s === 201 || s === 409)).toBe(true);
    const used = (await me(who)).uploads;
    expect(used.usedBytes).toBeLessThanOrEqual(3 * size + 2);
    expect(used.usedFiles).toBe(statuses.filter((s) => s === 201).length);
    expect((await env.UPLOADS.list()).objects).toHaveLength(used.usedFiles);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(used.usedFiles);

    // And the file backstop, with plenty of bytes.
    const next = await account();
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: 62_914_560, uploadLimitFiles: 2 });
    const second = (await Promise.all(files.map((f) => upload(next, f)))).map((r) => r.status);
    expect(second.filter((s) => s === 201).length).toBe(2);
    expect((await me(next)).uploads.usedFiles).toBe(2);
  });

  it("checks the caps in the ledger before the file is written: a cap taken while the body was read refuses the upload and stores nothing", async () => {
    const who = await account();
    const a = png({ idatBytes: 2500 });
    await h.admin("PUT", "/api/settings", { uploadLimitBytes: a.length + 10 });
    // Another device of the same person reserves the place first (between the quick check and the reservation).
    const raced = new Proxy(env.DB, {
      get(target, property) {
        if (property === "prepare") {
          return (query: string) => {
            if (query.startsWith("INSERT INTO storage_objects (area, key, bytes, created_at, owner)")) {
              return {
                bind: (...values: unknown[]) => ({
                  run: async () => {
                    await env.DB.prepare("INSERT INTO storage_objects (area, key, bytes, created_at, owner) VALUES ('uploads', 'ffffffffffffffffffffffffffffffff.png', 20, ?, ?)").bind(h.now, who.userId).run();
                    return target.prepare(query).bind(...values).run();
                  },
                }),
              };
            }
            return target.prepare(query);
          };
        }
        const value = Reflect.get(target, property, target) as unknown;
        return typeof value === "function" ? (value as (...args: unknown[]) => unknown).bind(target) : value;
      },
    });
    const response = await handleRequest(
      new Request(`${API}/v1/uploads`, { method: "POST", headers: { ...who.auth, "CF-Connecting-IP": h.ip }, body: form([{ name: "file", bytes: a, type: "image/png" }]) }),
      { ...env, DB: raced },
      h.deps,
    );
    expect(response.status).toBe(409);
    expect(await response.json()).toMatchObject({ error: "upload_limit", limit: "bytes" });
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect(await rowCount("uploads")).toBe(0);
  });

  it("refuses new uploads while blocked globally, for the user, or while the emergency switch is on", async () => {
    const who = await account();
    const other = await account();
    expect((await h.admin("POST", `/api/users/${who.userId}/uploads`, { blocked: true })).status).toBe(200);
    const blocked = await upload(who, png());
    expect(blocked.status).toBe(403);
    expect(((await blocked.json()) as { error: string }).error).toBe("uploads_blocked");
    expect((await me(who)).uploads.blocked).toBe("uploads_blocked");
    expect((await upload(other, png())).status).toBe(201);

    await h.admin("POST", `/api/users/${who.userId}/uploads`, { blocked: false });
    await h.admin("PUT", "/api/settings", { uploadsBlocked: true });
    expect(((await (await upload(who, png({ idatBytes: 80 }))).json()) as { error: string }).error).toBe("uploads_blocked");
    expect((await me(other)).uploads.blocked).toBe("uploads_blocked");
    await h.admin("PUT", "/api/settings", { uploadsBlocked: false, emergencyStop: true });
    expect(((await (await upload(who, png({ idatBytes: 80 }))).json()) as { error: string }).error).toBe("online_features_off");
    await h.admin("PUT", "/api/settings", { emergencyStop: false });
    expect((await upload(who, png({ idatBytes: 80 }))).status).toBe(201);
    const actions = (await env.DB.prepare("SELECT action FROM audit_log WHERE action LIKE 'user.uploads_%' ORDER BY seq").all<{ action: string }>()).results.map((r) => r.action);
    expect(actions).toEqual(["user.uploads_block", "user.uploads_allow"]);
  });

  it("does not raise the signed config version for the upload settings", async () => {
    const before = ((await (await h.admin("GET", "/api/settings")).json()) as { configVersion: number }).configVersion;
    await h.admin("PUT", "/api/settings", { uploadsBlocked: true, uploadLimitBytes: 5_000_000, uploadLimitFiles: 5 });
    const after = (await (await h.admin("GET", "/api/settings")).json()) as { configVersion: number; settings: Record<string, unknown> };
    expect(after.configVersion).toBe(before);
    expect(after.settings).toMatchObject({ uploadsBlocked: true, uploadLimitBytes: 5_000_000, uploadLimitFiles: 5 });
    expect(after.settings).not.toHaveProperty("uploadLimit");
    expect((await h.admin("PUT", "/api/settings", { uploadLimitBytes: 120 * 1024 * 1024 + 1 })).status).toBe(400);
    expect((await h.admin("PUT", "/api/settings", { uploadLimitFiles: 1001 })).status).toBe(400);
    expect((await h.admin("PUT", "/api/settings", { uploadLimitBytes: 1.5 })).status).toBe(400);
    // The file limit of V2-S5 is gone (migration 0006).
    expect((await h.admin("PUT", "/api/settings", { uploadLimit: 20 })).status).toBe(400);
  });

  it("counts refused files per account and refuses at the failure cap (429), without touching other accounts", async () => {
    const who = await account();
    for (let i = 0; i < ACCOUNT_LIMITS.upload.failures.cap; i++) expect((await upload(who, concat(png(), new Uint8Array([i])))).status).toBe(422);
    expect((await upload(who, png())).status).toBe(429);
    const other = await account();
    expect((await upload(other, png())).status).toBe(201);
  });

  it("bounds the rows written per accepted upload and per day", async () => {
    const who = await account();
    const writes = async (run: () => Promise<Response>): Promise<{ status: number; rows: number }> => {
      const total = async () => {
        let n = 0;
        for (const t of ["uploads", "storage_objects", "storage_usage", "audit_log", "rate_limits"]) n += await rowCount(t);
        return n;
      };
      const before = await total();
      const response = await run();
      return { status: response.status, rows: (await total()) - before };
    };
    const first = await writes(() => upload(who, png({ idatBytes: 70 })));
    expect(first.status).toBe(201);
    // New rows only (an upsert that changes an existing counter or the usage row counts here as zero): the measured
    // figure stays well inside rowsPerRequest, which also covers index and trigger writes.
    expect(first.rows).toBeLessThanOrEqual(ACCOUNT_LIMITS.upload.rowsPerRequest);
    // Worst case for 50 accounts failing as often as they may, with uploads sized for a whole pack (FR-ONL-14): inside the
    // half of the D1 free tier's daily writes that the sensitive routes leave.
    expect(accountRouteDailyWrites(50)).toBeLessThan(D1_DAILY_WRITE_LIMIT - SENSITIVE_WRITE_BUDGET);
  });

  it("fuzz: corrupted files through the route never fail with 500 and are never stored when refused", async () => {
    const who = await account();
    await h.admin("PUT", "/api/settings", { uploadLimitFiles: 1000 });
    let seed = 0x1234567;
    const random = () => {
      seed ^= seed << 13;
      seed ^= seed >>> 17;
      seed ^= seed << 5;
      return (seed >>> 0) / 0x100000000;
    };
    const bases: [Uint8Array, string][] = [[png(), "image/png"], [jpeg(), "image/jpeg"]];
    let accepted = 0;
    for (let round = 0; round < 60; round++) {
      // The account's failure counter would stop the fuzzing at its cap; it is cleared each round.
      await env.DB.prepare("DELETE FROM rate_limits").run();
      const [base, type] = bases[round % 2]!;
      const file = Uint8Array.from(base);
      for (let i = 0; i < 1 + Math.floor(random() * 3); i++) file[Math.floor(random() * file.length)]! ^= 1 << Math.floor(random() * 8);
      const response = await upload(who, round % 3 === 0 ? file.slice(0, Math.floor(random() * file.length)) : file, type);
      expect([200, 201, 422]).toContain(response.status);
      if (response.status === 201) accepted++;
    }
    expect(await rowCount("uploads")).toBe(accepted);
    expect((await env.UPLOADS.list()).objects).toHaveLength(accepted);
  });
});

describe("the content host's /u/ path (V5.2 serving headers)", () => {
  function expectHardened(response: Response): void {
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(response.headers.get("Content-Security-Policy")).toBe("default-src 'none'; sandbox");
    expect(response.headers.get("Strict-Transport-Security")).toBe("max-age=63072000; includeSubDomains");
    expect(response.headers.get("Referrer-Policy")).toBe("no-referrer");
    expect(response.headers.has("Set-Cookie")).toBe(false);
    for (const name of response.headers.keys()) expect(name.startsWith("access-control-")).toBe(false);
  }

  it("serves an upload with a fixed image type, a short cache lifetime, noindex and no cookie", async () => {
    const who = await account();
    const stored = await uploaded(await upload(who, jpeg(), "image/jpeg"));
    // A misleading stored type is never used: the type comes from the name.
    const key = stored.url.slice(`${CDN}/u/`.length);
    await env.UPLOADS.put(key, jpeg(), { httpMetadata: { contentType: "text/html" } });
    for (const method of ["GET", "HEAD"]) {
      const response = await cdnGet(stored.url, { method });
      expect(response.status).toBe(200);
      expectHardened(response);
      expect(response.headers.get("Content-Type")).toBe("image/jpeg");
      expect(response.headers.get("Cache-Control")).toBe("public, max-age=3600");
      expect(response.headers.get("X-Robots-Tag")).toBe("noindex, nofollow");
    }
  });

  it("answers 404 with the same hardening for unknown, malformed and listing paths, and 405 for other methods", async () => {
    const who = await account();
    const stored = await uploaded(await upload(who, png()));
    const key = stored.url.slice(`${CDN}/u/`.length);
    const paths = ["/u/", "/u", `/u/${"0".repeat(32)}.png`, `/u/${key.toUpperCase()}`, `/u/${key}.png`, `/u/${key.slice(0, -4)}`, `/u/${key}?x=1`, `/u/../packs/${key}`, `/u/${who.userId}.png`, `/packs/${key}`];
    for (const path of paths) {
      const response = await cdnGet(`${CDN}${path}`);
      if (path.endsWith("?x=1")) {
        // The query is ignored; the path is the upload itself.
        expect(response.status, path).toBe(200);
        continue;
      }
      expect(response.status, path).toBe(404);
      expectHardened(response);
      expect(response.headers.get("Content-Type")).toBe("text/plain; charset=utf-8");
    }
    const post = await cdnGet(stored.url, { method: "POST", body: "x" });
    expect(post.status).toBe(405);
    expectHardened(post);
    expect((await cdnGet(stored.url.replace("https:", "http:"))).status).toBe(404);
  });
});

describe("DELETE /v1/uploads/{id}", () => {
  it("deletes the caller's own upload: the address stops working and the bytes leave the storage total", async () => {
    const who = await account();
    const stored = await uploaded(await upload(who, png()));
    expect((await cdnGet(stored.url)).status).toBe(200);
    expect((await h.call("DELETE", `${API}/v1/uploads/${stored.id}`, { headers: who.auth })).status).toBe(200);
    expect((await cdnGet(stored.url)).status).toBe(404);
    expect(await rowCount("uploads")).toBe(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.uploads).toEqual({ bytes: 0, objects: 0 });
    expect(await rowCount("audit_log", "action = 'upload.delete'")).toBe(1);
    expect((await h.call("DELETE", `${API}/v1/uploads/${stored.id}`, { headers: who.auth })).status).toBe(404);
  });

  it("answers 404 for another user's upload and leaves it alone", async () => {
    const alice = await account();
    const bob = await account();
    const stored = await uploaded(await upload(alice, png()));
    expect((await h.call("DELETE", `${API}/v1/uploads/${stored.id}`, { headers: bob.auth })).status).toBe(404);
    expect((await cdnGet(stored.url)).status).toBe(200);
    expect((await h.call("DELETE", `${API}/v1/uploads/not-a-uuid`, { headers: bob.auth })).status).toBe(404);
  });
});

describe("owner: uploads list, takedown, per-user block, storage", () => {
  it("lists every upload with its uploader, takes one down and blocks the user", async () => {
    const who = await account();
    const stored = await uploaded(await upload(who, png()));
    const listed = (await (await h.admin("GET", "/api/uploads")).json()) as {
      uploads: (UploadBody & { user: { id: string; displayName: string; uploadsBlocked: boolean } })[];
      total: number;
      settings: { uploadsBlocked: boolean; uploadLimitBytes: number; uploadLimitFiles: number };
      storage: { usedBytes: number; capBytes: number; warnBytes: number; warning: boolean };
    };
    expect(listed.total).toBe(1);
    expect(listed.uploads[0]).toMatchObject({ id: stored.id, url: stored.url, user: { id: who.userId, displayName: "Test Player", uploadsBlocked: false } });
    expect(JSON.stringify(listed)).not.toContain("590000000000");
    expect(listed.storage).toMatchObject({ usedBytes: stored.size, capBytes: PRODUCTION_STORAGE_LIMITS.capBytes, warnBytes: PRODUCTION_STORAGE_LIMITS.warnBytes, warning: false });
    expect(listed.settings).toEqual({ uploadsBlocked: false, uploadLimitBytes: 62_914_560, uploadLimitFiles: 300 });

    const takedown = await h.admin("POST", `/api/uploads/${stored.id}/takedown`, {});
    expect(takedown.status).toBe(200);
    expect((await cdnGet(stored.url)).status).toBe(404);
    expect((await me(who)).uploads.items).toEqual([]);
    const audit = await env.DB.prepare("SELECT actor, target, before_json FROM audit_log WHERE action = 'upload.takedown'").first<{ actor: string; target: string; before_json: string }>();
    expect(audit).toMatchObject({ actor: "owner:owner@example.com", target: `upload:${stored.id}` });
    expect((await h.admin("POST", `/api/uploads/${stored.id}/takedown`, {})).status).toBe(404);

    expect((await h.admin("POST", `/api/users/${who.userId}/uploads`, { blocked: true })).status).toBe(200);
    const users = (await (await h.admin("GET", "/api/users")).json()) as { users: { id: string; uploadsBlocked: boolean; uploads: number }[] };
    expect(users.users.find((u) => u.id === who.userId)).toMatchObject({ uploadsBlocked: true, uploads: 0 });
    const blocked = (await (await h.admin("GET", "/api/uploads")).json()) as { blockedUsers: { id: string }[] };
    expect(blocked.blockedUsers.map((u) => u.id)).toEqual([who.userId]);
  });

  it("guards the admin upload routes like every admin call (Access, header, Origin)", async () => {
    const who = await account();
    const stored = await uploaded(await upload(who, png()));
    expect((await h.call("GET", `https://dbdadmin.arcavale.com/api/uploads`)).status).toBe(401);
    expect((await h.admin("POST", `/api/uploads/${stored.id}/takedown`, {}, { Origin: "https://evil.example" })).status).toBe(403);
    expect((await h.admin("POST", `/api/uploads/${stored.id}/takedown`, {}, { "X-DBDP-Admin": null })).status).toBe(403);
    expect((await h.admin("POST", `/api/users/${who.userId}/uploads`, { blocked: true }, { "Sec-Fetch-Site": "cross-site" })).status).toBe(403);
    expect((await cdnGet(stored.url)).status).toBe(200);
  });

  it("reports storage use with the 6 GB warning", async () => {
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'packs'").bind(PRODUCTION_STORAGE_LIMITS.warnBytes).run();
    const body = (await (await h.admin("GET", "/api/storage")).json()) as { storage: { usedBytes: number; warning: boolean; full: boolean } };
    expect(body.storage).toMatchObject({ usedBytes: PRODUCTION_STORAGE_LIMITS.warnBytes, warning: true, full: false });
  });
});

describe("storage cap (D18: refuse any write that would pass 8 GB)", () => {
  it("refuses an upload that would pass 6 GB (L3) with 507 upload_storage_full and stores nothing; one that fits exactly is accepted", async () => {
    const who = await account();
    const bytes = png();
    expect(PRODUCTION_STORAGE_LIMITS.uploadCapBytes).toBe(PRODUCTION_STORAGE_LIMITS.warnBytes);
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(PRODUCTION_STORAGE_LIMITS.uploadCapBytes - bytes.length + 1).run();
    const refused = await upload(who, bytes);
    expect(refused.status).toBe(507);
    expect(((await refused.json()) as { error: string }).error).toBe("upload_storage_full");
    expect(await rowCount("uploads")).toBe(0);
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).uploadsFull).toBe(false);
    expect((await me(who)).uploads.blocked).toBeNull();
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(PRODUCTION_STORAGE_LIMITS.uploadCapBytes - bytes.length).run();
    expect((await upload(who, bytes)).status).toBe(201);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).usedBytes).toBe(PRODUCTION_STORAGE_LIMITS.uploadCapBytes);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).uploadsFull).toBe(true);
    // GET /v1/me says saving is paused service-wide, so the app waits until it is allowed again.
    expect((await me(who)).uploads.blocked).toBe("upload_storage_full");
  });

  it("keeps the room between 6 and 8 GB for pack files and audit exports (L3)", async () => {
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'uploads'").bind(PRODUCTION_STORAGE_LIMITS.uploadCapBytes).run();
    const who = await account();
    expect(((await (await upload(who, png())).json()) as { error: string }).error).toBe("upload_storage_full");
    expect((await h.upload(png({ idatBytes: 333 }), "image/png")).status).toBe(200);
    await h.admin("PUT", "/api/settings", { banner: "room" });
    expect((await runAuditExport(env.DB, env.AUDIT_EXPORT, env.ENVIRONMENT, h.now)).objects).toBe(1);
    const listed = await env.AUDIT_EXPORT.list({ prefix: `audit/${env.ENVIRONMENT}/` });
    await env.AUDIT_EXPORT.delete(listed.objects.map((o) => o.key));
  });

  it("refuses an owner pack file over the cap the same way, but re-uploading an existing pack file needs no room", async () => {
    const bytes = png({ idatBytes: 300 });
    expect((await h.upload(bytes, "image/png")).status).toBe(200);
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(PRODUCTION_STORAGE_LIMITS.capBytes).run();
    expect((await h.upload(bytes, "image/png")).status).toBe(200);
    const refused = await h.upload(png({ idatBytes: 301 }), "image/png");
    expect(refused.status).toBe(507);
    expect(await rowCount("pack_objects")).toBe(1);
    expect((await env.PACKS.list()).objects).toHaveLength(1);
  });

  it("keeps the ledger equal to the objects: pack files are counted and released with their rows", async () => {
    const bytes = png({ idatBytes: 400 });
    const uploadedFile = (await (await h.upload(bytes, "image/png")).json()) as { file: { sha256: string } };
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.packs).toEqual({ bytes: bytes.length, objects: 1 });
    // An unused file is removed by the purge after its grace period, and its bytes with it.
    h.now += 2 * 60 * 60 * 1000;
    expect((await h.admin("POST", "/api/pack-files/purge", {})).status).toBe(200);
    expect(await rowCount("pack_objects", "sha256 = ?", uploadedFile.file.sha256)).toBe(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.packs).toEqual({ bytes: 0, objects: 0 });
  });

  it("never lets concurrent reservations pass the cap together", async () => {
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(PRODUCTION_STORAGE_LIMITS.capBytes - 1000).run();
    const outcomes = await Promise.allSettled(Array.from({ length: 10 }, (_, i) => reserveStorage(env.DB, "packs", `k${i}.png`, 300, h.now, env.ENVIRONMENT)));
    expect(outcomes.filter((o) => o.status === "fulfilled")).toHaveLength(3);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).usedBytes).toBe(PRODUCTION_STORAGE_LIMITS.capBytes - 100);
  });

  it("counts audit exports, and over the cap the export stops without advancing its head", async () => {
    await h.admin("GET", "/api/settings");
    await h.admin("PUT", "/api/settings", { banner: "hello" });
    const first = await runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now);
    expect(first.objects).toBe(1);
    const counted = (await storageUsage(env.DB, env.ENVIRONMENT)).areas.audit;
    expect(counted.objects).toBe(1);
    expect(counted.bytes).toBeGreaterThan(0);
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(PRODUCTION_STORAGE_LIMITS.capBytes).run();
    await h.admin("PUT", "/api/settings", { banner: "again" });
    const head = await env.DB.prepare("SELECT last_seq FROM audit_export_state").first<{ last_seq: number }>();
    await expect(runAuditExport(env.DB, env.AUDIT_EXPORT, "test", h.now + 1)).rejects.toThrow("storage_full");
    expect(await env.DB.prepare("SELECT last_seq FROM audit_export_state").first<{ last_seq: number }>()).toEqual(head);
    const listed = await env.AUDIT_EXPORT.list({ prefix: "audit/test/" });
    expect(listed.objects).toHaveLength(1);
    await env.AUDIT_EXPORT.delete(listed.objects.map((o) => o.key));
  });
});

describe("storage caps per environment (D18: staging and production share one 10 GB free tier)", () => {
  /** Runs `body` with the Worker's ENVIRONMENT set to `environment` (the tests otherwise run with production's). */
  async function asEnvironment(environment: string, body: () => Promise<void>): Promise<void> {
    const vars = env as { ENVIRONMENT: string };
    const previous = vars.ENVIRONMENT;
    vars.ENVIRONMENT = environment;
    try {
      await body();
    } finally {
      vars.ENVIRONMENT = previous;
    }
  }

  it("keeps production's caps exactly as the owner chose them: 8 GB cap, warning and upload stop at 6 GB", () => {
    expect(env.ENVIRONMENT).toBe("production");
    expect(storageLimits("production")).toEqual({ capBytes: 8_000_000_000, warnBytes: 6_000_000_000, uploadCapBytes: 6_000_000_000 });
    expect(storageLimits("production")).toBe(PRODUCTION_STORAGE_LIMITS);
  });

  it("gives staging a 750 MB cap with the warning and upload stop at 500 MB", () => {
    expect(storageLimits("staging")).toEqual({ capBytes: 750_000_000, warnBytes: 500_000_000, uploadCapBytes: 500_000_000 });
    expect(storageLimits("staging")).toBe(STAGING_STORAGE_LIMITS);
  });

  it("gives an unknown or missing environment the stricter staging limits (fail safe)", () => {
    for (const name of [undefined, "", "Production", "production ", "prod", "test", "development"]) {
      expect(storageLimits(name), String(name)).toBe(STAGING_STORAGE_LIMITS);
    }
  });

  it("keeps the worst case of both environments plus both backup budgets within the 10 GB free tier", () => {
    const backups = 800_000_000 + 200_000_000;
    const total = PRODUCTION_STORAGE_LIMITS.capBytes + STAGING_STORAGE_LIMITS.capBytes + backups;
    expect(total).toBe(9_750_000_000);
    expect(total).toBeLessThan(10_000_000_000);
  });

  it("reports staging's caps and warning in storage use, and the admin API returns them", async () => {
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'packs'").bind(STAGING_STORAGE_LIMITS.warnBytes).run();
    expect(await storageUsage(env.DB, "staging")).toMatchObject({
      usedBytes: 500_000_000,
      capBytes: 750_000_000,
      warnBytes: 500_000_000,
      uploadCapBytes: 500_000_000,
      warning: true,
      full: false,
      uploadsFull: true,
    });
    expect(await storageUsage(env.DB, "production")).toMatchObject({ capBytes: 8_000_000_000, warnBytes: 6_000_000_000, warning: false, uploadsFull: false });
    await asEnvironment("staging", async () => {
      const body = (await (await h.admin("GET", "/api/storage")).json()) as { storage: { capBytes: number; warnBytes: number; uploadCapBytes: number; warning: boolean } };
      expect(body.storage).toMatchObject({ capBytes: 750_000_000, warnBytes: 500_000_000, uploadCapBytes: 500_000_000, warning: true });
    });
  });

  it("refuses a staging write that would pass 750 MB, where production would still accept it", async () => {
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(STAGING_STORAGE_LIMITS.capBytes - 100).run();
    await expect(reserveStorage(env.DB, "packs", "staging-over.png", 101, h.now, "staging")).rejects.toMatchObject({ status: 507, code: "storage_full" });
    await expect(reserveStorage(env.DB, "packs", "unknown-over.png", 101, h.now, "test")).rejects.toMatchObject({ status: 507, code: "storage_full" });
    expect(await reserveStorage(env.DB, "packs", "staging-fits.png", 100, h.now, "staging")).toBe("reserved");
    expect(await reserveStorage(env.DB, "packs", "production-fits.png", 1000, h.now, "production")).toBe("reserved");
  });

  it("stops staging uploads at 500 MB with 507 upload_storage_full, through the route and the reservation", async () => {
    const who = await account();
    const bytes = png();
    await env.DB.prepare("UPDATE storage_usage SET bytes = ? WHERE area = 'releases'").bind(STAGING_STORAGE_LIMITS.uploadCapBytes - bytes.length + 1).run();
    const quota = { bytes: 1024 * 1024, files: 10 };
    await expect(reserveUpload(env.DB, "uploads/staging-key", bytes.length, h.now, who.userId, quota, "staging")).rejects.toMatchObject({ status: 507, code: "upload_storage_full" });
    await asEnvironment("staging", async () => {
      const refused = await upload(who, bytes);
      expect(refused.status).toBe(507);
      expect(((await refused.json()) as { error: string }).error).toBe("upload_storage_full");
    });
    expect(await rowCount("uploads")).toBe(0);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(0);
    // The same total is far below production's 6 GB upload stop.
    expect((await upload(who, bytes)).status).toBe(201);
  });
});

describe("account deletion removes uploads (V5.3)", () => {
  async function withUploads(): Promise<{ who: Account; urls: string[] }> {
    const who = await account();
    const urls = [(await uploaded(await upload(who, png()))).url, (await uploaded(await upload(who, jpeg(), "image/jpeg"))).url];
    return { who, urls };
  }

  async function expectGone(who: Account, urls: string[]): Promise<void> {
    for (const url of urls) expect((await cdnGet(url)).status).toBe(404);
    expect(await rowCount("uploads", "user_id = ?", who.userId)).toBe(0);
    expect(await rowCount("users", "id = ?", who.userId)).toBe(0);
    expect(await rowCount("token_families", "user_id = ?", who.userId)).toBe(0);
    expect(await rowCount("devices", "user_id = ?", who.userId)).toBe(0);
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.uploads).toEqual({ bytes: 0, objects: 0 });
    // Pseudonymised: the audit log names only the random user id, never the Discord id or the display name.
    const audit = JSON.stringify((await env.DB.prepare("SELECT * FROM audit_log").all()).results);
    expect(audit).toContain(`user:${who.userId}`);
    expect(audit).not.toContain("590000000000");
    expect(audit).not.toContain("Test Player");
  }

  it("Delete my account (DELETE /v1/me) removes the account, sessions, uploads and their files", async () => {
    const { who, urls } = await withUploads();
    expect((await h.call("DELETE", `${API}/v1/me`, { headers: who.auth })).status).toBe(200);
    await expectGone(who, urls);
    const audit = await env.DB.prepare("SELECT before_json FROM audit_log WHERE action = 'account.delete'").first<{ before_json: string }>();
    expect(JSON.parse(audit!.before_json)).toMatchObject({ uploads: 2 });
    // The session no longer works.
    expect((await h.call("GET", `${API}/v1/me`, { headers: who.auth })).status).toBe(401);
  });

  it("the owner's delete does the same", async () => {
    const { who, urls } = await withUploads();
    expect((await h.admin("DELETE", `/api/users/${who.userId}`, { confirm: who.userId })).status).toBe(200);
    await expectGone(who, urls);
  });

  it("the 12-month purge of inactive accounts does the same", async () => {
    const { who, urls } = await withUploads();
    const report = await runRetention(env.DB, env.UPLOADS, loadBanKey(env.BAN_HMAC_KEY), h.now + INACTIVE_ACCOUNT_MS + 1);
    expect(report.accounts).toBe(1);
    await expectGone(who, urls);
  });

  it("includes the uploads in the owner's export for a subject-access request", async () => {
    const { who, urls } = await withUploads();
    const exported = (await (await h.admin("GET", `/api/users/${who.userId}/export`)).json()) as { uploads: { url: string }[]; user: { uploads: number } };
    expect(exported.uploads.map((u) => u.url)).toEqual(urls);
    expect(exported.user.uploads).toBe(2);
  });
});

describe("daily upload sweep", () => {
  it("deletes objects no row names and gives back reservations an interrupted upload left, after the grace period", async () => {
    const who = await account();
    const kept = await uploaded(await upload(who, png()));
    await env.UPLOADS.put(`${"a".repeat(32)}.png`, png());
    await reserveStorage(env.DB, "uploads", `${"b".repeat(32)}.png`, 500, h.now, env.ENVIRONMENT);
    await env.UPLOADS.put(`${"b".repeat(32)}.png`, png());
    // R2 records the real time of each write; the reservation carries the harness clock. Neither is old enough yet.
    const realNow = Date.now();
    expect(await sweepUploads(env.DB, env.UPLOADS, Math.min(realNow, h.now))).toEqual({ reservations: 0, orphanedObjects: 0, released: 0, stillPending: 0 });
    const report = await sweepUploads(env.DB, env.UPLOADS, Math.max(realNow, h.now) + SWEEP_GRACE_MS + 1);
    expect(report).toEqual({ reservations: 1, orphanedObjects: 1, released: 0, stillPending: 0 });
    expect((await env.UPLOADS.list()).objects.map((o) => o.key)).toEqual([kept.url.slice(`${CDN}/u/`.length)]);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.uploads).toEqual({ bytes: kept.size, objects: 1 });
  });
});

describe("CPU time of an upload's own work (V5.4, local workerd)", () => {
  // The synchronous and native work an accepted upload does on a worst-case 2 MB file, measured here in the Workers
  // runtime the tests use (local workerd, not Cloudflare's machines): multipart reading, the strict parse and the
  // SHA-256. Best median of up to three rounds of 21 runs (test/cpu.ts); recorded in docs/evidence/upload-cpu.md.
  const head = concat(SOI, DQT, sof(SOF0, 1024, 1024), DHT, sos());
  const worst = {
    "PNG, one 2 MB IDAT": [png({ width: 1024, height: 1024, idatBytes: MAX_IMAGE_BYTES - 57 }), "image/png"],
    "PNG, 2 MB of 1-byte IDAT chunks": [
      concatList([PNG_SIG, ihdr(1024, 1024), ...Array.from({ length: Math.floor((MAX_IMAGE_BYTES - 57) / 13) }, () => chunk("IDAT", new Uint8Array([1]))), chunk("IEND")]),
      "image/png",
    ],
    "JPEG, 2 MB of entropy data": [concat(head, entropy(Math.floor((MAX_IMAGE_BYTES - head.length - 2) * 0.99), 17), EOI), "image/jpeg"],
    // The multipart part adds about 200 bytes, so the files are a little under the cap.
    "JPEG, FF 00 pairs": [denseJpeg([0xff, 0x00], MAX_IMAGE_BYTES - 1024), "image/jpeg"],
    "JPEG, FF D0 pairs": [denseJpeg([0xff, 0xd0], MAX_IMAGE_BYTES - 1024), "image/jpeg"],
  } as const;

  it.each(Object.entries(worst))("%s: multipart + strict parse + SHA-256 median under 10 ms", async (name, [file, type]) => {
    const { inspectImage } = await import("../src/images/strict.ts");
    const { readSingleFilePart } = await import("../src/lib/multipart.ts");
    const request = new Request("https://x/", { method: "POST", body: form([{ name: "file", bytes: file, type }]) });
    const contentType = request.headers.get("Content-Type")!;
    const body = new Uint8Array(await request.arrayBuffer());
    const timing = await bestMedian(async () => {
      const part = readSingleFilePart(body, contentType);
      if (!part.ok) throw new Error(`${name}: multipart refused`);
      if (!inspectImage(part.bytes).ok) throw new Error(`${name} refused`);
      await sha256Hex(part.bytes);
    });
    console.log(JSON.stringify({ measurement: "upload-request-work", file: name, ...timing }));
    expect(timing.medianMs).toBeLessThan(CPU_BUDGET_MS);
  });
});

/** An R2 bucket whose deletes fail, as when R2 is briefly unavailable. Everything else goes to the real bucket. */
function failingDeletes(bucket: R2Bucket): R2Bucket & { attempts: number } {
  const wrapper = {
    attempts: 0,
    delete: async () => {
      wrapper.attempts++;
      throw new Error("R2 unavailable");
    },
    get: bucket.get.bind(bucket),
    head: bucket.head.bind(bucket),
    put: bucket.put.bind(bucket),
    list: bucket.list.bind(bucket),
  };
  return wrapper as unknown as R2Bucket & { attempts: number };
}

describe("a failed R2 delete (security review M2, L2)", () => {
  it("takedown: the address answers 404 at once, the reply says the file is not removed yet, the bytes stay counted, and the sweep finishes it", async () => {
    const who = await account();
    const stored = await uploaded(await upload(who, png()));
    const broken = failingDeletes(env.UPLOADS);
    const brokenEnv = { ...env, UPLOADS: broken };
    const headers = await h.adminHeaders();
    const response = await handleRequest(new Request(`https://dbdadmin.arcavale.com/api/uploads/${stored.id}/takedown`, { method: "POST", headers: { ...headers, "Content-Type": "application/json" }, body: "{}" }), brokenEnv, h.deps);
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({ takenDown: true, fileRemoved: false, storage: { pendingDeleteBytes: stored.size } });
    expect(broken.attempts).toBe(2);
    // The object is still in R2, but /u/ serves only files that have a row.
    expect((await env.UPLOADS.list()).objects).toHaveLength(1);
    expect((await cdnGet(stored.url)).status).toBe(404);
    // Too high, never too low: the bytes are counted until the object is really gone.
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.uploads).toEqual({ bytes: stored.size, objects: 1 });
    expect(await rowCount("storage_objects", "area = 'uploads' AND released = 1")).toBe(1);

    const report = await sweepUploads(env.DB, env.UPLOADS, h.now);
    expect(report).toMatchObject({ released: 1, stillPending: 0 });
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.uploads).toEqual({ bytes: 0, objects: 0 });
  });

  it("the user's delete and Delete my account say when a file is still pending", async () => {
    const who = await account();
    const a = await uploaded(await upload(who, png()));
    h.now += 1;
    await uploaded(await upload(who, jpeg(), "image/jpeg"));
    const brokenEnv = { ...env, UPLOADS: failingDeletes(env.UPLOADS) };
    const one = await handleRequest(new Request(`${API}/v1/uploads/${a.id}`, { method: "DELETE", headers: { ...who.auth, "CF-Connecting-IP": h.ip } }), brokenEnv, h.deps);
    expect(await one.json()).toMatchObject({ deleted: true, fileRemoved: false });
    expect((await cdnGet(a.url)).status).toBe(404);
    const all = await handleRequest(new Request(`${API}/v1/me`, { method: "DELETE", headers: { ...who.auth, "CF-Connecting-IP": h.ip } }), brokenEnv, h.deps);
    expect(await all.json()).toMatchObject({ deleted: true, filesRemoved: false });
    expect(await rowCount("storage_objects", "area = 'uploads' AND released = 1")).toBe(2);
    await sweepUploads(env.DB, env.UPLOADS, h.now);
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(0);
  });

  it("pack files: a failed delete stays a counted tombstone until the owner's purge deletes it", async () => {
    const bytes = png({ idatBytes: 444 });
    const file = ((await (await h.upload(bytes, "image/png")).json()) as { file: { sha256: string } }).file;
    expect(await releaseObjects(env.DB, failingDeletes(env.PACKS), [file.sha256])).toBe(1);
    expect(await rowCount("pack_objects")).toBe(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.packs).toEqual({ bytes: bytes.length, objects: 1 });
    expect((await env.PACKS.list()).objects).toHaveLength(1);
    expect((await h.admin("POST", "/api/pack-files/purge", {})).status).toBe(200);
    expect((await env.PACKS.list()).objects).toHaveLength(0);
    expect((await storageUsage(env.DB, env.ENVIRONMENT)).areas.packs).toEqual({ bytes: 0, objects: 0 });
  });

  it("account deletion also removes an upload that committed between its read and the delete", async () => {
    const who = await account();
    await uploaded(await upload(who, png()));
    const late = `${"c".repeat(32)}.png`;
    const racing = interceptBatches(env.DB, {
      match: /DELETE FROM users/,
      before: async () => {
        await reserveStorage(env.DB, "uploads", late, 77, h.now, env.ENVIRONMENT);
        await env.UPLOADS.put(late, png());
        await env.DB.prepare("INSERT INTO uploads (id, user_id, object_key, sha256, format, size, width, height, created_at) VALUES (?, ?, ?, ?, 'png', 77, 1, 1, ?)")
          .bind("99999999-9999-4999-8999-999999999999", who.userId, late, "d".repeat(64), h.now)
          .run();
      },
    });
    const result = await deleteUser(racing, env.UPLOADS, loadBanKey(env.BAN_HMAC_KEY), h.now, who.userId, `user:${who.userId}`, "account.delete");
    expect(result).toEqual({ outcome: "deleted", filesRemoved: true });
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(0);
  });
});

describe("the per-account attempt counter (security review L1)", () => {
  it("counts every upload request before the body is read: same-bytes answers, refused types and the limit included", async () => {
    const who = await account();
    const bytes = png();
    expect((await upload(who, bytes)).status).toBe(201);
    const statuses: number[] = [];
    for (let i = 1; i < ACCOUNT_LIMITS["upload-attempt"].account.cap; i++) statuses.push((await upload(who, bytes, i % 2 === 0 ? "image/png" : "image/webp")).status);
    expect(new Set(statuses)).toEqual(new Set([200, 415]));
    const limited = await upload(who, bytes);
    expect(limited.status).toBe(429);
    expect(Number(limited.headers.get("Retry-After"))).toBeGreaterThan(0);
    // Other accounts are not affected.
    const other = await account();
    expect((await upload(other, bytes)).status).toBe(201);
  });

  it("charges only principals allowed to upload: revoked, uninvited and blocked sessions use up no counter", async () => {
    const attemptRows = () => rowCount("rate_limits", "bucket LIKE 'upload-attempt:%'");
    const uninvited = await account({ access: false });
    expect((await upload(uninvited, png())).status).toBe(403);
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const revoked = await account();
    await env.DB.prepare("UPDATE users SET status = 'revoked' WHERE id = ?").bind(revoked.userId).run();
    expect((await upload(revoked, png())).status).toBe(403);
    const blocked = await account();
    await h.admin("POST", `/api/users/${blocked.userId}/uploads`, { blocked: true });
    expect((await upload(blocked, png())).status).toBe(403);
    await h.admin("PUT", "/api/settings", { uploadsBlocked: true });
    const everyone = await account();
    expect((await upload(everyone, png())).status).toBe(403);
    expect(await attemptRows()).toBe(0);

    await h.admin("PUT", "/api/settings", { uploadsBlocked: false });
    expect((await upload(everyone, png())).status).toBe(201);
    // The allowed principal's attempt is counted on its account counter and the shared one.
    expect(await attemptRows()).toBe(2);
  });
});

describe("the reservation between the file and its row (round-2 QA review F5)", () => {
  it("answers 503 upload_interrupted, stores no row and frees the quota when the reservation is swept after the file was written", async () => {
    const who = await account();
    // The hourly sweep (or the owner's purge) removes the ledger row while the request is between R2 put and the insert.
    const sweeping = {
      delete: env.UPLOADS.delete.bind(env.UPLOADS),
      get: env.UPLOADS.get.bind(env.UPLOADS),
      head: env.UPLOADS.head.bind(env.UPLOADS),
      list: env.UPLOADS.list.bind(env.UPLOADS),
      put: async (...args: Parameters<R2Bucket["put"]>) => {
        const stored = await env.UPLOADS.put(...args);
        await env.DB.prepare("DELETE FROM storage_objects WHERE area = 'uploads'").run();
        return stored;
      },
    } as unknown as R2Bucket;
    const request = new Request(`${API}/v1/uploads`, { method: "POST", headers: { ...who.auth, "CF-Connecting-IP": h.ip }, body: form([{ name: "file", bytes: png(), type: "image/png" }]) });
    const response = await handleRequest(request, { ...env, UPLOADS: sweeping }, h.deps);
    expect(response.status).toBe(503);
    expect(await response.json()).toMatchObject({ error: "upload_interrupted" });
    expect(await rowCount("uploads")).toBe(0);
    expect((await env.UPLOADS.list()).objects).toHaveLength(0);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(0);
    expect(await rowCount("audit_log", "action = 'upload.create'")).toBe(0);
    // The person can upload again: nothing of the lost attempt is held against them.
    expect((await me(who)).uploads).toMatchObject({ usedFiles: 0, usedBytes: 0 });
    expect((await upload(who, png())).status).toBe(201);
  });

  it("reserveUpload refuses a user who is not active, has no access or is blocked, and writes no ledger row", async () => {
    const who = await account();
    const quota = { bytes: 1024 * 1024, files: 10 };
    await reserveUpload(env.DB, "uploads/ok-key", 10, h.now, who.userId, quota, env.ENVIRONMENT);
    expect(await rowCount("storage_objects", "area = 'uploads'")).toBe(1);
    for (const change of ["status = 'revoked'", "access_granted_at = NULL", "uploads_blocked = 1"]) {
      await resetDatabase();
      h = new Harness();
      const user = await account();
      await env.DB.prepare(`UPDATE users SET ${change} WHERE id = ?`).bind(user.userId).run();
      await expect(reserveUpload(env.DB, "uploads/refused-key", 10, h.now, user.userId, quota, env.ENVIRONMENT), change).rejects.toMatchObject({ status: 403, code: "uploads_blocked" });
      expect(await rowCount("storage_objects", "area = 'uploads'"), change).toBe(0);
    }
  });
});
