// Brings a PNG the app would refuse into the form it accepts (owner request 2026-11-02, FR-ADM-08, ADR-0013): palette-based,
// greyscale, 16-bit and transparent-colour-key (tRNS) PNGs become 8-bit RGBA (or 8-bit RGB when no pixel is see-through).
// Before this the owner had to re-export such files by hand ("png_not_rgb8").
//
// How: the panel's own code reads the chunks, inflates the image data with the browser's zlib (DecompressionStream), undoes
// the row filters, maps every pixel to 8-bit RGBA and writes a new PNG with only IHDR, IDAT and IEND. The browser's image
// decoder is never used and nothing is drawn or shown (ADR-0014: before an upload the panel shows name, size and pixel size
// only). Palette colours, 8-bit samples and transparency carry over exactly; 16-bit samples are rounded to the nearest 8-bit value and low bit depths are scaled up.
// Limits are checked before any work: at most MAX_DIMENSION pixels a side, and the inflated data may not exceed the exact size
// the header promises, so a small file cannot expand without bound. The service checks the result again as it checks any upload.

import { MAX_DIMENSION, MAX_FILE } from "./image-check.ts";

export type PngConversion =
  /** Not a PNG, already in the app's form, or something this code leaves to the service's checks (animation, a bad header). */
  | { kind: "unchanged" }
  | { kind: "converted"; bytes: Uint8Array<ArrayBuffer>; from: string }
  /** A service `image_rejected` reason, so the panel explains it with the same words. */
  | { kind: "failed"; reason: string };

interface Header {
  width: number;
  height: number;
  depth: number;
  colourType: number;
  interlace: number;
}

interface Parsed {
  header: Header;
  palette: Uint8Array | null;
  transparency: Uint8Array | null;
  data: Uint8Array[];
  animated: boolean;
}

const SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
/** Bit depths allowed for each colour type (PNG specification, table 11.1). */
const DEPTHS: Readonly<Record<number, readonly number[]>> = { 0: [1, 2, 4, 8, 16], 2: [8, 16], 3: [1, 2, 4, 8], 4: [8, 16], 6: [8, 16] };
const CHANNELS: Readonly<Record<number, number>> = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 };
// Adam7 passes: first column, first row, column step, row step.
const ADAM7 = [
  [0, 0, 8, 8],
  [4, 0, 8, 8],
  [0, 4, 4, 8],
  [2, 0, 4, 4],
  [0, 2, 2, 4],
  [1, 0, 2, 2],
  [0, 1, 1, 2],
] as const;

const u32 = (b: Uint8Array, at: number) => ((b[at]! << 24) | (b[at + 1]! << 16) | (b[at + 2]! << 8) | b[at + 3]!) >>> 0;
const type4 = (b: Uint8Array, at: number) => String.fromCharCode(b[at]!, b[at + 1]!, b[at + 2]!, b[at + 3]!);

function parse(bytes: Uint8Array): Parsed | null {
  if (bytes.length < 8 || !SIGNATURE.every((v, i) => bytes[i] === v)) return null;
  let header: Header | null = null;
  let palette: Uint8Array | null = null;
  let transparency: Uint8Array | null = null;
  let animated = false;
  const data: Uint8Array[] = [];
  let at = 8;
  while (at + 12 <= bytes.length) {
    const length = u32(bytes, at);
    if (length > bytes.length - at - 12) return null;
    const type = type4(bytes, at + 4);
    const body = bytes.subarray(at + 8, at + 8 + length);
    if (header === null && type !== "IHDR") return null;
    if (type === "IHDR") {
      if (header !== null || length !== 13) return null;
      header = { width: u32(body, 0), height: u32(body, 4), depth: body[8]!, colourType: body[9]!, interlace: body[12]! };
      if (body[10] !== 0 || body[11] !== 0) return null;
    } else if (type === "PLTE") palette = body;
    else if (type === "tRNS") transparency = body;
    else if (type === "IDAT") data.push(body);
    else if (type === "acTL" || type === "fcTL" || type === "fdAT") animated = true;
    else if (type === "IEND") break;
    at += 12 + length;
  }
  return header === null ? null : { header, palette, transparency, data, animated };
}

/** True when the app's rule (8-bit, colour type 2 or 6, no tRNS) already holds. */
function alreadyAccepted(p: Parsed): boolean {
  return p.header.depth === 8 && (p.header.colourType === 2 || p.header.colourType === 6) && p.transparency === null;
}

function describe(p: Parsed): string {
  const { colourType, depth } = p.header;
  const kind = colourType === 3 ? "palette-based" : colourType === 0 || colourType === 4 ? "greyscale" : "colour";
  const parts = [kind, depth !== 8 ? `${depth}-bit` : null, p.transparency !== null && colourType !== 3 ? "with a transparent colour key" : null];
  return parts.filter(Boolean).join(", ");
}

interface Pass {
  x0: number;
  y0: number;
  dx: number;
  dy: number;
  width: number;
  height: number;
  rowBytes: number;
}

function passes(h: Header): Pass[] {
  const bits = CHANNELS[h.colourType]! * h.depth;
  const layout = h.interlace === 1 ? ADAM7 : ([[0, 0, 1, 1]] as const);
  const out: Pass[] = [];
  for (const [x0, y0, dx, dy] of layout) {
    const width = Math.ceil((h.width - x0) / dx);
    const height = Math.ceil((h.height - y0) / dy);
    if (width > 0 && height > 0) out.push({ x0, y0, dx, dy, width, height, rowBytes: Math.ceil((width * bits) / 8) });
  }
  return out;
}

/** Inflates zlib data, refusing more than `limit` bytes (the size the header promises). */
async function inflate(parts: readonly Uint8Array[], limit: number): Promise<Uint8Array | null> {
  const input = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const part of parts) controller.enqueue(part.slice());
      controller.close();
    },
  });
  const reader = input.pipeThrough(new DecompressionStream("deflate") as unknown as ReadableWritablePair<Uint8Array, Uint8Array>).getReader();
  const out = new Uint8Array(limit);
  let filled = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (filled + value.length > limit) {
        await reader.cancel();
        return null;
      }
      out.set(value, filled);
      filled += value.length;
    }
  } catch {
    // hygiene: allow-swallow A damaged or truncated zlib stream is reported to the owner as a damaged file by the caller.
    return null;
  }
  return filled === limit ? out : null;
}

function paeth(a: number, b: number, c: number): number {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

/** Undoes the row filters of one pass; returns null for an unknown filter type. */
function unfilter(raw: Uint8Array, offset: number, pass: Pass, bpp: number): Uint8Array | null {
  const out = new Uint8Array(pass.rowBytes * pass.height);
  for (let y = 0; y < pass.height; y++) {
    const filter = raw[offset + y * (pass.rowBytes + 1)]!;
    const src = offset + y * (pass.rowBytes + 1) + 1;
    const row = y * pass.rowBytes;
    const prev = row - pass.rowBytes;
    for (let i = 0; i < pass.rowBytes; i++) {
      const x = raw[src + i]!;
      const a = i >= bpp ? out[row + i - bpp]! : 0;
      const b = y > 0 ? out[prev + i]! : 0;
      const c = y > 0 && i >= bpp ? out[prev + i - bpp]! : 0;
      let v: number;
      if (filter === 0) v = x;
      else if (filter === 1) v = x + a;
      else if (filter === 2) v = x + b;
      else if (filter === 3) v = x + ((a + b) >> 1);
      else if (filter === 4) v = x + paeth(a, b, c);
      else return null;
      out[row + i] = v & 0xff;
    }
  }
  return out;
}

/** Converts the parsed image to 8-bit RGBA pixels, or null when the data does not fit the header. */
async function toRgba(p: Parsed): Promise<Uint8Array | null> {
  const h = p.header;
  const channels = CHANNELS[h.colourType]!;
  const bpp = Math.max(1, Math.ceil((channels * h.depth) / 8));
  const layout = passes(h);
  const expected = layout.reduce((n, pass) => n + pass.height * (pass.rowBytes + 1), 0);
  const raw = await inflate(p.data, expected);
  if (raw === null) return null;

  const palette = p.palette;
  if (h.colourType === 3 && (palette === null || palette.length % 3 !== 0 || palette.length === 0)) return null;
  const trns = p.transparency;
  const maxSample = (1 << h.depth) - 1;
  const to8 = (v: number) => (h.depth === 8 ? v : h.depth === 16 ? Math.round(v / 257) : Math.round((v * 255) / maxSample));
  const greyKey = h.colourType === 0 && trns !== null && trns.length >= 2 ? (trns[0]! << 8) | trns[1]! : -1;
  const rgbKey = h.colourType === 2 && trns !== null && trns.length >= 6 ? [(trns[0]! << 8) | trns[1]!, (trns[2]! << 8) | trns[3]!, (trns[4]! << 8) | trns[5]!] : null;

  const rgba = new Uint8Array(h.width * h.height * 4);
  let offset = 0;
  for (const pass of layout) {
    const rows = unfilter(raw, offset, pass, bpp);
    if (rows === null) return null;
    offset += pass.height * (pass.rowBytes + 1);
    const sample = (row: number, index: number): number => {
      if (h.depth === 8) return rows[row + index]!;
      if (h.depth === 16) return (rows[row + index * 2]! << 8) | rows[row + index * 2 + 1]!;
      const bit = index * h.depth;
      return (rows[row + (bit >> 3)]! >> (8 - h.depth - (bit & 7))) & maxSample;
    };
    for (let y = 0; y < pass.height; y++) {
      const row = y * pass.rowBytes;
      for (let x = 0; x < pass.width; x++) {
        const o = ((pass.y0 + y * pass.dy) * h.width + pass.x0 + x * pass.dx) * 4;
        const s = x * channels;
        if (h.colourType === 3) {
          const index = sample(row, s);
          if (index * 3 + 2 >= palette!.length) return null;
          rgba[o] = palette![index * 3]!;
          rgba[o + 1] = palette![index * 3 + 1]!;
          rgba[o + 2] = palette![index * 3 + 2]!;
          rgba[o + 3] = trns !== null && index < trns.length ? trns[index]! : 255;
        } else if (h.colourType === 0 || h.colourType === 4) {
          const grey = sample(row, s);
          const g = to8(grey);
          rgba[o] = g;
          rgba[o + 1] = g;
          rgba[o + 2] = g;
          rgba[o + 3] = h.colourType === 4 ? to8(sample(row, s + 1)) : grey === greyKey ? 0 : 255;
        } else {
          const r = sample(row, s);
          const g = sample(row, s + 1);
          const b = sample(row, s + 2);
          rgba[o] = to8(r);
          rgba[o + 1] = to8(g);
          rgba[o + 2] = to8(b);
          rgba[o + 3] = h.colourType === 6 ? to8(sample(row, s + 3)) : rgbKey !== null && r === rgbKey[0] && g === rgbKey[1] && b === rgbKey[2] ? 0 : 255;
        }
      }
    }
  }
  return rgba;
}

// ---------------------------------------------------------------------------------------------------------------
// Writing

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(parts: readonly Uint8Array[]): number {
  let c = 0xffffffff;
  for (const part of parts) for (let i = 0; i < part.length; i++) c = CRC_TABLE[(c ^ part[i]!) & 0xff]! ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, body: Uint8Array): Uint8Array {
  const out = new Uint8Array(12 + body.length);
  const view = new DataView(out.buffer);
  view.setUint32(0, body.length);
  const name = new Uint8Array([...type].map((ch) => ch.charCodeAt(0)));
  out.set(name, 4);
  out.set(body, 8);
  view.setUint32(8 + body.length, crc32([name, body]));
  return out;
}

/** Filters every row with the filter whose output has the smallest sum of absolute values (the usual PNG heuristic). */
function filterRows(pixels: Uint8Array, width: number, height: number, bpp: number): Uint8Array {
  const stride = width * bpp;
  const out = new Uint8Array(height * (stride + 1));
  const candidate = new Uint8Array(stride);
  const best = new Uint8Array(stride);
  for (let y = 0; y < height; y++) {
    const row = y * stride;
    let bestSum = Infinity;
    let bestFilter = 0;
    for (let filter = 0; filter <= 4; filter++) {
      let sum = 0;
      for (let i = 0; i < stride; i++) {
        const x = pixels[row + i]!;
        const a = i >= bpp ? pixels[row + i - bpp]! : 0;
        const b = y > 0 ? pixels[row - stride + i]! : 0;
        const c = y > 0 && i >= bpp ? pixels[row - stride + i - bpp]! : 0;
        const predicted = filter === 0 ? 0 : filter === 1 ? a : filter === 2 ? b : filter === 3 ? (a + b) >> 1 : paeth(a, b, c);
        const v = (x - predicted) & 0xff;
        candidate[i] = v;
        sum += v < 128 ? v : 256 - v;
        if (sum >= bestSum) break;
      }
      if (sum < bestSum) {
        bestSum = sum;
        bestFilter = filter;
        best.set(candidate);
      }
    }
    out[y * (stride + 1)] = bestFilter;
    out.set(best, y * (stride + 1) + 1);
  }
  return out;
}

async function deflate(data: Uint8Array): Promise<Uint8Array> {
  const input = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(data);
      controller.close();
    },
  });
  const reader = input.pipeThrough(new CompressionStream("deflate") as unknown as ReadableWritablePair<Uint8Array, Uint8Array>).getReader();
  const parts: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    parts.push(value);
    total += value.length;
  }
  const out = new Uint8Array(total);
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.length;
  }
  return out;
}

async function encode(rgba: Uint8Array, width: number, height: number): Promise<Uint8Array<ArrayBuffer>> {
  let opaque = true;
  for (let i = 3; i < rgba.length; i += 4) {
    if (rgba[i] !== 255) {
      opaque = false;
      break;
    }
  }
  let pixels = rgba;
  if (opaque) {
    pixels = new Uint8Array(width * height * 3);
    for (let i = 0, j = 0; i < rgba.length; i += 4, j += 3) {
      pixels[j] = rgba[i]!;
      pixels[j + 1] = rgba[i + 1]!;
      pixels[j + 2] = rgba[i + 2]!;
    }
  }
  const ihdr = new Uint8Array(13);
  const view = new DataView(ihdr.buffer);
  view.setUint32(0, width);
  view.setUint32(4, height);
  ihdr[8] = 8;
  ihdr[9] = opaque ? 2 : 6;
  const idat = await deflate(filterRows(pixels, width, height, opaque ? 3 : 4));
  const parts = [new Uint8Array(SIGNATURE), chunk("IHDR", ihdr), chunk("IDAT", idat), chunk("IEND", new Uint8Array(0))];
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const part of parts) {
    out.set(part, at);
    at += part.length;
  }
  return out;
}

/**
 * Converts a PNG the app would refuse for its colour form into 8-bit RGBA (or RGB). Anything else, including PNGs this code
 * cannot judge (animation, an unreadable header, a size over the limit), comes back unchanged for the usual checks to explain.
 */
export async function convertPngForApp(bytes: Uint8Array): Promise<PngConversion> {
  const parsed = parse(bytes);
  if (parsed === null || parsed.animated || alreadyAccepted(parsed)) return { kind: "unchanged" };
  const h = parsed.header;
  if (!(DEPTHS[h.colourType] ?? []).includes(h.depth) || h.interlace > 1) return { kind: "unchanged" };
  if (h.width < 1 || h.height < 1 || h.width > MAX_DIMENSION || h.height > MAX_DIMENSION) return { kind: "unchanged" };
  const rgba = await toRgba(parsed);
  if (rgba === null) return { kind: "failed", reason: "png_damaged" };
  const out = await encode(rgba, h.width, h.height);
  if (out.length > MAX_FILE) return { kind: "failed", reason: "too_large" };
  return { kind: "converted", bytes: out, from: describe(parsed) };
}

/** The words the panel shows after a conversion. */
export function convertedText(from: string): string {
  return `Converted from a ${from} PNG to the 8-bit form the app shows; the picture and its transparency are unchanged.`;
}
