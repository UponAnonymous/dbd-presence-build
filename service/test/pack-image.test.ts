// Owner pack images (FR-ADM-08, ADR-0013; docs/evidence/pack-index-contract.md §4): what is stored is exactly what the
// desktop app accepts. PNG: 8-bit RGB or RGBA with only IHDR, IDAT and IEND (the app's StrictPng). JPEG: no APPn or
// COM. Metadata is removed without touching image data; anything that cannot be cleaned safely is refused.

import { describe, expect, it } from "vitest";
import { exifOrientation, preparePackImage } from "../src/images/pack-image.ts";
import { MAX_IMAGE_BYTES, inspectImage } from "../src/images/strict.ts";
import { CPU_BUDGET_MS, bestMedian } from "./cpu.ts";
import { DHT, DQT, EOI, PNG_SIG, SOF0, SOI, app, chunk, com, concat, concatList, denseJpeg, entropy, filler, ihdr, jpeg, png, sof, sos } from "./images.ts";

/** The chunk types of a PNG, in order. */
function chunks(bytes: Uint8Array): string[] {
  const out: string[] = [];
  let at = 8;
  while (at + 12 <= bytes.length) {
    const length = ((bytes[at]! << 24) | (bytes[at + 1]! << 16) | (bytes[at + 2]! << 8) | bytes[at + 3]!) >>> 0;
    out.push(String.fromCharCode(...bytes.slice(at + 4, at + 8)));
    at += 12 + length;
  }
  return out;
}

/** The app's StrictPng rules (src/DbdPresence.Core/Images/StrictPng.cs), restated. */
function passesAppStrictPng(bytes: Uint8Array): boolean {
  const types = chunks(bytes);
  return (
    inspectImage(bytes).ok &&
    types[0] === "IHDR" &&
    types.at(-1) === "IEND" &&
    types.every((t) => t === "IHDR" || t === "IDAT" || t === "IEND") &&
    bytes[24] === 8 &&
    (bytes[25] === 2 || bytes[25] === 6)
  );
}

function exif(orientation: number | null, little = true): Uint8Array {
  const u16 = (n: number) => (little ? [n & 0xff, n >> 8] : [n >> 8, n & 0xff]);
  const u32 = (n: number) => (little ? [n & 0xff, (n >> 8) & 0xff, (n >> 16) & 0xff, n >>> 24] : [n >>> 24, (n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff]);
  const entries = orientation === null ? [] : [[...u16(0x0112), ...u16(3), ...u32(1), ...u16(orientation), 0, 0]];
  const tiff = [...(little ? [0x49, 0x49] : [0x4d, 0x4d]), ...u16(42), ...u32(8), ...u16(entries.length), ...entries.flat(), ...u32(0)];
  return new Uint8Array([...new TextEncoder().encode("Exif\0\0"), ...tiff]);
}

function adobe(transform: number): Uint8Array {
  return new Uint8Array([...new TextEncoder().encode("Adobe"), 0, 100, 0, 0, 0, 0, transform]);
}

describe("PNG", () => {
  it("stores a clean RGBA or RGB file unchanged", () => {
    for (const file of [png(), png({ colour: 2 })]) {
      const result = preparePackImage(file);
      expect(result.ok).toBe(true);
      if (!result.ok) continue;
      expect(result.bytes).toBe(file);
      expect(result.removed).toEqual([]);
      expect(passesAppStrictPng(result.bytes)).toBe(true);
    }
  });

  it("removes metadata chunks and a suggested palette, keeping the image data byte for byte", () => {
    const idat1 = chunk("IDAT", filler(300, 4));
    const idat2 = chunk("IDAT", filler(200, 5));
    const file = concat(
      PNG_SIG,
      ihdr(640, 480, 8, 6),
      chunk("iCCP", filler(40)),
      chunk("sRGB", new Uint8Array([0])),
      chunk("gAMA", filler(4)),
      chunk("pHYs", filler(9)),
      chunk("PLTE", filler(30)),
      idat1,
      idat2,
      chunk("tEXt", new TextEncoder().encode("Software\0Some editor")),
      chunk("tIME", filler(7)),
      chunk("IEND"),
    );
    const result = preparePackImage(file);
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.removed).toEqual(["iCCP", "sRGB", "gAMA", "pHYs", "PLTE", "tEXt", "tIME"]);
    expect(chunks(result.bytes)).toEqual(["IHDR", "IDAT", "IDAT", "IEND"]);
    expect(result.bytes).toEqual(concat(PNG_SIG, ihdr(640, 480, 8, 6), idat1, idat2, chunk("IEND")));
    expect(result.info).toEqual({ format: "png", width: 640, height: 480 });
    expect(passesAppStrictPng(result.bytes)).toBe(true);
  });

  it.each([
    ["greyscale", 0, 8],
    ["greyscale with alpha", 4, 8],
    ["palette", 3, 8],
    ["16-bit RGBA", 6, 16],
    ["16-bit RGB", 2, 16],
  ])("refuses a %s PNG with png_not_rgb8 (the app cannot show it)", (_name, colour, depth) => {
    const middle = colour === 3 ? [chunk("PLTE", filler(30)), chunk("IDAT", filler(20))] : [chunk("IDAT", filler(20))];
    expect(preparePackImage(png({ colour, depth, middle }))).toEqual({ ok: false, reason: "png_not_rgb8" });
  });

  it("refuses an RGB PNG with a transparent-colour key (tRNS), which cannot be dropped without changing the picture", () => {
    expect(preparePackImage(concat(PNG_SIG, ihdr(8, 8, 8, 2), chunk("tRNS", filler(6)), chunk("IDAT", filler(20)), chunk("IEND")))).toEqual({ ok: false, reason: "png_not_rgb8" });
  });

  it("refuses animation, unknown chunks, bad CRCs and trailing data even when metadata was removed", () => {
    expect(preparePackImage(png({ middle: [chunk("tEXt", filler(5)), chunk("acTL", filler(8)), chunk("IDAT", filler(20))] }))).toEqual({ ok: false, reason: "animation_not_allowed" });
    expect(preparePackImage(png({ middle: [chunk("tEXt", filler(5)), chunk("prVt", filler(8)), chunk("IDAT", filler(20))] }))).toEqual({ ok: false, reason: "chunk_not_allowed" });
    expect(preparePackImage(png({ middle: [chunk("tEXt", filler(5)), chunk("IDAT", filler(20), true)] }))).toEqual({ ok: false, reason: "bad_checksum" });
    expect(preparePackImage(concat(png({ middle: [chunk("tEXt", filler(5)), chunk("IDAT", filler(20))] }), new Uint8Array([0])))).toEqual({ ok: false, reason: "trailing_data" });
    expect(preparePackImage(png({ width: 1100, middle: [chunk("tEXt", filler(5)), chunk("IDAT", filler(20))] }))).toEqual({ ok: false, reason: "dimensions_out_of_range" });
  });
});

describe("JPEG", () => {
  it("removes JFIF, Exif (upright), XMP, ICC, Adobe (YCbCr) and comment segments, keeping the rest byte for byte", () => {
    const xmp = new TextEncoder().encode("http://ns.adobe.com/xap/1.0/\0<x/>");
    const clean = jpeg({ width: 800, height: 800 });
    const dirty = concat(clean.slice(0, 2), app(0), app(1, exif(1)), app(1, xmp), app(2, filler(50)), app(14, adobe(1)), com("Created with an editor"), app(13, filler(10)), clean.slice(2));
    const result = preparePackImage(dirty);
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.bytes).toEqual(clean);
    expect(result.removed).toEqual(["JFIF", "Exif", "XMP", "ICC profile", "Adobe", "comment", "APP13"]);
    expect(result.info).toEqual({ format: "jpeg", width: 800, height: 800 });
  });

  it("removes Exif without an orientation tag, and in big-endian order", () => {
    const clean = jpeg();
    for (const segment of [exif(null), exif(1, false)]) {
      const result = preparePackImage(concat(clean.slice(0, 2), app(1, segment), clean.slice(2)));
      expect(result.ok && result.bytes).toEqual(clean);
    }
  });

  it.each([2, 3, 6, 8])("refuses Exif orientation %i (removing it would turn the picture)", (orientation) => {
    const clean = jpeg();
    expect(preparePackImage(concat(clean.slice(0, 2), app(1, exif(orientation)), clean.slice(2)))).toEqual({ ok: false, reason: "jpeg_rotated" });
    expect(exifOrientation(exif(orientation, false))).toBe(orientation);
  });

  it("refuses an Adobe segment that declares RGB-coded colour in a 3-component image", () => {
    const clean = jpeg();
    expect(preparePackImage(concat(clean.slice(0, 2), app(14, adobe(0)), clean.slice(2)))).toEqual({ ok: false, reason: "jpeg_rgb_coded" });
  });

  it("refuses metadata after the first scan and anything else the strict parser refuses", () => {
    const head = concat(SOI, DQT, sof(SOF0, 64, 64), DHT, sos(), entropy(30));
    expect(preparePackImage(concat(head, com("late"), sos(), entropy(10), EOI))).toEqual({ ok: false, reason: "metadata_not_allowed" });
    expect(preparePackImage(concat(jpeg(), new Uint8Array([1])))).toEqual({ ok: false, reason: "trailing_data" });
    expect(preparePackImage(jpeg({ width: 2000 }))).toEqual({ ok: false, reason: "dimensions_out_of_range" });
  });
});

describe("both", () => {
  it("refuses empty, oversize and unknown files", () => {
    expect(preparePackImage(new Uint8Array(0))).toEqual({ ok: false, reason: "empty" });
    expect(preparePackImage(new Uint8Array(MAX_IMAGE_BYTES + 1))).toEqual({ ok: false, reason: "too_large" });
    expect(preparePackImage(new TextEncoder().encode("GIF89a"))).toEqual({ ok: false, reason: "unknown_format" });
  });

  it("fuzz: never throws, and every accepted PNG passes the app's StrictPng and every accepted JPEG has no APPn or COM", () => {
    let seed = 0x12345678;
    const random = () => {
      seed ^= seed << 13;
      seed ^= seed >>> 17;
      seed ^= seed << 5;
      return (seed >>> 0) / 0x100000000;
    };
    const clean = jpeg();
    const seeds = [
      png({ middle: [chunk("tEXt", filler(9)), chunk("PLTE", filler(9)), chunk("IDAT", filler(40)), chunk("pHYs", filler(9))] }),
      png({ colour: 2 }),
      concat(clean.slice(0, 2), app(0), app(1, exif(1)), com("x"), clean.slice(2)),
      jpeg({ progressive: true }),
    ];
    for (let round = 0; round < 3000; round++) {
      const file = Uint8Array.from(seeds[round % seeds.length]!);
      let mutated: Uint8Array = file;
      const kind = Math.floor(random() * 3);
      if (kind === 0) for (let i = 0; i < 1 + Math.floor(random() * 3); i++) file[Math.floor(random() * file.length)]! ^= 1 << Math.floor(random() * 8);
      else if (kind === 1) mutated = file.slice(0, Math.floor(random() * file.length));
      else {
        const at = Math.floor(random() * file.length);
        mutated = concat(file.slice(0, at), filler(1 + Math.floor(random() * 12), round + 7), file.slice(at));
      }
      let result: ReturnType<typeof preparePackImage> | undefined;
      expect(() => (result = preparePackImage(mutated))).not.toThrow();
      if (!result!.ok) continue;
      expect(inspectImage(result!.bytes).ok).toBe(true);
      if (result!.info.format === "png") expect(passesAppStrictPng(result!.bytes)).toBe(true);
      else {
        // Walk the header segments up to the first scan: none is APPn or COM.
        let at = 2;
        while (result!.bytes[at] === 0xff && result!.bytes[at + 1] !== 0xda) {
          const marker = result!.bytes[at + 1]!;
          expect(marker >= 0xe0 && marker <= 0xef).toBe(false);
          expect(marker).not.toBe(0xfe);
          at += 2 + ((result!.bytes[at + 2]! << 8) | result!.bytes[at + 3]!);
        }
      }
    }
  });

  it("stays well inside the CPU budget at 2 MB with 160 000 metadata chunks to remove", async () => {
    const file = concatList([PNG_SIG, ihdr(1024, 1024), chunk("IDAT", filler(1000)), ...Array.from({ length: Math.floor((MAX_IMAGE_BYTES - 1100) / 13) }, () => chunk("tEXt", new Uint8Array([1]))), chunk("IEND")]);
    expect(file.length).toBeLessThanOrEqual(MAX_IMAGE_BYTES);
    expect(preparePackImage(file).ok).toBe(true);
    expect((await bestMedian(() => preparePackImage(file))).medianMs).toBeLessThan(CPU_BUDGET_MS);
  });

  it("stays inside the CPU budget for 2 MB JPEGs of dense FF 00 and FF D0 pairs", async () => {
    for (const pattern of [[0xff, 0x00], [0xff, 0xd0]]) {
      const file = denseJpeg(pattern, MAX_IMAGE_BYTES);
      expect(preparePackImage(file).ok).toBe(true);
      expect((await bestMedian(() => preparePackImage(file))).medianMs).toBeLessThan(CPU_BUDGET_MS);
    }
  });
});
