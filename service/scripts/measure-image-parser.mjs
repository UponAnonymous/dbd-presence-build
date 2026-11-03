// Measures the CPU time of the strict image parser (src/images/strict.ts) and of the owner pack-image clean-up
// (src/images/pack-image.ts, which ends with the strict parse) on worst-case 2 MB files, for
// docs/evidence/upload-cpu.md (FR-ONL-09, QUALITY_GATES V5.4). The Workers free tier allows 10 ms of CPU per request.
//
//   node scripts/measure-image-parser.mjs        (from service/, Node 22.18 or later: strips the TypeScript types)
//
// CPU time is process.cpuUsage() (user + system) around each parse, after a warm-up, and the median of 25 runs is
// reported with the maximum. The SHA-256 of the file (WebCrypto, native code) is timed separately because the upload
// route hashes every accepted file. The user upload route (src/routes/uploads.ts) also reads the one multipart part
// (src/lib/multipart.ts) before the strict parse; that pair is timed as "upload read and parse".

import { webcrypto } from "node:crypto";
import { preparePackImage } from "../src/images/pack-image.ts";
import { MAX_IMAGE_BYTES, inspectImage } from "../src/images/strict.ts";
import { readSingleFilePart } from "../src/lib/multipart.ts";
import { DHT, DQT, EOI, PNG_SIG, SOF0, SOI, chunk, concat, concatList, denseJpeg, entropy, filler, ihdr, png, sof, sos } from "../test/images.ts";

const files = {
  "PNG, one IDAT of 2 MB": png({ width: 1024, height: 1024, idatBytes: MAX_IMAGE_BYTES - 57 }),
  "PNG, 2 MB of 1-byte IDAT chunks (161 000 chunks)": concatList([
    PNG_SIG,
    ihdr(1024, 1024),
    ...Array.from({ length: Math.floor((MAX_IMAGE_BYTES - 57) / 13) }, () => chunk("IDAT", new Uint8Array([1]))),
    chunk("IEND"),
  ]),
  "PNG, 2 MB with 161 000 metadata chunks to remove": concatList([
    PNG_SIG,
    ihdr(1024, 1024),
    chunk("IDAT", filler(1000)),
    ...Array.from({ length: Math.floor((MAX_IMAGE_BYTES - 1100) / 13) }, () => chunk("tEXt", new Uint8Array([1]))),
    chunk("IEND"),
  ]),
  "JPEG, 2 MB of entropy-coded data": (() => {
    const head = concat(SOI, DQT, sof(SOF0, 1024, 1024), DHT, sos());
    return concat(head, entropy(Math.floor((MAX_IMAGE_BYTES - head.length - 2) * 0.99), 17), EOI);
  })(),
  // The JPEG scanner's per-pair worst cases (security review 2026-10-24, M1): entropy data that is nothing but stuffed bytes or
  // restart markers, and pairs separated by one byte.
  "JPEG, 2 MB of FF 00 pairs": denseJpeg([0xff, 0x00], MAX_IMAGE_BYTES),
  "JPEG, 2 MB of FF D0 pairs": denseJpeg([0xff, 0xd0], MAX_IMAGE_BYTES),
  "JPEG, 2 MB of FF 00 01 triples": denseJpeg([0xff, 0x00, 0x01], MAX_IMAGE_BYTES),
};

function cpuMs(run) {
  const start = process.cpuUsage();
  run();
  const used = process.cpuUsage(start);
  return (used.user + used.system) / 1000;
}

async function cpuMsAsync(run) {
  const start = process.cpuUsage();
  await run();
  const used = process.cpuUsage(start);
  return (used.user + used.system) / 1000;
}

const median = (values) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
const RUNS = 25;

console.log(`Node ${process.version}, V8 ${process.versions.v8}, ${process.arch}`);
for (const [name, bytes] of Object.entries(files)) {
  for (let i = 0; i < 5; i++) if (!preparePackImage(bytes).ok) throw new Error(`${name} was refused`);
  const parse = Array.from({ length: RUNS }, () => cpuMs(() => inspectImage(bytes)));
  const prepare = Array.from({ length: RUNS }, () => cpuMs(() => preparePackImage(bytes)));
  const multipart = concat(new TextEncoder().encode('--b0undary\r\nContent-Disposition: form-data; name="file"\r\nContent-Type: image/png\r\n\r\n'), bytes, new TextEncoder().encode("\r\n--b0undary--\r\n"));
  const contentType = "multipart/form-data; boundary=b0undary";
  const upload = Array.from({ length: RUNS }, () => cpuMs(() => inspectImage(readSingleFilePart(multipart, contentType).bytes)));
  const hash = [];
  for (let i = 0; i < RUNS; i++) hash.push(await cpuMsAsync(() => webcrypto.subtle.digest("SHA-256", bytes)));
  console.log(
    `${name}: ${bytes.length} bytes; strict parse median ${median(parse).toFixed(2)} ms, max ${Math.max(...parse).toFixed(2)} ms; ` +
      `clean-up and parse (owner pack upload) median ${median(prepare).toFixed(2)} ms, max ${Math.max(...prepare).toFixed(2)} ms; ` +
      `upload read and parse median ${median(upload).toFixed(2)} ms, max ${Math.max(...upload).toFixed(2)} ms; ` +
      `SHA-256 median ${median(hash).toFixed(2)} ms, max ${Math.max(...hash).toFixed(2)} ms`,
  );
}
