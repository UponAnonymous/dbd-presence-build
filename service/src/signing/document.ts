// Signed documents (ADR-0012, NFR-SVC-04). Byte-for-byte the format verified by
// src/DbdPresence.Packs/Signing/SignedDocumentVerifier.cs:
//   outer    {"kid":..., "signed": base64url(envelope bytes), "sig": base64url(P1363 r||s)}
//   envelope {"kid","purpose","channel","version","issuedAt","expiresAt","payload"}
// ECDSA P-256 with SHA-256 over exactly the envelope bytes. The verifier below mirrors the C# rules and their order
// so that the shared vectors in tests/fixtures/signing give the same outcome in both languages.

import { decodeBase64Url, encodeBase64Url } from "../lib/base64url.ts";
import { JsonSyntaxError, parseStrict, type JsonNode } from "../lib/json.ts";

export type Purpose = "pack-index" | "config" | "releases" | "licence" | "roster" | "key-replacement";

export const PURPOSES: readonly Purpose[] = ["pack-index", "config", "releases", "licence", "roster", "key-replacement"];

const DAY_MS = 24 * 60 * 60 * 1000;

/** Longest `expiresAt - issuedAt` per purpose (PLAN_V2 §6.1 and §3.3); the same caps as SignedPurposes.MaxLifetime in C#. */
export const MAX_LIFETIME_MS: Readonly<Record<Purpose, number>> = {
  config: 14 * DAY_MS,
  "pack-index": 30 * DAY_MS,
  releases: 30 * DAY_MS,
  licence: 7 * DAY_MS,
  // The character roster (PLAN_V2_S5B §2.3): signed by the content key, re-signed unchanged each week.
  roster: 30 * DAY_MS,
  // Key replacements (V2-S6, PLAN_V2 §6.2): signed offline by the recovery key; a revocation must still reach an install that was
  // offline for a long time (D8). The service only verifies these, it never signs them.
  "key-replacement": 3650 * DAY_MS,
};

export const MAX_DOCUMENT_BYTES = 1024 * 1024;
const MAX_OUTER_DEPTH = 4;
const MAX_ENVELOPE_DEPTH = 24;
const MAX_IDENTIFIER_LENGTH = 128;
const SIGNATURE_LENGTH = 64;
const MAX_CLOCK_SKEW_TICKS = 5n * 60n * 10_000_000n;
const TICKS_PER_MS = 10_000n;
/**
 * How far above the signing time in seconds a version may be (same constant as C# SignedDocumentVerifier.MaxVersionAheadOfIssue; TM-01).
 * A genuine version is max(previous + 1, seconds now), so it exceeds the signing time only by one per change made within the same second.
 */
export const MAX_VERSION_AHEAD_OF_ISSUE = 1_000_000n;

/** Highest accepted envelope version, 2^53 - 1 (the same cap as the C# verifier; security review 2026-10-25, L1). */
export const MAX_VERSION = 9_007_199_254_740_991n;

const ECDSA_P256 = { name: "ECDSA", namedCurve: "P-256" } as const;
const ES256 = { name: "ECDSA", hash: "SHA-256" } as const;

export function isPurpose(value: string): value is Purpose {
  return (PURPOSES as readonly string[]).includes(value);
}

export interface SignOptions {
  /** Signing time; defaults to now. */
  issuedAt?: Date;
  /** Expiry; defaults to `issuedAt` plus the purpose's maximum lifetime. Must not exceed that cap. */
  expiresAt?: Date;
}

/** Thrown when asked to sign something the client would refuse (a programming error, never remote input). */
export class SigningError extends Error {
  constructor(reason: string) {
    super(reason);
    this.name = "SigningError";
  }
}

/**
 * Signs `payload` into the ADR-0012 document format and returns the document text.
 * `key` is an ECDSA P-256 private key with the `sign` usage.
 */
export async function signDocument(
  purpose: Purpose,
  channel: string,
  version: number,
  payload: unknown,
  key: CryptoKey,
  kid: string,
  options: SignOptions = {},
): Promise<string> {
  if (!isPurpose(purpose)) throw new SigningError("unknown purpose");
  if (!isIdentifier(kid)) throw new SigningError("bad kid");
  if (!isIdentifier(channel)) throw new SigningError("bad channel");
  if (!Number.isSafeInteger(version) || version < 0) throw new SigningError("bad version");
  const issuedAt = options.issuedAt ?? new Date();
  const expiresAt = options.expiresAt ?? new Date(issuedAt.getTime() + MAX_LIFETIME_MS[purpose]);
  const lifetime = expiresAt.getTime() - issuedAt.getTime();
  if (!(lifetime > 0)) throw new SigningError("expiresAt must be after issuedAt");
  if (lifetime > MAX_LIFETIME_MS[purpose]) throw new SigningError("lifetime above the cap for this purpose");
  const payloadText = JSON.stringify(payload);
  if (payloadText === undefined) throw new SigningError("payload is not JSON");

  const envelopeText =
    `{"kid":${JSON.stringify(kid)},"purpose":${JSON.stringify(purpose)},"channel":${JSON.stringify(channel)},` +
    `"version":${version},"issuedAt":${JSON.stringify(issuedAt.toISOString())},` +
    `"expiresAt":${JSON.stringify(expiresAt.toISOString())},"payload":${payloadText}}`;
  const envelopeBytes = new TextEncoder().encode(envelopeText);
  const signature = new Uint8Array(await crypto.subtle.sign(ES256, key, envelopeBytes));
  return `{"kid":${JSON.stringify(kid)},"signed":"${encodeBase64Url(envelopeBytes)}","sig":"${encodeBase64Url(signature)}"}`;
}

/** Imports a PKCS#8 ECDSA P-256 private key for {@link signDocument}. */
export async function importSigningKey(pkcs8: Uint8Array): Promise<CryptoKey> {
  return crypto.subtle.importKey("pkcs8", pkcs8, ECDSA_P256, false, ["sign"]);
}

// ---------------------------------------------------------------------------------------------------------------
// Verifier

export type VerificationOutcome =
  | "Valid"
  | "BadSignature"
  | "UnknownKey"
  | "WrongPurpose"
  | "KeyNotAllowedForPurpose"
  | "WrongChannel"
  | "Expired"
  | "LifetimeTooLong"
  | "RolledBack"
  | "VersionTooFarAhead"
  | "VersionNotDerivedFromIssueTime"
  | "Malformed";

export interface SignedEnvelope {
  kid: string;
  purpose: string;
  channel: string;
  version: bigint;
  /** 100-nanosecond ticks since the Unix epoch (the C# DateTimeOffset precision). */
  issuedAtTicks: bigint;
  expiresAtTicks: bigint;
  /** The exact source text of the payload member. */
  payloadJson: string;
}

export interface VerificationResult {
  outcome: VerificationOutcome;
  envelope: SignedEnvelope | null;
}

export interface TrustedKey {
  key: CryptoKey;
  purposes: ReadonlySet<string>;
}

/** Trusted ECDSA P-256 public keys by kid, each scoped to purposes. */
export class TrustedKeySet {
  private readonly keys: ReadonlyMap<string, TrustedKey>;

  constructor(keys: ReadonlyMap<string, TrustedKey>) {
    this.keys = keys;
  }

  /** Builds the set from `{ keys: [{ kid, spki (base64url DER), purposes }] }` (tests/fixtures/signing/keys/keys.json). */
  static async fromJson(json: { keys: { kid: string; spki: string; purposes: string[] }[] }): Promise<TrustedKeySet> {
    const map = new Map<string, TrustedKey>();
    for (const entry of json.keys) {
      const spki = decodeBase64Url(entry.spki);
      if (!entry.kid || !spki || entry.purposes.length === 0) throw new SigningError("bad trusted key");
      const key = await crypto.subtle.importKey("spki", spki, ECDSA_P256, false, ["verify"]);
      map.set(entry.kid, { key, purposes: new Set(entry.purposes) });
    }
    return new TrustedKeySet(map);
  }

  get(kid: string): TrustedKey | undefined {
    return this.keys.get(kid);
  }
}

export interface VerifyOptions {
  keys: TrustedKeySet;
  now: Date;
  /** Highest version already accepted for (purpose, channel); undefined when none. */
  highestVersion?: (purpose: string, channel: string) => bigint | undefined;
}

const fail = (outcome: VerificationOutcome): VerificationResult => ({ outcome, envelope: null });

/** Verifies a signed document with the same rules, in the same order, as the C# SignedDocumentVerifier. Never throws on bad input. */
export async function verifyDocument(
  document: Uint8Array | string,
  expectedPurpose: Purpose,
  expectedChannel: string,
  options: VerifyOptions,
): Promise<VerificationResult> {
  if (!isPurpose(expectedPurpose)) throw new SigningError("unknown expected purpose");
  const bytes = typeof document === "string" ? new TextEncoder().encode(document) : document;
  if (bytes.length === 0 || bytes.length > MAX_DOCUMENT_BYTES) return fail("Malformed");

  const outer = readOuter(bytes);
  if (!outer) return fail("Malformed");

  const trusted = options.keys.get(outer.kid);
  if (!trusted) return fail("UnknownKey");
  if (!trusted.purposes.has(expectedPurpose)) return fail("KeyNotAllowedForPurpose");
  if (outer.signature.length !== SIGNATURE_LENGTH) return fail("BadSignature");
  let good: boolean;
  try {
    good = await crypto.subtle.verify(ES256, trusted.key, outer.signature, outer.envelopeBytes);
  } catch (error) {
    if (error instanceof Error && (error.name === "OperationError" || error.name === "DataError")) return fail("BadSignature");
    throw error;
  }
  if (!good) return fail("BadSignature");

  // The signature is good: only now are the envelope bytes parsed.
  const envelope = readEnvelope(outer.envelopeBytes);
  if (!envelope || envelope.kid !== outer.kid) return fail("Malformed");
  if (envelope.purpose !== expectedPurpose) return fail("WrongPurpose");
  if (envelope.channel !== expectedChannel) return fail("WrongChannel");

  // TM-01, same order as C#: the version relation comes before the lifetime and freshness rules.
  const issuedSeconds = floorDiv(envelope.issuedAtTicks, TICKS_PER_MS * 1000n);
  if (expectedPurpose === "licence") {
    if (envelope.version !== issuedSeconds) return fail("VersionNotDerivedFromIssueTime");
  } else if (envelope.version - issuedSeconds > MAX_VERSION_AHEAD_OF_ISSUE) {
    return fail("VersionTooFarAhead");
  }

  const now = BigInt(options.now.getTime()) * TICKS_PER_MS;
  if (envelope.expiresAtTicks - envelope.issuedAtTicks > BigInt(MAX_LIFETIME_MS[expectedPurpose]) * TICKS_PER_MS) {
    return fail("LifetimeTooLong");
  }
  if (envelope.issuedAtTicks - now > MAX_CLOCK_SKEW_TICKS) return fail("Malformed");
  if (now >= envelope.expiresAtTicks) return fail("Expired");
  const highest = options.highestVersion?.(expectedPurpose, expectedChannel);
  if (highest !== undefined && envelope.version < highest) return fail("RolledBack");

  return { outcome: "Valid", envelope };
}

function floorDiv(a: bigint, b: bigint): bigint {
  const q = a / b;
  return a % b !== 0n && a < 0n !== b < 0n ? q - 1n : q;
}

interface Outer {
  kid: string;
  envelopeBytes: Uint8Array;
  signature: Uint8Array;
}

function decodeUtf8(bytes: Uint8Array): string | null {
  try {
    return new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes);
  } catch (error) {
    if (error instanceof TypeError) return null;
    throw error;
  }
}

function parseOrNull(bytes: Uint8Array, maxDepth: number): { text: string; root: JsonNode } | null {
  const text = decodeUtf8(bytes);
  if (text === null) return null;
  try {
    return { text, root: parseStrict(text, maxDepth) };
  } catch (error) {
    if (error instanceof JsonSyntaxError) return null;
    throw error;
  }
}

function stringMember(node: JsonNode, name: string): string | null {
  if (node.kind !== "object") return null;
  const member = node.entries.get(name);
  return member?.kind === "string" ? member.value : null;
}

function isIdentifier(value: string | null): value is string {
  return value !== null && value.length > 0 && value.length <= MAX_IDENTIFIER_LENGTH;
}

function readOuter(bytes: Uint8Array): Outer | null {
  const parsed = parseOrNull(bytes, MAX_OUTER_DEPTH);
  if (!parsed || parsed.root.kind !== "object") return null;
  const kid = stringMember(parsed.root, "kid");
  const signed = stringMember(parsed.root, "signed");
  const sig = stringMember(parsed.root, "sig");
  if (!isIdentifier(kid) || !signed || !sig) return null;
  const envelopeBytes = decodeBase64Url(signed);
  const signature = decodeBase64Url(sig);
  if (!envelopeBytes || !signature) return null;
  return { kid, envelopeBytes, signature };
}

function readEnvelope(bytes: Uint8Array): SignedEnvelope | null {
  const parsed = parseOrNull(bytes, MAX_ENVELOPE_DEPTH);
  if (!parsed || parsed.root.kind !== "object") return null;
  const root = parsed.root;
  const kid = stringMember(root, "kid");
  const purpose = stringMember(root, "purpose");
  const channel = stringMember(root, "channel");
  if (!isIdentifier(kid) || !isIdentifier(purpose) || !isIdentifier(channel)) return null;
  const versionNode = root.entries.get("version");
  if (versionNode?.kind !== "number" || !/^-?(?:0|[1-9]\d*)$/.test(versionNode.raw)) return null;
  const version = BigInt(versionNode.raw);
  if (version < 0n || version > MAX_VERSION) return null;
  const issuedAt = parseTimestampTicks(stringMember(root, "issuedAt"));
  const expiresAt = parseTimestampTicks(stringMember(root, "expiresAt"));
  if (issuedAt === null || expiresAt === null || expiresAt <= issuedAt) return null;
  const payload = root.entries.get("payload");
  if (!payload) return null;
  return {
    kid,
    purpose,
    channel,
    version,
    issuedAtTicks: issuedAt,
    expiresAtTicks: expiresAt,
    payloadJson: parsed.text.slice(payload.start, payload.end),
  };
}

const TIMESTAMP = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})?$/;

/**
 * Parses the envelope timestamp format accepted by C# (`yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK`, assumed UTC when no offset)
 * into 100-ns ticks since the Unix epoch, or null.
 */
export function parseTimestampTicks(text: string | null): bigint | null {
  if (text === null) return null;
  const m = TIMESTAMP.exec(text);
  if (!m) return null;
  const [year, month, day, hour, minute, second] = m.slice(1, 7).map(Number) as [number, number, number, number, number, number];
  if (year < 1 || month < 1 || month > 12 || day < 1 || hour > 23 || minute > 59 || second > 59) return null;
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(hour, minute, second, 0);
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null;
  let offsetMinutes = 0;
  const zone = m[8];
  if (zone && zone !== "Z") {
    const oh = Number(zone.slice(1, 3));
    const om = Number(zone.slice(4, 6));
    if (oh > 14 || om > 59) return null;
    offsetMinutes = (zone[0] === "-" ? -1 : 1) * (oh * 60 + om);
  }
  const fraction = BigInt((m[7] ?? "").padEnd(7, "0"));
  return BigInt(date.getTime() - offsetMinutes * 60_000) * TICKS_PER_MS + fraction;
}
