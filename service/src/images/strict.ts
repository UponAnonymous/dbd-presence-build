// Strict PNG and JPEG parsers for images the service stores (PLAN_V2 §3.5, FR-ONL-09, ADR-0013). Used for the
// owner's pack images now and for user uploads in V2-S5. Nothing is decoded: the parsers walk the container structure
// and refuse anything outside a small allow-list, so what is stored is plain image data with no metadata, no animation
// and nothing after the end marker. Pure functions over bytes (no Workers APIs), so the same code is measured in Node
// (scripts/measure-image-parser.mjs) and tested in the Workers runtime.
//
// PNG:  signature; IHDR first (13 bytes, valid bit depth and colour type, compression 0, filter 0, interlace 0 or 1);
//       only IHDR, PLTE, tRNS, IDAT, IEND; PLTE and tRNS at most once and before the first IDAT; IDAT chunks
//       consecutive; PLTE required for colour type 3 and refused for grey types; every CRC checked; exactly one IEND,
//       empty, and nothing after it.
// JPEG: SOI; markers SOF0 or SOF2 (exactly one, 8-bit, 1 or 3 components), DHT, DQT, DRI, SOS; RSTn only inside
//       entropy-coded data; no APPn, no COM, no other marker; EOI last with nothing after it.
// Both: width and height 1 to 1024; at most 2 MB.

export const MAX_IMAGE_BYTES = 2 * 1024 * 1024;
export const MAX_IMAGE_DIMENSION = 1024;

export type ImageFormat = "png" | "jpeg";

export interface ImageInfo {
  format: ImageFormat;
  width: number;
  height: number;
}

/** Why an image was refused. Short codes, safe to return to the owner; never file content. */
export type ImageRejection =
  | "empty"
  | "too_large"
  | "unknown_format"
  | "truncated"
  | "bad_checksum"
  | "bad_header"
  | "dimensions_out_of_range"
  | "chunk_not_allowed"
  | "chunk_order"
  | "marker_not_allowed"
  | "metadata_not_allowed"
  | "animation_not_allowed"
  | "trailing_data"
  | "missing_end"
  | "no_image_data";

export type ImageCheck = { ok: true; info: ImageInfo } | { ok: false; reason: ImageRejection };

const reject = (reason: ImageRejection): ImageCheck => ({ ok: false, reason });

const PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

/** Identifies the format by its magic bytes and runs the strict parser for it. */
export function inspectImage(bytes: Uint8Array): ImageCheck {
  if (bytes.length === 0) return reject("empty");
  if (bytes.length > MAX_IMAGE_BYTES) return reject("too_large");
  if (bytes.length >= 8 && PNG_SIGNATURE.every((b, i) => bytes[i] === b)) return parsePng(bytes);
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return parseJpeg(bytes);
  return reject("unknown_format");
}

// ---------------------------------------------------------------------------------------------------------------
// PNG

// Slicing-by-8 tables: eight table lookups per 8 bytes instead of one per byte. CRC checking is most of the parse
// time for a large PNG, and the Workers free tier allows 10 ms of CPU per request (docs/evidence/upload-cpu.md).
const CRC_TABLES = (() => {
  const tables = new Uint32Array(8 * 256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    tables[n] = c >>> 0;
  }
  for (let n = 0; n < 256; n++) {
    let c = tables[n]!;
    for (let t = 1; t < 8; t++) {
      c = tables[c & 0xff]! ^ (c >>> 8);
      tables[t * 256 + n] = c >>> 0;
    }
  }
  return tables;
})();

/** CRC-32 (ISO 3309, as PNG uses it) of bytes[start, end). */
export function crc32(bytes: Uint8Array, start: number, end: number): number {
  const t = CRC_TABLES;
  let c = 0xffffffff;
  let i = start;
  for (; i + 8 <= end; i += 8) {
    const lo = c ^ (bytes[i]! | (bytes[i + 1]! << 8) | (bytes[i + 2]! << 16) | (bytes[i + 3]! << 24));
    c =
      t[7 * 256 + (lo & 0xff)]! ^
      t[6 * 256 + ((lo >>> 8) & 0xff)]! ^
      t[5 * 256 + ((lo >>> 16) & 0xff)]! ^
      t[4 * 256 + (lo >>> 24)]! ^
      t[3 * 256 + bytes[i + 4]!]! ^
      t[2 * 256 + bytes[i + 5]!]! ^
      t[256 + bytes[i + 6]!]! ^
      t[bytes[i + 7]!]!;
  }
  for (; i < end; i++) c = t[(c ^ bytes[i]!) & 0xff]! ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function u32(bytes: Uint8Array, at: number): number {
  return ((bytes[at]! << 24) | (bytes[at + 1]! << 16) | (bytes[at + 2]! << 8) | bytes[at + 3]!) >>> 0;
}

function u16(bytes: Uint8Array, at: number): number {
  return (bytes[at]! << 8) | bytes[at + 1]!;
}

/** A chunk type as its four bytes read big-endian, so chunks are compared without building strings. */
function code(type: string): number {
  return ((type.charCodeAt(0) << 24) | (type.charCodeAt(1) << 16) | (type.charCodeAt(2) << 8) | type.charCodeAt(3)) >>> 0;
}

const IHDR = code("IHDR");
const PLTE = code("PLTE");
const TRNS = code("tRNS");
const IDAT = code("IDAT");
const IEND = code("IEND");

/** Allowed bit depths per PNG colour type (PNG specification, table 11.1). */
const PNG_DEPTHS: Readonly<Record<number, readonly number[]>> = {
  0: [1, 2, 4, 8, 16],
  2: [8, 16],
  3: [1, 2, 4, 8],
  4: [8, 16],
  6: [8, 16],
};

const PNG_METADATA = new Set(["tEXt", "zTXt", "iTXt", "eXIf", "iCCP", "sRGB", "gAMA", "cHRM", "pHYs", "tIME", "bKGD", "sBIT", "hIST", "sPLT", "cICP", "mDCv", "cLLI"].map(code));
const PNG_ANIMATION = new Set(["acTL", "fcTL", "fdAT"].map(code));

export function parsePng(bytes: Uint8Array): ImageCheck {
  if (bytes.length > MAX_IMAGE_BYTES) return reject("too_large");
  if (bytes.length < 8 || !PNG_SIGNATURE.every((b, i) => bytes[i] === b)) return reject("unknown_format");
  let offset = 8;
  let index = 0;
  let width = 0;
  let height = 0;
  let colourType = -1;
  let depth = 0;
  let sawPlte = false;
  let sawTrns = false;
  let idatState: "none" | "in" | "after" = "none";
  let sawIend = false;
  while (offset < bytes.length) {
    if (sawIend) return reject("trailing_data");
    if (bytes.length - offset < 12) return reject("truncated");
    const length = u32(bytes, offset);
    if (length > bytes.length - offset - 12) return reject("truncated");
    const type = u32(bytes, offset + 4);
    const body = offset + 8;
    const stored = u32(bytes, body + length);
    if (crc32(bytes, offset + 4, body + length) !== stored) return reject("bad_checksum");

    if (index === 0) {
      if (type !== IHDR || length !== 13) return reject("bad_header");
      width = u32(bytes, body);
      height = u32(bytes, body + 4);
      depth = bytes[body + 8]!;
      colourType = bytes[body + 9]!;
      if (width < 1 || height < 1 || width > MAX_IMAGE_DIMENSION || height > MAX_IMAGE_DIMENSION) return reject("dimensions_out_of_range");
      if (!(PNG_DEPTHS[colourType] ?? []).includes(depth)) return reject("bad_header");
      if (bytes[body + 10] !== 0 || bytes[body + 11] !== 0 || bytes[body + 12]! > 1) return reject("bad_header");
    } else if (type === IDAT) {
      if (idatState === "after") return reject("chunk_order");
      if (colourType === 3 && !sawPlte) return reject("chunk_order");
      idatState = "in";
    } else {
      if (idatState === "in") idatState = "after";
      switch (type) {
        case IHDR:
          return reject("chunk_order");
        case PLTE:
          if (sawPlte || sawTrns || idatState !== "none") return reject("chunk_order");
          if (colourType === 0 || colourType === 4) return reject("chunk_order");
          if (length === 0 || length % 3 !== 0 || length / 3 > 2 ** (colourType === 3 ? depth : 8)) return reject("bad_header");
          sawPlte = true;
          break;
        case TRNS:
          if (sawTrns || idatState !== "none" || colourType === 4 || colourType === 6) return reject("chunk_order");
          if (colourType === 3 && !sawPlte) return reject("chunk_order");
          sawTrns = true;
          break;
        case IEND:
          if (length !== 0) return reject("bad_header");
          sawIend = true;
          break;
        default:
          if (PNG_ANIMATION.has(type)) return reject("animation_not_allowed");
          if (PNG_METADATA.has(type)) return reject("metadata_not_allowed");
          return reject("chunk_not_allowed");
      }
    }
    offset = body + length + 4;
    index++;
  }
  if (!sawIend) return reject("missing_end");
  if (idatState === "none") return reject("no_image_data");
  return { ok: true, info: { format: "png", width, height } };
}

// ---------------------------------------------------------------------------------------------------------------
// JPEG

const SOI = 0xd8;
const EOI = 0xd9;
const SOS = 0xda;
const SOF0 = 0xc0;
const SOF2 = 0xc2;
const DHT = 0xc4;
const DQT = 0xdb;
const DRI = 0xdd;

export function parseJpeg(bytes: Uint8Array): ImageCheck {
  if (bytes.length > MAX_IMAGE_BYTES) return reject("too_large");
  if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== SOI) return reject("unknown_format");
  let offset = 2;
  let width = 0;
  let height = 0;
  let sawFrame = false;
  let scans = 0;
  for (;;) {
    // A marker: one or more 0xFF fill bytes, then the marker code.
    if (offset >= bytes.length) return reject("missing_end");
    if (bytes[offset] !== 0xff) return reject("bad_header");
    while (offset < bytes.length && bytes[offset] === 0xff) offset++;
    if (offset >= bytes.length) return reject("missing_end");
    const marker = bytes[offset]!;
    offset++;

    if (marker === EOI) {
      if (!sawFrame || scans === 0) return reject("no_image_data");
      if (offset !== bytes.length) return reject("trailing_data");
      return { ok: true, info: { format: "jpeg", width, height } };
    }
    if (marker >= 0xe0 && marker <= 0xef) return reject("metadata_not_allowed");
    if (marker === 0xfe) return reject("metadata_not_allowed");
    if (marker !== SOF0 && marker !== SOF2 && marker !== DHT && marker !== DQT && marker !== DRI && marker !== SOS) return reject("marker_not_allowed");

    if (bytes.length - offset < 2) return reject("truncated");
    const length = u16(bytes, offset);
    if (length < 2 || length > bytes.length - offset) return reject("truncated");
    const body = offset + 2;
    const end = offset + length;

    if (marker === SOF0 || marker === SOF2) {
      if (sawFrame) return reject("marker_not_allowed");
      if (length < 8) return reject("bad_header");
      const precision = bytes[body]!;
      height = u16(bytes, body + 1);
      width = u16(bytes, body + 3);
      const components = bytes[body + 5]!;
      if (precision !== 8 || (components !== 1 && components !== 3) || length !== 8 + 3 * components) return reject("bad_header");
      if (width < 1 || height < 1 || width > MAX_IMAGE_DIMENSION || height > MAX_IMAGE_DIMENSION) return reject("dimensions_out_of_range");
      sawFrame = true;
    } else if (marker === DRI) {
      if (length !== 4) return reject("bad_header");
    } else if (marker === SOS) {
      if (!sawFrame) return reject("chunk_order");
      const components = length >= 3 ? bytes[body]! : 0;
      if (components < 1 || components > 4 || length !== 6 + 2 * components) return reject("bad_header");
    }
    offset = end;

    if (marker === SOS) {
      scans++;
      // Entropy-coded data runs to the next marker that is not a stuffed 0xFF00 or a restart marker. One plain loop over the
      // bytes, with the pairs handled inline: a call per pair (indexOf) made data that is all FF00 or FFD0 pairs cost about
      // 20-30 ms of CPU on a 2 MB file (docs/evidence/upload-cpu.md).
      // Short gaps are walked byte by byte; a gap longer than 16 bytes is skipped with indexOf (native), so the calls are at
      // most one per 16 bytes whatever the data.
      const n = bytes.length;
      let i = offset;
      for (;;) {
        const near = Math.min(n, i + 16);
        while (i < near && bytes[i] !== 0xff) i++;
        if (i === near && i < n) {
          i = bytes.indexOf(0xff, i);
          if (i < 0) return reject("missing_end");
        }
        if (i + 1 >= n) return reject("missing_end");
        const following = bytes[i + 1]!;
        // 0x00 (stuffed) or 0xD0-0xD7 (RSTn).
        if (following === 0x00 || (following & 0xf8) === 0xd0) {
          i += 2;
          continue;
        }
        break;
      }
      offset = i;
    }
  }
}
