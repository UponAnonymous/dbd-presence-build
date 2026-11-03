// Owner pack images (FR-ADM-08, ADR-0013): what the desktop app accepts, and lossless clean-up of what it would refuse.
//
// The app refuses a whole sync if one pack file fails its checks (docs/evidence/pack-index-contract.md §4), so every
// file is brought into the app's form here, at upload, or refused with a reason the panel explains:
//   PNG  8-bit truecolour (colour type 2, RGB) or truecolour with alpha (6, RGBA); only IHDR, IDAT and IEND. The
//        app's StrictPng check refuses everything else.
//   JPEG baseline or progressive, no APPn and no COM segments (PLAN_V2 §3.5).
// Clean-up removes only chunks and segments that carry metadata, never image data, so it needs no decoder and changes
// no pixel: text, time, physical size, colour-space hints and profiles (PNG), and JFIF, Exif, XMP, ICC profiles,
// Adobe and comment segments (JPEG). Two cases are refused instead of cleaned, because dropping the segment would
// change how the picture looks: an Exif orientation other than "upright" (the picture would turn) and an Adobe
// segment declaring RGB-coded colour (the colours would change). The cleaned bytes then go through the strict parser
// (src/images/strict.ts), and only bytes that pass it are stored. Pure code: no Workers APIs.

import { inspectImage, MAX_IMAGE_BYTES, type ImageInfo, type ImageRejection } from "./strict.ts";

export type PackImageRejection =
  | ImageRejection
  /** A PNG that is greyscale, palette-based, 16-bit or uses a transparent-colour key (tRNS). */
  | "png_not_rgb8"
  /** A JPEG whose Exif orientation is not upright. */
  | "jpeg_rotated"
  /** A JPEG whose Adobe segment says its colour is RGB-coded rather than YCbCr. */
  | "jpeg_rgb_coded";

export type PackImageResult = { ok: true; bytes: Uint8Array; info: ImageInfo; removed: string[] } | { ok: false; reason: PackImageRejection };

const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
/** PNG ancillary chunks that hold only metadata: dropping them changes no pixel. */
const PNG_REMOVABLE = new Set(["tEXt", "zTXt", "iTXt", "tIME", "pHYs", "sRGB", "gAMA", "cHRM", "iCCP", "eXIf", "bKGD", "sBIT", "hIST", "sPLT", "cICP", "mDCv", "cLLI"]);

function u32(b: Uint8Array, at: number): number {
  return ((b[at]! << 24) | (b[at + 1]! << 16) | (b[at + 2]! << 8) | b[at + 3]!) >>> 0;
}

function u16(b: Uint8Array, at: number): number {
  return (b[at]! << 8) | b[at + 1]!;
}

function ascii(b: Uint8Array, at: number, length: number): string {
  let s = "";
  for (let i = 0; i < length && at + i < b.length; i++) s += String.fromCharCode(b[at + i]!);
  return s;
}

function join(parts: readonly Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

/** Checks and cleans one owner pack image. */
export function preparePackImage(bytes: Uint8Array): PackImageResult {
  if (bytes.length === 0) return { ok: false, reason: "empty" };
  if (bytes.length > MAX_IMAGE_BYTES) return { ok: false, reason: "too_large" };
  if (bytes.length >= 8 && PNG_SIGNATURE.every((v, i) => bytes[i] === v)) return preparePng(bytes);
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return prepareJpeg(bytes);
  return { ok: false, reason: "unknown_format" };
}

// ---------------------------------------------------------------------------------------------------------------
// PNG

const code = (type: string): number => ((type.charCodeAt(0) << 24) | (type.charCodeAt(1) << 16) | (type.charCodeAt(2) << 8) | type.charCodeAt(3)) >>> 0;
const PNG_REMOVABLE_CODES = new Map([...PNG_REMOVABLE].map((t) => [code(t), t]));
const IHDR = code("IHDR");
const PLTE = code("PLTE");
const TRNS = code("tRNS");
const IDAT = code("IDAT");
const IEND = code("IEND");

function preparePng(bytes: Uint8Array): PackImageResult {
  // Walk the chunks by their lengths only, noting the byte ranges to drop; the strict parser then checks every CRC
  // of what is kept. Nothing is copied unless something is removed.
  const drop: number[] = [];
  const removed = new Set<string>();
  let offset = 8;
  let colourType = -1;
  let depth = -1;
  let colourKey = false;
  while (offset + 12 <= bytes.length) {
    const length = u32(bytes, offset);
    if (length > bytes.length - offset - 12) break; // truncated: left for the strict parser to refuse
    const type = u32(bytes, offset + 4);
    const end = offset + 12 + length;
    if (type === IDAT) {
      offset = end;
      continue;
    }
    if (type === IHDR && length === 13 && colourType === -1) {
      depth = bytes[offset + 16]!;
      colourType = bytes[offset + 17]!;
    }
    if (type === TRNS) colourKey = true;
    const metadata = PNG_REMOVABLE_CODES.get(type);
    // A suggested palette in a truecolour image is a hint for old displays, not image data.
    const name = metadata ?? (type === PLTE && (colourType === 2 || colourType === 6) ? "PLTE" : null);
    if (name !== null) {
      removed.add(name);
      if (drop.length > 0 && drop[drop.length - 1] === offset) drop[drop.length - 1] = end;
      else drop.push(offset, end);
    }
    offset = end;
    if (type === IEND) break;
  }
  const cleaned = drop.length === 0 ? bytes : without(bytes, drop);
  const check = inspectImage(cleaned);
  if (!check.ok) return check;
  // A PLTE that was not removed belongs to a palette image, which inspectImage passes and the app refuses.
  if (depth !== 8 || (colourType !== 2 && colourType !== 6) || colourKey) return { ok: false, reason: "png_not_rgb8" };
  return { ok: true, bytes: cleaned, info: check.info, removed: [...removed] };
}

/** `bytes` without the ranges [drop[0], drop[1]), [drop[2], drop[3]) ... (sorted, not overlapping). */
function without(bytes: Uint8Array, drop: readonly number[]): Uint8Array {
  let removedBytes = 0;
  for (let i = 0; i < drop.length; i += 2) removedBytes += drop[i + 1]! - drop[i]!;
  const out = new Uint8Array(bytes.length - removedBytes);
  let from = 0;
  let written = 0;
  for (let i = 0; i <= drop.length; i += 2) {
    const until = i < drop.length ? drop[i]! : bytes.length;
    out.set(bytes.subarray(from, until), written);
    written += until - from;
    if (i < drop.length) from = drop[i + 1]!;
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// JPEG

/** The Exif orientation (tag 0x0112) in an APP1 Exif segment body, 1 when absent, or null when unreadable. */
export function exifOrientation(body: Uint8Array): number | null {
  if (ascii(body, 0, 6) !== "Exif\0\0") return null;
  const tiff = 6;
  const order = ascii(body, tiff, 2);
  if (order !== "II" && order !== "MM") return null;
  const little = order === "II";
  const read16 = (at: number) => (at + 2 > body.length ? -1 : little ? body[at]! | (body[at + 1]! << 8) : u16(body, at));
  const read32 = (at: number) => (at + 4 > body.length ? -1 : little ? (body[at]! | (body[at + 1]! << 8) | (body[at + 2]! << 16) | (body[at + 3]! << 24)) >>> 0 : u32(body, at));
  if (read16(tiff + 2) !== 42) return null;
  const ifd = read32(tiff + 4);
  if (ifd < 8 || tiff + ifd + 2 > body.length) return null;
  const count = read16(tiff + ifd);
  for (let i = 0; i < count; i++) {
    const entry = tiff + ifd + 2 + i * 12;
    if (entry + 12 > body.length) return null;
    if (read16(entry) === 0x0112) return read16(entry + 8);
  }
  return 1;
}

function prepareJpeg(bytes: Uint8Array): PackImageResult {
  const kept: Uint8Array[] = [bytes.subarray(0, 2)];
  const removed: string[] = [];
  let offset = 2;
  let adobeRgb = false;
  let components = 0;
  // Only the segments before the first scan are cleaned; the strict parser refuses metadata anywhere later.
  while (offset + 4 <= bytes.length && bytes[offset] === 0xff) {
    const marker = bytes[offset + 1]!;
    if (marker === 0xda || marker === 0xd9 || marker === 0xff || (marker >= 0xd0 && marker <= 0xd8)) break;
    const length = u16(bytes, offset + 2);
    if (length < 2 || offset + 2 + length > bytes.length) break;
    const end = offset + 2 + length;
    const body = bytes.subarray(offset + 4, end);
    if (marker === 0xc0 || marker === 0xc2) components = body[5] ?? 0;
    if (marker >= 0xe0 && marker <= 0xef) {
      const n = marker - 0xe0;
      if (n === 1 && ascii(body, 0, 6) === "Exif\0\0") {
        const orientation = exifOrientation(body);
        if (orientation !== null && orientation !== 1) return { ok: false, reason: "jpeg_rotated" };
        removed.push("Exif");
      } else if (n === 14 && ascii(body, 0, 5) === "Adobe") {
        if (body.length >= 12 && body[11] === 0) adobeRgb = true;
        removed.push("Adobe");
      } else {
        removed.push(n === 0 ? "JFIF" : n === 1 ? "XMP" : n === 2 ? "ICC profile" : `APP${n}`);
      }
    } else if (marker === 0xfe) {
      removed.push("comment");
    } else {
      kept.push(bytes.subarray(offset, end));
    }
    offset = end;
  }
  if (adobeRgb && components === 3) return { ok: false, reason: "jpeg_rgb_coded" };
  if (offset < bytes.length) kept.push(bytes.subarray(offset));
  const cleaned = removed.length === 0 ? bytes : join(kept);
  const check = inspectImage(cleaned);
  if (!check.ok) return check;
  return { ok: true, bytes: cleaned, info: check.info, removed: [...new Set(removed)] };
}
