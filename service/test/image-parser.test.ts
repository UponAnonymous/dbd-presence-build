// FR-ONL-09 / PLAN_V2 §3.5 (the strict PNG and JPEG parsers used for owner pack images and, from V2-S5, uploads):
// chunk and marker allow-lists, CRCs, trailing data, dimensions, size, and fuzzing (never throws, never accepts a
// file that breaks a rule). QUALITY_GATES V5.1 and V5.4 (CPU time recorded in docs/evidence/upload-cpu.md).

import { describe, expect, it } from "vitest";
import { MAX_IMAGE_BYTES, crc32, inspectImage, parseJpeg, parsePng } from "../src/images/strict.ts";
import { CPU_BUDGET_MS, bestMedian } from "./cpu.ts";
import { DHT, DQT, DRI, EOI, PNG_SIG, SOF0, SOI, app, chunk, com, concat, concatList, denseJpeg, entropy, filler, ihdr, jpeg, png, sof, sos } from "./images.ts";

const reason = (bytes: Uint8Array) => {
  const r = inspectImage(bytes);
  return r.ok ? "ok" : r.reason;
};

describe("PNG", () => {
  it("computes the standard CRC-32 (known answers, every length around the 8-byte stride)", () => {
    const text = new TextEncoder().encode("123456789");
    expect(crc32(text, 0, text.length)).toBe(0xcbf43926);
    const iend = new TextEncoder().encode("IEND");
    expect(crc32(iend, 0, 4)).toBe(0xae426082);
    // A byte-at-a-time reference.
    const reference = (bytes: Uint8Array) => {
      let c = 0xffffffff;
      for (const b of bytes) {
        c ^= b;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      }
      return (c ^ 0xffffffff) >>> 0;
    };
    for (let length = 0; length <= 40; length++) {
      const bytes = filler(length + 3, length + 1);
      expect(crc32(bytes, 3, bytes.length)).toBe(reference(bytes.slice(3)));
    }
  });

  it("accepts a plain RGBA file and reports its size", () => {
    expect(inspectImage(png({ width: 1024, height: 512 }))).toEqual({ ok: true, info: { format: "png", width: 1024, height: 512 } });
  });

  it("accepts the allow-listed chunks in their places: PLTE and tRNS before IDAT, several consecutive IDATs", () => {
    const palette = chunk("PLTE", filler(3 * 16, 5));
    const trns = chunk("tRNS", new Uint8Array([0, 255, 128]));
    const idat = chunk("IDAT", filler(40, 2));
    const file = concat(PNG_SIG, ihdr(64, 64, 4, 3), palette, trns, idat, idat, chunk("IEND"));
    expect(reason(file)).toBe("ok");
    expect(reason(concat(PNG_SIG, ihdr(8, 8, 8, 2), chunk("tRNS", new Uint8Array(6)), idat, chunk("IEND")))).toBe("ok");
    expect(reason(concat(PNG_SIG, ihdr(8, 8, 16, 0), idat, chunk("IEND")))).toBe("ok");
  });

  it.each(["tEXt", "zTXt", "iTXt", "eXIf", "iCCP", "sRGB", "gAMA", "pHYs", "tIME"])("refuses the metadata chunk %s", (type) => {
    expect(reason(png({ middle: [chunk(type, filler(12)), chunk("IDAT", filler(30))] }))).toBe("metadata_not_allowed");
  });

  it.each(["acTL", "fcTL", "fdAT"])("refuses the animation chunk %s", (type) => {
    expect(reason(png({ middle: [chunk(type, filler(26)), chunk("IDAT", filler(30))] }))).toBe("animation_not_allowed");
  });

  it("refuses unknown and private chunks", () => {
    expect(reason(png({ middle: [chunk("prVt", filler(4)), chunk("IDAT", filler(30))] }))).toBe("chunk_not_allowed");
    expect(reason(png({ middle: [chunk("IDAT", filler(30)), chunk("zzzz")] }))).toBe("chunk_not_allowed");
  });

  it("checks every CRC", () => {
    expect(reason(png({ middle: [chunk("IDAT", filler(30), true)] }))).toBe("bad_checksum");
    expect(reason(concat(PNG_SIG, ihdr(4, 4).map((b, i, a) => (i === a.length - 1 ? b ^ 1 : b)), chunk("IDAT", filler(9)), chunk("IEND")))).toBe("bad_checksum");
    expect(reason(concat(PNG_SIG, ihdr(4, 4), chunk("IDAT", filler(9)), chunk("IEND", new Uint8Array(0), true)))).toBe("bad_checksum");
  });

  it("refuses anything after IEND, a second IEND, a missing IEND and a file without IDAT", () => {
    expect(reason(concat(png(), new Uint8Array([0])))).toBe("trailing_data");
    expect(reason(concat(png(), chunk("IEND")))).toBe("trailing_data");
    expect(reason(concat(PNG_SIG, ihdr(4, 4), chunk("IDAT", filler(9))))).toBe("missing_end");
    expect(reason(concat(PNG_SIG, ihdr(4, 4), chunk("IEND")))).toBe("no_image_data");
  });

  it("refuses bad order: IHDR not first, IDAT runs split, PLTE after IDAT, palette image without PLTE", () => {
    expect(reason(concat(PNG_SIG, chunk("IDAT", filler(9)), ihdr(4, 4), chunk("IEND")))).toBe("bad_header");
    expect(reason(png({ middle: [chunk("IDAT", filler(9)), chunk("PLTE", filler(9)), chunk("IDAT", filler(9))] }))).toBe("chunk_order");
    expect(reason(concat(PNG_SIG, ihdr(4, 4, 8, 3), chunk("IDAT", filler(9)), chunk("IEND")))).toBe("chunk_order");
    expect(reason(concat(PNG_SIG, ihdr(4, 4, 8, 0), chunk("PLTE", filler(9)), chunk("IDAT", filler(9)), chunk("IEND")))).toBe("chunk_order");
    expect(reason(concat(PNG_SIG, ihdr(4, 4), ihdr(4, 4), chunk("IDAT", filler(9)), chunk("IEND")))).toBe("chunk_order");
  });

  it("refuses invalid headers: bit depth and colour type combinations, compression, filter, interlace", () => {
    expect(reason(png({ depth: 4, colour: 6 }))).toBe("bad_header");
    expect(reason(png({ depth: 8, colour: 5 }))).toBe("bad_header");
    const header = (bytes: number[]) => concat(PNG_SIG, chunk("IHDR", new Uint8Array([0, 0, 0, 4, 0, 0, 0, 4, ...bytes])), chunk("IDAT", filler(9)), chunk("IEND"));
    expect(reason(header([8, 6, 1, 0, 0]))).toBe("bad_header");
    expect(reason(header([8, 6, 0, 1, 0]))).toBe("bad_header");
    expect(reason(header([8, 6, 0, 0, 2]))).toBe("bad_header");
    expect(reason(header([8, 6, 0, 0, 1]))).toBe("ok");
  });

  it("refuses dimensions of 0 or above 1024", () => {
    expect(reason(png({ width: 1025, height: 10 }))).toBe("dimensions_out_of_range");
    expect(reason(png({ width: 10, height: 1025 }))).toBe("dimensions_out_of_range");
    expect(reason(png({ width: 0, height: 10 }))).toBe("dimensions_out_of_range");
    expect(reason(png({ width: 0x7fffffff, height: 10 }))).toBe("dimensions_out_of_range");
  });

  it("refuses a chunk length running past the end", () => {
    const file = png();
    expect(reason(file.slice(0, file.length - 13))).toBe("truncated");
    const lying = png();
    lying[33] = 0x7f; // the IDAT length's top byte
    expect(reason(lying)).toBe("truncated");
  });
});

describe("JPEG", () => {
  it("accepts baseline and progressive files and reports their size", () => {
    expect(inspectImage(jpeg({ width: 800, height: 600 }))).toEqual({ ok: true, info: { format: "jpeg", width: 800, height: 600 } });
    expect(inspectImage(jpeg({ width: 1024, height: 1024, progressive: true }))).toEqual({ ok: true, info: { format: "jpeg", width: 1024, height: 1024 } });
  });

  it("accepts a greyscale (one component) frame and fill bytes before a marker", () => {
    const file = concat(SOI, DQT, sof(SOF0, 64, 64, 1), DHT, sos(1), entropy(40), new Uint8Array([0xff, 0xff]), EOI);
    expect(reason(file)).toBe("ok");
  });

  it.each([0, 1, 2, 13, 14, 15])("refuses APP%i (EXIF, ICC, JFIF and every other application segment)", (n) => {
    expect(reason(jpeg({ before: [app(n)] }))).toBe("metadata_not_allowed");
  });

  it("refuses comments", () => {
    expect(reason(jpeg({ before: [com("made with something")] }))).toBe("metadata_not_allowed");
  });

  it.each([0xc1, 0xc3, 0xc5, 0xc9, 0xcc, 0xdc, 0xde, 0xdf, 0xf0, 0x01])("refuses the marker 0x%s", (marker) => {
    const segmentBytes = concat(new Uint8Array([0xff, marker, 0, 4]), new Uint8Array([0, 0]));
    expect(reason(jpeg({ before: [segmentBytes] }))).toBe("marker_not_allowed");
  });

  it("refuses anything after EOI, a missing EOI and a file with no scan", () => {
    expect(reason(concat(jpeg(), new Uint8Array([0])))).toBe("trailing_data");
    expect(reason(concat(jpeg(), EOI))).toBe("trailing_data");
    const file = jpeg();
    expect(reason(file.slice(0, file.length - 2))).toBe("missing_end");
    expect(reason(concat(SOI, DQT, sof(SOF0, 8, 8), EOI))).toBe("no_image_data");
  });

  it("refuses a scan before the frame, two frames and a restart marker outside a scan", () => {
    expect(reason(concat(SOI, DQT, DHT, sos(), entropy(20), sof(SOF0, 8, 8), EOI))).toBe("chunk_order");
    expect(reason(concat(SOI, sof(SOF0, 8, 8), sof(SOF0, 8, 8), sos(), entropy(20), EOI))).toBe("marker_not_allowed");
    expect(reason(concat(SOI, new Uint8Array([0xff, 0xd0]), sof(SOF0, 8, 8), sos(), entropy(20), EOI))).toBe("marker_not_allowed");
  });

  it("refuses 12-bit precision, CMYK frames, bad segment lengths and DRI of the wrong length", () => {
    expect(reason(concat(SOI, sof(SOF0, 8, 8, 3, 12), sos(), entropy(20), EOI))).toBe("bad_header");
    expect(reason(concat(SOI, sof(SOF0, 8, 8, 4), sos(4), entropy(20), EOI))).toBe("bad_header");
    expect(reason(concat(SOI, new Uint8Array([0xff, 0xdd, 0, 6, 0, 0, 0, 0]), sof(SOF0, 8, 8), sos(), entropy(20), EOI))).toBe("bad_header");
    expect(reason(concat(SOI, new Uint8Array([0xff, 0xdb, 0, 1]), EOI))).toBe("truncated");
    expect(reason(concat(SOI, new Uint8Array([0xff, 0xdb, 0x7f, 0xff, 0]), EOI))).toBe("truncated");
    expect(reason(concat(SOI, DRI, new Uint8Array([0x00]), EOI))).toBe("bad_header");
  });

  it("refuses dimensions of 0 or above 1024 (0 would need a DNL marker, which is refused too)", () => {
    expect(reason(jpeg({ width: 1025, height: 8 }))).toBe("dimensions_out_of_range");
    expect(reason(jpeg({ width: 8, height: 1025 }))).toBe("dimensions_out_of_range");
    expect(reason(jpeg({ width: 8, height: 0 }))).toBe("dimensions_out_of_range");
  });
});

describe("both formats", () => {
  it("refuses an empty body, unknown formats (GIF, WebP, SVG, BMP) and anything over 2 MB", () => {
    expect(reason(new Uint8Array(0))).toBe("empty");
    expect(reason(new TextEncoder().encode("GIF89a\u0001\u0000\u0001\u0000"))).toBe("unknown_format");
    expect(reason(new TextEncoder().encode("RIFF\u0000\u0000\u0000\u0000WEBPVP8 "))).toBe("unknown_format");
    expect(reason(new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"/>'))).toBe("unknown_format");
    expect(reason(new TextEncoder().encode("BM...."))).toBe("unknown_format");
    expect(reason(new Uint8Array(MAX_IMAGE_BYTES + 1))).toBe("too_large");
    expect(parsePng(new Uint8Array(MAX_IMAGE_BYTES + 1)).ok).toBe(false);
    expect(parseJpeg(new Uint8Array(MAX_IMAGE_BYTES + 1)).ok).toBe(false);
  });

  it("accepts files right at the 2 MB cap", () => {
    const big = png({ idatBytes: MAX_IMAGE_BYTES - 8 - 25 - 12 - 12 });
    expect(big.length).toBe(MAX_IMAGE_BYTES);
    expect(reason(big)).toBe("ok");
    expect(reason(png({ idatBytes: MAX_IMAGE_BYTES - 8 - 25 - 12 - 12 + 1 }))).toBe("too_large");
  });

  it("refuses every truncation of a valid file", () => {
    for (const file of [png({ middle: [chunk("PLTE", filler(9)), chunk("IDAT", filler(20)), chunk("IDAT", filler(5))], colour: 2 }), jpeg({ entropyBytes: 40 }), jpeg({ progressive: true, entropyBytes: 30 })]) {
      for (let length = 0; length < file.length; length++) expect(inspectImage(file.slice(0, length)).ok).toBe(false);
    }
  });

  it("fuzz: random corruption never throws, and anything accepted still keeps every rule", () => {
    let seed = 0x9e3779b9;
    const random = () => {
      seed ^= seed << 13;
      seed ^= seed >>> 17;
      seed ^= seed << 5;
      return (seed >>> 0) / 0x100000000;
    };
    const seeds = [png(), png({ colour: 3, depth: 8, middle: [chunk("PLTE", filler(30)), chunk("tRNS", filler(4)), chunk("IDAT", filler(50))] }), jpeg(), jpeg({ progressive: true })];
    let accepted = 0;
    let checked = 0;
    for (let round = 0; round < 4000; round++) {
      const base = seeds[round % seeds.length]!;
      const file = Uint8Array.from(base);
      const mutation = Math.floor(random() * 4);
      let mutated: Uint8Array = file;
      if (mutation === 0) for (let i = 0; i < 1 + Math.floor(random() * 4); i++) file[Math.floor(random() * file.length)]! ^= 1 << Math.floor(random() * 8);
      else if (mutation === 1) mutated = file.slice(0, Math.floor(random() * file.length));
      else if (mutation === 2) {
        const at = Math.floor(random() * file.length);
        mutated = concat(file.slice(0, at), filler(1 + Math.floor(random() * 16), round + 1), file.slice(at));
      } else {
        const at = Math.floor(random() * file.length);
        mutated = concat(file.slice(0, at), file.slice(at + 1 + Math.floor(random() * 8)));
      }
      let result: ReturnType<typeof inspectImage> | undefined;
      expect(() => (result = inspectImage(mutated))).not.toThrow();
      checked++;
      if (result!.ok) {
        accepted++;
        const { info } = result!;
        expect(info.width).toBeGreaterThanOrEqual(1);
        expect(info.width).toBeLessThanOrEqual(1024);
        expect(info.height).toBeGreaterThanOrEqual(1);
        expect(info.height).toBeLessThanOrEqual(1024);
        // An accepted PNG still ends with an IEND chunk; an accepted JPEG still ends with EOI.
        if (info.format === "png") expect([...mutated.slice(-8, -4)]).toEqual([0x49, 0x45, 0x4e, 0x44]);
        else expect([...mutated.slice(-2)]).toEqual([0xff, 0xd9]);
      }
    }
    expect(checked).toBe(4000);
    // Only flips inside JPEG entropy data or filler can survive; everything structural is caught.
    expect(accepted).toBeLessThan(checked / 2);
  });
});

describe("CPU time per 2 MB file (FR-ONL-09; the Workers free tier allows 10 ms of CPU per request)", () => {
  // Worst cases at the 2 MB cap, measured here in the Workers runtime the tests use (test/cpu.ts: best median of up to three
  // rounds of 21 parses, so a load spike from parallel test files does not decide the result); scripts/measure-image-parser.mjs
  // measures the same files in Node with process CPU time. Both figures are recorded in docs/evidence/upload-cpu.md. The
  // median must stay within the free tier's 10 ms, leaving room for the SHA-256 (native code, about 2 ms per 2 MB).
  const worst = {
    "PNG, one 2 MB IDAT": png({ width: 1024, height: 1024, idatBytes: MAX_IMAGE_BYTES - 57 }),
    "PNG, 2 MB of 1-byte IDAT chunks": concatList([PNG_SIG, ihdr(1024, 1024), ...Array.from({ length: Math.floor((MAX_IMAGE_BYTES - 57) / 13) }, () => chunk("IDAT", new Uint8Array([1]))), chunk("IEND")]),
    "JPEG, 2 MB of entropy data": (() => {
      const head = concat(SOI, DQT, sof(SOF0, 1024, 1024), DHT, sos());
      const body = entropy(Math.floor((MAX_IMAGE_BYTES - head.length - 2) * 0.99), 17);
      return concat(head, body, EOI);
    })(),
    "JPEG, 2 MB of FF 00 pairs": denseJpeg([0xff, 0x00], MAX_IMAGE_BYTES),
    "JPEG, 2 MB of FF D0 pairs": denseJpeg([0xff, 0xd0], MAX_IMAGE_BYTES),
    "JPEG, 2 MB of FF 00 01 triples": denseJpeg([0xff, 0x00, 0x01], MAX_IMAGE_BYTES),
  };

  it.each(Object.entries(worst))("%s is accepted, with a median parse time under 10 ms", async (_name, file) => {
    expect(file.length).toBeLessThanOrEqual(MAX_IMAGE_BYTES);
    expect(file.length).toBeGreaterThan(MAX_IMAGE_BYTES * 0.98);
    expect(inspectImage(file).ok).toBe(true);
    const timing = await bestMedian(() => inspectImage(file));
    expect(timing.medianMs).toBeLessThan(CPU_BUDGET_MS);
  });
});
