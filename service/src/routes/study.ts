// Automatic labelling of the owner's study recordings (owner decisions D37 and D38, ADR-0018, docs/PLAN_AUTO_LABELLING.md).
//
// POST /v1/study/label (session): the owner's app sends one labelling request built from a review file (pictures,
// close-ups, times and the closed lists; never a detector reading). Only an account the owner marked as their own (the
// D35 flag) may use it: every other account gets 404, as if the route did not exist. The relay is off (503
// `labelling_not_set_up`) until the owner sets the secret STUDY_LABELLER_KEY.
//
// The request is never parsed: on the free Workers plan (10 ms CPU) a 30 MB JSON body could not be. The body must start
// with exactly LABEL_PREFIX; the relay swaps it for the configured model and its own answer budget and streams the rest
// with a fixed length, while TopLevelKeyGuard checks the structure (only the allowed top-level members, so a repeated
// `model` or `max_tokens` cannot override the relay's). The app therefore never holds a model id, a provider host or a
// key (project rule #7, ADR-0016). The provider's address and header names live in the Worker variable
// STUDY_LABELLER_PROVIDER (wrangler.toml), not in this source, so the Worker bundle holds no provider text and the release
// branding check can keep scanning it. The model's answer is small:
// it is parsed and reduced to the answer text, the stop reason and the token counts, so no model name reaches the app.
// Neither the request (pictures that show other players' names) nor the answer is logged or stored; the audit row
// records sizes, token counts and the outcome only.

import { requireSession } from "../auth/session.ts";
import type { Context } from "../context.ts";
import { writeAudited } from "../db/audit.ts";
import { getSettings } from "../db/settings.ts";
import { HttpError, json } from "../http.ts";
import { countingFailures, limiterFor, revokedResponse } from "./common.ts";

/** The exact bytes every labelling request starts with; `reviewer` is a placeholder the relay replaces. */
export const LABEL_PREFIX = '{"model":"reviewer",';
/** Largest labelling request (a chunk of a review file); the provider and the Workers request limit are higher. */
export const MAX_LABEL_BYTES = 30 * 1024 * 1024;
/** Largest provider message the relay reads (thinking blocks included); at the answer budget it is far below this. */
export const MAX_ANSWER_BYTES = 2 * 1024 * 1024;
/** Largest answer text the relay returns; equal to the app's own response limit (StudyLabellingClient.MaxResponseBytes). */
export const MAX_REPLY_TEXT_BYTES = 1024 * 1024;
const MODEL_PATTERN = /^[a-z0-9][a-z0-9.-]{2,63}$/;

/** The provider the relay forwards to, read from the Worker variable STUDY_LABELLER_PROVIDER. */
export interface ProviderConfig {
  url: string;
  /** Name of the header that carries the key. */
  keyHeader: string;
  /** Further fixed headers the provider requires (names and values from the owner's configuration). */
  headers: Record<string, string>;
}

const HEADER_NAME = /^[A-Za-z0-9][A-Za-z0-9-]{0,63}$/;
const HEADER_VALUE = /^[\x21-\x7e][\x20-\x7e]{0,199}$/;
/** The relay sets these itself; the configuration may not. */
const RESERVED_HEADERS = new Set(["content-type", "content-length", "host", "transfer-encoding", "connection", "cookie", "authorization", "proxy-authorization"]);
const MAX_EXTRA_HEADERS = 8;

/**
 * Checks and reads the provider configuration: an https address without credentials, a token-shaped header name for the
 * key and at most a few token-shaped extra headers. Anything else (missing, not JSON, wrong shape) is null, which the
 * route answers as "labelling not set up", so a typo in the variable cannot send the key anywhere unexpected.
 */
export function parseProvider(raw: string | undefined): ProviderConfig | null {
  if (!raw) return null;
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) return null;
  const { url, keyHeader, headers } = value as { url?: unknown; keyHeader?: unknown; headers?: unknown };
  if (typeof url !== "string" || typeof keyHeader !== "string" || !HEADER_NAME.test(keyHeader) || RESERVED_HEADERS.has(keyHeader.toLowerCase())) return null;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== "https:" || parsed.username !== "" || parsed.password !== "" || parsed.hostname === "") return null;
  const extra: Record<string, string> = {};
  if (headers !== undefined) {
    if (typeof headers !== "object" || headers === null || Array.isArray(headers)) return null;
    const entries = Object.entries(headers as Record<string, unknown>);
    if (entries.length > MAX_EXTRA_HEADERS) return null;
    for (const [name, v] of entries) {
      if (!HEADER_NAME.test(name) || RESERVED_HEADERS.has(name.toLowerCase()) || name.toLowerCase() === keyHeader.toLowerCase()) return null;
      if (typeof v !== "string" || !HEADER_VALUE.test(v)) return null;
      extra[name] = v;
    }
  }
  return { url: parsed.href, keyHeader, headers: extra };
}

const encoder = new TextEncoder();
const PREFIX_BYTES = encoder.encode(LABEL_PREFIX);

/** The relay, not the app, sets the answer budget: the app's body may not carry `max_tokens`. */
export const MAX_ANSWER_TOKENS = 16000;
/**
 * The only top-level members the app may send after the placeholder (compared as raw bytes, so an escaped spelling such as
 * `mod\u0065l` is refused too). `model` and `max_tokens` come from the relay; everything else (tools, speed, service tier,
 * stream, ...) is refused, so a body cannot pick another model, a larger budget or a costlier mode by repeating a member.
 */
export const ALLOWED_TOP_LEVEL_KEYS: readonly string[] = ["system", "messages", "output_config", "fallbacks"];
const ALLOWED_KEYS = new Set(ALLOWED_TOP_LEVEL_KEYS);
const MAX_KEY_BYTES = 32;
const QUOTE = 34;
const BACKSLASH = 92;

/** The only value the `fallbacks` member may have: the provider's default fallback list (raw bytes, no escapes). */
const FALLBACKS_VALUE = "default";
const MAX_NESTING = 64;
const State = {
  /** After `{` or `,` at the top level: a quoted key must follow. */
  Key: 0,
  /** After a top-level key: `:` must follow. */
  Colon: 1,
  /** After `:`: a value must start. */
  Value: 2,
  /** Inside a number or literal at the top level. */
  Scalar: 3,
  /** After a complete top-level value: `,` or `}` must follow. */
  After: 4,
} as const;
type State = (typeof State)[keyof typeof State];

/**
 * Checks the JSON structure of the bytes after the prefix as they stream past, without parsing values: it tracks the
 * top-level grammar (key, colon, value, comma), the nesting of brackets and string boundaries, and inside strings it
 * jumps to the next quote with the runtime's native search, so the large base64 pictures cost almost no CPU. It refuses
 * any top-level member not in ALLOWED_TOP_LEVEL_KEYS (or given twice), a `fallbacks` value other than "default", a
 * missing colon or comma at the top level (a lenient provider parser could otherwise read a second `model` out of
 * `"messages":[] "model":...`), mismatched brackets, any byte after the top-level object closes, and a body whose object
 * never closes. Duplicate-member and parser-differential tricks therefore cannot reach the provider.
 */
export class TopLevelKeyGuard {
  private state: State = State.Key;
  private readonly stack: number[] = [];
  private inString = false;
  /** What the string being read is: a top-level key, the `fallbacks` value, or anything else (not collected). */
  private collecting: "key" | "fallbacks" | null = null;
  private currentKey = "";
  private readonly seen = new Set<string>();
  private key: number[] = [];
  private keyTooLong = false;
  private pendingBackslashes = 0;
  private closed = false;
  failed = false;

  /** Feeds the next chunk; returns false (and stays failed) as soon as the structure is refused. */
  push(chunk: Uint8Array): boolean {
    let i = 0;
    while (!this.failed && i < chunk.length) {
      if (this.inString) {
        const q = chunk.indexOf(QUOTE, i);
        const end = q < 0 ? chunk.length : q;
        if (this.collecting !== null) this.collect(chunk, i, end);
        let run = 0;
        while (end - run - 1 >= i && chunk[end - run - 1] === BACKSLASH) run++;
        const backslashes = run === end - i ? run + this.pendingBackslashes : run;
        if (q < 0) {
          this.pendingBackslashes = backslashes;
          return true;
        }
        this.pendingBackslashes = 0;
        if (backslashes % 2 === 1) {
          if (this.collecting !== null) this.collect(chunk, q, q + 1);
          i = q + 1;
          continue;
        }
        this.inString = false;
        this.endString();
        i = q + 1;
        continue;
      }
      const c = chunk[i]!;
      i++;
      if (c === 0x20 || c === 0x0a || c === 0x0d || c === 0x09) {
        if (this.state === State.Scalar && this.stack.length === 0) this.state = State.After;
        continue;
      }
      if (this.closed) {
        this.failed = true;
        break;
      }
      if (this.stack.length === 0) this.topLevel(c);
      else this.nested(c);
    }
    return !this.failed;
  }

  /** True when the whole body was a closed top-level object with allowed members only. */
  finish(): boolean {
    return !this.failed && this.closed && !this.inString;
  }

  /** A significant byte outside every string and bracket opened after the prefix (depth 1 of the object). */
  private topLevel(c: number): void {
    switch (this.state) {
      case State.Key:
        if (c === QUOTE) this.beginString("key");
        else this.failed = true;
        return;
      case State.Colon:
        if (c === 0x3a) this.state = State.Value;
        else this.failed = true;
        return;
      case State.Value:
        if (this.currentKey === "fallbacks") {
          // Only the exact string "default" is allowed.
          if (c === QUOTE) this.beginString("fallbacks");
          else this.failed = true;
          return;
        }
        if (c === QUOTE) this.beginString(null);
        else if (c === 0x7b || c === 0x5b) this.open(c);
        else if ((c >= 0x30 && c <= 0x39) || c === 0x2d || c === 0x74 || c === 0x66 || c === 0x6e) this.state = State.Scalar;
        else this.failed = true;
        return;
      case State.Scalar:
        if (c === 0x2c) this.state = State.Key;
        else if (c === 0x7d) this.closed = true;
        else if (!((c >= 0x30 && c <= 0x39) || (c >= 0x41 && c <= 0x5a) || (c >= 0x61 && c <= 0x7a) || c === 0x2e || c === 0x2b || c === 0x2d)) this.failed = true;
        return;
      case State.After:
        if (c === 0x2c) this.state = State.Key;
        else if (c === 0x7d) this.closed = true;
        else this.failed = true;
        return;
    }
  }

  /** A significant byte inside a nested object or array: brackets must match; the content is the provider's business. */
  private nested(c: number): void {
    if (c === QUOTE) {
      this.beginString(null);
      return;
    }
    if (c === 0x7b || c === 0x5b) {
      this.open(c);
      return;
    }
    if (c === 0x7d || c === 0x5d) {
      if (this.stack.pop() !== c) {
        this.failed = true;
        return;
      }
      if (this.stack.length === 0) this.state = State.After;
    }
  }

  private open(c: number): void {
    if (this.stack.length >= MAX_NESTING) {
      this.failed = true;
      return;
    }
    this.stack.push(c === 0x7b ? 0x7d : 0x5d);
  }

  private beginString(kind: "key" | "fallbacks" | null): void {
    this.inString = true;
    this.collecting = kind;
    this.key = [];
    this.keyTooLong = false;
  }

  private endString(): void {
    const kind = this.collecting;
    this.collecting = null;
    if (this.stack.length > 0) return;
    if (kind === "key") {
      const name = this.keyTooLong ? "" : String.fromCharCode(...this.key);
      if (!ALLOWED_KEYS.has(name) || this.seen.has(name)) {
        this.failed = true;
        return;
      }
      this.seen.add(name);
      this.currentKey = name;
      this.state = State.Colon;
      return;
    }
    if (kind === "fallbacks" && (this.keyTooLong || String.fromCharCode(...this.key) !== FALLBACKS_VALUE)) {
      this.failed = true;
      return;
    }
    this.state = State.After;
  }

  private collect(chunk: Uint8Array, from: number, to: number): void {
    if (this.keyTooLong) return;
    if (this.key.length + (to - from) > MAX_KEY_BYTES) {
      this.keyTooLong = true;
      return;
    }
    for (let k = from; k < to; k++) this.key.push(chunk[k]!);
  }
}

/**
 * Reads the first bytes of `body` and checks that they are exactly LABEL_PREFIX. Returns a stream of `length` bytes in
 * which the prefix is replaced by `replacement`, with the new length, or null when the prefix is wrong or the body ends
 * early. The rest is checked by TopLevelKeyGuard while it streams; a refused body aborts the stream (so the provider never
 * receives a complete request) and `refused()` turns true.
 */
export async function swapPrefix(
  body: ReadableStream<Uint8Array>,
  length: number,
  replacement: string,
): Promise<{ stream: ReadableStream<Uint8Array>; length: number; refused: () => boolean } | null> {
  const reader = body.getReader();
  const head = new Uint8Array(PREFIX_BYTES.length);
  let filled = 0;
  let rest: Uint8Array | null = null;
  while (filled < head.length) {
    const { done, value } = await reader.read();
    if (done) {
      await reader.cancel().catch(() => undefined);
      return null;
    }
    const take = Math.min(value.length, head.length - filled);
    head.set(value.subarray(0, take), filled);
    filled += take;
    if (take < value.length) rest = value.subarray(take);
  }
  for (let i = 0; i < head.length; i++) {
    if (head[i] !== PREFIX_BYTES[i]) {
      await reader.cancel().catch(() => undefined);
      return null;
    }
  }
  const guard = new TopLevelKeyGuard();
  let refused = false;
  const start = encoder.encode(replacement);
  const total = length - PREFIX_BYTES.length + start.length;
  const out = new FixedLengthStream(total);
  void (async () => {
    const writer = out.writable.getWriter();
    try {
      await writer.write(start);
      const forward = async (chunk: Uint8Array): Promise<void> => {
        if (!guard.push(chunk)) {
          refused = true;
          throw new Error("refused body");
        }
        await writer.write(chunk);
      };
      if (rest !== null && rest.length > 0) await forward(rest);
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        await forward(value);
      }
      if (!guard.finish()) {
        refused = true;
        throw new Error("refused body");
      }
      await writer.close();
    } catch (error) {
      await writer.abort(error).catch(() => undefined);
      await reader.cancel().catch(() => undefined);
    }
  })();
  return { stream: out.readable, length: total, refused: () => refused };
}

/** Reads at most `max` bytes of a response body as text; longer answers are refused. */
async function readCapped(response: Response, max: number): Promise<string | null> {
  if (response.body === null) return "";
  const reader = response.body.getReader();
  const parts: Uint8Array[] = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > max) {
      await reader.cancel().catch(() => undefined);
      return null;
    }
    parts.push(value);
  }
  const all = new Uint8Array(size);
  let at = 0;
  for (const part of parts) {
    all.set(part, at);
    at += part.length;
  }
  return new TextDecoder().decode(all);
}

interface Answer {
  text: string;
  stopReason: string;
  inputTokens: number;
  outputTokens: number;
}

/** The answer text (every text block, in order), the stop reason and the token counts of a provider message. */
export function reduceAnswer(value: unknown): Answer | null {
  if (typeof value !== "object" || value === null) return null;
  const message = value as { content?: unknown; stop_reason?: unknown; usage?: { input_tokens?: unknown; output_tokens?: unknown; cache_read_input_tokens?: unknown; cache_creation_input_tokens?: unknown } };
  if (!Array.isArray(message.content) || typeof message.stop_reason !== "string") return null;
  const text = message.content
    .filter((b): b is { type: "text"; text: string } => typeof b === "object" && b !== null && (b as { type?: unknown }).type === "text" && typeof (b as { text?: unknown }).text === "string")
    .map((b) => b.text)
    .join("");
  const count = (n: unknown): number => (typeof n === "number" && Number.isSafeInteger(n) && n >= 0 ? n : 0);
  const usage = message.usage ?? {};
  return {
    text,
    stopReason: message.stop_reason.slice(0, 40),
    inputTokens: count(usage.input_tokens) + count(usage.cache_read_input_tokens) + count(usage.cache_creation_input_tokens),
    outputTokens: count(usage.output_tokens),
  };
}

/** The provider's error type only (never its message, which could quote the request). */
function errorType(text: string): string {
  try {
    const parsed = JSON.parse(text) as { error?: { type?: unknown } };
    const type = parsed.error?.type;
    return typeof type === "string" && /^[a-z_]{1,40}$/.test(type) ? type : "unknown";
  } catch {
    return "unknown";
  }
}

export async function labelStudy(ctx: Context): Promise<Response> {
  const session = await requireSession(ctx);
  const limiter = limiterFor(ctx);
  await limiter.checkAccount("study-label", session.userId);
  return countingFailures(limiter, "study-label", session.userId, async () => {
    if (session.status === "revoked") return revokedResponse(ctx, session.userId, session.thumbprint);
    if (!session.hasAccess) throw new HttpError(403, "invite_required");
    const db = ctx.env.DB;
    const [settings, user] = await Promise.all([getSettings(db), db.prepare("SELECT roster_reporter FROM users WHERE id = ?").bind(session.userId).first<{ roster_reporter: number }>()]);
    if (!user) throw new HttpError(401, "unauthenticated", { "WWW-Authenticate": "Bearer" });
    // Not the owner's own account: the route does not exist for it.
    if (user.roster_reporter !== 1) throw new HttpError(404, "not_found");
    if (settings.emergencyStop) throw new HttpError(403, "online_features_off");
    const key = ctx.env.STUDY_LABELLER_KEY;
    const model = ctx.env.STUDY_LABELLER_MODEL;
    const provider = parseProvider(ctx.env.STUDY_LABELLER_PROVIDER);
    if (!key || !model || !MODEL_PATTERN.test(model) || provider === null) throw new HttpError(503, "labelling_not_set_up");

    const type = (ctx.request.headers.get("Content-Type") ?? "").split(";")[0]!.trim().toLowerCase();
    if (type !== "application/json") throw new HttpError(415, "unsupported_media_type");
    const declared = ctx.request.headers.get("Content-Length");
    if (declared === null || !/^\d{1,12}$/.test(declared)) throw new HttpError(411, "length_required");
    const length = Number(declared);
    if (length > MAX_LABEL_BYTES) throw new HttpError(413, "too_large", {}, { limit: String(MAX_LABEL_BYTES) });
    if (length <= LABEL_PREFIX.length || ctx.request.body === null) throw new HttpError(400, "invalid_request");
    const swapped = await swapPrefix(ctx.request.body, length, `{"model":${JSON.stringify(model)},"max_tokens":${MAX_ANSWER_TOKENS},`);
    if (swapped === null) throw new HttpError(400, "invalid_request");

    // Counted before the provider is called: every forwarded request uses up the owner's daily allowance.
    await limiter.verified("study-label", session.userId);
    let upstream: Response;
    try {
      upstream = await ctx.deps.fetch(provider.url, {
        method: "POST",
        // A redirect would carry the key to another host: never followed, and treated as a failure below.
        redirect: "manual",
        headers: {
          ...provider.headers,
          [provider.keyHeader]: key,
          "Content-Type": "application/json",
          "Content-Length": String(swapped.length),
        },
        body: swapped.stream,
      });
    } catch {
      if (swapped.refused()) return await finish(ctx, session.userId, length, null, "body_refused", json({ error: "invalid_request" }, 400));
      return await finish(ctx, session.userId, length, null, "unreachable", json({ error: "labeller_unreachable" }, 502));
    }
    const text = await readCapped(upstream, MAX_ANSWER_BYTES);
    // The provider may answer an aborted request before the abort reaches it; a refused body is never a success.
    if (swapped.refused()) return await finish(ctx, session.userId, length, null, "body_refused", json({ error: "invalid_request" }, 400));
    if (text === null) return await finish(ctx, session.userId, length, null, "answer_too_large", json({ error: "labeller_answer_too_large" }, 502));
    if (upstream.status === 401 || upstream.status === 403) return await finish(ctx, session.userId, length, null, `provider_${upstream.status}`, json({ error: "labelling_not_set_up" }, 503));
    if (upstream.status === 429 || upstream.status === 529 || upstream.status >= 500) {
      const retry = upstream.headers.get("retry-after");
      const headers: Record<string, string> = retry !== null && /^\d{1,5}$/.test(retry) ? { "Retry-After": retry } : {};
      return await finish(ctx, session.userId, length, null, `provider_${upstream.status}`, json({ error: "labeller_busy" }, 503, headers));
    }
    if (upstream.status >= 300 && upstream.status < 400) return await finish(ctx, session.userId, length, null, "provider_redirect", json({ error: "labeller_refused", reason: "unknown" }, 502));
    if (upstream.status !== 200) {
      return await finish(ctx, session.userId, length, null, `provider_${upstream.status}`, json({ error: "labeller_refused", reason: errorType(text) }, 502));
    }
    let answer: Answer | null = null;
    try {
      answer = reduceAnswer(JSON.parse(text) as unknown);
    } catch {
      answer = null;
    }
    if (answer === null) return await finish(ctx, session.userId, length, null, "answer_unreadable", json({ error: "labeller_answer_unreadable" }, 502));
    if (encoder.encode(answer.text).length > MAX_REPLY_TEXT_BYTES) return await finish(ctx, session.userId, length, null, "answer_too_large", json({ error: "labeller_answer_too_large" }, 502));
    return await finish(ctx, session.userId, length, answer, "ok", json({ ...answer }));
  });
}

/** Writes the audit row (sizes, tokens, outcome; never content) and returns `response`. */
async function finish(ctx: Context, userId: string, bytes: number, answer: Answer | null, outcome: string, response: Response): Promise<Response> {
  await writeAudited(ctx.env.DB, ctx.now, {
    actor: `user:${userId}`,
    action: "study.label",
    target: null,
    after: answer === null ? { bytes, outcome } : { bytes, outcome, stopReason: answer.stopReason, inputTokens: answer.inputTokens, outputTokens: answer.outputTokens },
  });
  return response;
}
