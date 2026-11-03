// The checks the panel makes on a chosen image before it is sent (FR-ADM-08, ADR-0013): the size caps, the type from the
// file's first bytes, and the pixel size from the PNG or JPEG header. The service checks every rule again
// (service/src/images/strict.ts); these only spare the owner an upload that would be refused. Nothing here decodes or
// shows the picture: before an upload the panel shows the file's name, size and pixel size only (ADR-0014).

export const MAX_FILE = 2 * 1024 * 1024;
/** ADR-0013: a 1024 px image should stay under 60 KB. Larger files work but slow every sync. */
export const TARGET_FILE = 60 * 1024;
/** service/src/images/strict.ts MAX_IMAGE_DIMENSION. */
export const MAX_DIMENSION = 1024;

/** What each upload rule means for the owner, and what to do instead (the service's `image_rejected` reasons). */
export const IMAGE_REASONS: Readonly<Record<string, string>> = {
  png_not_rgb8: "The app shows only 8-bit RGB or RGBA PNG files. The panel converts greyscale, palette-based, 16-bit and colour-key PNGs by itself, but could not convert this one. Export it again as an 8-bit RGBA PNG (often called “PNG-24” or “PNG-32”).",
  png_damaged: "The PNG's picture data is damaged or does not match its header, so it could not be converted. Export it again as an 8-bit RGBA PNG.",
  animation_not_allowed: "Animated PNG files are not supported. Export a still PNG.",
  metadata_not_allowed: "The file has a comment or metadata block in a place the panel cannot clean. Export it again without metadata.",
  dimensions_out_of_range: "Images can be at most 1024 × 1024 pixels. Resize it and export again.",
  too_large: "Files can be at most 2 MB. Export it smaller; aim for under 60 KB.",
  empty: "The file is empty.",
  unknown_format: "Only PNG and JPEG files are accepted.",
  type_mismatch: "The file's content does not match its type. Export it again as PNG or JPEG.",
  jpeg_rotated: "This JPEG relies on a rotation tag to show the right way up. Rotate it in your editor, export it again, and check it looks right.",
  jpeg_rgb_coded: "This JPEG stores its colours in a form some apps show wrongly. Export it again as a standard JPEG, or as a PNG.",
  marker_not_allowed: "This JPEG uses a feature that is not supported (for example arithmetic coding or 12-bit colour). Export it as a standard baseline or progressive JPEG.",
};
const DAMAGED = "The file is damaged or incomplete. Export it again as an 8-bit RGBA PNG or a standard JPEG.";

export function imageReasonText(reason: string | null): string {
  return (reason && IMAGE_REASONS[reason]) ?? `${DAMAGED}${reason ? ` (${reason})` : ""}`;
}

export type ImageType = "image/png" | "image/jpeg";

/** Detects PNG or JPEG by the first bytes; null for anything else. */
export function sniffImageType(bytes: Uint8Array): ImageType | null {
  if (bytes.length >= 8 && [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a].every((b, i) => bytes[i] === b)) return "image/png";
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  return null;
}

const u16 = (b: Uint8Array, at: number) => (b[at]! << 8) | b[at + 1]!;
const u32 = (b: Uint8Array, at: number) => ((b[at]! << 24) | (b[at + 1]! << 16) | (b[at + 2]! << 8) | b[at + 3]!) >>> 0;

/** The pixel size in the header (PNG IHDR, JPEG start-of-frame), or null when the header cannot be read here. */
export function imageDimensions(bytes: Uint8Array, type: ImageType): { width: number; height: number } | null {
  if (type === "image/png") {
    // Signature (8), IHDR length (4), "IHDR" (4), width (4), height (4).
    if (bytes.length < 24 || u32(bytes, 12) !== 0x49484452) return null;
    return { width: u32(bytes, 16), height: u32(bytes, 20) };
  }
  let at = 2;
  while (at + 4 <= bytes.length) {
    if (bytes[at] !== 0xff) return null;
    while (at < bytes.length && bytes[at] === 0xff) at++;
    const marker = bytes[at];
    at++;
    if (marker === undefined || marker === 0xd9 || marker === 0xda) return null;
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
    if (at + 2 > bytes.length) return null;
    const length = u16(bytes, at);
    // Start-of-frame markers C0–CF, except C4 (DHT), C8 (reserved) and CC (DAC).
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
      if (length < 7 || at + 7 > bytes.length) return null;
      return { width: u16(bytes, at + 5), height: u16(bytes, at + 3) };
    }
    if (length < 2) return null;
    at += length;
  }
  return null;
}

export interface CheckedImage {
  type: ImageType;
  width: number | null;
  height: number | null;
}

/**
 * The reason (a service `image_rejected` reason, so the same plain-words text explains it) a file would be refused, or
 * the type and pixel size when it passes the checks the panel can make.
 */
export function checkImage(bytes: Uint8Array): { ok: true; image: CheckedImage } | { ok: false; reason: string } {
  if (bytes.length === 0) return { ok: false, reason: "empty" };
  if (bytes.length > MAX_FILE) return { ok: false, reason: "too_large" };
  const type = sniffImageType(bytes);
  if (type === null) return { ok: false, reason: "unknown_format" };
  const size = imageDimensions(bytes, type);
  if (size && (size.width < 1 || size.height < 1 || size.width > MAX_DIMENSION || size.height > MAX_DIMENSION)) return { ok: false, reason: "dimensions_out_of_range" };
  return { ok: true, image: { type, width: size?.width ?? null, height: size?.height ?? null } };
}
