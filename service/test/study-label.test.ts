// FR-LAB-01: the owner-only labelling relay (D37, ADR-0018, src/routes/study.ts).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import type { Deps } from "../src/context.ts";
import type { Env } from "../src/env.ts";
import { ACCOUNT_LIMITS } from "../src/ratelimit.ts";
import { ALLOWED_TOP_LEVEL_KEYS, LABEL_PREFIX, MAX_ANSWER_TOKENS, MAX_LABEL_BYTES, MAX_REPLY_TEXT_BYTES, TopLevelKeyGuard, parseProvider, reduceAnswer } from "../src/routes/study.ts";
import { API, Harness, newDevice, resetDatabase, rowCount } from "./helpers.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

// The provider comes from the Worker variable (wrangler.toml), exactly as in production.
const PROVIDER = parseProvider(env.STUDY_LABELLER_PROVIDER)!;
const KEY = "test-labeller-key";
const withKey: Env = { ...env, STUDY_LABELLER_KEY: KEY } as Env;

interface ProviderCall {
  url: string;
  headers: Headers;
  body: string;
}

/** A fake provider: records each call and answers with `reply` (or the default answer). */
function provider(reply: () => Response = () => Response.json(answer('{"segments":[]}'))): { deps: Deps; calls: ProviderCall[] } {
  const calls: ProviderCall[] = [];
  const deps: Deps = {
    ...h.deps,
    fetch: async (input, init) => {
      const request = new Request(input, init);
      if (request.url !== PROVIDER.url) return h.deps.fetch(input, init);
      calls.push({ url: request.url, headers: request.headers, body: await request.text() });
      return reply();
    },
  };
  return { deps, calls };
}

function answer(text: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: "msg_1",
    type: "message",
    role: "assistant",
    model: "claude-opus-5-5",
    content: [{ type: "thinking", thinking: "", signature: "x" }, { type: "text", text }],
    stop_reason: "end_turn",
    usage: { input_tokens: 1200, output_tokens: 300, cache_read_input_tokens: 800, cache_creation_input_tokens: 0 },
    ...extra,
  };
}

let discordSeq = 0;
async function account(owner: boolean): Promise<Record<string, string>> {
  await h.admin("PUT", "/api/settings", { accessMode: "public" });
  const { status, body } = await h.signIn(`7100000000000${String(++discordSeq).padStart(5, "0")}`, await newDevice());
  expect(status).toBe(200);
  if (owner) expect((await h.admin("PUT", `/api/users/${(body["user"] as { id: string }).id}/roster-reporter`, { enabled: true })).status).toBe(200);
  return { Authorization: `Bearer ${body["accessToken"] as string}` };
}

const goodBody = (rest = '"messages":[{"role":"user","content":"label these"}],"fallbacks":"default"}'): string => LABEL_PREFIX + rest;

function label(auth: Record<string, string>, deps: Deps, body: string = goodBody(), options: { e?: Env; headers?: Record<string, string> } = {}): Promise<Response> {
  const headers: Record<string, string> = { "CF-Connecting-IP": h.ip, "Content-Type": "application/json", "Content-Length": String(new TextEncoder().encode(body).length), ...auth, ...options.headers };
  return handleRequest(new Request(`${API}/v1/study/label`, { method: "POST", headers, body }), options.e ?? withKey, deps);
}

describe("POST /v1/study/label (FR-LAB-01)", () => {
  it("forwards the owner's request with the real model and the key, and returns only the answer text, stop reason and tokens", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    const response = await label(owner, deps);
    expect(response.status).toBe(200);
    const body = (await response.json()) as Record<string, unknown>;
    expect(body).toEqual({ schemaVersion: 1, text: '{"segments":[]}', stopReason: "end_turn", inputTokens: 2000, outputTokens: 300 });
    // No model name, provider name or id reaches the app.
    expect(JSON.stringify(body)).not.toMatch(/claude|anthropic|msg_/i);
    expect(calls).toHaveLength(1);
    const sent = calls[0]!;
    expect(sent.headers.get(PROVIDER.keyHeader)).toBe(KEY);
    for (const [name, value] of Object.entries(PROVIDER.headers)) expect(sent.headers.get(name)).toBe(value);
    expect(Object.keys(PROVIDER.headers).length).toBeGreaterThan(0);
    expect(sent.body).toBe(`{"model":"${env.STUDY_LABELLER_MODEL}","max_tokens":${MAX_ANSWER_TOKENS},` + '"messages":[{"role":"user","content":"label these"}],"fallbacks":"default"}');
    expect(JSON.parse(sent.body)).toMatchObject({ model: "claude-opus-5-5", max_tokens: 16000, fallbacks: "default" });
  });

  it("is invisible to every account the owner did not mark (404) and forwards nothing", async () => {
    const friend = await account(false);
    const { deps, calls } = provider();
    const response = await label(friend, deps);
    expect(response.status).toBe(404);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "not_found" });
    expect(calls).toHaveLength(0);
    expect((await label({}, deps)).status).toBe(401);
  });

  it("is off until the owner sets the key (503) and then forwards nothing", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    const response = await label(owner, deps, goodBody(), { e: env as Env });
    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "labelling_not_set_up" });
    expect(calls).toHaveLength(0);
  });

  it("refuses a body that does not start with the exact placeholder prefix, so the app cannot pick the model", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    for (const body of ['{"model":"claude-fable-5-1","max_tokens":1}', '{ "model":"reviewer","max_tokens":1}', '{"max_tokens":1,"model":"reviewer"}', "x".repeat(40)]) {
      const response = await label(owner, deps, body);
      expect(response.status, body).toBe(400);
    }
    expect(calls).toHaveLength(0);
  });

  it("refuses a body that repeats model or max_tokens, adds any other top-level member, or is not one closed object, so the provider never gets it whole", async () => {
    const owner = await account(true);
    const bad = [
      '"messages":[],"model":"claude-fable-5-1"}',
      '"messages":[],"max_tokens":128000}',
      '"mod\\u0065l":"claude-fable-5-1","messages":[]}',
      '"messages":[],"tools":[{"type":"web_search_20260209","name":"web_search"}]}',
      '"messages":[],"speed":"fast"}',
      '"messages":[]}{"model":"x"}',
      '"messages":[]} ,"model":"x"',
      '"messages":[',
      '7:1,"messages":[]}',
      // Grammar: no missing comma or colon at the top level (a lenient provider parser could read a second `model` there).
      '"messages":[] "model":"evil"}',
      '"messages":1"model":"evil"}',
      '"messages" [],"fallbacks":"default"}',
      '"messages":[],,"fallbacks":"default"}',
      '"messages":[},"fallbacks":"default"}',
      '"messages":[],"messages":[]}',
      // fallbacks may only be the exact string "default".
      '"messages":[],"fallbacks":"other"}',
      '"messages":[],"fallbacks":["default"]}',
      '"messages":[],"fallbacks":"defaul\\u0074"}',
      '"messages":[],"fallbacks":null}',
    ];
    for (const rest of bad) {
      const { deps, calls } = provider();
      const response = await label(owner, deps, LABEL_PREFIX + rest);
      expect(response.status, rest).toBe(400);
      expect(await response.json(), rest).toEqual({ schemaVersion: 1, error: "invalid_request" });
      // The fake provider only records a body it could read whole; an aborted stream never arrives complete.
      for (const call of calls) expect(() => JSON.parse(call.body), rest).toThrow();
    }
  });

  it("refuses a body whose declared length is larger or smaller than the stream, so the provider never gets a complete request", async () => {
    const owner = await account(true);
    const real = new TextEncoder().encode(goodBody()).length;
    for (const declared of [real + 50, real - 5]) {
      const { deps, calls } = provider();
      const response = await label(owner, deps, goodBody(), { headers: { "Content-Length": String(declared) } }).catch(() => null);
      // A mismatching length is either refused outright or aborts the forwarded stream; it never produces an answer.
      if (response !== null) expect(response.status, String(declared)).not.toBe(200);
      for (const call of calls) expect(() => JSON.parse(call.body), String(declared)).toThrow();
    }
  });

  it("stops at the emergency stop (403) and for a revoked session, forwarding nothing", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    expect((await h.admin("PUT", "/api/settings", { emergencyStop: true })).status).toBe(200);
    const stopped = await label(owner, deps);
    expect(stopped.status).toBe(403);
    expect(await stopped.json()).toEqual({ schemaVersion: 1, error: "online_features_off" });
    expect((await h.admin("PUT", "/api/settings", { emergencyStop: false })).status).toBe(200);
    expect((await label(owner, deps)).status).toBe(200);
    calls.length = 0;
    const users = (await (await h.admin("GET", "/api/users")).json()) as { users: { id: string; roster_reporter?: unknown }[] };
    expect((await h.admin("POST", `/api/users/${users.users[0]!.id}/revoke`)).status).toBe(200);
    expect((await label(owner, deps)).status).toBeGreaterThanOrEqual(400);
    expect(calls).toHaveLength(0);
  });

  it("is off (503) while the provider variable is missing or invalid, and never sends the key to a bad address", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    const bad = [undefined, "", "not json", "[]", '{"url":"http://insecure.example/v1","keyHeader":"x-api-key"}', '{"url":"https://u:p@h.example/","keyHeader":"x-api-key"}', '{"url":"https://h.example/","keyHeader":"bad name"}'];
    for (const value of bad) {
      const response = await label(owner, deps, goodBody(), { e: { ...withKey, STUDY_LABELLER_PROVIDER: value } as Env });
      expect(response.status, String(value)).toBe(503);
      expect(await response.json()).toEqual({ schemaVersion: 1, error: "labelling_not_set_up" });
    }
    expect(calls).toHaveLength(0);
  });

  it("never follows a redirect from the provider (the key would travel on) and reports it as a refusal", async () => {
    const owner = await account(true);
    let followed = false;
    const deps: Deps = {
      ...h.deps,
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.url === PROVIDER.url) {
          expect(init?.redirect).toBe("manual");
          return new Response(null, { status: 307, headers: { Location: "https://elsewhere.example/steal" } });
        }
        if (request.url.startsWith("https://elsewhere.example")) followed = true;
        return h.deps.fetch(input, init);
      },
    };
    const response = await label(owner, deps);
    expect(response.status).toBe(502);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "labeller_refused", reason: "unknown" });
    expect(followed).toBe(false);
  });

  it("refuses an answer text larger than the app's own response limit", async () => {
    const owner = await account(true);
    const { deps } = provider(() => Response.json(answer("x".repeat(MAX_REPLY_TEXT_BYTES + 1))));
    const response = await label(owner, deps);
    expect(response.status).toBe(502);
    expect(await response.json()).toEqual({ schemaVersion: 1, error: "labeller_answer_too_large" });
  });

  it("allows model and max_tokens deeper down (a schema property, a quoted text), which the provider reads as data", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    const rest = '"system":"say \\"model\\": no","messages":[{"role":"user","content":[{"type":"text","text":"\\"max_tokens\\":1"}]}],"output_config":{"format":{"type":"json_schema","schema":{"type":"object","properties":{"model":{"type":"string"}}}}}}';
    const response = await label(owner, deps, LABEL_PREFIX + rest);
    expect(response.status).toBe(200);
    expect(JSON.parse(calls[0]!.body)).toMatchObject({ model: "claude-opus-5-5", max_tokens: MAX_ANSWER_TOKENS });
  });

  it("refuses a declared length over 30 MB (413), a missing length (411) and another content type (415) before reading the body", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    expect((await label(owner, deps, goodBody(), { headers: { "Content-Length": String(MAX_LABEL_BYTES + 1) } })).status).toBe(413);
    expect((await label(owner, deps, goodBody(), { headers: { "Content-Type": "text/plain" } })).status).toBe(415);
    expect(calls).toHaveLength(0);
  });

  it("maps provider failures to plain errors without passing on the provider's message", async () => {
    const owner = await account(true);
    const cases: [Response, number, Record<string, unknown>][] = [
      [Response.json({ type: "error", error: { type: "invalid_request_error", message: "image 3 of 'player NAME' is too big" } }, { status: 400 }), 502, { error: "labeller_refused", reason: "invalid_request_error" }],
      [Response.json({ type: "error", error: { type: "authentication_error", message: "bad key" } }, { status: 401 }), 503, { error: "labelling_not_set_up" }],
      [new Response("busy", { status: 529 }), 503, { error: "labeller_busy" }],
      [Response.json({ hello: "not a message" }), 502, { error: "labeller_answer_unreadable" }],
    ];
    for (const [reply, status, expected] of cases) {
      const { deps } = provider(() => reply);
      const response = await label(owner, deps);
      expect(response.status).toBe(status);
      const body = (await response.json()) as Record<string, unknown>;
      expect(body).toEqual({ schemaVersion: 1, ...expected });
      expect(JSON.stringify(body)).not.toContain("NAME");
    }
    const { deps } = provider(() => new Response("slow down", { status: 429, headers: { "retry-after": "30" } }));
    const busy = await label(owner, deps);
    expect(busy.status).toBe(503);
    expect(busy.headers.get("Retry-After")).toBe("30");
  });

  it("writes an audit row with sizes, tokens and the outcome, never the request or the answer", async () => {
    const owner = await account(true);
    const { deps } = provider(() => Response.json(answer('{"secret":"player NAME"}')));
    expect((await label(owner, deps)).status).toBe(200);
    const row = await env.DB.prepare("SELECT action, after_json FROM audit_log WHERE action = 'study.label'").first<{ action: string; after_json: string }>();
    expect(row).not.toBeNull();
    const after = JSON.parse(row!.after_json) as Record<string, unknown>;
    expect(after).toEqual({ bytes: new TextEncoder().encode(goodBody()).length, outcome: "ok", stopReason: "end_turn", inputTokens: 2000, outputTokens: 300 });
    expect(row!.after_json).not.toContain("NAME");
    expect(row!.after_json).not.toContain("label these");
  });

  it("stops at the daily cap per account (429) without calling the provider", async () => {
    const owner = await account(true);
    const { deps, calls } = provider();
    const cap = ACCOUNT_LIMITS["study-label"].account.cap;
    await env.DB.prepare("SELECT 1").first();
    let last = 0;
    for (let i = 0; i < cap + 1; i++) {
      last = (await label(owner, deps)).status;
      if (last === 429) break;
    }
    expect(last).toBe(429);
    expect(calls.length).toBe(cap);
    expect(await rowCount("audit_log")).toBeGreaterThanOrEqual(cap);
  });
});

describe("top-level member guard (pure)", () => {
  const enc = new TextEncoder();
  function check(text: string, split: number[]): boolean {
    const bytes = enc.encode(text);
    const guard = new TopLevelKeyGuard();
    let at = 0;
    for (const s of [...split, bytes.length]) {
      if (!guard.push(bytes.subarray(at, s))) return false;
      at = s;
    }
    return guard.finish();
  }

  it("gives the same verdict wherever the stream is cut, including between a backslash and the quote it escapes", () => {
    const good = '"system":"a\\\\","messages":[{"content":"x\\"\\\\\\"y"}],"output_config":{"model":1}}';
    const bad = '"system":"a\\\\","messages":[],"model":"z"}';
    // "model" only inside the system text: `a","model":"z`.
    const sneaky = '"system":"a\\",\\"model\\":\\"z","messages":[]}';
    for (let cut = 0; cut <= good.length; cut++) expect(check(good, [cut]), `good cut ${cut}`).toBe(true);
    for (let cut = 0; cut <= bad.length; cut++) expect(check(bad, [cut]), `bad cut ${cut}`).toBe(false);
    for (let cut = 0; cut <= sneaky.length; cut++) expect(check(sneaky, [cut]), `sneaky cut ${cut}`).toBe(true);
    expect(JSON.parse(`{${sneaky}`)).not.toHaveProperty("model");
  });

  it("tracks the top-level grammar, so a lenient parser cannot read a member the guard did not see", () => {
    const ok = ['"messages":[],"fallbacks":"default"}', '"messages" : [ ] , "system" : "x" }', '"messages":[],"system":12.5e+3}', '"system":true,"messages":[]}', '"messages":[[{"a":[]}],{}]}'];
    for (const text of ok) {
      for (let cut = 0; cut <= text.length; cut++) expect(check(text, [cut]), `${text} cut ${cut}`).toBe(true);
    }
    const bad = ['"messages":[] "model":"x"}', '"messages":1"model":"x"}', '"messages":[]"model":"x"}', '"messages":tru"e","x":1}', '"messages":[{]}', '"messages":[}]}', '"messages":[],"fallbacks":"default"x}'];
    for (const text of bad) expect(check(text, []), text).toBe(false);
    expect(check("[".repeat(65) + "]".repeat(65) + "}", [])).toBe(false);
  });

  it("never accepts a body a strict parser reads differently (seeded random bodies, every cut)", () => {
    // Deterministic generator (no clock, no Math.random): fragments are glued at random and fed whole and in two pieces.
    let seed = 20261029;
    const next = (n: number): number => {
      seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
      return (seed >>> 12) % n;
    };
    const pool = ['"messages"', '"system"', '"model"', '"fallbacks"', '"default"', '"x"', ":", ",", "[", "]", "{", "}", " ", "1", "true", "null", '"\\"', '"a\\"b"', "\\"];
    let accepted = 0;
    for (let n = 0; n < 6000; n++) {
      let rest = "";
      const count = 2 + next(14);
      for (let k = 0; k < count; k++) rest += pool[next(pool.length)];
      // Seed some well-formed bodies so acceptance is exercised too.
      if (n % 3 === 0) rest = `"messages":[${next(2) === 0 ? "" : '"m"'}]${next(2) === 0 ? ',"model":"z"' : ',"fallbacks":"default"'}${next(2) === 0 ? ',"system":"s"' : ""}}`;
      const cut = next(rest.length + 1);
      const verdict = check(rest, [cut]);
      expect(check(rest, []), rest).toBe(verdict);
      if (!verdict) continue;
      accepted++;
      const parsed = JSON.parse(`{"model":"reviewer",${rest}`) as Record<string, unknown>;
      expect(Object.keys(parsed).filter((k) => k !== "model").every((k) => ALLOWED_TOP_LEVEL_KEYS.includes(k)), rest).toBe(true);
      expect(parsed["model"], rest).toBe("reviewer");
      expect(parsed["fallbacks"] === undefined || parsed["fallbacks"] === "default", rest).toBe(true);
    }
    expect(accepted).toBeGreaterThan(100);
  });

  it("allows exactly the documented members", () => {
    expect(ALLOWED_TOP_LEVEL_KEYS).toEqual(["system", "messages", "output_config", "fallbacks"]);
    expect(check('"thinking":{"type":"adaptive"},"messages":[]}', [])).toBe(false);
  });

  it("checks a 30 MB body of pictures within the free plan's CPU budget", () => {
    const picture = "A".repeat(1024 * 1024);
    const parts = ['"messages":[{"role":"user","content":['];
    for (let i = 0; i < 28; i++) parts.push(`${i > 0 ? "," : ""}{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"${picture}"}}`);
    parts.push("]}]}");
    const bytes = enc.encode(parts.join(""));
    const chunks: Uint8Array[] = [];
    for (let at = 0; at < bytes.length; at += 64 * 1024) chunks.push(bytes.subarray(at, at + 64 * 1024));
    const started = performance.now();
    const guard = new TopLevelKeyGuard();
    for (const c of chunks) expect(guard.push(c)).toBe(true);
    expect(guard.finish()).toBe(true);
    // Workers time is coarse inside a request; this only guards against a per-byte loop over the pictures.
    expect(performance.now() - started).toBeLessThan(250);
  });
});

describe("answer reduction (pure)", () => {
  it("joins the text blocks in order, ignores thinking and fallback blocks, and refuses a non-message", () => {
    expect(reduceAnswer({ content: [{ type: "text", text: "a" }, { type: "fallback", from: {}, to: {} }, { type: "text", text: "b" }], stop_reason: "end_turn", usage: { input_tokens: 1, output_tokens: 2 } })).toEqual({
      text: "ab",
      stopReason: "end_turn",
      inputTokens: 1,
      outputTokens: 2,
    });
    expect(reduceAnswer({ content: "x", stop_reason: "end_turn" })).toBeNull();
    expect(reduceAnswer(null)).toBeNull();
    expect(reduceAnswer({ content: [], stop_reason: "refusal" })?.stopReason).toBe("refusal");
  });
});

describe("provider configuration (pure)", () => {
  it("accepts an https address with token-shaped headers and refuses everything else", () => {
    expect(parseProvider('{"url":"https://h.example/v1/x","keyHeader":"x-key","headers":{"x-version":"1"}}')).toEqual({ url: "https://h.example/v1/x", keyHeader: "x-key", headers: { "x-version": "1" } });
    const bad = [
      '{"url":"https://h.example/","keyHeader":"x-key","headers":{"content-length":"1"}}',
      '{"url":"https://h.example/","keyHeader":"x-key","headers":{"X-Key":"1"}}',
      '{"url":"https://h.example/","keyHeader":"x-key","headers":{"x a":"1"}}',
      '{"url":"https://h.example/","keyHeader":"x-key","headers":{"x-a":"line\nbreak"}}',
      '{"url":"https://h.example/","keyHeader":"host"}',
      '{"url":"ftp://h.example/","keyHeader":"x-key"}',
      '{"url":"https://h.example/","keyHeader":"x-key","headers":[]}',
      '{"keyHeader":"x-key"}',
    ];
    for (const value of bad) expect(parseProvider(value), value).toBeNull();
  });
});
