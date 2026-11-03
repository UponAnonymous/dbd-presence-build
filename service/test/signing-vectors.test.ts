// NFR-SVC-04, ADR-0012, QUALITY_GATES V2.3/V4.1: the shared vectors in tests/fixtures/signing/vectors give the same
// outcome in the TypeScript verifier as in the C# suite (tests/DbdPresence.Packs.Tests/SigningVectorTests.cs).

import { describe, expect, it } from "vitest";
import { inject } from "vitest";
import { verifyDocument, type Purpose, type VerificationOutcome } from "../src/signing/document.ts";
import { trustedKeys } from "./helpers.ts";

interface Vector {
  name: string;
  expectedPurpose: Purpose;
  expectedChannel: string;
  now: string;
  storedVersions: { purpose: string; channel: string; version: number }[];
  expected: VerificationOutcome;
  documentText: string;
}

const vectors = inject("signingVectors").map((f) => JSON.parse(f.text) as Vector);

const ALL_OUTCOMES: VerificationOutcome[] = [
  "Valid",
  "BadSignature",
  "UnknownKey",
  "WrongPurpose",
  "KeyNotAllowedForPurpose",
  "WrongChannel",
  "Expired",
  "LifetimeTooLong",
  "RolledBack",
  "VersionTooFarAhead",
  "VersionNotDerivedFromIssueTime",
  "Malformed",
];

describe("shared signing vectors", () => {
  it("are all present (same set the C# suite runs)", () => {
    expect(vectors.map((v) => v.name).sort()).toEqual([
      "expired",
      "issued-in-future",
      "key-wrong-purpose",
      "kid-mismatch",
      "licence-version-mismatch",
      "lifetime-too-long",
      "malformed-document",
      "malformed-envelope",
      "rolled-back",
      "tampered-payload",
      "tampered-signature",
      "unknown-kid",
      "valid",
      "valid-equal-version",
      "valid-newer-version",
      "version-too-far-ahead",
      "wrong-channel",
      "wrong-purpose",
    ]);
  });

  it("cover every outcome", () => {
    expect(new Set(vectors.map((v) => v.expected))).toEqual(new Set(ALL_OUTCOMES));
  });

  it.each(vectors.map((v) => [v.name, v] as const))("%s gives the expected outcome", async (_name, vector) => {
    const keys = await trustedKeys();
    const stored = new Map(vector.storedVersions.map((s) => [`${s.purpose}|${s.channel}`, BigInt(s.version)]));
    const result = await verifyDocument(vector.documentText, vector.expectedPurpose, vector.expectedChannel, {
      keys,
      now: new Date(vector.now),
      highestVersion: (purpose, channel) => stored.get(`${purpose}|${channel}`),
    });
    expect(result.outcome).toBe(vector.expected);
    expect(result.envelope !== null).toBe(vector.expected === "Valid");
  });

  it("valid vector exposes the exact payload text", async () => {
    const vector = vectors.find((v) => v.name === "valid")!;
    const result = await verifyDocument(vector.documentText, "pack-index", "stable", { keys: await trustedKeys(), now: new Date(vector.now) });
    const payload = JSON.parse(result.envelope!.payloadJson) as { entries: unknown[] };
    expect(payload.entries).toHaveLength(3);
    expect(result.envelope!.version).toBe(5n);
  });
});
