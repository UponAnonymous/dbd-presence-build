// Builders for PNG and JPEG test files. The strict parser checks structure only (it never decodes), so the image data
// here is filler of the right shape; the CRCs, lengths and markers are real.

import { crc32 } from "../src/images/strict.ts";

const enc = new TextEncoder();

export function concat(...parts: Uint8Array[]): Uint8Array {
  return concatList(parts);
}

export function concatList(parts: readonly Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

function be32(n: number): Uint8Array {
  return new Uint8Array([(n >>> 24) & 0xff, (n >>> 16) & 0xff, (n >>> 8) & 0xff, n & 0xff]);
}

export function chunk(type: string, data: Uint8Array = new Uint8Array(0), badCrc = false): Uint8Array {
  const typed = concat(enc.encode(type), data);
  const crc = crc32(typed, 0, typed.length) ^ (badCrc ? 1 : 0);
  return concat(be32(data.length), typed, be32(crc >>> 0));
}

export const PNG_SIG = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export function ihdr(width: number, height: number, depth = 8, colour = 6, interlace = 0): Uint8Array {
  return chunk("IHDR", concat(be32(width), be32(height), new Uint8Array([depth, colour, 0, 0, interlace])));
}

/** A valid PNG: signature, IHDR, the given middle chunks (default one IDAT), IEND. */
export function png(options: { width?: number; height?: number; colour?: number; depth?: number; middle?: Uint8Array[]; idatBytes?: number } = {}): Uint8Array {
  const idat = chunk("IDAT", filler(options.idatBytes ?? 64, 7));
  return concat(PNG_SIG, ihdr(options.width ?? 256, options.height ?? 256, options.depth ?? 8, options.colour ?? 6), ...(options.middle ?? [idat]), chunk("IEND"));
}

/** Deterministic pseudo-random bytes. */
export function filler(length: number, seed = 1): Uint8Array {
  const out = new Uint8Array(length);
  let x = seed >>> 0 || 1;
  for (let i = 0; i < length; i++) {
    x ^= x << 13;
    x ^= x >>> 17;
    x ^= x << 5;
    out[i] = x & 0xff;
  }
  return out;
}

function segment(marker: number, data: Uint8Array): Uint8Array {
  const length = data.length + 2;
  return concat(new Uint8Array([0xff, marker, length >> 8, length & 0xff]), data);
}

/** Entropy-coded filler: no 0xFF except stuffed 0xFF00 pairs, with a restart marker in the middle. */
export function entropy(length: number, seed = 3): Uint8Array {
  const raw = filler(length, seed);
  const out: number[] = [];
  for (let i = 0; i < raw.length; i++) {
    const b = raw[i]!;
    out.push(b);
    if (b === 0xff) out.push(0x00);
    if (i === Math.floor(raw.length / 2)) out.push(0xff, 0xd0);
  }
  return new Uint8Array(out);
}

export const SOF0 = 0xc0;
export const SOF2 = 0xc2;

export function sof(marker: number, width: number, height: number, components = 3, precision = 8): Uint8Array {
  const comps: number[] = [];
  for (let i = 1; i <= components; i++) comps.push(i, 0x11, 0);
  return segment(marker, new Uint8Array([precision, height >> 8, height & 0xff, width >> 8, width & 0xff, components, ...comps]));
}

export const DQT = segment(0xdb, concat(new Uint8Array([0]), filler(64, 9).map((b) => (b % 50) + 1)));
export const DHT = segment(0xc4, concat(new Uint8Array([0]), new Uint8Array(16).fill(1), filler(16, 11).map((b) => b & 0x7f)));
export const DRI = segment(0xdd, new Uint8Array([0, 8]));

export function sos(components = 3): Uint8Array {
  const comps: number[] = [];
  for (let i = 1; i <= components; i++) comps.push(i, 0);
  return segment(0xda, new Uint8Array([components, ...comps, 0, 63, 0]));
}

export function app(n: number, data: Uint8Array = enc.encode("JFIF\0")): Uint8Array {
  return segment(0xe0 + n, data);
}

export function com(text: string): Uint8Array {
  return segment(0xfe, enc.encode(text));
}

export const SOI = new Uint8Array([0xff, 0xd8]);
export const EOI = new Uint8Array([0xff, 0xd9]);

/** A valid baseline (or progressive) JPEG. */
export function jpeg(options: { width?: number; height?: number; progressive?: boolean; entropyBytes?: number; before?: Uint8Array[] } = {}): Uint8Array {
  const frame = sof(options.progressive ? SOF2 : SOF0, options.width ?? 512, options.height ?? 512);
  const scan = concat(sos(), entropy(options.entropyBytes ?? 256));
  const scans = options.progressive ? [scan, DHT, concat(sos(), entropy(128, 5))] : [scan];
  return concat(SOI, ...(options.before ?? []), DQT, frame, DHT, DRI, ...scans, EOI);
}

/**
 * A baseline JPEG whose entropy-coded data is `pattern` repeated to fill `bytes` (for example FF 00 or FF D0 pairs: the parser's
 * per-pair worst case). A final 0xFF that would swallow the EOI marker is replaced.
 */
export function denseJpeg(pattern: readonly number[], bytes: number): Uint8Array {
  const head = concat(SOI, DQT, sof(SOF0, 1024, 1024), DHT, sos());
  const length = bytes - head.length - 2;
  const data = new Uint8Array(length);
  for (let i = 0; i < length; i++) data[i] = pattern[i % pattern.length]!;
  if (data[length - 1] === 0xff) data[length - 1] = 0x01;
  return concat(head, data, EOI);
}
