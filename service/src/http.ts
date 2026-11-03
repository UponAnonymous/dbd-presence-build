// Responses, security headers and request-body reading (NFR-SVC-01). Every response leaves through `finalize`.

import { JsonSyntaxError, parseStrict, toPlain } from "./lib/json.ts";

export const SCHEMA_VERSION = 1;

/** A deliberate error response: `{ "schemaVersion": 1, "error": code }`, no detail, no stack. */
export class HttpError extends Error {
  readonly status: number;
  readonly code: string;
  readonly headers: Record<string, string>;
  /** Extra members of the error body, for example `limit` on 409 `upload_limit`. Never secrets or user data. */
  readonly details: Record<string, string>;

  constructor(status: number, code: string, headers: Record<string, string> = {}, details: Record<string, string> = {}) {
    super(code);
    this.name = "HttpError";
    this.status = status;
    this.code = code;
    this.headers = headers;
    this.details = details;
  }
}

/** Headers set on every response, including errors and 404s. JSON only, so the CSP forbids everything. */
export const SECURITY_HEADERS: Readonly<Record<string, string>> = {
  "Strict-Transport-Security": "max-age=63072000; includeSubDomains",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Content-Security-Policy": "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
  "Referrer-Policy": "no-referrer",
  "Cross-Origin-Resource-Policy": "same-origin",
  "Permissions-Policy": "interest-cohort=()",
};

/** Response headers that must never be sent (no cookies, no CORS). */
const FORBIDDEN_HEADERS = ["set-cookie", "access-control-allow-origin", "access-control-allow-credentials", "access-control-allow-methods", "access-control-allow-headers", "access-control-expose-headers"];

export function json(body: Record<string, unknown>, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify({ schemaVersion: SCHEMA_VERSION, ...body }), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", ...headers },
  });
}

export function errorResponse(error: HttpError): Response {
  return json({ ...error.details, error: error.code }, error.status, error.headers);
}

/** Applies the security headers and strips anything forbidden. */
export function finalize(response: Response): Response {
  const out = new Response(response.body, response);
  // A handler may set its own, stricter-for-its-content CSP (the static legal pages need inline CSS); JSON responses never do.
  const ownCsp = out.headers.get("Content-Security-Policy");
  for (const [name, value] of Object.entries(SECURITY_HEADERS)) out.headers.set(name, value);
  if (ownCsp !== null) out.headers.set("Content-Security-Policy", ownCsp);
  for (const name of FORBIDDEN_HEADERS) out.headers.delete(name);
  if (!out.headers.has("Cache-Control")) out.headers.set("Cache-Control", "no-store");
  return out;
}

/** Reads a request body as bytes with a hard cap (declared length and bytes actually read); 413 above it. */
export async function readBodyBytes(request: Request, maxBytes: number): Promise<Uint8Array> {
  const declared = request.headers.get("Content-Length");
  if (declared !== null && (!/^\d{1,10}$/.test(declared) || Number(declared) > maxBytes)) throw new HttpError(413, "body_too_large");
  if (!request.body) throw new HttpError(400, "invalid_request");
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxBytes) {
      await reader.cancel();
      throw new HttpError(413, "body_too_large");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return bytes;
}

/**
 * Reads a JSON request body with a hard byte cap, strict parsing (duplicate members rejected, depth cap) and
 * `Content-Type: application/json`. Returns plain values for the route's validator.
 */
export async function readJsonBody(request: Request, maxBytes: number): Promise<unknown> {
  const type = request.headers.get("Content-Type") ?? "";
  if (!/^application\/json(\s*;\s*charset=utf-8)?$/i.test(type.trim())) throw new HttpError(415, "unsupported_media_type");
  const bytes = await readBodyBytes(request, maxBytes);

  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes);
  } catch (error) {
    if (error instanceof TypeError) throw new HttpError(400, "invalid_request");
    throw error;
  }
  try {
    return toPlain(parseStrict(text, 6));
  } catch (error) {
    if (error instanceof JsonSyntaxError) throw new HttpError(400, "invalid_request");
    throw error;
  }
}

/** Bearer token from `Authorization`, or null. Only the opaque token format the service issues is accepted. */
export function bearerToken(request: Request): string | null {
  const header = request.headers.get("Authorization");
  if (!header) return null;
  const match = /^Bearer ([A-Za-z0-9_-]{8,128})$/.exec(header);
  return match ? match[1]! : null;
}

/** The client IP that Cloudflare reports; used only as a rate-limit key, never stored or logged in clear. */
export function clientIp(request: Request): string {
  const ip = request.headers.get("CF-Connecting-IP") ?? "";
  return /^[0-9a-fA-F:.]{2,45}$/.test(ip) ? ip.toLowerCase() : "unknown";
}
