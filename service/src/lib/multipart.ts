// A strict reader for the one multipart/form-data shape the upload route accepts (RFC 7578): exactly one part, named
// `file`, with a Content-Type. It returns a view of the part's bytes without copying or scanning them, because the
// platform's FormData parser costs about as much CPU as the image check itself on a 2 MB file (docs/evidence/upload-cpu.md)
// and the Workers free tier allows 10 ms per request.
//
// Shape: "--" boundary CRLF, part headers (at most 1 KiB, Content-Disposition and Content-Type, optionally
// Content-Length), CRLF CRLF, the bytes, CRLF "--" boundary "--", optionally one CRLF. No preamble, no epilogue.
// Anything between the header block and the closing delimiter is the part's content; a body that smuggles a second
// part therefore hands the image parser bytes with trailing data, and the file is refused there.

const MAX_HEADER_BYTES = 1024;
const BOUNDARY = /^[0-9A-Za-z'()+_,\-./:=?]{1,70}$/;

export type MultipartProblem = "bad_content_type" | "malformed" | "wrong_part";

export type FilePart = { ok: true; type: string; bytes: Uint8Array } | { ok: false; problem: MultipartProblem };

/** The boundary of a `multipart/form-data` Content-Type, or null. */
export function multipartBoundary(contentType: string): string | null {
  const match = /^multipart\/form-data\s*;\s*boundary=("([^"]+)"|[^\s;]+)\s*$/i.exec(contentType.trim());
  if (!match) return null;
  const boundary = match[2] ?? match[1]!;
  return BOUNDARY.test(boundary) && !boundary.endsWith(" ") ? boundary : null;
}

function startsWithAt(haystack: Uint8Array, needle: Uint8Array, at: number): boolean {
  if (at < 0 || at + needle.length > haystack.length) return false;
  for (let i = 0; i < needle.length; i++) if (haystack[at + i] !== needle[i]) return false;
  return true;
}

function headerValue(lines: readonly string[], name: string): string | null | undefined {
  const found = lines.filter((l) => l.slice(0, l.indexOf(":")).trim().toLowerCase() === name);
  if (found.length > 1) return undefined;
  return found.length === 1 ? found[0]!.slice(found[0]!.indexOf(":") + 1).trim() : null;
}

/** Reads the single `file` part of `body`. */
export function readSingleFilePart(body: Uint8Array, contentType: string): FilePart {
  const boundary = multipartBoundary(contentType);
  if (boundary === null) return { ok: false, problem: "bad_content_type" };
  const ascii = new TextEncoder();
  const opening = ascii.encode(`--${boundary}\r\n`);
  if (!startsWithAt(body, opening, 0)) return { ok: false, problem: "malformed" };

  // The end of the header block: the first CRLF CRLF within MAX_HEADER_BYTES.
  const limit = Math.min(body.length - 3, opening.length + MAX_HEADER_BYTES);
  let headerEnd = -1;
  for (let i = opening.length; i < limit; i++) {
    if (body[i] === 0x0d && body[i + 1] === 0x0a && body[i + 2] === 0x0d && body[i + 3] === 0x0a) {
      headerEnd = i;
      break;
    }
  }
  if (headerEnd < 0) return { ok: false, problem: "malformed" };
  let headerText: string;
  try {
    headerText = new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(body.subarray(opening.length, headerEnd));
  } catch (error) {
    if (error instanceof TypeError) return { ok: false, problem: "malformed" };
    throw error;
  }
  const lines = headerText.split("\r\n");
  if (lines.some((l) => !/^[A-Za-z-]+:[^\r\n]*$/.test(l))) return { ok: false, problem: "malformed" };
  const known = new Set(["content-disposition", "content-type", "content-length"]);
  if (lines.some((l) => !known.has(l.slice(0, l.indexOf(":")).trim().toLowerCase()))) return { ok: false, problem: "malformed" };
  const disposition = headerValue(lines, "content-disposition");
  const type = headerValue(lines, "content-type");
  const length = headerValue(lines, "content-length");
  if (!disposition || !type || length === undefined) return { ok: false, problem: "malformed" };
  const params = disposition.split(";").map((p) => p.trim());
  if (params[0]!.toLowerCase() !== "form-data") return { ok: false, problem: "malformed" };
  const names = params.filter((p) => /^name=/i.test(p)).map((p) => p.slice(5).replace(/^"(.*)"$/, "$1"));
  if (names.length !== 1 || names[0] !== "file") return { ok: false, problem: "wrong_part" };

  // The closing delimiter, with or without one final CRLF.
  const closing = ascii.encode(`\r\n--${boundary}--`);
  let contentEnd = body.length - closing.length;
  if (!startsWithAt(body, closing, contentEnd)) {
    contentEnd -= 2;
    if (!startsWithAt(body, closing, contentEnd) || body[body.length - 2] !== 0x0d || body[body.length - 1] !== 0x0a) return { ok: false, problem: "malformed" };
  }
  const contentStart = headerEnd + 4;
  if (contentEnd < contentStart) return { ok: false, problem: "malformed" };
  const bytes = body.subarray(contentStart, contentEnd);
  if (length !== null && (!/^\d{1,10}$/.test(length) || Number(length) !== bytes.length)) return { ok: false, problem: "malformed" };
  return { ok: true, type: type.split(";")[0]!.trim().toLowerCase(), bytes };
}
