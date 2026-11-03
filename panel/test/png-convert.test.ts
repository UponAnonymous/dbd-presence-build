// FR-ADM-08 (owner request 2026-11-02): the panel converts palette, greyscale, 16-bit and colour-key PNGs to the 8-bit RGBA
// (or RGB) form the app shows, exactly and without the browser's image decoder, and leaves everything else to the usual checks.

import { describe, expect, it } from "vitest";
import { convertPngForApp, convertedText } from "../src/png-convert.ts";
import { buttonByLabel, chooseFile, imageFile, mount, settle } from "./fake.ts";

/** zlib through the same browser streams the panel uses (the panel has no Node types). */
async function through(data: Uint8Array, stream: CompressionStream | DecompressionStream): Promise<Uint8Array> {
  const input = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(data.slice());
      controller.close();
    },
  });
  return new Uint8Array(await new Response(input.pipeThrough(stream as unknown as ReadableWritablePair<Uint8Array, Uint8Array>)).arrayBuffer());
}
const deflate = (data: Uint8Array) => through(data, new CompressionStream("deflate"));
const inflate = (data: Uint8Array) => through(data, new DecompressionStream("deflate"));

function crc32(bytes: Uint8Array): number {
  let c = 0xffffffff;
  for (const b of bytes) {
    c ^= b;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  }
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, body: Uint8Array): Uint8Array {
  const out = new Uint8Array(12 + body.length);
  const view = new DataView(out.buffer);
  view.setUint32(0, body.length);
  for (let i = 0; i < 4; i++) out[4 + i] = type.charCodeAt(i);
  out.set(body, 8);
  view.setUint32(8 + body.length, crc32(out.subarray(4, 8 + body.length)));
  return out;
}

interface Spec {
  width: number;
  height: number;
  depth: number;
  colourType: number;
  /** Packed rows without filter bytes, one entry per row. */
  rows: number[][];
  palette?: number[];
  trns?: number[];
  extra?: Uint8Array[];
  idat?: Uint8Array;
}

async function png(spec: Spec): Promise<Uint8Array<ArrayBuffer>> {
  const ihdr = new Uint8Array(13);
  const view = new DataView(ihdr.buffer);
  view.setUint32(0, spec.width);
  view.setUint32(4, spec.height);
  ihdr[8] = spec.depth;
  ihdr[9] = spec.colourType;
  const raw = Uint8Array.from(spec.rows.flatMap((row, y) => [y % 2, ...row.map((v, i) => (y % 2 === 1 ? (v - (i >= 1 ? row[i - 1]! : 0)) & 0xff : v))]));
  // Odd rows use the Sub filter with a one-byte step, which is only correct for bpp 1: the tests that use it pass bpp-1 images.
  const parts = [new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr)];
  if (spec.palette) parts.push(chunk("PLTE", Uint8Array.from(spec.palette)));
  if (spec.trns) parts.push(chunk("tRNS", Uint8Array.from(spec.trns)));
  for (const extra of spec.extra ?? []) parts.push(extra);
  parts.push(chunk("IDAT", spec.idat ?? (await deflate(raw))), chunk("IEND", new Uint8Array(0)));
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

/** Decodes the converter's own output (8-bit, colour type 2 or 6, not interlaced) back to RGBA. */
async function decode(bytes: Uint8Array): Promise<{ width: number; height: number; colourType: number; chunks: string[]; rgba: number[] }> {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const chunks: string[] = [];
  const data: Uint8Array[] = [];
  let width = 0;
  let height = 0;
  let colourType = 0;
  for (let at = 8; at < bytes.length; ) {
    const length = view.getUint32(at);
    const type = String.fromCharCode(...bytes.subarray(at + 4, at + 8));
    const body = bytes.subarray(at + 8, at + 8 + length);
    expect(view.getUint32(at + 8 + length)).toBe(crc32(bytes.subarray(at + 4, at + 8 + length)));
    chunks.push(type);
    if (type === "IHDR") {
      width = view.getUint32(at + 8);
      height = view.getUint32(at + 12);
      expect(body[8]).toBe(8);
      colourType = body[9]!;
      expect(body[12]).toBe(0);
    }
    if (type === "IDAT") data.push(body);
    at += 12 + length;
  }
  const joined = new Uint8Array(data.reduce((n, d) => n + d.length, 0));
  data.reduce((at, d) => (joined.set(d, at), at + d.length), 0);
  const raw = await inflate(joined);
  const bpp = colourType === 6 ? 4 : 3;
  const stride = width * bpp;
  const pixels = new Uint8Array(stride * height);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)]!;
    for (let i = 0; i < stride; i++) {
      const x = raw[y * (stride + 1) + 1 + i]!;
      const a = i >= bpp ? pixels[y * stride + i - bpp]! : 0;
      const b = y > 0 ? pixels[(y - 1) * stride + i]! : 0;
      const c = y > 0 && i >= bpp ? pixels[(y - 1) * stride + i - bpp]! : 0;
      const p = a + b - c;
      const paeth = Math.abs(p - a) <= Math.abs(p - b) && Math.abs(p - a) <= Math.abs(p - c) ? a : Math.abs(p - b) <= Math.abs(p - c) ? b : c;
      pixels[y * stride + i] = (x + [0, a, b, (a + b) >> 1, paeth][filter]!) & 0xff;
    }
  }
  const rgba: number[] = [];
  for (let i = 0; i < width * height; i++) rgba.push(pixels[i * bpp]!, pixels[i * bpp + 1]!, pixels[i * bpp + 2]!, bpp === 4 ? pixels[i * bpp + 3]! : 255);
  return { width, height, colourType, chunks, rgba };
}

async function converted(bytes: Uint8Array) {
  const result = await convertPngForApp(bytes);
  if (result.kind !== "converted") throw new Error(`expected a conversion, got ${JSON.stringify(result)}`);
  return { ...(await decode(result.bytes)), from: result.from };
}

describe("PNG conversion for the app", () => {
  it("turns a palette PNG with transparency into 8-bit RGBA with the same colours and transparency, and nothing else", async () => {
    const out = await converted(
      await png({ width: 3, height: 2, depth: 8, colourType: 3, rows: [[0, 1, 2], [2, 2, 0]], palette: [255, 0, 0, 0, 255, 0, 0, 0, 255], trns: [0, 128], extra: [chunk("tEXt", new TextEncoder().encode("Comment\0hi"))] }),
    );
    expect(out.from).toBe("palette-based");
    expect(out.colourType).toBe(6);
    expect(out.chunks).toEqual(["IHDR", "IDAT", "IEND"]);
    expect(out.rgba).toEqual([255, 0, 0, 0, 0, 255, 0, 128, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 0, 0]);
  });

  it("reads 1-bit and 4-bit palette indexes across byte boundaries", async () => {
    const palette = Array.from({ length: 16 }, (_, i) => [i * 16, i, 255 - i]).flat();
    const four = await converted(await png({ width: 3, height: 1, depth: 4, colourType: 3, rows: [[0x1f, 0x70]], palette }));
    expect(four.rgba).toEqual([16, 1, 254, 255, 240, 15, 240, 255, 112, 7, 248, 255]);
    expect(four.colourType).toBe(2);
    const one = await converted(await png({ width: 9, height: 1, depth: 1, colourType: 0, rows: [[0b10100000, 0b10000000]] }));
    expect(one.from).toBe("greyscale, 1-bit");
    expect(one.rgba.filter((_, i) => i % 4 === 0)).toEqual([255, 0, 255, 0, 0, 0, 0, 0, 255]);
  });

  it("scales 16-bit samples to 8 bits and applies a 16-bit colour key exactly", async () => {
    const out = await converted(await png({ width: 2, height: 1, depth: 16, colourType: 2, rows: [[0x75, 0x30, 0, 0, 0xea, 0x60, 0xff, 0xff, 0x80, 0x80, 0, 0]], trns: [0x75, 0x30, 0, 0, 0xea, 0x60] }));
    expect(out.from).toBe("colour, 16-bit, with a transparent colour key");
    expect(out.rgba).toEqual([117, 0, 233, 0, 255, 128, 0, 255]); // 30000/257 = 116.7, 60000/257 = 233.5 (rounded half down: 233.46)
  });

  it("keeps grey with alpha and applies a grey colour key", async () => {
    expect((await converted(await png({ width: 2, height: 1, depth: 8, colourType: 4, rows: [[10, 20, 30, 40]] }))).rgba).toEqual([10, 10, 10, 20, 30, 30, 30, 40]);
    expect((await converted(await png({ width: 2, height: 1, depth: 8, colourType: 0, rows: [[5, 6]], trns: [0, 6] }))).rgba).toEqual([5, 5, 5, 255, 6, 6, 6, 0]);
  });

  it("de-interlaces an Adam7 image", async () => {
    // A 3 x 3 8-bit grey image; Adam7 stores pixel (0,0) in pass 1, (2,0) in pass 4, (0,2) in pass 5, (1,0),(1,2) in pass 6, row 1 in pass 7.
    const ihdr = new Uint8Array(13);
    new DataView(ihdr.buffer).setUint32(0, 3);
    new DataView(ihdr.buffer).setUint32(4, 3);
    ihdr[8] = 8;
    ihdr[12] = 1;
    const raw = Uint8Array.from([0, 1, 0, 3, 0, 7, 9, 0, 2, 0, 8, 0, 4, 5, 6]);
    const bytes = await png({ width: 3, height: 3, depth: 8, colourType: 0, rows: [], idat: await deflate(raw) });
    bytes.set(chunk("IHDR", ihdr), 8);
    const out = await converted(bytes);
    expect(out.rgba.filter((_, i) => i % 4 === 0)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9]);
  });

  it("leaves PNGs the app already accepts, animations, other files and oversized images to the usual checks", async () => {
    expect(await convertPngForApp(await png({ width: 1, height: 1, depth: 8, colourType: 6, rows: [[1, 2, 3, 4]] }))).toEqual({ kind: "unchanged" });
    expect(await convertPngForApp(await png({ width: 1, height: 1, depth: 8, colourType: 3, rows: [[0]], palette: [1, 2, 3], extra: [chunk("acTL", new Uint8Array(8))] }))).toEqual({ kind: "unchanged" });
    expect(await convertPngForApp(new Uint8Array([0xff, 0xd8, 0xff, 0xe0]))).toEqual({ kind: "unchanged" });
    expect(await convertPngForApp(await png({ width: 2000, height: 1, depth: 8, colourType: 0, rows: [] }))).toEqual({ kind: "unchanged" });
  });

  it("refuses damaged data, and data that inflates beyond what the header allows, without expanding it", async () => {
    const bomb = await deflate(new Uint8Array(50 * 1024 * 1024));
    expect(await convertPngForApp(await png({ width: 4, height: 4, depth: 8, colourType: 0, rows: [], idat: bomb }))).toEqual({ kind: "failed", reason: "png_damaged" });
    expect(await convertPngForApp(await png({ width: 4, height: 4, depth: 8, colourType: 0, rows: [], idat: new Uint8Array([1, 2, 3, 4]) }))).toEqual({ kind: "failed", reason: "png_damaged" });
    expect(await convertPngForApp(await png({ width: 2, height: 1, depth: 8, colourType: 3, rows: [[0, 5]], palette: [1, 2, 3] }))).toEqual({ kind: "failed", reason: "png_damaged" });
  });

  it("says in plain words what was converted", () => {
    expect(convertedText("palette-based")).toBe("Converted from a palette-based PNG to the 8-bit form the app shows; the picture and its transparency are unchanged.");
  });
});

describe("uploading a palette PNG in the panel", () => {
  it("converts it before it is sent and tells the owner", async () => {
    const { root, backend } = await mount("packs");
    buttonByLabel(root, "Edit Original").click();
    await settle();
    const palettePng = await png({ width: 2, height: 1, depth: 8, colourType: 3, rows: [[0, 1]], palette: [9, 8, 7, 6, 5, 4], trns: [0] });
    chooseFile(root.querySelector<HTMLInputElement>("#add-image-file")!, imageFile("art.png", palettePng));
    root.querySelector<HTMLSelectElement>("#add-image-slot")!.value = "character:the-hag";
    root.querySelector<HTMLSelectElement>("#add-image-slot")!.dispatchEvent(new Event("change"));
    root.querySelector('form[aria-label="Add an image"]')!.dispatchEvent(new Event("submit", { cancelable: true }));
    for (let i = 0; i < 20 && !backend.requests.some((r) => r.url.includes("/entries")); i++) await settle();
    const upload = backend.requests.find((r) => r.url === "/api/pack-files" && r.method === "POST")!;
    const sent = new Uint8Array(upload.body as ArrayBuffer | Uint8Array);
    expect((await decode(sent)).rgba).toEqual([9, 8, 7, 0, 6, 5, 4, 255]);
    await settle();
    expect(root.textContent).toContain("Converted from a palette-based PNG");
  });
});
