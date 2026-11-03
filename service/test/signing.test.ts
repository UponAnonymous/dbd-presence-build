// NFR-SVC-04, ADR-0012: the TypeScript signer produces exactly the C# document format, enforces the per-purpose
// lifetime caps, and its committed output (tests/fixtures/signing/ts-signed) verifies in both suites.

import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { inject } from "vitest";
import { decodeBase64Url, encodeBase64Url } from "../src/lib/base64url.ts";
import { JsonSyntaxError, parseStrict } from "../src/lib/json.ts";
import {
  MAX_LIFETIME_MS,
  MAX_VERSION_AHEAD_OF_ISSUE,
  SigningError,
  TrustedKeySet,
  parseTimestampTicks,
  signDocument,
  verifyDocument,
  type Purpose,
} from "../src/signing/document.ts";
import { loadSigningKey } from "../src/signing/keys.ts";
import { envelopeOf, trustedKeys } from "./helpers.ts";

const DAY = 24 * 60 * 60 * 1000;
const ISSUED = new Date("2026-03-01T12:00:00.000Z");

async function testKey() {
  return loadSigningKey(env.LICENCE_SIGNING_KEY);
}

describe("signDocument", () => {
  it("produces the outer {kid, signed, sig} around the exact envelope member order", async () => {
    const { key, kid } = await testKey();
    const text = await signDocument("config", "stable", 42, { a: 1, b: ["x"] }, key, kid, { issuedAt: ISSUED });
    const outer = JSON.parse(text) as Record<string, string>;
    expect(Object.keys(outer)).toEqual(["kid", "signed", "sig"]);
    expect(outer["kid"]).toBe("test-content-1");
    expect(outer["signed"]).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(decodeBase64Url(outer["sig"]!)).toHaveLength(64);
    const envelopeText = new TextDecoder().decode(decodeBase64Url(outer["signed"]!)!);
    expect(envelopeText).toBe(
      '{"kid":"test-content-1","purpose":"config","channel":"stable","version":42,' +
        '"issuedAt":"2026-03-01T12:00:00.000Z","expiresAt":"2026-03-15T12:00:00.000Z","payload":{"a":1,"b":["x"]}}',
    );
  });

  it.each([
    ["config", 14],
    ["pack-index", 30],
    ["releases", 30],
    ["key-replacement", 3650],
    ["licence", 7],
    ["roster", 30],
  ] as [Purpose, number][])("caps %s at %i days and defaults to the cap", async (purpose, days) => {
    const { key, kid } = await testKey();
    expect(MAX_LIFETIME_MS[purpose]).toBe(days * DAY);
    const text = await signDocument(purpose, "stable", 1, {}, key, kid, { issuedAt: ISSUED });
    expect(Date.parse(envelopeOf(text)["expiresAt"] as string) - ISSUED.getTime()).toBe(days * DAY);
    await expect(
      signDocument(purpose, "stable", 1, {}, key, kid, { issuedAt: ISSUED, expiresAt: new Date(ISSUED.getTime() + days * DAY + 1) }),
    ).rejects.toBeInstanceOf(SigningError);
  });

  it("refuses inputs the client would reject", async () => {
    const { key, kid } = await testKey();
    await expect(signDocument("config", "stable", -1, {}, key, kid)).rejects.toBeInstanceOf(SigningError);
    await expect(signDocument("config", "stable", 1.5, {}, key, kid)).rejects.toBeInstanceOf(SigningError);
    await expect(signDocument("config", "", 1, {}, key, kid)).rejects.toBeInstanceOf(SigningError);
    await expect(signDocument("config", "stable", 1, {}, key, "")).rejects.toBeInstanceOf(SigningError);
    await expect(signDocument("nope" as Purpose, "stable", 1, {}, key, kid)).rejects.toBeInstanceOf(SigningError);
    await expect(signDocument("config", "stable", 1, {}, key, kid, { issuedAt: ISSUED, expiresAt: ISSUED })).rejects.toBeInstanceOf(SigningError);
    await expect(signDocument("config", "stable", 1, undefined, key, kid)).rejects.toBeInstanceOf(SigningError);
  });

  it("round-trips through the TypeScript verifier for every purpose", async () => {
    const { key, kid } = await testKey();
    const keys = await trustedKeys();
    for (const purpose of ["config", "pack-index", "releases", "licence", "roster", "key-replacement"] as Purpose[]) {
      // A licence's version is its signing time in whole seconds (TM-01); the other purposes take any small counter.
      const version = purpose === "licence" ? Math.floor(ISSUED.getTime() / 1000) : 9;
      const text = await signDocument(purpose, "beta", version, { purpose, text: "é — 🎃 \"q\"" }, key, kid, { issuedAt: ISSUED });
      const result = await verifyDocument(text, purpose, "beta", { keys, now: new Date(ISSUED.getTime() + 1000) });
      expect(result.outcome).toBe("Valid");
      expect(JSON.parse(result.envelope!.payloadJson)).toEqual({ purpose, text: "é — 🎃 \"q\"" });
    }
  });

  it("is refused by the verifier after any change to the signed bytes", async () => {
    const { key, kid } = await testKey();
    const text = await signDocument("licence", "stable", 1, { sub: "a" }, key, kid, { issuedAt: ISSUED });
    const outer = JSON.parse(text) as { kid: string; signed: string; sig: string };
    const bytes = decodeBase64Url(outer.signed)!;
    bytes[bytes.length - 3] = bytes[bytes.length - 3]! ^ 1;
    const tampered = JSON.stringify({ ...outer, signed: encodeBase64Url(bytes) });
    const result = await verifyDocument(tampered, "licence", "stable", { keys: await trustedKeys(), now: ISSUED });
    expect(result.outcome).toBe("BadSignature");
  });
});

describe("TypeScript-signed fixtures (cross-stack, also verified by the C# suite)", () => {
  const fixtures = inject("crossStackDocuments").map(
    (f) =>
      JSON.parse(f.text) as {
        name: string;
        expectedPurpose: Purpose;
        expectedChannel: string;
        now: string;
        expected: string;
        expectedVersion: number;
        expectedIssuedAt: string;
        expectedExpiresAt: string;
        expectedPayload: unknown;
        documentText: string;
      },
  );

  it("include one document per purpose", () => {
    expect(fixtures.map((f) => f.expectedPurpose).sort()).toEqual(["config", "key-replacement", "licence", "pack-index", "releases", "roster"]);
  });

  it.each(fixtures.map((f) => [f.name, f] as const))("%s verifies with the expected envelope", async (_name, fixture) => {
    const result = await verifyDocument(fixture.documentText, fixture.expectedPurpose, fixture.expectedChannel, {
      keys: await trustedKeys(),
      now: new Date(fixture.now),
    });
    expect(result.outcome).toBe(fixture.expected);
    expect(result.envelope!.version).toBe(BigInt(fixture.expectedVersion));
    expect(result.envelope!.issuedAtTicks).toBe(BigInt(Date.parse(fixture.expectedIssuedAt)) * 10_000n);
    expect(result.envelope!.expiresAtTicks).toBe(BigInt(Date.parse(fixture.expectedExpiresAt)) * 10_000n);
    expect(JSON.parse(result.envelope!.payloadJson)).toEqual(fixture.expectedPayload);
  });
});

describe("verifier rules", () => {
  it("refuses a key that is not trusted for the expected purpose before checking the signature", async () => {
    const { key, kid } = await testKey();
    const text = await signDocument("config", "stable", 1, {}, key, kid, { issuedAt: ISSUED });
    const configOnly = await TrustedKeySet.fromJson({ keys: [{ kid, spki: (JSON.parse(inject("signingKeys")) as { keys: { spki: string }[] }).keys[0]!.spki, purposes: ["licence"] }] });
    expect((await verifyDocument(text, "config", "stable", { keys: configOnly, now: ISSUED })).outcome).toBe("KeyNotAllowedForPurpose");
  });

  it("maps hostile outer documents to Malformed and never throws", async () => {
    const keys = await trustedKeys();
    const options = { keys, now: ISSUED };
    const cases = [
      "",
      "[]",
      '{"kid":"test-content-1","kid":"test-content-1","signed":"e30","sig":"AA"}',
      '{"kid":"test-content-1","signed":"e30!","sig":"AA"}',
      '{"kid":"","signed":"e30","sig":"AA"}',
      `{"kid":"${"k".repeat(129)}","signed":"e30","sig":"AA"}`,
      '{"kid":"test-content-1","signed":"e30","sig":"AA","x":{"a":{"b":{"c":{}}}}}',
    ];
    for (const text of cases) expect((await verifyDocument(text, "config", "stable", options)).outcome).toBe("Malformed");
    expect((await verifyDocument(new Uint8Array([0x7b, 0xff, 0x7d]), "config", "stable", options)).outcome).toBe("Malformed");
    expect((await verifyDocument(new Uint8Array(1024 * 1024 + 1), "config", "stable", options)).outcome).toBe("Malformed");
    expect((await verifyDocument('{"kid":"test-content-1","signed":"e30","sig":"AAAA"}', "config", "stable", options)).outcome).toBe("BadSignature");
  });
});

describe("strict JSON reader", () => {
  it("rejects duplicates, trailing data, bad escapes and excess depth", () => {
    for (const text of ['{"a":1,"a":2}', "{} x", '"\\x"', "[01]", '{"a":}', "[1,]", '"\u0001"']) {
      expect(() => parseStrict(text, 8)).toThrow(JsonSyntaxError);
    }
    expect(() => parseStrict("[[[[1]]]]", 3)).toThrow(JsonSyntaxError);
    expect(() => parseStrict("[[[1]]]", 3)).not.toThrow();
  });

  it("keeps the exact source span of each value", () => {
    const text = '{ "payload" : { "x" : "\\u00e9" , "y":[1, 2.5e3] } }';
    const root = parseStrict(text, 8);
    if (root.kind !== "object") throw new Error("expected object");
    const payload = root.entries.get("payload")!;
    expect(text.slice(payload.start, payload.end)).toBe('{ "x" : "\\u00e9" , "y":[1, 2.5e3] }');
  });
});

describe("timestamps", () => {
  it("parse the C# envelope format to 100-ns ticks", () => {
    const base = BigInt(Date.parse("2026-01-01T00:00:00Z")) * 10_000n;
    expect(parseTimestampTicks("2026-01-01T00:00:00.000Z")).toBe(base);
    expect(parseTimestampTicks("2026-01-01T00:00:00Z")).toBe(base);
    expect(parseTimestampTicks("2026-01-01T00:00:00")).toBe(base);
    expect(parseTimestampTicks("2026-01-01T00:00:00.0000001Z")).toBe(base + 1n);
    expect(parseTimestampTicks("2026-01-01T01:00:00+01:00")).toBe(base);
    for (const bad of ["2026-02-30T00:00:00Z", "2026-01-01 00:00:00Z", "2026-01-01T24:00:00Z", "2026-01-01T00:00:00.12345678Z", "0000-01-01T00:00:00Z"]) {
      expect(parseTimestampTicks(bad)).toBeNull();
    }
  });
});

describe("version bounds (threat-model review 2026-10-25, TM-01)", () => {
  async function signWith(purpose: Purpose, version: bigint, issuedAt: string): Promise<string> {
    const { key, kid } = await testKey();
    const envelope = new TextEncoder().encode(
      `{"kid":"${kid}","purpose":"${purpose}","channel":"stable","version":${version},"issuedAt":"${issuedAt}","expiresAt":"2026-03-02T12:00:00.000Z","payload":{}}`,
    );
    const sig = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, envelope));
    return JSON.stringify({ kid, signed: encodeBase64Url(envelope), sig: encodeBase64Url(sig) });
  }
  const issued = "2026-03-01T12:00:00.000Z";
  const seconds = BigInt(Date.parse(issued) / 1000);
  const now = new Date(Date.parse(issued) + 1000);

  it("allows a version up to MAX_VERSION_AHEAD_OF_ISSUE above the signing time and no more", async () => {
    const keys = await trustedKeys();
    expect((await verifyDocument(await signWith("config", seconds + MAX_VERSION_AHEAD_OF_ISSUE, issued), "config", "stable", { keys, now })).outcome).toBe("Valid");
    expect((await verifyDocument(await signWith("config", seconds + MAX_VERSION_AHEAD_OF_ISSUE + 1n, issued), "config", "stable", { keys, now })).outcome).toBe(
      "VersionTooFarAhead",
    );
  });

  it("requires a licence version equal to the signing time in whole seconds", async () => {
    const keys = await trustedKeys();
    expect((await verifyDocument(await signWith("licence", seconds, issued), "licence", "stable", { keys, now })).outcome).toBe("Valid");
    expect((await verifyDocument(await signWith("licence", seconds + 1n, issued), "licence", "stable", { keys, now })).outcome).toBe("VersionNotDerivedFromIssueTime");
    expect((await verifyDocument(await signWith("licence", seconds - 1n, issued), "licence", "stable", { keys, now })).outcome).toBe("VersionNotDerivedFromIssueTime");
    // Milliseconds in the signing time are floored, as in the C# verifier.
    expect((await verifyDocument(await signWith("licence", seconds, "2026-03-01T12:00:00.999Z"), "licence", "stable", { keys, now })).outcome).toBe("Valid");
  });
});

describe("version cap (security review 2026-10-25, L1)", () => {
  async function signRaw(version: string): Promise<string> {
    const { key, kid } = await testKey();
    const envelope = new TextEncoder().encode(
      `{"kid":"${kid}","purpose":"pack-index","channel":"stable","version":${version},"issuedAt":"2026-03-01T12:00:00.000Z","expiresAt":"2026-03-02T12:00:00.000Z","payload":{}}`,
    );
    const sig = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, envelope));
    return JSON.stringify({ kid, signed: encodeBase64Url(envelope), sig: encodeBase64Url(sig) });
  }

  it("refuses 2^53 - 1 as too far ahead (TM-01) and anything higher as Malformed, like the C# verifier", async () => {
    const keys = await trustedKeys();
    const now = new Date(ISSUED.getTime() + 1000);
    expect((await verifyDocument(await signRaw("9007199254740991"), "pack-index", "stable", { keys, now })).outcome).toBe("VersionTooFarAhead");
    expect((await verifyDocument(await signRaw("9007199254740992"), "pack-index", "stable", { keys, now })).outcome).toBe("Malformed");
    expect((await verifyDocument(await signRaw("9223372036854775807"), "pack-index", "stable", { keys, now })).outcome).toBe("Malformed");
  });
});
